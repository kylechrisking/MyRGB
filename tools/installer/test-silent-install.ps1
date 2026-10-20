# Silent install/uninstall test of the MyRGB installer into a temp folder. No UAC: the smbus task is not selected.
param(
    [string]$Setup = (Get-ChildItem (Join-Path $PSScriptRoot '..\..\artifacts') -Filter 'MyRGB-Setup-*.exe' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName),
    [string]$Out = (Join-Path $PSScriptRoot '..\..\artifacts\test'))
$ErrorActionPreference = 'Stop'
# The test installs and uninstalls under the real AppId, so on a PC with MyRGB installed it would
# take over that install's uninstall entry and shortcut, then remove them. Don't run there.
$existing = Get-ChildItem HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall -ErrorAction SilentlyContinue |
    Where-Object { (Get-ItemProperty $_.PSPath).DisplayName -eq 'MyRGB' }
if ($existing) { throw 'MyRGB is installed on this PC; uninstall it first, or run this test on another machine.' }
$stage = Join-Path $PSScriptRoot '..\..\artifacts\stage'
$dir = Join-Path ([IO.Path]::GetTempPath()) 'MyRGB-install-test\MyRGB'
New-Item -ItemType Directory -Force $Out | Out-Null
$report = Join-Path $Out 'silent-install-test.log'
$results = [System.Collections.Generic.List[string]]::new()
function Check([string]$name, [bool]$ok) { $results.Add(("[{0}] {1}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name)) }

if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
$runBefore = (Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run -Name MyRGB -ErrorAction SilentlyContinue).MyRGB
function TaskState { $null = & schtasks.exe /query /tn '\MyRGB\OpenRGB' 2>$null; $LASTEXITCODE -eq 0 }
$taskBefore = TaskState
$pfBefore = Test-Path "$env:ProgramFiles\MyRGB"
$procsBefore =Get-Process MyRGB, MyRGB.StreamDeck, OpenRGB -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id

$results.Add("Installer: $Setup ($([math]::Round((Get-Item $Setup).Length/1MB,1)) MB, SHA-256 $((Get-FileHash $Setup).Hash))")
$results.Add("Target:    $dir")
$results.Add("Started:   $(Get-Date -Format s)")

# --- install ---
$p = Start-Process $Setup -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /MERGETASKS=`"!startup,!streamdeck,!smbus,!desktopicon`" /DIR=`"$dir`" /LOG=`"$Out\inno-install.log`"" -Wait -PassThru -WindowStyle Hidden
Check "install exit code 0 (got $($p.ExitCode))" ($p.ExitCode -eq 0)
foreach ($f in 'MyRGB.exe', 'coreclr.dll', 'hostfxr.dll', 'OpenRGB\OpenRGB.exe', 'OpenRGB\LICENSE.txt', 'OpenRGB\Qt6Core.dll',
               'setup\setup-openrgb-admin.ps1', 'THIRD-PARTY-NOTICES.txt', 'INSTALL.txt', 'unins000.exe') {
    Check "installed $f" (Test-Path (Join-Path $dir $f))
}
Check 'no .pdb files installed' (-not (Get-ChildItem $dir -Recurse -Filter *.pdb))
Check 'OpenRGB.exe identical to official 1.1 zip' ((Get-FileHash "$dir\OpenRGB\OpenRGB.exe").Hash -eq (Get-FileHash "$stage\app\OpenRGB\OpenRGB.exe").Hash)
Check 'no runtimeconfig framework reference (self-contained)' (-not ((Get-Content "$dir\MyRGB.runtimeconfig.json" -Raw) -match '"framework"'))
$uninst = Get-ChildItem HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall | Where-Object { $_.PSChildName -like '*8C1E3B52*' }
Check 'per-user uninstall entry in HKCU' ([bool]$uninst)
Check 'no machine-wide uninstall entry' (-not (Get-ChildItem HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall | Where-Object { $_.PSChildName -like '*8C1E3B52*' }))
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'MyRGB.lnk'
Check "Start menu shortcut ($lnk)" (Test-Path $lnk)
Check 'Run key untouched (startup task off)' ((Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run -Name MyRGB -ErrorAction SilentlyContinue).MyRGB -eq $runBefore)
Check "scheduled task \MyRGB\OpenRGB unchanged (existed before: $taskBefore)" ((TaskState) -eq $taskBefore)
Check 'nothing written to Program Files\MyRGB' ((Test-Path "$env:ProgramFiles\MyRGB") -eq $pfBefore)

# Smoke test: the self-contained exe starts without a .NET install on PATH. Another MyRGB already owns the
# single-instance mutex, so with --minimized it loads the runtime, sees it, and exits 0 without a window.
if (Get-Process MyRGB -ErrorAction SilentlyContinue) {
    $env:DOTNET_ROOT = $null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $s = Start-Process "$dir\MyRGB.exe" -ArgumentList '--minimized' -PassThru -WindowStyle Hidden
    if (-not $s.WaitForExit(20000)) { $s | Stop-Process -Force }
    Check "bundled runtime starts MyRGB.exe (exit $($s.ExitCode), $($sw.ElapsedMilliseconds) ms)" ($s.HasExited -and $s.ExitCode -eq 0)
} else { $results.Add('[SKIP] runtime smoke test: no other MyRGB running to hand off to') }

# Non-admin run of the elevated script must refuse cleanly.
$log = Join-Path $Out 'admin-script-nonadmin.log'
Remove-Item $log -ErrorAction SilentlyContinue
$a = Start-Process powershell.exe -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$dir\setup\setup-openrgb-admin.ps1`" -InstallPawnIO -OpenRgbPath `"$dir\OpenRGB\OpenRGB.exe`" -LogPath `"$log`"" -Wait -PassThru -WindowStyle Hidden
Check "admin script refuses without elevation (exit $($a.ExitCode): $(Get-Content $log -ErrorAction SilentlyContinue))" ($a.ExitCode -eq 1)

# --- uninstall ---
$u = Start-Process "$dir\unins000.exe" -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$Out\inno-uninstall.log`"" -Wait -PassThru -WindowStyle Hidden
# The uninstaller copies itself to %TEMP% and finishes there; wait for the folder to go.
$deadline = (Get-Date).AddSeconds(30)
while ((Test-Path $dir) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
Check "uninstall exit code 0 (got $($u.ExitCode))" ($u.ExitCode -eq 0)
Check 'install folder removed' (-not (Test-Path $dir))
Check 'uninstall entry removed' (-not (Get-ChildItem HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall | Where-Object { $_.PSChildName -like '*8C1E3B52*' }))
Check 'Start menu shortcut removed' (-not (Test-Path $lnk))
Check 'Run key untouched after uninstall' ((Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run -Name MyRGB -ErrorAction SilentlyContinue).MyRGB -eq $runBefore)
$procsAfter = Get-Process MyRGB, MyRGB.StreamDeck, OpenRGB -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id
Check "other MyRGB/OpenRGB processes left running ($($procsBefore -join ','))" (-not (Compare-Object @($procsBefore) @($procsAfter)))
Check 'user settings kept (%APPDATA%\MyRGB)' (Test-Path (Join-Path $env:APPDATA 'MyRGB'))

$results.Add("Finished:  $(Get-Date -Format s)")
$fail = @($results | Where-Object { $_ -like '`[FAIL`]*' }).Count
$results.Add("$($results.Count - 4) checks, $fail failed")
$results | Set-Content $report -Encoding UTF8
$results
