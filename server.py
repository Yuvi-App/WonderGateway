#!/usr/bin/env python3
"""
WonderGateway test servers (replaces the old .ps1 scripts).

Two listeners:
  * TCP 5555 - mopera bpl01/bpl02 gatekeeper for the MobileWonderGate browser;
               replies "allow" (code 0x0010).
  * TCP 80   - HTTP/1.0 server:
                 - Rainbow Islands (POST /service/charge/nph-gettime, ...):
                     nph-gettime -> server time; other /service/ -> "OK".
                 - anything else -> "Welcome to WonderGateway" page (browser).

Point the mopera / game hosts at 127.0.0.1 in wondergateway.ini, then run:
    py server.py
Everything is logged to server.log and the console.
"""
import socket
import threading
import datetime
import os
import re

HTTP_PORT = 80
BPL_PORT = 5555
LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "server.log")
_loglock = threading.Lock()


def log(msg):
    line = f"{datetime.datetime.now():%H:%M:%S.%f} {msg}"
    with _loglock:
        print(line, flush=True)
        with open(LOG, "a", encoding="utf-8", errors="replace") as f:
            f.write(line + "\n")


# Browser landing page (HTML 3.2 subset the WonderGate browser understands).
WELCOME = (
    "<html><head><title>WonderGateway</title></head>\n"
    '<body bgcolor="#ffffff"><center>\n'
    "<h1>Welcome to WonderGateway!</h1><hr>\n"
    "<p>Your WonderSwan is online.</p>\n"
    "</center></body></html>\n"
).encode("ascii")


def http_response(body, ctype="text/html", extra_headers=None, keep_alive=False):
    if isinstance(body, str):
        body = body.encode("ascii", "replace")
    lines = [
        "HTTP/1.0 200 OK",
        f"Content-Type: {ctype}",
        f"Content-Length: {len(body)}",
    ]
    if extra_headers:
        lines += list(extra_headers)
    lines += ["Connection: keep-alive" if keep_alive else "Connection: close", "", ""]
    return "\r\n".join(lines).encode("ascii") + body


def swan_response(fields):
    """Emit each field as an `X-SWAN-<name>: <value>` header (the format the games parse)."""
    hdrs = [f"X-SWAN-{name}: {val}" for name, val in fields.items()]
    return http_response(b"", "text/plain", extra_headers=hdrs)


# Raku Jongg leaderboard body: 3 sections (one per game mode), each a
# "COURSE|0|0|0|0|1" line, a "RAKUJANG=<count>" line, then <count> "|score|name|LL"
# entries. We remember the player's submissions so the list echoes them back.
_rank = {"owner": "PLAYER", "scores": {}}  # scores: mode -> (value, level)


def capture_submission(text):
    body = text.split("\r\n\r\n", 1)[-1]
    m = re.search(r"owner=([^&\r\n]+)", body)
    if m:
        _rank["owner"] = m.group(1).strip()
    for mm in re.finditer(r"score=(\d+)\|(\d+)\|(\d+)", body):
        mode, val, lvl = mm.group(1), mm.group(2), mm.group(3)
        _rank["scores"][mode] = (val, lvl)


def _raku_course(entries):
    out = ["COURSE|0|0|0|0|1", f"RAKUJANG={len(entries)}"]
    out += [f"|{score}|{name}|{lvl}" for score, name, lvl in entries]
    return "\r\n".join(out) + "\r\n"


def ranking_list_response(keep_alive=True):
    owner = _rank["owner"]
    sections = []
    for mode in ("1", "2", "3"):
        sub = _rank["scores"].get(mode)
        entries = []
        if sub:  # the player's own submitted score, ranked #1
            entries.append((sub[0], owner, sub[1]))
        entries.append(("000100", "CPU", "01"))  # a filler runner-up (6-digit like submits)
        sections.append(_raku_course(entries))
    body = "".join(sections)
    log(f"HTTP => swan-ranking leaderboard for '{owner}' ({len(body)} bytes, "
        f"{'keep-alive' if keep_alive else 'close'})")
    return http_response(body, "text/plain", keep_alive=keep_alive)


def read_http_request(conn):
    """Read a full request (headers + Content-Length body). Patient, because the
    bytes trickle in slowly over the emulated serial link."""
    conn.settimeout(8.0)
    data = bytearray()
    hdr_end = -1
    clen = 0
    try:
        while True:
            chunk = conn.recv(4096)
            if not chunk:
                break
            data += chunk
            if hdr_end < 0:
                hdr_end = data.find(b"\r\n\r\n")
                if hdr_end >= 0:
                    head = bytes(data[:hdr_end]).decode("latin1")
                    for ln in head.split("\r\n"):
                        if ln.lower().startswith("content-length:"):
                            try:
                                clen = int(ln.split(":", 1)[1].strip())
                            except ValueError:
                                clen = 0
            if hdr_end >= 0 and len(data) >= hdr_end + 4 + clen:
                break
    except socket.timeout:
        pass
    return bytes(data)


def handle_http(conn, addr):
    # Loop for multiple requests on one connection (Raku Jongg re-sends its POST
    # 2-3x on the same socket; closing early would fail those -> error 400).
    conn.settimeout(10.0)
    try:
        while True:
            data = read_http_request(conn)
            if not data:
                break  # client closed
            text = data.decode("latin1", "replace")
            reqline = text.split("\r\n", 1)[0]
            # Log the request line + any X-swan-* headers (games carry session/account
            # tokens in these).
            xswan = [ln for ln in text.split("\r\n") if ln.lower().startswith("x-swan-")]
            log(f"HTTP <= [{reqline}]" + ("  {" + " | ".join(xswan) + "}" if xswan else ""))

            keep_alive = False
            if "nph-gettime" in text:
                # Server time. Games read X-SWAN-TIME-NOW; TIME-RET:0 = OK.
                t = datetime.datetime.now().strftime("%Y%m%d%H%M%S")
                log(f"HTTP => TIME-RET:0 TIME-NOW:{t}")
                resp = swan_response({"TIME-RET": "0", "TIME-NOW": t})
            elif "nph-srvccount" in text or "nph-srvcinfo" in text:
                # Pocket Fighter billing pre-flight. SRVC-CNT (the service count) is
                # required -- omitting it = error 700. The rest is the service window
                # / pricing that srvcinfo displays; a wide-open, free, not-in-
                # maintenance service.
                now = datetime.datetime.now().strftime("%Y%m%d%H%M%S")
                past, future = "20000101000000", "20991231235959"
                resp = swan_response({
                    "SRVC-RET": "0",       # service result = OK
                    "SRVC-CNT": "1",       # >=1 available service (else error 700)
                    "WM-RET": "0",         # module result = OK
                    "SRVC-OPEN": past,     # service opened long ago
                    "SRVC-CLOSE": future,  # ...closes far in the future
                    "SRVC-TIM": now,
                    "SRVC-TRM": future,
                    "SRVC-PRC": "0",       # free
                    "SRVC-WAY": "0",
                    "SRVC-MTN-FROM": past, "SRVC-MTN-TO": past,   # maintenance in the
                    "CHRG-MTN-FROM": past, "CHRG-MTN-TO": past,   # past, not now
                })
            elif "nph-session" in text:
                # Session result. PF requires CMND-AID (an account id it echoes back
                # as x-swan-cnts-aid) and CMND-URL (where it fetches content next);
                # missing either = error 700. The URL host must route to us via
                # hostname_hack (*.channel.or.jp).
                resp = swan_response({
                    "CMND-RET": "0",
                    "CMND-AID": "PFAID0000000000000001",
                    "CMND-URL": "http://wgg01.channel.or.jp/service/swan-ranking/pocketf",
                })
            elif "swan-ranking" in text and "pocketf" in text:
                # Pocket Fighter score ranking -- a content response, not Raku Jongg's
                # format (that gives error 08). Reply CNTS-RET:0 + echo the cnts-aid,
                # then the score list: an "ANSPF_SCORE=<N>" header and one
                # "course=<char>|<name>|<score>|<date>|0|0" line per entry, where
                # field0 = character (portrait), field1 = 3-char name, field2 = 8-digit
                # score. Parser caps at 50 entries.
                m = re.search(r"x-swan-cnts-aid:\s*(\S+)", text, re.I)
                aid = m.group(1).strip() if m else "PFAID0000000000000001"
                CHARS = ["RYU", "KEN", "CHN", "MOR", "LEI", "FEL",
                         "SAK", "DAN", "GOU", "DVL", "TSA", "IBU"]
                N = 50
                _lines = ["ANSPF_SCORE=%d" % N]
                for _i in range(N):
                    _ch = _i % len(CHARS)
                    _name = CHARS[_ch]
                    _score = (98765432 - _i * 1234567) % 100000000  # descending
                    _lines.append("course=%d|%s|%08d|20240101|0|0" % (_ch, _name, _score))
                pf_body = ("\r\n".join(_lines) + "\r\n").encode("ascii")
                log(f"HTTP => PF ranking ({len(pf_body)}B, {N} entries)")
                resp = http_response(
                    pf_body, "text/plain",
                    extra_headers=["X-SWAN-CNTS-RET: 0", f"X-SWAN-CNTS-AID: {aid}"],
                )
                keep_alive = False
            elif "swan-ranking" in text:
                # Raku Jongg: every request gets the leaderboard body (an empty 200
                # makes the game treat the submit as failed). Submits stay keep-alive
                # (they share one socket); the final list= fetch closes.
                capture_submission(text)
                is_final = "list=" in text
                resp = ranking_list_response(keep_alive=not is_final)
                keep_alive = not is_final
            elif "/service/" in reqline:
                resp = http_response("OK", "text/plain")
            else:
                resp = http_response(WELCOME)

            try:
                conn.sendall(resp)
            except OSError:
                break
            if not keep_alive:
                break
    except (socket.timeout, OSError):
        pass
    try:
        conn.close()
    except OSError:
        pass


def handle_bpl(conn, addr):
    """mopera bpl gatekeeper: read the request, reply 'allow' (0x0010)."""
    data = bytearray()
    try:
        conn.settimeout(0.4)
        while True:
            chunk = conn.recv(4096)
            if not chunk:
                break
            data += chunk
    except socket.timeout:
        pass
    except OSError:
        pass
    printable = "".join(chr(b) if 32 <= b < 127 else "." for b in data)
    log(f"BPL  <= {len(data)} bytes [{printable}]")

    # 24-byte header: 01 18 <code:LE16> <total:LE32> then zeros.  code 0x0010 = allow.
    resp = bytearray(24)
    resp[0] = 0x01
    resp[1] = 0x18
    resp[2] = 0x10
    resp[4] = 0x18
    try:
        conn.sendall(bytes(resp))
    except OSError:
        pass
    conn.close()


def _safe(handler, conn, addr):
    try:
        handler(conn, addr)
    except Exception as e:  # noqa: BLE001
        log(f"handler error: {e}")
        try:
            conn.close()
        except OSError:
            pass


def serve(port, handler):
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        s.bind(("0.0.0.0", port))
    except OSError as e:
        log(f"!! cannot bind port {port}: {e}")
        return
    s.listen(8)
    log(f"listening on 0.0.0.0:{port}")
    while True:
        conn, addr = s.accept()
        threading.Thread(target=_safe, args=(handler, conn, addr), daemon=True).start()


def main():
    log("===== WonderGateway test server starting =====")
    threading.Thread(target=serve, args=(BPL_PORT, handle_bpl), daemon=True).start()
    serve(HTTP_PORT, handle_http)  # runs on the main thread


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\nbye")
