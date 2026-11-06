param(
    [switch]$SkipDependencyRestore,
    [string]$SignerScript = '',
    [string]$SigningPublisher = '',
    [string]$OutputDir = (Join-Path $PSScriptRoot 'dist'),
    [string]$PayloadDir = (Join-Path $PSScriptRoot 'dist\app'),
    [string]$OutputName = 'Uninstaller77-Setup'
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $web = Join-Path $PSScriptRoot 'app\WinUninstaller\WebUI'
    if (!(Test-Path -LiteralPath (Join-Path $web 'node_modules\vite\package.json'))) {
        if ($SkipDependencyRestore) { throw 'Web dependencies are absent. Run Build.ps1 without -SkipDependencyRestore to restore them.' }
        Push-Location $web
        try { & npm ci; if ($LASTEXITCODE) { throw 'npm dependency restore failed.' } }
        finally { Pop-Location }
    }
    & (Join-Path $PSScriptRoot 'installer\Build-Release.ps1') -SignerScript $SignerScript -SigningPublisher $SigningPublisher `
        -OutputDir $OutputDir -PayloadDir $PayloadDir -OutputName $OutputName
    if ($LASTEXITCODE) { throw 'Release build failed.' }
    $setup = Join-Path $OutputDir ($OutputName + '.exe')
    & (Join-Path $PSScriptRoot 'tests\ReleaseIdentity.Tests.ps1') -PayloadDirectory $PayloadDir -Setup $setup
    Write-Output ('Installer: ' + $setup)
} finally { Pop-Location }
