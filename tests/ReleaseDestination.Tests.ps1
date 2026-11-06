$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$builder = Join-Path $root 'installer/Build-Release.ps1'
$fixture = Join-Path $root ('_build/release-destination-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $fixture 'payload'
$setup = Join-Path $fixture 'setup'
$missingPrerequisite = Join-Path $fixture 'deliberately-absent.exe'
$passed = 0

function Assert-Rejected([string]$Payload, [string]$Output, [string]$Message) {
    try { & $builder -PayloadDir $Payload -OutputDir $Output -WebView2Installer $missingPrerequisite }
    catch {
        if (!$_.Exception.Message.Contains($Message)) { throw }
        $script:passed++
        Write-Output "PASS $Message"
        return
    }
    throw "Expected rejection: $Message"
}

Assert-Rejected (Join-Path $root 'app') $setup 'Release destinations must be inside dist or _build.'
Assert-Rejected (Join-Path $root 'dist') $setup 'Release destinations must be inside dist or _build.'
Assert-Rejected $payload (Join-Path $root 'docs') 'Release destinations must be inside dist or _build.'
Assert-Rejected $payload (Join-Path $payload 'nested') 'Setup output must not be inside the application payload.'
[IO.Directory]::CreateDirectory((Join-Path $payload 'wwwroot')) | Out-Null
$sentinel = Join-Path $payload 'Uninstaller77.exe'
[IO.File]::WriteAllText($sentinel, 'Owned unchanged sentinel')
Assert-Rejected $payload $setup 'Existing payload has no Uninstaller 77 release manifest.'
$manifest = Join-Path $payload 'release-manifest.json'
@{product='Uninstaller 77'; version='1.0.3'; exeSHA256=('0' * 64); dllSHA256=('0' * 64); indexSHA256=('0' * 64)} |
    ConvertTo-Json | Set-Content -LiteralPath $manifest
Assert-Rejected $payload $setup 'Existing payload does not match its release manifest:'
if ([IO.File]::ReadAllText($sentinel) -ne 'Owned unchanged sentinel' -or (Test-Path -LiteralPath $setup)) {
    throw 'Rejected destinations must not move or overwrite files.'
}
Write-Output "RESULT $passed passed / 0 failed; no release files were moved."
