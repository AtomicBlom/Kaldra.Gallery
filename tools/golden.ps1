<#
.SYNOPSIS
  M13 golden-image regression harness (local). Captures each tracked demo deterministically and diffs it against the
  committed baselines in Loom.Tests/Goldens via the opt-in GoldenImageTests.

.DESCRIPTION
  The capture step needs a GPU, so this runs locally (GitHub-hosted CI runners have none — the CI workflow only
  builds + runs the no-GPU unit tests, which skip the golden test). LOOM_CAPTURE freezes time + camera, so a re-run
  reproduces the baseline bit-for-bit within tolerance.

.PARAMETER Backend
  "Vulkan" (default) or "DX12" (LOOM_BACKEND). Note: the DX12 demo gallery is currently blocked by a missing
  ssao_blur.ps.dxil, so DX12 golden capture is not yet usable for the gallery.

.PARAMETER Update
  Overwrite the committed baselines with the fresh captures (accept new goldens).

.EXAMPLE
  pwsh tools/golden.ps1            # capture + diff against baselines
  pwsh tools/golden.ps1 -Update    # regenerate baselines
#>
param(
	[string]$Backend = "Vulkan",
	[switch]$Update
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$goldens = Join-Path $repo "Loom.Tests/Goldens"
$captureDir = Join-Path ([IO.Path]::GetTempPath()) ("loom-golden-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force -Path $captureDir | Out-Null

# filename (without .png) -> exact demo Name (LOOM_DEMO). Keep in sync with the committed baselines.
$demos = [ordered]@{
	"FullReference" = "Full reference frame"
	"Bloom"         = "Bloom"
	"Particles"     = "Particles"
}

Write-Host "[golden] building..."
dotnet build (Join-Path $repo "Loom.slnx") -c Debug --nologo | Out-Null

$env:LOOM_BACKEND = if ($Backend -eq "DX12") { "DX12" } else { $null }
foreach ($entry in $demos.GetEnumerator()) {
	$env:LOOM_DEMO = $entry.Value
	$env:LOOM_CAPTURE = Join-Path $captureDir ($entry.Key + ".png")
	Write-Host "[golden] capturing '$($entry.Value)' -> $($entry.Key).png ($Backend)"
	dotnet run --project (Join-Path $repo "Loom.Sandbox") -c Debug --no-build | Out-Null
}
$env:LOOM_DEMO = $null; $env:LOOM_CAPTURE = $null; $env:LOOM_BACKEND = $null

if ($Update) {
	New-Item -ItemType Directory -Force -Path $goldens | Out-Null
	Copy-Item (Join-Path $captureDir "*.png") $goldens -Force
	Write-Host "[golden] baselines updated in $goldens"
	exit 0
}

Write-Host "[golden] diffing captures against baselines..."
$env:LOOM_GOLDEN_DIR = $captureDir
dotnet test (Join-Path $repo "Loom.Tests/Loom.Tests.csproj") --filter "FullyQualifiedName~GoldenImageTests" --nologo
$code = $LASTEXITCODE
$env:LOOM_GOLDEN_DIR = $null
exit $code
