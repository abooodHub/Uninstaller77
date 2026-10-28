param([string]$ReportDirectory = '', [string]$LegacyPayload = '')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if (!$ReportDirectory) { $ReportDirectory = Join-Path $workspace '_build\product-v1\LEGACY' }
if (!$LegacyPayload) { $LegacyPayload = Join-Path $workspace 'dist\current\app' }
$fixture = Join-Path $workspace ('_build\legacy-runtime-fixture-' + [Guid]::NewGuid().ToString('N'))
$prefix = [IO.Path]::GetFullPath((Join-Path $workspace '_build')).TrimEnd('\') + '\'
if (![IO.Path]::GetFullPath($fixture).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture path.' }
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
foreach($name in @('WinUninstaller.exe','WinUninstaller.dll','WinUninstaller.deps.json','WinUninstaller.runtimeconfig.json')) {
    Copy-Item -LiteralPath (Join-Path $LegacyPayload $name) -Destination (Join-Path $fixture $name)
}
foreach($name in @('Uninstaller77.exe','user.sqlite','settings.json','Unrelated.exe')) {
    [IO.File]::WriteAllText((Join-Path $fixture $name), 'Owned unrelated marker: ' + $name)
}
$source = @'
[Setup]
AppName=Owned Legacy Runtime Test
AppVersion=1.0
PrivilegesRequired=lowest
CreateAppDir=no
Uninstallable=no
OutputDir={#TestTarget}
OutputBaseFilename=LegacyRuntimeProbe
[Code]
#include LegacyImplementation
var Passed: Integer;
procedure Check(Condition: Boolean; Name: String);
begin
  if not Condition then RaiseException(Name);
  Passed := Passed + 1;
  Log('PASS ' + Name);
end;
function InitializeSetup: Boolean;
var Target, Original: String; Marker: AnsiString; Files: TStringList; Lock: TFileStream;
begin
  Result := False;
  Target := '{#TestTarget}'; Passed := 0;
  try
    Check(IsKnownLegacyFile(Target, 'WinUninstaller.exe'), 'Real historical EXE identified by exact hash');
    Check(IsKnownLegacyFile(Target, 'WinUninstaller.dll'), 'Real historical assembly identified by exact hash');
    Files := TStringList.Create;
    try AddKnownLegacyFiles(Target, Files); Check(Files.Count = 4, 'Only four exact historical runtime artifacts selected'); finally Files.Free; end;
    Check(FirstUnavailableLegacyRuntime(Target) = '', 'Unrelated user/settings files are outside legacy inventory');
    Original := GetSHA256OfFile(Target + '\WinUninstaller.exe');
    Lock := TFileStream.Create(Target + '\WinUninstaller.dll', fmOpenRead or fmShareDenyWrite);
    try Check(CompareText(FirstUnavailableLegacyRuntime(Target), Target + '\WinUninstaller.dll') = 0, 'Locked old assembly blocks upgrade before replacement'); finally Lock.Free; end;
    Check(GetSHA256OfFile(Target + '\WinUninstaller.exe') = Original, 'Read-only preflight preserved old executable bytes');
    Check(FirstUnavailableLegacyRuntime(Target) = '', 'Upgrade preflight recovers after lock release');
    Check(not IsKnownLegacyFile(Target, 'Unrelated.exe'), 'Unknown same-folder executable is never selected');
    SaveStringToFile(Target + '\WinUninstaller.dll', 'unknown replacement bytes', False);
    Check(not IsKnownLegacyFile(Target, 'WinUninstaller.dll'), 'Same filename with different bytes is not owned');
    RetireKnownLegacyRuntime(Target);
    Check(not FileExists(Target + '\WinUninstaller.exe') and not FileExists(Target + '\WinUninstaller.deps.json') and not FileExists(Target + '\WinUninstaller.runtimeconfig.json'), 'Known renamed artifacts actually retired inside fixture only');
    Check(FileExists(Target + '\WinUninstaller.dll'), 'Unknown replacement assembly preserved');
    LoadStringFromFile(Target + '\settings.json', Marker);
    Check(Marker = 'Owned unrelated marker: settings.json', 'Settings unchanged');
    Check(FileExists(Target + '\user.sqlite') and FileExists(Target + '\Uninstaller77.exe') and FileExists(Target + '\Unrelated.exe'), 'Database, new EXE and unrelated EXE preserved');
    RetireKnownLegacyRuntime(Target);
    Check(FileExists(Target + '\WinUninstaller.dll'), 'Repeated retirement remains safe');
    SaveStringToFile(Target + '\RESULT.txt', Format('%d passed / 0 failed', [Passed]), False);
  except SaveStringToFile(Target + '\RESULT.txt', 'FAIL ' + GetExceptionMessage, False); end;
end;
'@
$script = Join-Path $fixture 'LegacyRuntimeProbe.iss'
$source | Set-Content -LiteralPath $script -Encoding utf8BOM
& (Join-Path $workspace 'tools\InnoSetup\ISCC.exe') /Q "/DTestTarget=$fixture" "/DLegacyImplementation=$(Join-Path $workspace 'installer\LegacyRuntime.iss')" $script *> (Join-Path $ReportDirectory 'COMPILE.log')
if ($LASTEXITCODE) { Get-Content -LiteralPath (Join-Path $ReportDirectory 'COMPILE.log') -Tail 12; throw 'Fixture compiler failed.' }
$runner = Start-Process -FilePath (Join-Path $fixture 'LegacyRuntimeProbe.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/SP-','/NORESTART',('/LOG="' + (Join-Path $fixture 'RUNTIME.log') + '"')) -WindowStyle Hidden -PassThru -Wait
$result = Get-Content -LiteralPath (Join-Path $fixture 'RESULT.txt') -Raw
Copy-Item -LiteralPath (Join-Path $fixture 'RUNTIME.log') -Destination (Join-Path $ReportDirectory 'RUNTIME.log')
if ($result -ne '14 passed / 0 failed') { throw $result }
@{Passed=14;Failed=0;NativeInstallPerformed=$false;Fixture=$fixture;RunnerExitCode=$runner.ExitCode} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportDirectory 'RESULTS.json') -Encoding utf8
Write-Output "14 passed / 0 failed; actual Inno helper runtime, no installation or real program deletion."
if (@(Get-ChildItem -LiteralPath $fixture -Force -Recurse | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count) { throw 'Unsafe fixture cleanup.' }
Remove-Item -LiteralPath $fixture -Recurse -Force
