<#
.SYNOPSIS
  Regenerate the committed golden-image baselines (Kaldra.Gallery.Tests/Goldens) from a fresh in-process render.

.DESCRIPTION
  Golden + cross-backend parity are ordinary GPU-gated unit tests now (GalleryGoldenTests / GalleryParityTests
  in Kaldra.Gallery.Tests): they render the gallery demos offscreen in-process — no sandbox window, no --capture. They run
  as part of `dotnet run --project Kaldra.Gallery.Tests` on a machine with a GPU and SKIP on the no-GPU CI runner.

  This script only handles the one thing a test can't do for you: accepting new baselines. It runs
  GalleryGoldenTests with KALDRA_GOLDEN_UPDATE set, which re-renders each tracked demo and overwrites its PNG under
  Kaldra.Gallery.Tests/Goldens. Review the diff and commit. Omit -Update to just run the golden + parity tests once.

.EXAMPLE
  pwsh tools/golden.ps1            # run the golden + parity tests once (GPU required)
  pwsh tools/golden.ps1 -Update    # regenerate the committed baselines, then review + commit the PNG diff
#>
param(
	[switch]$Update
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$tests = Join-Path $repo "Kaldra.Gallery.Tests"

if ($Update) {
	Write-Host "[golden] regenerating baselines in Kaldra.Gallery.Tests/Goldens (KALDRA_GOLDEN_UPDATE) ..."
	$env:KALDRA_GOLDEN_UPDATE = "1"
	try {
		dotnet run --project $tests -c Debug -- --treenode-filter "/*/*/GalleryGoldenTests/*"
	}
	finally {
		$env:KALDRA_GOLDEN_UPDATE = $null
	}
	Write-Host "[golden] done — review 'git diff Kaldra.Gallery.Tests/Goldens' and commit if the new renders are correct."
	exit 0
}

Write-Host "[golden] running golden + parity tests (GPU required; skips without one) ..."
dotnet run --project $tests -c Debug -- --treenode-filter "/*/*/GalleryGoldenTests/*"
$golden = $LASTEXITCODE
dotnet run --project $tests -c Debug -- --treenode-filter "/*/*/GalleryParityTests/*"
$parity = $LASTEXITCODE
exit ([Math]::Max($golden, $parity))
