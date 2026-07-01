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


def http_response(body, ctype="text/html", extra_headers=None):
    if isinstance(body, str):
        body = body.encode("ascii", "replace")
    lines = [
        "HTTP/1.0 200 OK",
        f"Content-Type: {ctype}",
        f"Content-Length: {len(body)}",
    ]
    if extra_headers:
        lines += list(extra_headers)
    lines += ["Connection: close", "", ""]
    return "\r\n".join(lines).encode("ascii") + body


def swan_response(fields):
    """RI parses response lines of the form `X-SWAN-<FIELD>: <value>` (see ce1e
    in the Rainbow Islands ROM). Emit each field as such a header."""
    hdrs = [f"X-SWAN-{name}: {val}" for name, val in fields.items()]
    return http_response(b"", "text/plain", extra_headers=hdrs)


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
    data = read_http_request(conn)
    text = data.decode("latin1", "replace")
    reqline = text.split("\r\n", 1)[0]
    log(f"HTTP <= [{reqline}]")

    if "nph-gettime" in text:
        # RI wants the time in an "X-SWAN-TIME-NOW:" header (YYYYMMDDHHMMSS).
        t = datetime.datetime.now().strftime("%Y%m%d%H%M%S")
        log(f"HTTP => X-SWAN-TIME-NOW: {t}")
        resp = swan_response({"TIME-NOW": t})
    elif "/service/" in reqline:
        resp = http_response("OK", "text/plain")
    else:
        resp = http_response(WELCOME)

    try:
        conn.sendall(resp)
    except OSError:
        pass
    conn.close()


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
