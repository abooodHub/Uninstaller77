param([string]$ReportDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$ReportDirectory) { $ReportDirectory = Join-Path $root 'reports\INSTALLER_PAYLOAD_PREFLIGHT_FIX' }
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
. (Join-Path $root 'installer\PayloadInventory.ps1')
$fixture = Join-Path $root ('_build\installer-preflight-' + [Guid]::NewGuid().ToString('N'))
$prefix = [IO.Path]::GetFullPath((Join-Path $root '_build')).TrimEnd('\') + '\'
if (![IO.Path]::GetFullPath($fixture).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture must be inside workspace.' }
$source = Join-Path $fixture 'source'
$target = Join-Path $fixture 'target'
$generated = Join-Path $fixture 'generated'
foreach ($directory in @($source,$target,$generated)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
$ownedFiles = @('Uninstaller77.exe','Uninstaller77.dll','wwwroot\index.html',"wwwroot\واجهة'تجريبية.js",'settings.json')
$excluded = @('debug.pdb','trace.log','user.db','user.db-shm','user.db-wal','WebView2\profile.dll')
foreach ($relative in @($ownedFiles)+@($excluded)) {
    foreach ($directory in @($source,$target)) {
        $path = Join-Path $directory $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [IO.File]::WriteAllText($path, 'Owned preflight fixture: ' + $relative)
    }
}
[IO.File]::WriteAllText((Join-Path $target 'Unrelated.exe'), 'Not an application payload file')
New-Item -ItemType Directory -Path (Join-Path $target 'collision\Uninstaller77.dll') -Force | Out-Null
$inventory = New-InstallerPayloadInventory $source $generated
if (@(Compare-Object ($inventory.Paths | Sort-Object) ($ownedFiles | Sort-Object)).Count) { throw 'Generated inventory does not match expected installed files.' }
if (@(Get-Content -LiteralPath $inventory.InstallInclude).Count -ne $ownedFiles.Count) { throw '[Files] inventory count mismatch.' }
if (@(Get-Content -LiteralPath $inventory.PreflightInclude).Count -ne $ownedFiles.Count) { throw 'Preflight inventory count mismatch.' }
Write-Output 'PASS Shared inventory includes exactly the files installed, excluding profiles, PDBs, logs and databases'
$before = @(Get-ChildItem -LiteralPath $target -Recurse -File | ForEach-Object {[pscustomobject]@{Path=$_.FullName;Hash=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$scriptFile = Join-Path $fixture 'Probe.iss'
$pascal = @'
[Setup]
AppName=Owned Preflight Test Runner
AppVersion=1.0
PrivilegesRequired=lowest
CreateAppDir=no
Uninstallable=no
OutputBaseFilename=Uninstaller77Setup
OutputDir={#TestTarget}
SetupLogging=yes
UseSetupLdr=yes

[Code]
#include PreflightImplementation
#include LegacyImplementation
var Passed: Integer;

procedure Check(Condition: Boolean; Name: String);
begin
  if not Condition then RaiseException('FAIL ' + Name);
  Passed := Passed + 1;
  Log('PASS ' + Name);
end;

function InitializeSetup: Boolean;
var Target, ResultPath, UnicodePath: String; Lock: TFileStream;
begin
  Result := False;
  Passed := 0;
  Target := ExpandConstant('{src}');
  try
    if ExpandConstant('{param:case|normal}') = 'readonly' then begin
      Check(CompareText(FirstUnavailablePayloadFile(Target), Target + '\settings.json') = 0,
        'Read-only installed payload is still rejected before copying');
      SaveStringToFile(Target + '\RESULT.txt', '1 passed / 0 failed', False);
      Exit;
    end;
    ResultPath := LegacyFirstUnavailablePayloadFile(Target);
    Check(CompareText(ResultPath, ExpandConstant('{srcexe}')) = 0,
      'Reproduced old bug: destination EXE scan rejects the currently running copied installer');
    Check(FirstUnavailablePayloadFile(Target) = '',
      'Fixed preflight allows Setup running inside the destination directory');

    Lock := TFileStream.Create(Target + '\Uninstaller77.dll', fmOpenRead or fmShareDenyWrite);
    try
      Check(CompareText(FirstUnavailablePayloadFile(Target), Target + '\Uninstaller77.dll') = 0,
        'Actual payload DLL in use is still rejected before replacement');
    finally Lock.Free; end;
    Check(FirstUnavailablePayloadFile(Target) = '', 'Releasing payload DLL allows retry');

    Lock := TFileStream.Create(Target + '\Unrelated.exe', fmOpenRead or fmShareDenyWrite);
    try
      Check(FirstUnavailablePayloadFile(Target) = '', 'Unrelated locked destination EXE does not block installation');
    finally Lock.Free; end;

    Lock := TFileStream.Create(Target + '\WebView2\profile.dll', fmOpenRead or fmShareDenyWrite);
    try
      Check(FirstUnavailablePayloadFile(Target) = '', 'Excluded WebView2 profile files are never probed');
    finally Lock.Free; end;

    UnicodePath := Target + '\wwwroot\واجهة''تجريبية.js';
    Lock := TFileStream.Create(UnicodePath, fmOpenRead or fmShareDenyWrite);
    try
      Check(CompareText(FirstUnavailablePayloadFile(Target), UnicodePath) = 0,
        'Packaged web assets are protected, including Arabic and apostrophe paths');
    finally Lock.Free; end;
    Check(FirstUnavailablePayloadFile(Target + '\not-created') = '', 'Fresh destination permits initial installation');
    Check(CompareText(FirstUnavailablePayloadFile(Target + '\collision'), Target + '\collision\Uninstaller77.dll') = 0,
      'A directory occupying a packaged file destination is rejected');
    Check(FirstUnavailablePayloadFile(Target) = '', 'No stale failure state persists after a retry');
    SaveStringToFile(Target + '\RESULT.txt', Format('%d passed / 0 failed', [Passed]), False);
  except
    SaveStringToFile(Target + '\RESULT.txt', 'FAIL ' + GetExceptionMessage, False);
  end;
  { Test runner only. Never reach installation or create an uninstall registration. }
end;
'@
$pascal | Set-Content -LiteralPath $scriptFile -Encoding utf8BOM
$compiler = Join-Path $root 'tools\InnoSetup\ISCC.exe'
& $compiler /Q "/DTestTarget=$target" "/DPayloadPreflightList=$($inventory.PreflightInclude)" "/DPreflightImplementation=$(Join-Path $root 'installer\PayloadPreflight.iss')" "/DLegacyImplementation=$(Join-Path $PSScriptRoot 'fixtures\installer\LegacyPayloadScan.iss')" $scriptFile *> (Join-Path $ReportDirectory 'PREFLIGHT_COMPILE.log')
if ($LASTEXITCODE) { Get-Content -LiteralPath (Join-Path $ReportDirectory 'PREFLIGHT_COMPILE.log'); throw 'Preflight runner compilation failed.' }
$runner = Join-Path $target 'Uninstaller77Setup.exe'
$process = Start-Process -FilePath $runner -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/SP-','/NORESTART',('/LOG="' + (Join-Path $target 'ENGINE.log') + '"')) -WindowStyle Hidden -PassThru -Wait
if (!(Test-Path -LiteralPath (Join-Path $target 'RESULT.txt'))) { throw "Test runner produced no result; exit code $($process.ExitCode). Fixture preserved: $fixture" }
$result = Get-Content -LiteralPath (Join-Path $target 'RESULT.txt') -Raw
Copy-Item -LiteralPath (Join-Path $target 'ENGINE.log') -Destination (Join-Path $ReportDirectory 'PREFLIGHT_RUNTIME.log')
Copy-Item -LiteralPath $inventory.InstallInclude -Destination (Join-Path $ReportDirectory 'FIXTURE_INSTALL_INVENTORY.iss')
Copy-Item -LiteralPath $inventory.PreflightInclude -Destination (Join-Path $ReportDirectory 'FIXTURE_PREFLIGHT_INVENTORY.iss')
if ($result -notmatch '^10 passed / 0 failed') { throw "$result; fixture preserved: $fixture" }
foreach ($file in $before) {
    if ((Get-FileHash -LiteralPath $file.Path).Hash -ne $file.Hash) { throw "Test runner changed fixture file: $($file.Path)" }
}
Write-Output 'PASS Actual Inno runtime: 10 checks passed / 0 failed; old false positive reproduced; no installation performed'
Write-Output 'PASS Fixture payload, profile and database bytes were not modified'
# Prove the payload protection distinguishes permissions from unrelated locks.
$readonly = Join-Path $target 'settings.json'
[IO.File]::SetAttributes($readonly, [IO.FileAttributes]::ReadOnly)
try {
    $readonlyRun = Start-Process -FilePath $runner -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/SP-','/NORESTART','/case=readonly',('/LOG="' + (Join-Path $target 'READONLY.log') + '"')) -WindowStyle Hidden -PassThru -Wait
    Copy-Item -LiteralPath (Join-Path $target 'READONLY.log') -Destination (Join-Path $ReportDirectory 'PREFLIGHT_READONLY_RUNTIME.log')
    $readonlyResult = Get-Content -LiteralPath (Join-Path $target 'RESULT.txt') -Raw
    if ($readonlyResult -notmatch '^1 passed / 0 failed') { throw "$readonlyResult; read-only regression" }
    Write-Output 'PASS Actual Inno runtime: read-only installed payload is still blocked'
} finally { [IO.File]::SetAttributes($readonly,[IO.FileAttributes]::Normal) }
$final = [ordered]@{ SharedInventoryChecks=1; InnoRuntimePassed=11; FixtureIntegrityChecks=1; TotalPassed=13; Failed=0; RunnerExitCode=$process.ExitCode; NativeInstallPerformed=$false; Fixture=$fixture }
$final | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportDirectory 'PREFLIGHT_RESULTS.json') -Encoding UTF8
# This runner contains only owned test bytes and generated includes, never user data.
$resolved = [IO.Path]::GetFullPath($fixture)
if (!$resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or @(Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count) { throw 'Unsafe fixture cleanup path.' }
Remove-Item -LiteralPath $resolved -Recurse -Force
