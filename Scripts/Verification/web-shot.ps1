<#
.SYNOPSIS
  Drive headless Edge over the DevTools protocol to load a URL, wait REAL time for the .NET WASM runtime to
  boot and render, then read a page expression (default: the #status text) and capture a screenshot.

.DESCRIPTION
  The obvious `msedge --headless=new --virtual-time-budget=N --screenshot=out.png URL` does NOT work for a
  .NET WASM app: virtual time fast-forwards through the idle initial load and closes the browser in well under
  a second — long before the multi-second runtime boot finishes, so the capture is always the pre-boot page.
  This helper instead launches Edge with a remote-debugging port, opens the target, sleeps a real number of
  seconds, and only then evaluates + screenshots over CDP (via System.Net.WebSockets, no external deps).

  Note: headless GPU-canvas compositing can still be flaky for a screenshot; the authoritative browser parity
  check is the engine's own readback→PNG path (a graph target copied to a buffer, mapped, and PNG-encoded in
  C#), which bypasses the compositor. Use this helper for status/■diagnostic reads and quick visual smoke.

.EXAMPLE
  pwsh Scripts/Verification/web-shot.ps1 -Url http://localhost:8093/ -Out shot.png
  pwsh Scripts/Verification/web-shot.ps1 -Url http://localhost:8093/ -Out s.png -Eval "globalThis.__kaldraDebug()"
#>
param(
	[Parameter(Mandatory)][string]$Url,
	[Parameter(Mandatory)][string]$Out,
	[int]$WaitSec = 10,
	[int]$Port = 9333,
	[string]$Eval = "document.querySelector('#status')?.textContent",
	[string]$EdgePath = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
)
$ErrorActionPreference = "Stop"
$profile = Join-Path ([IO.Path]::GetDirectoryName((Resolve-Path -LiteralPath (Split-Path $Out -Parent)))) ("web-shot-profile-" + $Port)

$proc = Start-Process $EdgePath -PassThru -ArgumentList @(
	"--headless=new", "--enable-unsafe-webgpu", "--no-sandbox", "--no-first-run", "--disable-extensions",
	"--remote-debugging-port=$Port", "--user-data-dir=$profile", "--window-size=640,480", "about:blank")

function Recv-Until($ws, $wantId) {
	$buf = [byte[]]::new(2097152)
	while ($true) {
		$sb = [System.Text.StringBuilder]::new()
		do {
			$seg = [ArraySegment[byte]]::new($buf)
			$res = $ws.ReceiveAsync($seg, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
			[void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $res.Count))
		} while (-not $res.EndOfMessage)
		$msg = $sb.ToString() | ConvertFrom-Json
		if ($msg.id -eq $wantId) { return $msg }
	}
}
function Send-Cdp($ws, $id, $method, $params) {
	$obj = @{ id = $id; method = $method }
	if ($params) { $obj.params = $params }
	$bytes = [Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json -Depth 8 -Compress))
	$ws.SendAsync([ArraySegment[byte]]::new($bytes), [Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
}

try {
	Start-Sleep -Seconds 2
	$tab = Invoke-RestMethod -Method Put -Uri "http://localhost:$Port/json/new?$([Uri]::EscapeDataString($Url))"
	$ws = [Net.WebSockets.ClientWebSocket]::new()
	$ws.ConnectAsync([Uri]$tab.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null

	Start-Sleep -Seconds $WaitSec

	Send-Cdp $ws 1 "Runtime.evaluate" @{ expression = $Eval; returnByValue = $true }
	Write-Host ("STATUS: " + (Recv-Until $ws 1).result.result.value)

	Send-Cdp $ws 2 "Page.captureScreenshot" @{ format = "png" }
	[IO.File]::WriteAllBytes($Out, [Convert]::FromBase64String((Recv-Until $ws 2).result.data))
	Write-Host ("SHOT: $Out (" + (Get-Item $Out).Length + " bytes)")
	$ws.Dispose()
}
finally {
	if ($proc -and -not $proc.HasExited) { $proc.Kill() }
}
