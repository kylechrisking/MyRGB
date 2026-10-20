# Rebuilds src\MyRGB.App\Assets\supported-devices.tsv: every device OpenRGB 1.1 knows how to detect,
# read from the detector registrations in its source (the release tarball build-installer.ps1 pins).
# Each line: name <tab> connection. MyRGB drives all of these through the bundled OpenRGB.
param([string]$Root = (Split-Path $PSScriptRoot -Parent))

$ErrorActionPreference = 'Stop'
$tarball = Join-Path $Root 'artifacts\cache\OpenRGB-release_1.1.tar.gz'
$sha = '8B919AD446DE7F0415E2271A1881F94CD4F14E384A3C59ACFCB3F540125F0107'
if (-not (Test-Path $tarball)) { throw "Run tools\build-installer.ps1 once first; it downloads $tarball." }
if ((Get-FileHash $tarball -Algorithm SHA256).Hash -ne $sha) { throw 'OpenRGB source hash mismatch.' }

$work = Join-Path ([IO.Path]::GetTempPath()) 'myrgb-openrgb-src'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory $work | Out-Null
& "$env:SystemRoot\System32\tar.exe" -xzf $tarball -C $work 'openrgb/Controllers'
if ($LASTEXITCODE -ne 0) { throw 'Could not extract the OpenRGB source.' }

# REGISTER_HID_DETECTOR_IPU("Corsair K70 RGB", ...), REGISTER_I2C_DETECTOR("ASUS Aura DRAM", ...), ...
$pattern = 'REGISTER_([A-Z0-9_]+?)_?DETECTOR[A-Z0-9_]*\s*\(\s*"([^"]+)"'
$found = @{}
Get-ChildItem (Join-Path $work 'openrgb\Controllers') -Recurse -Include *.cpp | ForEach-Object {
    foreach ($m in [regex]::Matches((Get-Content $_.FullName -Raw), $pattern)) {
        $bus = switch -Regex ($m.Groups[1].Value) {
            'PCI' { 'Graphics card'; break }
            'HID|USB|LIBUSB' { 'USB'; break }
            'I2C|SMBUS' { 'RAM, motherboard or GPU (SMBus)'; break }
            default { 'Other' }
        }
        $name = $m.Groups[2].Value.Trim()
        # Skip internal names ("AcerNitroHidKeyboardController"); real product names have a space.
        if ($name -match ' ' -and -not $found.ContainsKey($name)) { $found[$name] = $bus }
    }
}
Remove-Item $work -Recurse -Force

$out = Join-Path $Root 'src\MyRGB.App\Assets\supported-devices.tsv'
$lines = $found.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)`t$($_.Value)" }
[IO.File]::WriteAllLines($out, [string[]]$lines, (New-Object Text.UTF8Encoding $false))
Write-Output "$($lines.Count) devices written to $out"
