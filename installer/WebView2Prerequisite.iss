#ifndef MinimumWebView2RuntimeVersion
  #define MinimumWebView2RuntimeVersion "156.0.4314.42"
#endif

const
  WebView2ClientKey = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2SetupName = 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe';
  WebView2MinimumVersion = '{#MinimumWebView2RuntimeVersion}';

function HasWebView2Version(const Value: String): Boolean;
var Version, MinimumVersion: Int64;
begin
  Result := False;
  if not StrToVersion(Trim(Value), Version) then Exit;
  if not StrToVersion(WebView2MinimumVersion, MinimumVersion) then Exit;
  Result := Version >= MinimumVersion;
end;

function RegisteredWebView2Version: String;
var Value: String;
begin
  Result := '';
  { Microsoft's documented machine key is in the 32-bit registry view. }
  if RegQueryStringValue(HKLM32, WebView2ClientKey, 'pv', Value) and HasWebView2Version(Value) then
    Result := Value
  else if RegQueryStringValue(HKCU, WebView2ClientKey, 'pv', Value) and HasWebView2Version(Value) then
    Result := Value;
end;

function PrepareWebView2Runtime(var NeedsRestart: Boolean): String;
var Version, Installer: String; ExitCode: Integer;
begin
  Result := '';
  Version := RegisteredWebView2Version;
  if Version <> '' then begin
    Log('WebView2 Runtime already registered: ' + Version);
    Exit;
  end;

  Installer := ExpandConstant('{tmp}\') + WebView2SetupName;
  try
    if not FileExists(Installer) then ExtractTemporaryFile(WebView2SetupName);
    if CompareText(GetSHA256OfFile(Installer), '{#ExpectedWebView2Sha}') <> 0 then
      RaiseException('The embedded WebView2 installer failed its integrity check.');
  except
    Log('WebView2 preparation failed: ' + GetExceptionMessage);
    Result := FmtMessage(CustomMessage('WebView2PrepareFailed'), [GetExceptionMessage]);
    Exit;
  end;

  Log('Installing bundled WebView2 Evergreen Runtime before copying application files.');
  if not Exec(Installer, '/silent /install', ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then begin
    Log(Format('WebView2 installer launch failed with Windows error %d.', [ExitCode]));
    Result := FmtMessage(CustomMessage('WebView2LaunchFailed'), [IntToStr(ExitCode)]);
    Exit;
  end;
  Log(Format('WebView2 installer returned %d.', [ExitCode]));
  if (ExitCode = 3010) or (ExitCode = 1641) then begin
    NeedsRestart := True;
    Result := CustomMessage('WebView2Restart');
    Exit;
  end;
  if ExitCode <> 0 then begin
    Result := FmtMessage(CustomMessage('WebView2InstallFailed'), [IntToStr(ExitCode)]);
    Exit;
  end;
  Version := RegisteredWebView2Version;
  if Version = '' then begin
    Log('WebView2 installer exited successfully without a Runtime registration at or above ' + WebView2MinimumVersion + '.');
    Result := CustomMessage('WebView2RegistrationMissing');
    Exit;
  end;
  Log('WebView2 Runtime registered after installation: ' + Version);
end;
