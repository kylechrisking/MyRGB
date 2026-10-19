# Builds artifacts\MyRGB-Setup-<version>.exe: a per-user installer with the .NET runtime and OpenRGB bundled.
#   1. Publishes MyRGB and the Stream Deck plugin self-contained (win-x64), so no .NET install is needed.
#   2. Downloads the official OpenRGB 1.1 Windows zip and its source (pinned SHA-256), unmodified.
#   3. Compiles tools\installer\MyRGB.iss with Inno Setup 7 (ISCC.exe).
# Inno Setup: pass -Iscc, or install it per-user to .tools\InnoSetup (see docs\INSTALL.txt).
param(
    [string]$Iscc,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$cache = Join-Path $artifacts 'cache'
$stage = Join-Path $artifacts 'stage'

$openRgb = @{
    Zip       = 'OpenRGB_1.1_Windows_64_8e16de2.zip'
    ZipUrl    = 'https://codeberg.org/OpenRGB/OpenRGB/releases/download/release_1.1/OpenRGB_1.1_Windows_64_8e16de2.zip'
    ZipSha    = '63291726C6FC84EB479EF2EE752E97E1187F6CE9C49FD27EEE40DED588A8172A'
    Source    = 'OpenRGB-release_1.1.tar.gz'
    SourceUrl = 'https://codeberg.org/OpenRGB/OpenRGB/archive/release_1.1.tar.gz'
    SourceSha = '8B919AD446DE7F0415E2271A1881F94CD4F14E384A3C59ACFCB3F540125F0107'
}

function Get-Pinned([string]$url, [string]$file, [string]$sha) {
    $path = Join-Path $cache $file
    if (-not (Test-Path $path) -or (Get-FileHash $path -Algorithm SHA256).Hash -ne $sha) {
        Write-Host "Downloading $url" # not Write-Output: that would join the return value
        Invoke-WebRequest $url -OutFile $path -UseBasicParsing
        $actual = (Get-FileHash $path -Algorithm SHA256).Hash
        if ($actual -ne $sha) { Remove-Item $path; throw "$file hash mismatch: $actual" }
    }
    $path
}

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
if (-not $Iscc) {
    $Iscc = @(
        (Join-Path $root '.tools\InnoSetup\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles} 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Iscc) { throw 'Inno Setup (ISCC.exe) not found; pass -Iscc.' }
}

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
Write-Output "MyRGB $version"
New-Item -ItemType Directory -Force $cache | Out-Null

$appOut = Join-Path $stage 'app'
$deckOut = Join-Path $stage 'streamdeck\com.myrgb.streamdeck.sdPlugin'
if (-not $SkipPublish) {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

    Write-Output 'Publishing MyRGB (self-contained)...'
    & $dotnet publish (Join-Path $root 'src\MyRGB.App') -c Release -r win-x64 --self-contained true `
        -p:DebugType=none -p:SatelliteResourceLanguages=en -o $appOut -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'MyRGB publish failed.' }
    # DebugType=none covers our assemblies, not the ~100 MB of native symbols SkiaSharp/HarfBuzz ship.
    Get-ChildItem $appOut -Recurse -Filter *.pdb | Remove-Item

    Write-Output 'Publishing Stream Deck plugin (self-contained, single file)...'
    Copy-Item (Join-Path $root 'streamdeck\com.myrgb.streamdeck.sdPlugin') $deckOut -Recurse -Exclude bin
    Remove-Item (Join-Path $deckOut 'bin') -Recurse -Force -ErrorAction SilentlyContinue
    & $dotnet publish (Join-Path $root 'src\MyRGB.StreamDeck') -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
        -o (Join-Path $deckOut 'bin') -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Plugin publish failed.' }
}

# OpenRGB is redistributed exactly as released (GPLv2): its own files, plus its LICENSE from the matching source.
Write-Output 'Staging OpenRGB 1.1...'
$zip = Get-Pinned $openRgb.ZipUrl $openRgb.Zip $openRgb.ZipSha
$src = Get-Pinned $openRgb.SourceUrl $openRgb.Source $openRgb.SourceSha
$orgbOut = Join-Path $appOut 'OpenRGB'
$unzip = Join-Path $cache 'openrgb-unzip'
if (Test-Path $orgbOut) { Remove-Item $orgbOut -Recurse -Force }
if (Test-Path $unzip) { Remove-Item $unzip -Recurse -Force }
Expand-Archive $zip $unzip
Move-Item (Join-Path $unzip 'OpenRGB Windows 64-bit') $orgbOut
Remove-Item $unzip -Recurse -Force
& "$env:SystemRoot\System32\tar.exe" -xzf $src -C $cache 'openrgb/LICENSE'
if ($LASTEXITCODE -ne 0) { throw 'Could not extract OpenRGB LICENSE.' }
Move-Item (Join-Path $cache 'openrgb\LICENSE') (Join-Path $orgbOut 'LICENSE.txt') -Force
Remove-Item (Join-Path $cache 'openrgb') -Recurse -Force
# Kept next to the installer so the written source offer in the notices can be honoured.
New-Item -ItemType Directory -Force (Join-Path $artifacts 'third-party-source') | Out-Null
Copy-Item $src (Join-Path $artifacts 'third-party-source') -Force

$setupOut = Join-Path $appOut 'setup'
New-Item -ItemType Directory -Force $setupOut | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'setup-openrgb-admin.ps1') $setupOut
Copy-Item (Join-Path $PSScriptRoot 'installer\THIRD-PARTY-NOTICES.txt') $appOut
Copy-Item (Join-Path $root 'docs\INSTALL.txt') $appOut

Write-Output 'Compiling installer...'
& $Iscc /Q "/DAppVersion=$version" "/DStageDir=$stage" "/O$artifacts" (Join-Path $PSScriptRoot 'installer\MyRGB.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed.' }
$setup = Join-Path $artifacts "MyRGB-Setup-$version.exe"
Write-Output ("Built {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
