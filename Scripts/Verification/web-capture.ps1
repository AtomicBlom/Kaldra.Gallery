<#
.SYNOPSIS
  Capture one or more Loom.Web demo frames in a headless browser — the capture half of the WebGPU parity leg.

.DESCRIPTION
  Serves a published Loom.Web app from a local HttpListener (static files + a POST /capture endpoint), launches
  the browser headless at `?capture=<name>&post`, and waits for the page to POST back the PNG it grabs at frame
  60. The capture is read off the swapchain texture in C# (compositor-independent), so — unlike a CDP screenshot
  — it is reliable in headless mode.

  This script only captures; it does not diff. `Loom.Tests`' WebGpuParityTests drives it and compares with the
  same ImageComparison the desktop parity test uses, so both legs share one tolerance rule. For an eyeball diff
  of two PNGs by hand, run diff.ps1 afterwards.

  Publish first:  dotnet publish Loom.Web -c Release -o <dir>
  Pass either the publish root or its wwwroot as -PublishDir; both are accepted.

.OUTPUTS
  <OutDir>/<name>.png per captured demo, plus <OutDir>/adapter.txt naming the GPU the browser actually used.

  Exit codes are machine-readable, because the caller is a test that must SKIP rather than fail when the box
  simply cannot run this: 0 = every requested demo captured, 2 = no browser found, 3 = no usable publish dir,
  4 = at least one capture timed out.

.EXAMPLE
  pwsh Scripts/Verification/web-capture.ps1 -PublishDir pub -Names cube,bloom -OutDir web-shots
#>
param(
	[Parameter(Mandatory)][string]$PublishDir,
	[Parameter(Mandatory)][string[]]$Names,
	[Parameter(Mandatory)][string]$OutDir,
	[int]$Port = 8099,
	[int]$TimeoutSec = 120,
	[string]$BrowserPath
)
$ErrorActionPreference = "Stop"

# Accept both `-Names a,b` and `-Names "a,b"`. Launching pwsh as a child process flattens an array argument into
# one comma-joined string, so the C# caller cannot pass a real array however it spells the call — splitting here
# is what makes the two invocation styles agree. No demo key contains a comma.
$Names = @($Names | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

# ── Publish dir ────────────────────────────────────────────────────────────────────────────────────────────────
# The served root is the folder holding index.html. Accept the publish root or the wwwroot inside it, so a caller
# does not have to know which shape `dotnet publish -o` produced.
if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
	Write-Host "NO-PUBLISH: '$PublishDir' does not exist. Run: dotnet publish Loom.Web -c Release -o <dir>"
	exit 3
}
$publishRoot = (Resolve-Path -LiteralPath $PublishDir).Path
$root = @($publishRoot, (Join-Path $publishRoot "wwwroot")) |
	Where-Object { Test-Path -LiteralPath (Join-Path $_ "index.html") -PathType Leaf } |
	Select-Object -First 1
if (-not $root) {
	Write-Host "NO-PUBLISH: no index.html under '$publishRoot' (nor its wwwroot) — publish Loom.Web first."
	exit 3
}

# ── Browser ────────────────────────────────────────────────────────────────────────────────────────────────────
# WebGPU in headless needs --enable-unsafe-webgpu, so it has to be a Chromium; Edge is the one present by default
# on a Windows box. LOOM_WEB_BROWSER lets CI point at whatever it has without editing the script.
$browser = @(
	$BrowserPath
	$env:LOOM_WEB_BROWSER
	"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
	"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
	"C:\Program Files\Google\Chrome\Application\chrome.exe"
	"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
if (-not $browser) {
	Write-Host "NO-BROWSER: found no Chromium (checked -BrowserPath, LOOM_WEB_BROWSER, the default Edge/Chrome paths)."
	exit 2
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

# The profile directory doubles as this harness's ownership marker: it is unique to the port, every child process
# the browser spawns inherits it on the command line, and nothing else on the box mentions it.
$profileToken = "loom-web-capture-$Port"
$profileDir = Join-Path ([IO.Path]::GetTempPath()) $profileToken

# Kill ONLY the browsers this harness launched, matched by that marker. Never match on the executable name: the
# user's own browser is very likely open, and killing it would be an unrecoverable surprise.
#
# This exists because a stray browser is silently fatal rather than noisy — a later run reuses the same profile
# directory, the surviving instance owns its lock, and the fresh launch simply never navigates, so the capture
# "times out" with no hint as to why. Sweep before as well as after: an interrupted run leaves no `finally`.
function Stop-HarnessBrowsers {
	try {
		$procs = @(Get-CimInstance Win32_Process -Filter "CommandLine LIKE '%$profileToken%'" -ErrorAction Stop)
	}
	catch { return 0 } # Not Windows, or WMI unavailable — nothing to sweep.
	foreach ($p in $procs) {
		try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop } catch {}
	}
	if ($procs.Count -gt 0) {
		# The profile lock outlives the process by a moment; a relaunch into a still-locked profile never navigates.
		for ($i = 0; $i -lt 20; $i++) {
			Start-Sleep -Milliseconds 100
			$left = @(Get-CimInstance Win32_Process -Filter "CommandLine LIKE '%$profileToken%'" -ErrorAction SilentlyContinue)
			if ($left.Count -eq 0) { break }
		}
	}
	return $procs.Count
}

$mime = @{
	".html" = "text/html"; ".js" = "text/javascript"; ".mjs" = "text/javascript"; ".json" = "application/json";
	".wasm" = "application/wasm"; ".css" = "text/css"; ".png" = "image/png"; ".ico" = "image/x-icon";
	".dll" = "application/octet-stream"; ".dat" = "application/octet-stream"; ".blat" = "application/octet-stream";
	".woff" = "font/woff"; ".woff2" = "font/woff2"; ".wgsl" = "text/plain"; ".pdb" = "application/octet-stream";
}

$listener = $null
$pending = $null      # An in-flight GetContextAsync outlives a per-demo timeout; dropping it would swallow the
                      # next demo's first request, so it is carried across iterations rather than re-issued.
$adapter = $null
$captured = [Collections.Generic.List[string]]::new()
$timedOut = [Collections.Generic.List[string]]::new()

try {
	$swept = Stop-HarnessBrowsers
	if ($swept -gt 0) { Write-Host "SWEPT: killed $swept stray harness browser process(es) from an earlier run." }

	$listener = [System.Net.HttpListener]::new()
	$listener.Prefixes.Add("http://localhost:$Port/")
	$listener.Start()

	foreach ($name in $Names) {
		$out = Join-Path $OutDir ($name + ".png")
		$proc = Start-Process $browser -PassThru -ArgumentList @(
			"--headless=new", "--enable-unsafe-webgpu", "--no-sandbox", "--no-first-run", "--disable-extensions",
			"--use-angle=default", "--user-data-dir=$profileDir", "--window-size=1280,720",
			"http://localhost:$Port/?capture=$([Uri]::EscapeDataString($name))&post")

		$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
		$got = $false
		try {
			while ([DateTime]::UtcNow -lt $deadline) {
				if ($null -eq $pending) { $pending = $listener.GetContextAsync() }
				$remaining = [int][Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
				if (-not $pending.Wait($remaining)) { break }
				$ctx = $pending.Result
				$pending = $null
				$req = $ctx.Request
				$res = $ctx.Response
				try {
					if ($req.HttpMethod -eq "POST" -and $req.Url.AbsolutePath -eq "/capture") {
						$ms = [IO.MemoryStream]::new()
						$req.InputStream.CopyTo($ms)
						[IO.File]::WriteAllBytes($out, $ms.ToArray())
						if (-not $adapter) { $adapter = $req.QueryString["adapter"] }
						$res.StatusCode = 204
						$res.Close()
						$got = $true
						break
					}
					# Static file GET: map the URL path under the served root (default document index.html).
					$rel = [Uri]::UnescapeDataString($req.Url.AbsolutePath.TrimStart('/'))
					if ([string]::IsNullOrEmpty($rel)) { $rel = "index.html" }
					$path = Join-Path $root $rel
					if (Test-Path -LiteralPath $path -PathType Leaf) {
						$bytes = [IO.File]::ReadAllBytes($path)
						$ext = [IO.Path]::GetExtension($path).ToLowerInvariant()
						$res.ContentType = if ($mime.ContainsKey($ext)) { $mime[$ext] } else { "application/octet-stream" }
						$res.ContentLength64 = $bytes.Length
						$res.OutputStream.Write($bytes, 0, $bytes.Length)
					}
					else { $res.StatusCode = 404 }
					$res.Close()
				}
				catch { try { $res.Abort() } catch {} }
			}
		}
		finally {
			if ($proc -and -not $proc.HasExited) { try { $proc.Kill() } catch {} }
			Stop-HarnessBrowsers | Out-Null
		}

		if ($got) {
			$captured.Add($name)
			Write-Host ("CAPTURED: $name -> $out (" + (Get-Item -LiteralPath $out).Length + " bytes)")
		}
		else {
			$timedOut.Add($name)
			Write-Host "TIMEOUT: $name produced no capture within $TimeoutSec s."
		}
	}
}
finally {
	Stop-HarnessBrowsers | Out-Null
	if ($listener) { try { $listener.Stop(); $listener.Close() } catch {} }
}

# Name the GPU that actually rendered these frames. A headless browser can fall back to a software adapter, which
# moves pixels enough to fail a strict parity bar — so an unexplained failure should show this line first.
if ($adapter) {
	Write-Host "ADAPTER: $adapter"
	Set-Content -LiteralPath (Join-Path $OutDir "adapter.txt") -Value $adapter -NoNewline
}

Write-Host ("SUMMARY: captured " + $captured.Count + "/" + $Names.Count + " demo(s).")
if ($timedOut.Count -gt 0) { exit 4 }
exit 0
