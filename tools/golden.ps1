<#
.SYNOPSIS
  M13 golden-image regression harness (local). Captures each tracked demo deterministically and diffs it against the
  committed baselines in Loom.Tests/Goldens via the opt-in GoldenImageTests.

.DESCRIPTION
  The capture step needs a GPU, so this runs locally (GitHub-hosted CI runners have none — the CI workflow only
  builds + runs the no-GPU unit tests, which skip the golden test). The sandbox's --capture freezes time + camera,
  so a re-run reproduces the baseline bit-for-bit within tolerance.

.PARAMETER Backend
  "Vulkan" (default) or "DX12" (passes --backend dx12). Note: on DX12 a demo whose shader stage has no DXIL on
  this toolchain (e.g. ssao_blur on win-arm64) is skipped, so a golden that depends on it can't be captured there.

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

# filename (without .png) -> exact demo Name (--demo). Keep in sync with the committed baselines.
$demos = [ordered]@{
	"FullReference" = "Full reference frame"
	"Bloom"         = "Bloom"
	"Particles"     = "Particles"
	"PixelArt"      = "Pixel Art"
}

Write-Host "[golden] building..."
dotnet build (Join-Path $repo "Loom.slnx") -c Debug --nologo | Out-Null

# The sandbox is configured by command-line options (no LOOM_* env vars). --capture freezes time + camera and
# exits after one frame; --backend dx12 selects DX12 (Vulkan is the default).
$backendArgs = if ($Backend -eq "DX12") { @('--backend', 'dx12') } else { @() }
foreach ($entry in $demos.GetEnumerator()) {
	$out = Join-Path $captureDir ($entry.Key + ".png")
	Write-Host "[golden] capturing '$($entry.Value)' -> $($entry.Key).png ($Backend)"
	dotnet run --project (Join-Path $repo "Loom.Sandbox") -c Debug --no-build -- --demo $entry.Value --capture $out @backendArgs | Out-Null
}

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
