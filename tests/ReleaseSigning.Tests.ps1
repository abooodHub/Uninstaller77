param([string]$ReportDirectory = '', [switch]$ExerciseTestCertificate)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$ReportDirectory) { $ReportDirectory = Join-Path $root ('_build\signing-tests-' + [Guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$ReportDirectory = (Get-Item -LiteralPath $ReportDirectory).FullName
$verifier = Join-Path $root 'installer\Sign-ReleaseFile.ps1'
$hostExe = (Get-Process -Id $PID).Path
$configPath = Join-Path $ReportDirectory 'signing-test.json'
$provider = Join-Path $ReportDirectory 'no-op-provider.ps1'
Set-Content -LiteralPath $provider -Value 'param([string]$Path) exit 0'
$passed = 0

function Write-Config([string]$Publisher) {
    @{ signerScript = $provider; publisher = $Publisher } | ConvertTo-Json |
        Set-Content -LiteralPath $configPath -Encoding UTF8
}

function Check-Verification([string]$Path, [string]$Failure = '', [switch]$RunProvider) {
    $commandArgs = @('-NoProfile', '-NonInteractive', '-File', $verifier, '-ConfigPath', $configPath, '-Path', $Path)
    if (!$RunProvider) { $commandArgs += '-VerifyOnly' }
    $output = (& $hostExe @commandArgs 2>&1 | Out-String)
    $status = $LASTEXITCODE
    if ($Failure) {
        if ($status -eq 0 -or !$output.Contains($Failure)) { throw "Unexpected verification result: $status $output" }
    } elseif ($status -ne 0 -or !$output.Contains('Verified Authenticode')) {
        throw "The trusted timestamped fixture failed verification: $status $output"
    }
    $script:passed++
}

$trusted = Join-Path $root '_build\installer-prerequisites\MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
$trustedSignature = Get-AuthenticodeSignature -LiteralPath $trusted
Write-Config $trustedSignature.SignerCertificate.Subject
Check-Verification $trusted
Write-Output 'PASS real timestamped Authenticode signature verifies.'
Write-Config 'CN=Unrelated test publisher'
Check-Verification $trusted 'Unexpected signing publisher'
Write-Output 'PASS a trusted signature from the wrong publisher is refused.'

$unsigned = Join-Path $ReportDirectory 'owned-unsigned-fixture.exe'
[IO.File]::WriteAllText($unsigned, 'Owned unsigned test file')
Check-Verification $unsigned 'Expected a valid embedded Authenticode signature' -RunProvider
Write-Output 'PASS a provider returning success without a valid signature is refused.'
Set-Content -LiteralPath $provider -Value 'param([string]$Path) exit 17'
Check-Verification $unsigned 'exit 17' -RunProvider
Write-Output 'PASS a failed provider propagates its exit code.'

$compiler = $env:INNO_SETUP_COMPILER
if (!$compiler) { $compiler = Join-Path $root 'tools\InnoSetup\ISCC.exe' }
$innoFixture = Join-Path $ReportDirectory 'Inno signing fixture'
New-Item -ItemType Directory -Path $innoFixture -Force | Out-Null
$fixtureScript = Join-Path $innoFixture 'test.iss'
@'
[Setup]
AppName=Owned signing fixture
AppVersion=1.0
DefaultDirName={localappdata}\Owned signing fixture
PrivilegesRequired=lowest
OutputBaseFilename=owned-test-setup
SignTool=Uninstaller77Release
SignedUninstaller=yes
'@ | Set-Content -LiteralPath $fixtureScript -Encoding utf8BOM
@'
param([string]$Path)
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'inno-invoked.txt'), $Path)
exit 17
'@ | Set-Content -LiteralPath $provider
$innoCommand = '$q{0}$q -NoProfile -NonInteractive -File $q{1}$q -ConfigPath $q{2}$q -Path $f' -f
    $hostExe.Replace('$', '$$'), $verifier.Replace('$', '$$'), $configPath.Replace('$', '$$')
$innoOutput = (& $compiler /Q "/O$innoFixture" ("/SUninstaller77Release=" + $innoCommand) $fixtureScript 2>&1 | Out-String)
if ($LASTEXITCODE -eq 0 -or !(Test-Path -LiteralPath (Join-Path $ReportDirectory 'inno-invoked.txt')) -or
    (Test-Path -LiteralPath (Join-Path $innoFixture 'owned-test-setup.exe'))) {
    throw "Inno did not fail closed after the owned signing provider rejected the temporary file: $innoOutput"
}
$passed++
Write-Output 'PASS Inno calls the signing provider and stops when it fails.'

if ($ExerciseTestCertificate) {
    $subject = 'CN=Uninstaller77 local signing test ' + [Guid]::NewGuid().ToString('N')
    $certOptions = @{ Subject = $subject; Type = 'CodeSigningCert'; CertStoreLocation = 'Cert:\CurrentUser\My'
        KeyExportPolicy = 'NonExportable'; NotAfter = (Get-Date).AddDays(2); HashAlgorithm = 'SHA256' }
    $certificate = New-SelfSignedCertificate @certOptions
    try {
        $testBinary = Join-Path $ReportDirectory 'owned-self-signed-fixture.exe'
        Copy-Item -LiteralPath (Join-Path $root 'app\WinUninstaller\bin\Release\net10.0-windows\Uninstaller77.exe') -Destination $testBinary
        $signed = Set-AuthenticodeSignature -FilePath $testBinary -Certificate $certificate -HashAlgorithm SHA256
        if ($signed.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
            throw "The self-signed fixture was not signed: $($signed.StatusMessage)"
        }
        Write-Config $subject
        Check-Verification $testBinary 'Expected a valid embedded Authenticode signature'
        Write-Output 'PASS a real self-signed test binary is refused for distribution.'
    } finally {
        $certificatePath = 'Cert:\CurrentUser\My\' + $certificate.Thumbprint
        if ((Get-Item -LiteralPath $certificatePath).Subject -ceq $subject) {
            Remove-Item -LiteralPath $certificatePath -DeleteKey
        }
    }
}
Write-Output "RESULT $passed passed / 0 failed"
