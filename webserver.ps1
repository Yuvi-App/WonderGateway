# Tiny HTTP/1.0 web server for WonderGateway testing.
# Serves a simple "Welcome to WonderGateway" page to every request (using the
# HTML 3.2 subset)
#
# Usage (normal PowerShell window):
#   .\webserver.ps1                # listen on 127.0.0.1:80
#   .\webserver.ps1 -Port 8080     # if 80 is taken (then add a socket_hack)
param(
  [int]$Port = 80,
  [string]$BindAddress = "0.0.0.0"   # 0.0.0.0 = all interfaces; use 127.0.0.1 for loopback only
)

try {
  $ip = [System.Net.IPAddress]::Parse($BindAddress)
  $listener = [System.Net.Sockets.TcpListener]::new($ip, $Port)
  $listener.Start()
} catch {
  Write-Host "Failed to bind ${BindAddress}:$Port -> $($_.Exception.Message)" -ForegroundColor Red
  Write-Host "Port 80 may be held by IIS/http.sys. Re-run with -Port 8080 and add to"
  Write-Host "wondergateway.ini under [socket_hack]:   127.0.0.1:80 = 127.0.0.1:8080"
  return
}

# The page the WonderSwan will render. Simple HTML 3.2 - no CSS/JS.
$body = @"
<html>
<head><title>WonderGateway</title></head>
<body bgcolor="#ffffff">
<center>
<h1>Welcome to WonderGateway!</h1>
<hr>
<p>Your WonderSwan is online.</p>
<p>Served locally by WonderGateway.</p>
</center>
</body>
</html>
"@ -replace "`r`n", "`n"

$log = Join-Path $PSScriptRoot 'webserver.log'
Write-Host "WonderGateway web server on ${BindAddress}:$Port  (Ctrl+C to stop)" -ForegroundColor Green
Write-Host "Requests logged to $log`n"

while ($true) {
  $client = $listener.AcceptTcpClient()
  $stream = $client.GetStream()

  # Read the request. It arrives SLOWLY over the emulated serial link (in socksend
  # chunks), so block patiently until we see the end of the HTTP headers (blank
  # line) or time out - do NOT reply to an empty request and close early.
  $ms = New-Object System.IO.MemoryStream
  $buf = New-Object byte[] 4096
  $stream.ReadTimeout = 8000
  try {
    while ($true) {
      $n = $stream.Read($buf, 0, $buf.Length)
      if ($n -le 0) { break }
      $ms.Write($buf, 0, $n)
      $sofar = [System.Text.Encoding]::ASCII.GetString($ms.ToArray())
      if ($sofar.Contains("`r`n`r`n") -or $sofar.Contains("`n`n")) { break }
    }
  } catch {}   # ReadTimeout throws -> proceed with whatever arrived

  $bytes = $ms.ToArray()
  $ascii = ([System.Text.Encoding]::ASCII.GetString($bytes)) -replace '[^\x20-\x7E\r\n]', '.'
  $hex   = ($bytes | ForEach-Object { $_.ToString('X2') }) -join ' '
  $reqLine = ($ascii -split "`n")[0].Trim()
  $stamp = (Get-Date).ToString('HH:mm:ss.fff')
  $entry = "===== $stamp  $($bytes.Length) bytes  [$reqLine] =====`r`n--- ASCII ---`r`n$ascii`r`n--- HEX ---`r`n$hex`r`n"
  Write-Host $entry -ForegroundColor Cyan
  Add-Content -Path $log -Value $entry

  # HTTP/1.0 response.
  $bodyBytes = [System.Text.Encoding]::ASCII.GetBytes($body)
  $header = "HTTP/1.0 200 OK`r`n" +
            "Content-Type: text/html`r`n" +
            "Content-Length: $($bodyBytes.Length)`r`n" +
            "Connection: close`r`n`r`n"
  $respBytes = [System.Text.Encoding]::ASCII.GetBytes($header) + $bodyBytes
  try { $stream.Write($respBytes, 0, $respBytes.Length); $stream.Flush() } catch {}
  $client.Close()
}
