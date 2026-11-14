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
#ifndef ExpectedWebView2Sha
  #error Supply the signed WebView2 installer through Build-Release.ps1.
#endif

#include "Theme.iss"

[Setup]
#ifdef U77SignToolName
SignTool={#U77SignToolName}
SignedUninstaller=yes
#endif
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
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.14393
DisableWelcomePage=no
DisableDirPage=no
DisableReadyPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
UsePreviousTasks=no
ShowLanguageDialog=no
UsePreviousLanguage=no
WizardStyle=modern dark includetitlebar
WizardSizePercent=150
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

[Messages]
english.FinishedHeadingLabel=Installation complete
english.FinishedLabel=Uninstaller 77 is ready to use.
english.FinishedLabelNoIcons=Uninstaller 77 is ready to use.
english.ClickFinish=

[CustomMessages]
english.StepWelcome=Welcome
english.StepLocation=Location
english.StepOptions=Shortcuts
english.StepReady=Ready
english.StepInstall=Installing
english.StepDone=Completed
english.WebView2PrepareFailed=Setup could not prepare its bundled Microsoft Edge WebView2 installer.%n%n%1%n%nDownload a fresh copy of Setup and run it again.
english.WebView2LaunchFailed=Windows could not start the bundled Microsoft Edge WebView2 installer. Windows error %1.%n%nKeep the Setup log for support and try again.
english.WebView2InstallFailed=Microsoft Edge WebView2 Runtime setup returned code %1.%n%nRetry Setup or install the official WebView2 Evergreen Runtime, then run Setup again. No application files were replaced.
english.WebView2RegistrationMissing=Microsoft Edge WebView2 setup finished, but Runtime 156.0.4314.42 or newer is not registered.%n%nInstall or repair WebView2 Evergreen Runtime from Microsoft and run Setup again.
english.WebView2Restart=Microsoft Edge WebView2 Runtime requires Windows to restart. Restart Windows, then run Setup again.
english.DataPolicy=Your settings, logs, history, icon cache and quarantine are preserved during upgrades and removal. They remain in your Windows profile under LocalAppData\WinUninstaller. Quarantine is never erased by Setup.
english.DesktopShortcut=Create a desktop shortcut
english.LaunchApp=Launch Uninstaller 77
english.ApplicationInUse=Uninstaller 77 is still running. Close every Uninstaller 77 window, including shortcut uninstall windows, then try again. If an uninstall or cleanup is active, wait for its result first. Setup will not stop it for you. No application files have been replaced.
english.PayloadNotWritable=Setup cannot safely update this file because it is in use or is not writable:%n%n%1%n%nClose Uninstaller 77 and try again. If the problem persists, cancel Setup and check the folder permissions. Do not skip files. No application files have been replaced.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"

[Files]
Source: "Artwork\workflow-welcome.png"; Flags: dontcopy
Source: "Artwork\workflow-done.png"; Flags: dontcopy
Source: "Artwork\workflow-install-en.png"; Flags: dontcopy
Source: "Artwork\workflow-finish-en.png"; Flags: dontcopy
Source: "{#WebView2Installer}"; DestName: "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Flags: dontcopy
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
  InstallButton, FinishButton: TBitmapButton;
  CustomInstallButton: TNewButton;
  CustomInstall: Boolean;
  RestoreShortcutPaths, RestoreShortcutBackups: TArrayOfString;

#include "PayloadPreflight.iss"
#include "LegacyRuntime.iss"
#include "WebView2Prerequisite.iss"

function CheckApplicationFiles: String;
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
  if BlockedFile <> '' then begin
    Result := FmtMessage(CustomMessage('PayloadNotWritable'), [BlockedFile]);
    Exit;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := CheckApplicationFiles;
  if Result <> '' then Exit;
  Result := PrepareWebView2Runtime(NeedsRestart);
  if Result <> '' then Exit;
  { Setup may have waited for the runtime installer while the app was opened. }
  Result := CheckApplicationFiles;
end;

procedure AdvanceWizard(Sender: TObject);
begin
  WizardForm.NextButton.OnClick(WizardForm.NextButton);
end;

procedure StartCustomInstall(Sender: TObject);
begin
  CustomInstall := True;
  AdvanceWizard(Sender);
end;

procedure StartDefaultInstall(Sender: TObject);
begin
  CustomInstall := False;
  AdvanceWizard(Sender);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((PageID = wpSelectDir) or (PageID = wpSelectTasks)) and not CustomInstall;
end;

procedure CenterControl(Control: TControl; ParentWidth, Top, Width, Height: Integer);
begin
  Control.SetBounds((ParentWidth - ScaleX(Width)) div 2, ScaleY(Top), ScaleX(Width), ScaleY(Height));
end;

function SetupArt(Parent: TWinControl; const FileName: String; Top, Size: Integer): TBitmapImage;
var ImagePath: String;
begin
  ImagePath := ExpandConstant('{tmp}\') + FileName;
  if not FileExists(ImagePath) then ExtractTemporaryFile(FileName);
  Result := TBitmapImage.Create(WizardForm);
  Result.Parent := Parent;
  CenterControl(Result, Parent.ClientWidth, Top, Size, Size);
  Result.Stretch := True;
  Result.PngImage.LoadFromFile(ImagePath);
end;

function SetupButton(Parent: TWinControl; const FileName, Caption: String; Top: Integer): TBitmapButton;
begin
  ExtractTemporaryFile(FileName);
  Result := TBitmapButton.Create(WizardForm);
  Result.Parent := Parent;
  CenterControl(Result, Parent.ClientWidth, Top, 220, 50);
  Result.BackColor := {#ThemeCanvas};
  Result.Caption := Caption;
  Result.Cursor := crHand;
  Result.Stretch := True;
  Result.PngImage.LoadFromFile(ExpandConstant('{tmp}\') + FileName);
end;

procedure CenterSetupLabel(LabelControl: TNewStaticText; ParentWidth, Top, Height, FontSize: Integer; Color: TColor);
begin
  LabelControl.AutoSize := False;
  LabelControl.StyleElements := [];
  LabelControl.WordWrap := True;
  LabelControl.Alignment := taCenter;
  LabelControl.Font.Name := 'Segoe UI';
  LabelControl.Font.Size := FontSize;
  LabelControl.Font.Style := [];
  LabelControl.Font.Color := Color;
  LabelControl.SetBounds(ScaleX(20), ScaleY(Top), ParentWidth - ScaleX(40), ScaleY(Height));
end;

#include "InstallProgress.iss"

procedure InitializeWizard;
var WelcomeWidth, FinishedWidth: Integer;
begin
  WizardForm.StyleElements := WizardForm.StyleElements - [seClient];
  WizardForm.InnerPage.StyleElements := [];
  WizardForm.WelcomePage.StyleElements := [];
  WizardForm.FinishedPage.StyleElements := [];
  WizardForm.Color := {#ThemeCanvas};
  WizardForm.InnerPage.Color := {#ThemeCanvas};
  WizardForm.WelcomePage.Color := {#ThemeCanvas};
  WizardForm.FinishedPage.Color := {#ThemeCanvas};
  WizardForm.WizardBitmapImage.Visible := False;
  WizardForm.WizardBitmapImage2.Visible := False;
  WizardForm.Bevel.Visible := False;
  WelcomeWidth := WizardForm.WelcomePage.ClientWidth;
  FinishedWidth := WizardForm.FinishedPage.ClientWidth;
  SetupArt(WizardForm.WelcomePage, 'workflow-welcome.png', 58, 120);
  SetupArt(WizardForm.FinishedPage, 'workflow-done.png', 95, 110);
  CenterSetupLabel(WizardForm.WelcomeLabel1, WelcomeWidth, 208, 44, 19, {#ThemeText});
  WizardForm.WelcomeLabel1.Caption := 'Welcome to Uninstaller 77 {#AppVersion}';
  CenterSetupLabel(WizardForm.WelcomeLabel2, WelcomeWidth, 251, 56, 10, {#ThemeMuted});
  WizardForm.WelcomeLabel2.Caption := 'Choose Install to continue.';
  InstallButton := SetupButton(WizardForm.WelcomePage, 'workflow-install-en.png', 'Install', 318);
  InstallButton.OnClick := @StartDefaultInstall;
  CustomInstallButton := TNewButton.Create(WizardForm);
  CustomInstallButton.Parent := WizardForm.WelcomePage;
  CustomInstallButton.Caption := 'Custom Install';
  CenterControl(CustomInstallButton, WelcomeWidth, 378, 135, 25);
  CustomInstallButton.OnClick := @StartCustomInstall;

  CenterSetupLabel(WizardForm.FinishedHeadingLabel, FinishedWidth, 223, 44, 19, {#ThemeText});
  CenterSetupLabel(WizardForm.FinishedLabel, FinishedWidth, 270, 61, 10, {#ThemeMuted});
  FinishButton := SetupButton(WizardForm.FinishedPage, 'workflow-finish-en.png', 'Finish', 360);
  FinishButton.OnClick := @AdvanceWizard;
  CenterControl(WizardForm.RunList, FinishedWidth, 420, 240, 54);

  InitializeInstallProgress;
end;

procedure CurPageChanged(CurPageID: Integer);
var Installing: Boolean;
begin
  Installing := CurPageID = wpInstalling;
  WizardForm.OuterNotebook.Visible := not Installing;
  ShowInstallProgress(Installing);
  WizardForm.CancelButton.Visible := (CurPageID <> wpWelcome) and (CurPageID <> wpFinished) and not Installing;
  if CurPageID = wpWelcome then begin
    WizardForm.NextButton.Visible := False;
    WizardForm.ActiveControl := InstallButton;
  end else if CurPageID = wpFinished then begin
    WizardForm.NextButton.Visible := False;
    WizardForm.ActiveControl := FinishButton;
  end;
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
  ShowInstallVerification;
  if not GetVersionNumbersString(ExpandConstant('{app}\Uninstaller77.exe'), Version) or
     (Version <> '{#AppVersion}.0') then
    RaiseException('Installed EXE version verification failed. Expected {#AppVersion}.0; found ' + Version);
  if (CompareText(GetSHA256OfFile(ExpandConstant('{app}\Uninstaller77.exe')), '{#ExpectedExeSha}') <> 0) or
     (CompareText(GetSHA256OfFile(ExpandConstant('{app}\Uninstaller77.dll')), '{#ExpectedDllSha}') <> 0) or
     (CompareText(GetSHA256OfFile(ExpandConstant('{app}\wwwroot\index.html')), '{#ExpectedUiSha}') <> 0) then
    RaiseException('Installed payload verification failed. Setup cannot confirm this installation.');
  Log('VERIFIED Uninstaller 77 {#AppVersion}; path=' + ExpandConstant('{app}'));
  MigrateOwnedShortcut(ExpandConstant('{userdesktop}\Uninstaller77.lnk'));
  MigrateOwnedShortcut(ExpandConstant('{userdesktop}\Uninstaller 77.lnk'));
  MigrateOwnedShortcut(ExpandConstant('{userdesktop}\WinUninstaller.lnk'));
  MigrateOwnedShortcut(ExpandConstant('{userprograms}\Uninstaller77.lnk'));
  MigrateOwnedShortcut(ExpandConstant('{userprograms}\WinUninstaller.lnk'));
  MigrateLegacyExplorerVerb;
  RetireKnownLegacyRuntime(ExpandConstant('{app}'));
end;
