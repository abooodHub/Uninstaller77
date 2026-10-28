#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SetupOutputDir
  #define SetupOutputDir "..\dist"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\dist\app"
#endif
#ifndef OutputName
  #define OutputName "Uninstaller77-Setup"
#endif
#ifndef ExpectedExeSha
  #error Build this installer using installer\Build-Release.ps1 so payload hashes are verified.
#endif
#ifndef PayloadInstallList
  #error Build this installer using installer\Build-Release.ps1 to generate its file inventory.
#endif

[Setup]
AppId={{B1477720-8D55-4E92-8C77-771177100001}
AppName=Uninstaller 77
AppVersion={#AppVersion}
AppVerName=Uninstaller 77 {#AppVersion}
AppPublisher=Uninstaller 77
VersionInfoDescription=Uninstaller 77 Setup
VersionInfoProductName=Uninstaller 77
VersionInfoVersion={#AppVersion}.0
DefaultDirName={localappdata}\Programs\Uninstaller 77
DefaultGroupName=Uninstaller 77
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
UsePreviousTasks=no
ShowLanguageDialog=no
LanguageDetectionMethod=none
UsePreviousLanguage=no
WizardStyle=modern dark includetitlebar
WizardSizePercent=110
SetupIconFile=..\app\WinUninstaller\Resources\Brand\Uninstaller77.ico
UninstallDisplayIcon={app}\Uninstaller77.exe
WizardImageFile=Artwork\welcome.bmp
WizardSmallImageFile=Artwork\small.bmp
WizardImageStretch=yes
AppMutex=Local\Uninstaller77.Desktop,Local\Uninstaller77.InUse
SetupMutex=Local\Uninstaller77.Setup
CloseApplications=no
RestartApplications=no
SetupLogging=yes
UninstallLogging=yes
Compression=lzma2
SolidCompression=yes
OutputDir={#SetupOutputDir}
OutputBaseFilename={#OutputName}
UninstallDisplayName=Uninstaller 77
Uninstallable=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.StepWelcome=Welcome
english.StepLocation=Location
english.StepOptions=Shortcuts
english.StepReady=Ready
english.StepInstall=Installing
english.StepDone=Completed
english.RuntimesMissing=Uninstaller 77 requires the .NET 8 Desktop Runtime (x64) and Microsoft Edge WebView2 Runtime. Install the missing prerequisites and run Setup again. No application files were installed.
english.DataPolicy=Your settings, logs, history, icon cache and quarantine are preserved during upgrades and removal. They remain in your Windows profile under LocalAppData\WinUninstaller. Quarantine is never erased by Setup.
english.DesktopShortcut=Create a desktop shortcut
english.LaunchApp=Launch Uninstaller 77
english.ApplicationInUse=Uninstaller 77 is still running. Close every Uninstaller 77 window, including shortcut uninstall windows, then try again. If an uninstall or cleanup is active, wait for its result first. Setup will not stop it for you. No application files have been replaced.
english.PayloadNotWritable=Setup cannot safely update this file because it is in use or is not writable:%n%n%1%n%nClose Uninstaller 77 and try again. If the problem persists, cancel Setup and check the folder permissions. Do not skip files. No application files have been replaced.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"

[Files]
#include PayloadInstallList

[Icons]
Name: "{userprograms}\Uninstaller 77"; Filename: "{app}\Uninstaller77.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Uninstaller77.exe"
Name: "{userdesktop}\Uninstaller 77"; Filename: "{app}\Uninstaller77.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Uninstaller77.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Uninstaller77.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\legacy-shortcuts.ini"

[Code]
var
  StepText: TNewStaticText;
  StepGauge: TNewProgressBar;
  DataPolicyText: TNewStaticText;
  TimerID, TargetStep: LongWord;
  HeaderOffset, HeaderNameTop, HeaderDescriptionTop: Integer;
  Animate: Boolean;
  RestoreShortcutPaths, RestoreShortcutBackups: TArrayOfString;

function SetTimer(hWnd, nIDEvent, uElapse, lpTimerFunc: LongWord): LongWord;
  external 'SetTimer@user32.dll stdcall';
function KillTimer(hWnd, nIDEvent: LongWord): Boolean;
  external 'KillTimer@user32.dll stdcall';
function SystemParametersInfo(Action, Param: LongWord; var Value: LongWord; Flags: LongWord): Boolean;
  external 'SystemParametersInfoW@user32.dll stdcall';

function HasVersionedFile(Root, Pattern, FileName: String): Boolean;
var Find: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Root) + Pattern, Find) then begin
    try
      repeat
        if FileExists(AddBackslash(Root) + Find.Name + '\' + FileName) then begin
          Result := True;
          Break;
        end;
      until not FindNext(Find);
    finally FindClose(Find); end;
  end;
end;

function InitializeSetup: Boolean;
var DesktopRuntime, WebView: Boolean;
begin
  DesktopRuntime := HasVersionedFile(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App'), '8.*', 'PresentationFramework.dll');
  WebView := HasVersionedFile(ExpandConstant('{commonpf32}\Microsoft\EdgeWebView\Application'), '*', 'msedgewebview2.exe') or
    HasVersionedFile(ExpandConstant('{localappdata}\Microsoft\EdgeWebView\Application'), '*', 'msedgewebview2.exe');
  Log(Format('Prerequisites: DesktopRuntime=%d WebView2=%d', [Ord(DesktopRuntime), Ord(WebView)]));
  Result := DesktopRuntime and WebView;
  if not Result then SuppressibleMsgBox(CustomMessage('RuntimesMissing'), mbError, MB_OK, IDOK);
end;

#include "PayloadPreflight.iss"
#include "LegacyRuntime.iss"

function PrepareToInstall(var NeedsRestart: Boolean): String;
var BlockedFile: String;
begin
  Result := '';
  { AppMutex is checked only at startup; check again immediately before copying. }
  if CheckForMutexes('Local\Uninstaller77.Desktop,Local\Uninstaller77.InUse') then begin
    Log('Update blocked before file replacement: an application instance is active.');
    Result := CustomMessage('ApplicationInUse');
    Exit;
  end;
  { Covers older releases, teardown and nonstandard launches without our mutex. }
  BlockedFile := FirstUnavailablePayloadFile(ExpandConstant('{app}'));
  if BlockedFile = '' then BlockedFile := FirstUnavailableLegacyRuntime(ExpandConstant('{app}'));
  if BlockedFile <> '' then
    Result := FmtMessage(CustomMessage('PayloadNotWritable'), [BlockedFile]);
end;

procedure StepTick(Wnd: HWND; Msg: UINT; Event, Tick: LongWord);
var Delta, Movement: Integer;
begin
  if not Assigned(StepGauge) then Exit;
  Delta := Integer(TargetStep) - StepGauge.Position;
  if Delta <> 0 then begin
    Movement := Delta;
    if Movement < 0 then Movement := -Movement;
    Movement := Movement div 4;
    if Movement < 1 then Movement := 1;
    if Delta < 0 then Movement := -Movement;
    StepGauge.Position := StepGauge.Position + Movement;
  end;
  if HeaderOffset > 0 then begin
    HeaderOffset := HeaderOffset - 1;
    WizardForm.PageNameLabel.Top := HeaderNameTop + HeaderOffset;
    WizardForm.PageDescriptionLabel.Top := HeaderDescriptionTop + HeaderOffset;
  end;
  if (StepGauge.Position = Integer(TargetStep)) and (HeaderOffset = 0) then begin
    KillTimer(0, TimerID);
    TimerID := 0;
  end;
end;

procedure InitializeWizard;
var WindowsAnimations: LongWord; Preferences: AnsiString; CleanPreferences: String;
begin
  WindowsAnimations := 1;
  SystemParametersInfo($1042, 0, WindowsAnimations, 0);
  Animate := WindowsAnimations <> 0;
  HeaderNameTop := WizardForm.PageNameLabel.Top;
  HeaderDescriptionTop := WizardForm.PageDescriptionLabel.Top;
  if LoadStringFromFile(ExpandConstant('{localappdata}\WinUninstaller\settings.json'), Preferences) then begin
    CleanPreferences := String(Preferences);
    StringChangeEx(CleanPreferences, ' ', '', True);
    if Pos('"reduceMotion":true', CleanPreferences) > 0 then Animate := False;
  end;
  StepText := TNewStaticText.Create(WizardForm);
  StepText.Parent := WizardForm;
  StepText.SetBounds(ScaleX(20), WizardForm.ClientHeight - ScaleY(62), WizardForm.ClientWidth - ScaleX(40), ScaleY(18));
  StepText.Anchors := [akLeft, akRight, akBottom];
  StepText.Font.Color := $0081DF00;
  StepGauge := TNewProgressBar.Create(WizardForm);
  StepGauge.Parent := WizardForm;
  StepGauge.SetBounds(ScaleX(20), WizardForm.ClientHeight - ScaleY(44), ScaleX(190), ScaleY(3));
  StepGauge.Anchors := [akLeft, akBottom];
  StepGauge.Min := 0; StepGauge.Max := 600;
  DataPolicyText := TNewStaticText.Create(WizardForm);
  DataPolicyText.Parent := WizardForm.FinishedPage;
  DataPolicyText.Caption := CustomMessage('DataPolicy');
  DataPolicyText.WordWrap := True;
  DataPolicyText.SetBounds(WizardForm.FinishedLabel.Left, WizardForm.FinishedLabel.Top + ScaleY(80), WizardForm.FinishedLabel.Width, ScaleY(72));
end;

procedure CurPageChanged(CurPageID: Integer);
var Step: Integer; Caption: String;
begin
  Step := 1; Caption := CustomMessage('StepWelcome');
  case CurPageID of
    wpSelectDir: begin Step := 2; Caption := CustomMessage('StepLocation'); end;
    wpSelectTasks: begin Step := 3; Caption := CustomMessage('StepOptions'); end;
    wpReady: begin Step := 4; Caption := CustomMessage('StepReady'); end;
    wpInstalling: begin Step := 5; Caption := CustomMessage('StepInstall'); end;
    wpFinished: begin Step := 6; Caption := CustomMessage('StepDone'); end;
  end;
  StepText.Caption := Format('%d / 6  ·  %s', [Step, Caption]);
  TargetStep := Step * 100;
  if TimerID <> 0 then begin
    KillTimer(0, TimerID);
    TimerID := 0;
  end;
  HeaderOffset := 0;
  WizardForm.PageNameLabel.Top := HeaderNameTop;
  WizardForm.PageDescriptionLabel.Top := HeaderDescriptionTop;
  if Animate then begin
    HeaderOffset := ScaleY(6);
    WizardForm.PageNameLabel.Top := HeaderNameTop + HeaderOffset;
    WizardForm.PageDescriptionLabel.Top := HeaderDescriptionTop + HeaderOffset;
    TimerID := SetTimer(0, 0, 16, CreateCallback(@StepTick));
  end;
  if TimerID = 0 then begin
    HeaderOffset := 0;
    StepGauge.Position := TargetStep;
    WizardForm.PageNameLabel.Top := HeaderNameTop;
    WizardForm.PageDescriptionLabel.Top := HeaderDescriptionTop;
  end;
end;

procedure DeinitializeSetup;
begin
  if TimerID <> 0 then KillTimer(0, TimerID);
end;

procedure MigrateOwnedShortcut(Path: String);
var Shell, Link: Variant; OldTarget, Version, Backup, TargetLower, Manifest, Section: String; Count: Integer;
begin
  if not FileExists(Path) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Link := Shell.CreateShortcut(Path);
    OldTarget := Link.TargetPath;
    if CompareText(OldTarget, ExpandConstant('{app}\Uninstaller77.exe')) = 0 then Exit;
    if ((CompareText(ExtractFileName(OldTarget), 'Uninstaller77.exe') <> 0) and
        (CompareText(ExtractFileName(OldTarget), 'WinUninstaller.exe') <> 0)) or
       not GetVersionNumbersString(OldTarget, Version) then Exit;
    if (Pos('1.0.', Version) <> 1) and (Pos('1.1.', Version) <> 1) then Exit;
    TargetLower := Lowercase(OldTarget);
    if (Pos(Lowercase(ExpandConstant('{localappdata}\Programs\Uninstaller 77\')), TargetLower) <> 1) and
       (Pos(Lowercase(ExpandConstant('{localappdata}\Programs\Uninstaller77\')), TargetLower) <> 1) and
       (Pos(Lowercase(ExpandConstant('{userdesktop}\WinUninstaller\')), TargetLower) <> 1) then Exit;
    Backup := ExpandConstant('{localappdata}\WinUninstaller\InstallerBackups\') + GetDateTimeString('yyyymmdd-hhnnss', '-', ':');
    ForceDirectories(Backup);
    if not FileCopy(Path, AddBackslash(Backup) + ExtractFileName(Path), False) then begin
      Log('Shortcut migration skipped because its backup could not be saved: ' + Path);
      Exit;
    end;
    SaveStringToFile(AddBackslash(Backup) + 'targets.txt', Path + #13#10 + OldTarget + #13#10, True);
    Manifest := ExpandConstant('{app}\legacy-shortcuts.ini');
    Count := GetIniInt('Migration', 'Count', 0, 0, 20, Manifest);
    if Count >= 20 then begin Log('Legacy shortcut migration limit reached; shortcut kept unchanged.'); Exit; end;
    Count := Count + 1;
    Section := 'Shortcut' + IntToStr(Count);
    SetIniInt('Migration', 'Count', Count, Manifest);
    SetIniString(Section, 'Path', Path, Manifest);
    SetIniString(Section, 'Backup', AddBackslash(Backup) + ExtractFileName(Path), Manifest);
    Link.TargetPath := ExpandConstant('{app}\Uninstaller77.exe');
    Link.WorkingDirectory := ExpandConstant('{app}');
    Link.IconLocation := ExpandConstant('{app}\Uninstaller77.exe,0');
    Link.Description := 'Uninstaller 77 {#AppVersion}';
    Link.Save;
    Log('Migrated owned legacy shortcut: ' + Path + '; previous target: ' + OldTarget);
  except
    Log('Legacy shortcut migration failed: ' + Path + ': ' + GetExceptionMessage);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Manifest, Path, Backup, Section, Name, Command: String; Count, Index, Saved: Integer; Shell, Link: Variant;
begin
  if CurUninstallStep = usPostUninstall then begin
    for Index := 0 to GetArrayLength(RestoreShortcutPaths) - 1 do begin
      Path := RestoreShortcutPaths[Index]; Backup := RestoreShortcutBackups[Index];
      try
        { Native shortcut deletion has finished. Preserve any newer replacement. }
        if FileExists(Path) then begin
          Shell := CreateOleObject('WScript.Shell'); Link := Shell.CreateShortcut(Path);
          if CompareText(String(Link.TargetPath), ExpandConstant('{app}\Uninstaller77.exe')) <> 0 then Continue;
        end;
        if not FileCopy(Backup, Path, False) then Log('Could not restore legacy shortcut: ' + Path);
      except Log('Could not restore legacy shortcut: ' + GetExceptionMessage); end;
    end;
    Exit;
  end;
  if CurUninstallStep <> usUninstall then Exit;
  { Remove only this installation's Explorer verb, never another copy's registration. }
  if RegQueryStringValue(HKCU, 'Software\Classes\lnkfile\shell\Uninstaller77\command', '', Command) and
     (CompareText(Command, '"' + ExpandConstant('{app}\Uninstaller77.exe') + '" --shell-uninstall "%1"') = 0) then
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\lnkfile\shell\Uninstaller77');
  Manifest := ExpandConstant('{app}\legacy-shortcuts.ini');
  Count := GetIniInt('Migration', 'Count', 0, 0, 20, Manifest);
  if Count > 20 then Count := 20;
  for Index := 1 to Count do begin
    Section := 'Shortcut' + IntToStr(Index);
    Path := GetIniString(Section, 'Path', '', Manifest);
    Backup := GetIniString(Section, 'Backup', '', Manifest);
    Name := ExtractFileName(Path);
    if ((CompareText(ExtractFileDir(Path), ExpandConstant('{userdesktop}')) = 0) or
        (CompareText(ExtractFileDir(Path), ExpandConstant('{userprograms}')) = 0)) and
       ((CompareText(Name, 'Uninstaller77.lnk') = 0) or (CompareText(Name, 'WinUninstaller.lnk') = 0) or (CompareText(Name, 'Uninstaller 77.lnk') = 0)) and
       (Pos(Lowercase(ExpandConstant('{localappdata}\WinUninstaller\InstallerBackups\')), Lowercase(ExpandFileName(Backup))) = 1) and FileExists(Backup) and FileExists(Path) then begin
      try
        Shell := CreateOleObject('WScript.Shell'); Link := Shell.CreateShortcut(Path);
        if CompareText(String(Link.TargetPath), ExpandConstant('{app}\Uninstaller77.exe')) = 0 then begin
          Saved := GetArrayLength(RestoreShortcutPaths);
          SetArrayLength(RestoreShortcutPaths, Saved + 1);
          SetArrayLength(RestoreShortcutBackups, Saved + 1);
          RestoreShortcutPaths[Saved] := Path; RestoreShortcutBackups[Saved] := Backup;
          Log('Queued owned legacy shortcut restore after native shortcut removal: ' + Path);
        end;
      except Log('Could not restore legacy shortcut: ' + GetExceptionMessage); end;
    end;
  end;
end;

procedure MigrateLegacyExplorerVerb;
var Command: String;
begin
  if IsKnownLegacyFile(ExpandConstant('{app}'), 'WinUninstaller.exe') and
     RegQueryStringValue(HKCU, 'Software\Classes\lnkfile\shell\Uninstaller77\command', '', Command) and
     (CompareText(Command, '"' + ExpandConstant('{app}\WinUninstaller.exe') + '" --shell-uninstall "%1"') = 0) then begin
    RegWriteStringValue(HKCU, 'Software\Classes\lnkfile\shell\Uninstaller77\command', '', '"' + ExpandConstant('{app}\Uninstaller77.exe') + '" --shell-uninstall "%1"');
    RegWriteStringValue(HKCU, 'Software\Classes\lnkfile\shell\Uninstaller77', 'Icon', '"' + ExpandConstant('{app}\Uninstaller77.exe') + '",0');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Version: String;
begin
  if CurStep <> ssPostInstall then Exit;
  if not GetVersionNumbersString(ExpandConstant('{app}\Uninstaller77.exe'), Version) or
     (Version <> '{#AppVersion}.0') then
    RaiseException('Installed EXE version verification failed. Expected {#AppVersion}.0; found ' + Version);
  if (CompareText(GetSHA256OfFile(ExpandConstant('{app}\Uninstaller77.exe')), '{#ExpectedExeSha}') <> 0) or
     (CompareText(GetSHA256OfFile(ExpandConstant('{app}\Uninstaller77.dll')), '{#ExpectedDllSha}') <> 0) or
     (CompareText(GetSHA256OfFile(ExpandConstant('{app}\wwwroot\index.html')), '{#ExpectedUiSha}') <> 0) then
    RaiseException('Installed payload verification failed. Setup cannot confirm this installation.');
  Log('VERIFIED Uninstaller 77 {#AppVersion}; path=' + ExpandConstant('{app}'));
  WizardForm.FinishedLabel.Caption := 'Uninstaller 77 {#AppVersion}' + #13#10 + ExpandConstant('{app}') + #13#10#13#10 + WizardForm.FinishedLabel.Caption;
  MigrateOwnedShortcut(ExpandConstant('{userdesktop}\Uninstaller77.lnk'));
  MigrateOwnedShortcut(ExpandConstant('{userdesktop}\Uninstaller 77.lnk'));
  MigrateOwnedShortcut(ExpandConstant('{userdesktop}\WinUninstaller.lnk'));
  MigrateOwnedShortcut(ExpandConstant('{userprograms}\Uninstaller77.lnk'));
  MigrateOwnedShortcut(ExpandConstant('{userprograms}\WinUninstaller.lnk'));
  MigrateLegacyExplorerVerb;
  RetireKnownLegacyRuntime(ExpandConstant('{app}'));
end;
