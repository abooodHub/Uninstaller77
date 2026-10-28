param([switch]$SkipDependencyRestore)
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
    & (Join-Path $PSScriptRoot 'installer\Build-Release.ps1')
    if ($LASTEXITCODE) { throw 'Release build failed.' }
    & (Join-Path $PSScriptRoot 'tests\ReleaseIdentity.Tests.ps1')
    Write-Output ('Installer: ' + (Join-Path $PSScriptRoot 'dist\Uninstaller77-Setup.exe'))
} finally { Pop-Location }
