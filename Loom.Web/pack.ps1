# Publishes Loom.Web and produces a slim zip ready to upload to itch.io as an HTML5 game.
#
# itch.io serves the zip's root and looks for index.html there, so the *contents* of the published
# wwwroot are zipped (not the wwwroot folder itself). The pre-compressed .br/.gz asset variants are
# dropped: itch.io does no content negotiation, so they are dead weight.
#
# A Release publish is the shipping build: the csproj turns on WASM AOT + IL trimming + invariant
# globalization for it, so the engine's per-frame hot paths run as native WebAssembly. This script
# asserts the AOT native module is present so a misconfigured build can't silently ship as the much
# slower interpreter-only variant.

param(
	[string]$Configuration = "Release",
	[string]$Output = "$PSScriptRoot\itch"
)

$ErrorActionPreference = "Stop"

$proj = Join-Path $PSScriptRoot "Loom.Web.csproj"
$pubDir = Join-Path $PSScriptRoot "publish"

# Publish into a clean directory: a stale wwwroot from a prior run leaves orphaned static-web-asset
# manifests that make the next publish fail trying to copy files that no longer exist.
Remove-Item $pubDir -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish $proj -c $Configuration -o $pubDir

$wwwroot = Join-Path $pubDir "wwwroot"
if (-not (Test-Path $wwwroot)) { throw "Expected published wwwroot at $wwwroot" }

if ($Configuration -eq "Release" -and -not (Get-ChildItem (Join-Path $wwwroot "_framework") -Filter "dotnet.native.*.wasm" -ErrorAction SilentlyContinue)) {
	throw "No AOT native module (dotnet.native.*.wasm) in the published _framework — the Release build did not AOT-compile. Check that the wasm-tools workload is installed."
}

$staging = Join-Path $Output "staging"
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Copy-Item -Path (Join-Path $wwwroot '*') -Destination $staging -Recurse
Get-ChildItem $staging -Recurse -Include *.gz, *.br | Remove-Item -Force

$zip = Join-Path $Output "loom-web-itch.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
Remove-Item $staging -Recurse -Force

$mb = [math]::Round((Get-Item $zip).Length / 1MB, 2)
Write-Host "Packaged $zip ($mb MB) — upload to itch.io as an HTML5 project (tick 'This file will be played in the browser')."
