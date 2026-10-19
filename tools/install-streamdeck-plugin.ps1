# Builds the MyRGB Stream Deck plugin and installs it for the current user.
# Stream Deck only loads new plugins at startup, so pass -RestartStreamDeck to have it
# restarted for you (it comes back in the tray), or restart it yourself afterwards.
param([switch]$RestartStreamDeck)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root 'streamdeck\com.myrgb.streamdeck.sdPlugin'
$target = Join-Path $env:APPDATA 'Elgato\StreamDeck\Plugins\com.myrgb.streamdeck.sdPlugin'

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

Write-Output 'Publishing plugin...'
& $dotnet publish (Join-Path $root 'src\MyRGB.StreamDeck') -c Release -o (Join-Path $source 'bin') `
    --self-contained false -p:PublishSingleFile=true -p:DebugType=none -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$deck = Get-Process StreamDeck -ErrorAction SilentlyContinue
$deckPath = $deck | Select-Object -First 1 -ExpandProperty Path
if ($RestartStreamDeck -and $deck) {
    Write-Output 'Stopping Stream Deck...'
    $deck | Stop-Process -Force
    Start-Sleep 2
}
# A running plugin exe is locked by Stream Deck; stop it so the copy can replace it.
Get-Process MyRGB.StreamDeck -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $target) { Remove-Item $target -Recurse -Force }
Copy-Item $source $target -Recurse
Write-Output "Installed to $target"

if ($RestartStreamDeck -and $deckPath) {
    # --runinbk starts Stream Deck in the background (tray) without opening its window.
    Start-Process $deckPath -ArgumentList '--runinbk'
    Write-Output 'Stream Deck restarted in the background.'
} elseif (-not $RestartStreamDeck) {
    Write-Output 'Restart Stream Deck to load the plugin (or re-run with -RestartStreamDeck).'
}
