; MyRGB installer (Inno Setup 7). Build with tools\build-installer.ps1, which stages the files first.
; Installs per-user to %LOCALAPPDATA%\Programs\MyRGB with no admin rights. The one exception is the
; optional RAM / SMBus task, which runs tools\setup-openrgb-admin.ps1 elevated: one UAC prompt.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef StageDir
  #define StageDir "..\..\artifacts\stage"
#endif

#define PluginDir "{userappdata}\Elgato\StreamDeck\Plugins\com.myrgb.streamdeck.sdPlugin"

[Setup]
AppId={{8C1E3B52-5B0F-4E8A-9C61-2F7B9D4A6E10}
AppName=MyRGB
AppVersion={#AppVersion}
AppVerName=MyRGB {#AppVersion}
AppPublisher=Root Labs
AppPublisherURL=https://www.rootlabs.us
AppCopyright=Copyright (C) 2026 Root Labs
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\MyRGB
DefaultGroupName=MyRGB
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename=MyRGB-Setup-{#AppVersion}
SetupIconFile=..\..\src\MyRGB.App\Assets\icon.ico
UninstallDisplayIcon={app}\MyRGB.exe
UninstallDisplayName=MyRGB
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: startup; Description: "Start MyRGB with Windows (in the tray)"
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked
Name: streamdeck; Description: "Install the Stream Deck plugin"; Check: StreamDeckInstalled
Name: smbus; Description: "Enable RAM / SMBus lighting (installs the PawnIO driver; asks for administrator permission)"; Flags: unchecked

[InstallDelete]
; Files from an older OpenRGB must not linger next to the new one.
Type: filesandordirs; Name: "{app}\OpenRGB"
Type: filesandordirs; Name: "{#PluginDir}"; Tasks: streamdeck

[Files]
Source: "{#StageDir}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\streamdeck\com.myrgb.streamdeck.sdPlugin\*"; DestDir: "{#PluginDir}"; Tasks: streamdeck; Flags: ignoreversion recursesubdirs createallsubdirs

[INI]
; Read back by the uninstaller, which can't see which tasks were chosen.
Filename: "{app}\install.ini"; Section: "Tasks"; Key: "StreamDeck"; String: "1"; Tasks: streamdeck

[Registry]
; Same value the app's own "Start with Windows" toggle writes; the uninstaller removes it if it points here.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MyRGB"; ValueData: """{app}\MyRGB.exe"" --minimized"; Tasks: startup

[Icons]
Name: "{autoprograms}\MyRGB"; Filename: "{app}\MyRGB.exe"
Name: "{autodesktop}\MyRGB"; Filename: "{app}\MyRGB.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MyRGB.exe"; Description: "Launch MyRGB"; Flags: nowait postinstall skipifsilent
Filename: "powershell.exe"; Parameters: "-NoProfile -NonInteractive -WindowStyle Hidden -Command ""Get-Process StreamDeck -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 2; Start-Process '{commonpf64}\Elgato\StreamDeck\StreamDeck.exe' -ArgumentList '--runinbk'"""; Description: "Restart Stream Deck to load the MyRGB plugin"; Tasks: streamdeck; Check: StreamDeckExeExists; Flags: postinstall skipifsilent unchecked runhidden nowait

[UninstallDelete]
Type: files; Name: "{app}\install.ini"
Type: files; Name: "{app}\setup.log"
Type: dirifempty; Name: "{app}"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  TaskName = '\MyRGB\OpenRGB';

function StreamDeckExeExists: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf64}\Elgato\StreamDeck\StreamDeck.exe'));
end;

function StreamDeckInstalled: Boolean;
begin
  Result := StreamDeckExeExists or DirExists(ExpandConstant('{userappdata}\Elgato\StreamDeck'));
end;

{ A PowerShell single-quoted string literal. }
function PsQuote(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '''', '''''', True);
  Result := '''' + Result + '''';
end;

function RunHidden(const FileName, Params: String): Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
end;

{ Stops MyRGB, its plugin and OpenRGB, but only copies running from Folder: never someone's other build. }
procedure StopProcessesIn(const Folder: String);
begin
  RunHidden('powershell.exe', '-NoProfile -NonInteractive -Command "Get-Process MyRGB,MyRGB.StreamDeck,OpenRGB -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -and $_.Path.StartsWith(' + PsQuote(AddBackslash(Folder)) + ', ''OrdinalIgnoreCase'') } | Stop-Process -Force"');
end;

function TaskExists: Boolean;
begin
  Result := RunHidden(ExpandConstant('{sys}\schtasks.exe'), '/query /tn "' + TaskName + '"') = 0;
end;

{ True when the elevated part is already done for this OpenRGB build, so an upgrade needs no UAC prompt. }
function SmbusUpToDate: Boolean;
var
  Bundled, Machine: String;
begin
  Bundled := ExpandConstant('{app}\OpenRGB\OpenRGB.exe');
  Machine := ExpandConstant('{commonpf64}\MyRGB\OpenRGB\OpenRGB.exe');
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\PawnIO') and TaskExists
    and FileExists(Machine) and (GetSHA256OfFile(Bundled) = GetSHA256OfFile(Machine));
end;

{ Runs setup-openrgb-admin.ps1 elevated and hidden. False if UAC was declined. }
function RunAdminScript(const Params: String; var ExitCode: Integer): Boolean;
begin
  Result := ShellExec('runas', 'powershell.exe',
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' +
    ExpandConstant('{app}\setup\setup-openrgb-admin.ps1') + '" ' + Params,
    '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  StopProcessesIn(ExpandConstant('{app}'));
  if WizardIsTaskSelected('streamdeck') then
    StopProcessesIn(ExpandConstant('{#PluginDir}'));
end;

procedure EnableSmbus;
var
  ExitCode: Integer;
  User: String;
begin
  if SmbusUpToDate then begin
    Log('RAM / SMBus lighting already set up.');
    SetIniString('Tasks', 'Smbus', '1', ExpandConstant('{app}\install.ini'));
    Exit;
  end;
  WizardForm.StatusLabel.Caption := 'Enabling RAM / SMBus lighting (installing PawnIO)...';
  User := GetEnv('USERDOMAIN') + '\' + GetUserNameString;
  if not RunAdminScript('-InstallPawnIO -OpenRgbPath "' + ExpandConstant('{app}\OpenRGB\OpenRGB.exe') +
      '" -CopyTo "' + ExpandConstant('{commonpf64}\MyRGB\OpenRGB') + '" -User "' + User +
      '" -LogPath "' + ExpandConstant('{app}\setup.log') + '"', ExitCode) then
    SuppressibleMsgBox('RAM / SMBus lighting was not enabled because administrator permission was declined. ' +
      'Run setup again and tick the option to enable it later.', mbInformation, MB_OK, IDOK)
  else if ExitCode <> 0 then
    SuppressibleMsgBox('RAM / SMBus lighting could not be enabled (exit code ' + IntToStr(ExitCode) + '). ' +
      'Details are in ' + ExpandConstant('{app}\setup.log') + '.', mbError, MB_OK, IDOK)
  else begin
    Log('RAM / SMBus lighting enabled.');
    SetIniString('Tasks', 'Smbus', '1', ExpandConstant('{app}\install.ini'));
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('smbus') then
    EnableSmbus;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ExitCode: Integer;
  Value, Ini: String;
begin
  Ini := ExpandConstant('{app}\install.ini');
  if CurUninstallStep = usUninstall then begin
    StopProcessesIn(ExpandConstant('{app}'));
    if GetIniString('Tasks', 'StreamDeck', '', Ini) = '1' then
      StopProcessesIn(ExpandConstant('{#PluginDir}'));
    { Undo the elevated part (one UAC prompt), only if this install set it up: a task registered by hand
      (tools\setup-openrgb-admin.ps1) is left alone. PawnIO stays: other apps share it, and it has its own uninstaller. }
    if (GetIniString('Tasks', 'Smbus', '', Ini) = '1') and (TaskExists or DirExists(ExpandConstant('{commonpf64}\MyRGB'))) then
      if not RunAdminScript('-Remove', ExitCode) then
        SuppressibleMsgBox('The MyRGB scheduled task and ' + ExpandConstant('{commonpf64}\MyRGB') +
          ' were left in place because administrator permission was declined.', mbInformation, MB_OK, IDOK);
    if RegQueryStringValue(HKCU, RunKey, 'MyRGB', Value) and
       (Pos(Lowercase(AddBackslash(ExpandConstant('{app}'))), Lowercase(Value)) > 0) then
      RegDeleteValue(HKCU, RunKey, 'MyRGB');
  end;
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    if MsgBox('Also delete your MyRGB settings, presets and logs?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{userappdata}\MyRGB'), True, True, True);
end;
