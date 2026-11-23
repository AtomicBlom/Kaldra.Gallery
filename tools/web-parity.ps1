<#
.SYNOPSIS
  Publish Kaldra.Web and run the WebGPU↔Vulkan pixel-parity leg (WebGpuParityTests).

.DESCRIPTION
  WebGpuParityTests renders each checked demo twice — once on Vulkan in-process, once on WebGPU in a headless
  browser — and diffs them at the same tolerance the desktop parity test uses. It cannot publish Kaldra.Web itself:
  that takes minutes, needs the `wasm-tools` workload, and Kaldra.Gallery.Tests deliberately does not reference Kaldra.Web (the
  wasm SDK would follow it into every desktop test run). So the test reads KALDRA_WEB_PUBLISH and SKIPs without it,
  and this script is the wrapper that supplies one.

  Requires: the `wasm-tools` workload (`dotnet workload install wasm-tools`, from an ELEVATED terminal — the MSI's
  UAC prompt is cancelled in a non-interactive shell), a GPU with Vulkan, and Edge or Chrome for the browser half.
  The test SKIPs rather than fails when any of those is missing, so this is safe to run anywhere.

  Publishes Release: AOT changes how fast the frame arrives, not what is in it, and the capture is taken at a fixed
  frame on a frozen clock either way.

.EXAMPLE
  pwsh tools/web-parity.ps1                 # publish, then run the parity leg
  pwsh tools/web-parity.ps1 -SkipPublish    # reuse the last publish (iterating on the test or the harness)
#>
param(
	[string]$PublishDir,
	[switch]$SkipPublish
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $PublishDir) { $PublishDir = Join-Path $repo "artifacts/web-parity" }

if (-not $SkipPublish) {
	Write-Host "[web-parity] publishing Kaldra.Web (Release) to $PublishDir ..."
	# Kaldra.Web is deliberately outside BinaryVibrance.Kaldra.slnx (its wasm SDK would pull the browser toolchain into the desktop
	# build), so it is published by project path rather than as part of the solution.
	dotnet publish (Join-Path $repo "Kaldra.Web") -c Release -o $PublishDir
	if ($LASTEXITCODE -ne 0) {
		Write-Host "[web-parity] publish failed. If it could not find a browser-wasm runtime pack, install the" -ForegroundColor Yellow
		Write-Host "[web-parity] workload from an ELEVATED terminal:  dotnet workload install wasm-tools" -ForegroundColor Yellow
		exit $LASTEXITCODE
	}
}
elseif (-not (Test-Path -LiteralPath $PublishDir)) {
	throw "web-parity: -SkipPublish was given but '$PublishDir' does not exist."
}

Write-Host "[web-parity] running WebGpuParityTests (skips cleanly without Vulkan or a browser) ..."
$env:KALDRA_WEB_PUBLISH = (Resolve-Path -LiteralPath $PublishDir).Path
try {
	dotnet run --project (Join-Path $repo "Kaldra.Gallery.Tests") -c Debug -- --treenode-filter "/*/*/WebGpuParityTests/*"
	$code = $LASTEXITCODE
	# The test platform exits 8 when no test actually executed, and a filtered run of one skipped test is exactly
	# that. Skipping is the correct outcome on a box with no GPU or no browser, so it must not read as a red step.
	if ($code -eq 8) {
		Write-Host "[web-parity] the leg skipped — no Vulkan device, no browser, or no publish. Nothing was checked."
		exit 0
	}
	exit $code
}
finally {
	$env:KALDRA_WEB_PUBLISH = $null
}
