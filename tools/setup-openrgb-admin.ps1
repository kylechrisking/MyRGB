# Registers the scheduled task MyRGB uses to start OpenRGB with admin rights, hidden.
# RGB RAM and other SMBus devices are only reachable through the PawnIO driver, which OpenRGB can
# open only when elevated. A task set to "run with highest privileges" lets MyRGB start it on demand
# (schtasks /run) without a UAC prompt every time. Run this once, elevated; -Remove undoes it.
#
# The installer runs this (hidden, elevated) with -InstallPawnIO -CopyTo -User -LogPath: the task must
# never run an exe from a user-writable folder, so OpenRGB is copied to Program Files first.
param(
    [string]$OpenRgbPath = (Join-Path $env:LOCALAPPDATA 'Programs\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'),
    [int]$Port = 6742,
    # Copy OpenRGB's folder here (admin-only) and point the task at the copy.
    [string]$CopyTo,
    # Download the official PawnIO installer, check its hash and signature, and install it silently.
    [switch]$InstallPawnIO,
    # Copy the sensor helper from this app folder (its helper\ subfolder) to Program Files,
    # and register a log-on task that runs it elevated: CPU temperatures and motherboard fans
    # for MyRGB, without MyRGB itself running as administrator.
    [string]$InstallSensors,
    # Undo the sensor helper only (the OpenRGB task and its files stay).
    [switch]$RemoveSensors,
    # Account the task runs as; the installer passes the user who started it (UAC may elevate as another admin).
    [string]$User = [Security.Principal.WindowsIdentity]::GetCurrent().Name,
    [string]$LogPath,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$taskPath = '\MyRGB\'
$taskName = 'OpenRGB'
# The 64-bit Program Files even when the installer starts 32-bit PowerShell (where ProgramFiles is the x86 one).
$programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$machineDir = Join-Path $programFiles 'MyRGB'
# Where an earlier build of this script put its files by mistake; cleaned up below.
$strayDir = if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'MyRGB' }
# Everything the elevated task touches lives in Program Files, which only administrators can change:
# the launcher, and OpenRGB's config folder. OpenRGB loads plugin DLLs from its config folder, so with
# the default one (the user's AppData) any program could get its code run as administrator through the task.
$launcher = Join-Path $machineDir 'openrgb-hidden.vbs'
$configDir = Join-Path $machineDir 'config'
# The sensor helper lives beside it, equally admin-only: a task must never run an exe a user could swap.
$sensorsDir = Join-Path $machineDir 'sensors'
# Where older versions put the launcher. Ordinary users can create files in ProgramData, so it is retired.
$oldLauncher = Join-Path $env:ProgramData 'MyRGB\openrgb-hidden.vbs'

# Pinned PawnIO release. Its installer may not be redistributed, so it is fetched from the official release.
$pawnIoUrl = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe'
$pawnIoSha256 = '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'
$pawnIoSigner = 'F380DCC9F706E2756A5047B832FFE719E1BC35F5' # namazso.eu code-signing certificate

function Log([string]$message) {
    Write-Output $message
    if ($LogPath) { Add-Content -Path $LogPath -Value "$(Get-Date -Format s) $message" -Encoding UTF8 }
}

function Install-PawnIO {
    if (Get-Service PawnIO -ErrorAction SilentlyContinue) { Log 'PawnIO already installed.'; return }
    # An admin-only folder, so nothing unelevated can swap the file between the check and the run.
    $dir = Join-Path $machineDir 'download'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $exe = Join-Path $dir 'PawnIO_setup.exe'
    Log "Downloading $pawnIoUrl"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest $pawnIoUrl -OutFile $exe -UseBasicParsing
    try {
        $hash = (Get-FileHash $exe -Algorithm SHA256).Hash
        if ($hash -ne $pawnIoSha256) { throw "PawnIO installer hash mismatch ($hash)." }
        $sig = Get-AuthenticodeSignature $exe
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Thumbprint -ne $pawnIoSigner) {
            throw "PawnIO installer signature not valid ($($sig.Status), $($sig.SignerCertificate.Subject))."
        }
        Log 'Installing PawnIO 2.2.0'
        $p = Start-Process $exe -ArgumentList '-install', '-silent' -Wait -PassThru -WindowStyle Hidden
        if ($p.ExitCode -eq 3010) { Log 'PawnIO installed; Windows needs a restart before RAM lighting works.' }
        elseif ($p.ExitCode -ne 0) { throw "PawnIO setup failed with exit code $($p.ExitCode)." }
        else { Log 'PawnIO installed.' }
    }
    finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
}

try {
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $admin) { throw 'Run this script as administrator.' }

    if ($Remove) {
        Unregister-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskPath $taskPath -TaskName 'Sensors' -Confirm:$false -ErrorAction SilentlyContinue
        # The helper holds its copied files open.
        Get-Process MyRGB.Sensors.Helper -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$machineDir\*" } | Stop-Process -Force
        Remove-Item $oldLauncher -ErrorAction SilentlyContinue
        Remove-Item (Split-Path $oldLauncher) -ErrorAction SilentlyContinue # only if empty
        # The elevated OpenRGB the task started holds the copied files open.
        Get-Process OpenRGB -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$machineDir\*" } | Stop-Process -Force
        Start-Sleep -Milliseconds 500
        Remove-Item $machineDir -Recurse -Force -ErrorAction SilentlyContinue
        if ($strayDir) { Remove-Item $strayDir -Recurse -Force -ErrorAction SilentlyContinue }
        Log 'Removed.'
        return
    }

    if ($RemoveSensors) {
        Unregister-ScheduledTask -TaskPath $taskPath -TaskName 'Sensors' -Confirm:$false -ErrorAction SilentlyContinue
        Get-Process MyRGB.Sensors.Helper -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$sensorsDir\*" } | Stop-Process -Force
        Start-Sleep -Milliseconds 500
        Remove-Item $sensorsDir -Recurse -Force -ErrorAction SilentlyContinue
        Log 'Sensor helper removed.'
        return
    }

    if ($InstallSensors) {
        # Release installs stage the helper into <app>\helper; a dev build may point at the folder directly.
        $helperSrc = Join-Path $InstallSensors 'helper'
        if (-not (Test-Path (Join-Path $helperSrc 'MyRGB.Sensors.Helper.exe'))) { $helperSrc = $InstallSensors }
        if (-not (Test-Path (Join-Path $helperSrc 'MyRGB.Sensors.Helper.exe'))) {
            throw "Sensor helper not found under $InstallSensors"
        }

        Get-Process MyRGB.Sensors.Helper -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$sensorsDir\*" } | Stop-Process -Force
        Start-Sleep -Milliseconds 300
        New-Item -ItemType Directory -Force $sensorsDir | Out-Null
        Copy-Item (Join-Path $helperSrc '*') $sensorsDir -Recurse -Force
        Log "Copied the sensor helper to $sensorsDir"

        # The pipe accepts the desktop user MyRGB runs as (see --allow): UAC may have elevated a different admin.
        $action = New-ScheduledTaskAction -Execute (Join-Path $sensorsDir 'MyRGB.Sensors.Helper.exe') -Argument "--allow `"$User`""
        $principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Highest
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $User
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskPath $taskPath -TaskName 'Sensors' -Action $action -Principal $principal -Trigger $trigger `
            -Settings $settings -Description 'Reads temperatures and fans for MyRGB, so MyRGB itself needs no administrator rights.' -Force | Out-Null
        Start-ScheduledTask -TaskPath $taskPath -TaskName 'Sensors'
        # The task start is asynchronous; wait a little and say honestly whether it came up.
        $up = $false
        foreach ($i in 1..20) {
            Start-Sleep -Milliseconds 500
            if (Get-Process MyRGB.Sensors.Helper -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$sensorsDir\*" }) { $up = $true; break }
        }
        if ($up) { Log "Registered and started ${taskPath}Sensors for $User." }
        else { Log "Registered ${taskPath}Sensors for $User, but the helper has not come up yet; it may still be starting." }
    }

    if (-not (Test-Path $OpenRgbPath)) { throw "OpenRGB not found at $OpenRgbPath" }
    $sourceExe = (Resolve-Path $OpenRgbPath).Path
    if ($InstallPawnIO) { Install-PawnIO }
    if (-not (Get-Service PawnIO -ErrorAction SilentlyContinue)) {
        Write-Warning 'PawnIO is not installed; OpenRGB will run elevated but still cannot reach SMBus devices.'
    }

    if ($CopyTo) {
        Get-Process OpenRGB -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$CopyTo\*" } | Stop-Process -Force
        if (Test-Path $CopyTo) { Remove-Item $CopyTo -Recurse -Force }
        New-Item -ItemType Directory -Force $CopyTo | Out-Null
        Copy-Item (Join-Path (Split-Path $OpenRgbPath) '*') $CopyTo -Recurse -Force
        $OpenRgbPath = Join-Path $CopyTo (Split-Path $OpenRgbPath -Leaf)
        Log "Copied OpenRGB to $CopyTo"
    }

    # OpenRGB's settings (device sizes, profiles) carry over from the user's own config, but never its plugins.
    New-Item -ItemType Directory -Force $configDir | Out-Null
    $userConfig = Join-Path $env:APPDATA 'OpenRGB'
    if ((Test-Path $userConfig) -and -not (Get-ChildItem $configDir -File)) {
        Get-ChildItem $userConfig -File | Where-Object { $_.Extension -in '.json', '.ors' } | Copy-Item -Destination $configDir
        Log "Copied OpenRGB settings from $userConfig"
    }

    # wscript runs the launcher without a console window; window style 0 keeps OpenRGB hidden too.
    $vbs = @"
Set sh = CreateObject("WScript.Shell")
sh.CurrentDirectory = "$(Split-Path $OpenRgbPath)"
sh.Run """$OpenRgbPath"" --server --server-host 127.0.0.1 --server-port $Port --config ""$configDir""", 0, False
"@
    Set-Content -Path $launcher -Value $vbs -Encoding ASCII
    Remove-Item $oldLauncher -ErrorAction SilentlyContinue
    Remove-Item (Split-Path $oldLauncher) -ErrorAction SilentlyContinue # only if empty

    # A copy of this script beside the task, so removing it later runs a file only administrators can change.
    if ($PSCommandPath -and $PSCommandPath -ne (Join-Path $machineDir 'setup-openrgb-admin.ps1')) {
        Copy-Item $PSCommandPath (Join-Path $machineDir 'setup-openrgb-admin.ps1') -Force
    }

    $action = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument "`"$launcher`""
    $principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Action $action -Principal $principal `
        -Settings $settings -Description 'Starts OpenRGB elevated and hidden for MyRGB (SMBus devices such as RGB RAM).' -Force | Out-Null
    if ($strayDir -and $strayDir -ne $machineDir) { Remove-Item $strayDir -Recurse -Force -ErrorAction SilentlyContinue }

    # An unelevated OpenRGB that MyRGB started earlier can't see SMBus; stop it so MyRGB relaunches through the task.
    Get-Process OpenRGB -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $sourceExe } | Stop-Process -Force
    Log "Registered $taskPath$taskName for $User."
}
catch {
    Log "ERROR: $($_.Exception.Message)"
    exit 1
}
