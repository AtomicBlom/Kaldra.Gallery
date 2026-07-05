# Publishes Loom.Web and produces a slim zip ready to upload to itch.io as an HTML5 game.
#
# itch.io serves the zip's root and looks for index.html there, so the *contents* of the published
# wwwroot are zipped (not the wwwroot folder itself). The pre-compressed .br/.gz asset variants are
# dropped: itch.io does no content negotiation, so they are dead weight.

param(
	[string]$Configuration = "Release",
	[string]$Output = "$PSScriptRoot\itch"
)

$ErrorActionPreference = "Stop"

$proj = Join-Path $PSScriptRoot "Loom.Web.csproj"
$pubDir = Join-Path $PSScriptRoot "publish"

dotnet publish $proj -c $Configuration -o $pubDir

$wwwroot = Join-Path $pubDir "wwwroot"
if (-not (Test-Path $wwwroot)) { throw "Expected published wwwroot at $wwwroot" }

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
