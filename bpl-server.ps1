# Minimal bpl01/bpl02.mopera.ne.jp responder for the MobileWonderGate browser.
# Protocol ref: https://ws.nesdev.org/wiki/WonderGate/bplXX.mopera.ne.jp  (TCP 5555)
#
# Usage (own PowerShell window):
#   .\bpl-server.ps1                 # allow (0x0010)
#   .\bpl-server.ps1 -Code 0x0012    # try deny/redirect, etc.
param(
  [int]$Port = 5555,
  [int]$Code = 0x0010,
  [string]$BindAddress = "0.0.0.0"
)

try {
  $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse($BindAddress), $Port)
  $listener.Start()
} catch {
  Write-Host "Failed to bind ${BindAddress}:$Port -> $($_.Exception.Message)" -ForegroundColor Red
  return
}

$log = Join-Path $PSScriptRoot 'bpl.log'
Write-Host ("bpl responder on {0}:{1}  code=0x{2:X4}  (Ctrl+C to stop)" -f $BindAddress,$Port,$Code) -ForegroundColor Green
Write-Host "Requests logged to $log`n"

# Build the 24-byte response header: 01 18 <code:LE2> <total:LE4> <16 zero bytes>
$resp = New-Object byte[] 24
$resp[0] = 0x01
$resp[1] = 0x18
$resp[2] = [byte]($Code -band 0xFF)
$resp[3] = [byte](($Code -shr 8) -band 0xFF)
$resp[4] = 0x18   # total size = 24 (header only, 0 blocks)

while ($true) {
  $client = $listener.AcceptTcpClient()
  $stream = $client.GetStream()
  $ms = New-Object System.IO.MemoryStream
  $buf = New-Object byte[] 4096
  try {
    do {
      Start-Sleep -Milliseconds 120
      while ($stream.DataAvailable) {
        $n = $stream.Read($buf, 0, $buf.Length)
        if ($n -le 0) { break }
        $ms.Write($buf, 0, $n)
      }
    } while ($stream.DataAvailable)
  } catch {}

  $bytes = $ms.ToArray()
  $ascii = ([System.Text.Encoding]::ASCII.GetString($bytes)) -replace '[^\x20-\x7E]', '.'
  $hex   = ($bytes | ForEach-Object { $_.ToString('X2') }) -join ' '
  # pull the URL out of the trailing printable run
  $url = ([regex]::Match($ascii, '[!-~]{4,}$')).Value
  $stamp = (Get-Date).ToString('HH:mm:ss.fff')
  $entry = ("===== {0}  {1} bytes  URL=[{2}]  replying code=0x{3:X4} =====`r`n--- ASCII ---`r`n{4}`r`n--- HEX ---`r`n{5}`r`n" -f $stamp,$bytes.Length,$url,$Code,$ascii,$hex)
  Write-Host $entry -ForegroundColor Cyan
  Add-Content -Path $log -Value $entry

  try { $stream.Write($resp, 0, $resp.Length); $stream.Flush() } catch {}
  $client.Close()
}
