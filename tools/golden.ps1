<#
.SYNOPSIS
  Golden-image + cross-backend parity harness (local). Captures each tracked demo deterministically on BOTH
  backends, diffs each against the committed baselines in Loom.Tests/Goldens (GoldenImageTests), and diffs the
  Vulkan and DX12 captures against each other (CrossBackendParityTests).

.DESCRIPTION
  The capture step needs a GPU, so this runs locally (GitHub-hosted CI runners have none — the CI workflow only
  builds + runs the no-GPU unit tests, which SKIP the golden + parity tests). The sandbox's --capture freezes
  time + camera, so a re-run reproduces the baseline bit-for-bit within tolerance.

.PARAMETER Backend
  Which backend's captures define the committed baseline for GoldenImageTests: "Vulkan" (default) or "DX12".
  Both backends are always captured (the parity test needs both); this only selects the baseline source.
  Note: on DX12 a demo whose shader stage has no DXIL on this toolchain (e.g. ssao_blur on win-arm64) is
  skipped, so the parity test simply doesn't compare a frame missing from one backend.

.PARAMETER Update
  Overwrite the committed baselines with the chosen backend's fresh captures (accept new goldens).

.EXAMPLE
  pwsh tools/golden.ps1            # capture both backends; diff vs baselines + cross-backend parity
  pwsh tools/golden.ps1 -Update    # regenerate baselines from the chosen backend
#>
param(
	[string]$Backend = "Vulkan",
	[switch]$Update
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$goldens = Join-Path $repo "Loom.Tests/Goldens"
$captureRoot = Join-Path ([IO.Path]::GetTempPath()) ("loom-golden-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$vulkanDir = Join-Path $captureRoot "vulkan"
$dx12Dir = Join-Path $captureRoot "dx12"
New-Item -ItemType Directory -Force -Path $vulkanDir, $dx12Dir | Out-Null

# filename (without .png) -> exact demo Name (--demo). Keep in sync with the committed baselines.
$demos = [ordered]@{
	"FullReference"     = "Full reference frame"
	"Bloom"             = "Bloom"
	"Particles"         = "Particles"
	"PixelArt"          = "Pixel Art"
	"StylizedReference" = "Stylized reference"
}

Write-Host "[golden] building..."
dotnet build (Join-Path $repo "Loom.slnx") -c Debug --nologo | Out-Null

# Capture every demo on BOTH backends. --capture freezes time + camera and exits after one frame; --backend
# selects DX12 (Vulkan is the default). 'dx12' is the accepted backend value (aliased to directx12).
$targets = @(
	@{ Name = "Vulkan"; Dir = $vulkanDir; Extra = @() },
	@{ Name = "DX12"; Dir = $dx12Dir; Extra = @('--backend', 'dx12') }
)
foreach ($target in $targets) {
	foreach ($entry in $demos.GetEnumerator()) {
		$out = Join-Path $target.Dir ($entry.Key + ".png")
		Write-Host "[golden] capturing '$($entry.Value)' -> $($target.Name)/$($entry.Key).png"
		dotnet run --project (Join-Path $repo "Loom.Sandbox") -c Debug --no-build -- --demo $entry.Value --capture $out @($target.Extra) | Out-Null
	}
}

$baselineDir = if ($Backend -eq "DX12") { $dx12Dir } else { $vulkanDir }

if ($Update) {
	New-Item -ItemType Directory -Force -Path $goldens | Out-Null
	Copy-Item (Join-Path $baselineDir "*.png") $goldens -Force
	Write-Host "[golden] baselines updated in $goldens (from $Backend captures)"
	exit 0
}

Write-Host "[golden] diffing captures against baselines + cross-backend parity..."
# LOOM_GOLDEN_DIR drives GoldenImageTests (vs committed baselines); LOOM_VULKAN_DIR/LOOM_DX12_DIR drive
# CrossBackendParityTests (the two backends vs each other). MTP syntax (.NET 10): --project + --treenode-filter.
$env:LOOM_GOLDEN_DIR = $baselineDir
$env:LOOM_VULKAN_DIR = $vulkanDir
$env:LOOM_DX12_DIR = $dx12Dir
try {
	dotnet test --project (Join-Path $repo "Loom.Tests/Loom.Tests.csproj") --treenode-filter "/*/*/GoldenImageTests/*"
	$golden = $LASTEXITCODE
	dotnet test --project (Join-Path $repo "Loom.Tests/Loom.Tests.csproj") --treenode-filter "/*/*/CrossBackendParityTests/*"
	$parity = $LASTEXITCODE
}
finally {
	$env:LOOM_GOLDEN_DIR = $null
	$env:LOOM_VULKAN_DIR = $null
	$env:LOOM_DX12_DIR = $null
}
exit ([Math]::Max($golden, $parity))
