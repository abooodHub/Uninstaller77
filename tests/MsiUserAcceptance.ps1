param(
    [Parameter(Mandatory)] [string] $WixPath,
    [string] $ReportDirectory = '_build/msi-acceptance',
    [switch] $WithQueueProtection
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if ([Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run from a non-elevated PowerShell.'
    }
} finally { $identity.Dispose() }

$policy = 'Software\Policies\Microsoft\Windows\Installer'
if ([Microsoft.Win32.Registry]::GetValue("HKEY_CURRENT_USER\$policy", 'AlwaysInstallElevated', 0) -eq 1 -or
    [Microsoft.Win32.Registry]::GetValue("HKEY_LOCAL_MACHINE\$policy", 'AlwaysInstallElevated', 0) -eq 1) {
    throw 'The test requires AlwaysInstallElevated to be off.'
}

$wix = (Resolve-Path -LiteralPath $WixPath).Path
$product = [guid]::NewGuid()
$id = $product.ToString('N')
$code = $product.ToString('B').ToUpperInvariant()
$evidence = [IO.Path]::GetFullPath((Join-Path $ReportDirectory $id))
$registrations = 'HKCU', 'HKLM' | ForEach-Object { "${_}:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$code" }
$markerKey = "HKCU:\Software\Uninstaller77\Fixtures\$id"
$fixtureFolder = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Uninstaller77_MsiFixture_$id"
if (($registrations | Where-Object { Test-Path -LiteralPath $_ }) -or (Test-Path -LiteralPath $markerKey) -or (Test-Path -LiteralPath $fixtureFolder)) {
    throw 'The fixture identity is already in use.'
}

[IO.Directory]::CreateDirectory($evidence) | Out-Null
$payload = Join-Path $evidence 'ownership.txt'
[IO.File]::WriteAllText($payload, $code)
$package = Join-Path $evidence 'owned-fixture.msi'
$source = Join-Path $PSScriptRoot 'fixtures/msi/OwnedFixture.wxs'
$project = Join-Path $PSScriptRoot 'Engine.Tests/Engine.Tests.csproj'
& dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Engine test build failed.' }

& $wix build -arch x64 -d "Id=$id" -d "ProductCode=$code" -d "UpgradeCode=$([guid]::NewGuid().ToString('B'))" `
    -d "ComponentCode=$([guid]::NewGuid().ToString('B'))" -d "PayloadPath=$payload" -out $package $source
if ($LASTEXITCODE -ne 0) { throw 'WiX fixture build failed.' }

$start = [Diagnostics.ProcessStartInfo]::new((Join-Path ([Environment]::SystemDirectory) 'msiexec.exe'))
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
foreach ($argument in @('/i', $package, '/qn', '/norestart', '/L*v', (Join-Path $evidence 'install.log'))) {
    $start.ArgumentList.Add($argument)
}
$process = [Diagnostics.Process]::Start($start)
try {
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Windows Installer returned $($process.ExitCode). Evidence: $evidence" }
} finally { $process.Dispose() }

$mode = if ($WithQueueProtection) { '--msi-protected-queue-fixture' } else { '--msi-user-fixture' }
& dotnet run --project $project -c Release --no-build -- $mode $code $evidence
if ($LASTEXITCODE -ne 0) { throw "Fixture verification failed. Evidence: $evidence" }
Write-Host "Evidence: $evidence"
