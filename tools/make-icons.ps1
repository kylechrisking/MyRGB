# Rebuilds every brand bitmap (app icon, .ico, Stream Deck images, logo PNGs) from the SVG sources
# in branding/. The drawing lives in branding/build/MakeBrand.cs; this just runs it.
#   -Install   also copy the icon and fonts into src/MyRGB.App/Assets and the images into the plugin.
param([switch]$Install, [string]$Root = (Split-Path $PSScriptRoot -Parent))

$ErrorActionPreference = 'Stop'
$dotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path (Join-Path $dotnetDir 'dotnet.exe')) { $env:PATH = "$dotnetDir;$env:PATH" }

$runArgs = @('run', (Join-Path $Root 'branding\build\MakeBrand.cs'))
if ($Install) { $runArgs += '--'; $runArgs += '--install' }
Push-Location $Root
try { & dotnet @runArgs; if ($LASTEXITCODE) { throw "MakeBrand failed ($LASTEXITCODE)" } }
finally { Pop-Location }
