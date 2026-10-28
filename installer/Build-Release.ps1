param(
    [switch]$SkipBuild,
    [string]$Version = '',
    [string]$PayloadDir = '',
    [string]$OutputName = '',
    [string]$OutputDir = '',
    [switch]$KeepPreviousPayload
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
function Assert-WorkspacePath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\')
    if (!$resolved.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Generated payload paths must stay inside the project.' }
    $cursor = $resolved
    while ($cursor -ne $root) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Generated payload path contains a reparse point: $cursor"
        }
        $cursor = Split-Path -Parent $cursor
    }
}
$projectFile = Join-Path $projectRoot 'app\WinUninstaller\WinUninstaller.csproj'
if (!$Version) {
    [xml]$projectDefinition = Get-Content -LiteralPath $projectFile -Raw
    $Version = [string]($projectDefinition.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'The project must declare a valid three-part release version.' }
}
if (!$PayloadDir) { $PayloadDir = Join-Path $projectRoot 'dist\app' }
$PayloadDir = [IO.Path]::GetFullPath($PayloadDir)
if (!$OutputName) { $OutputName = 'Uninstaller77-Setup' }
if (!$OutputDir) { $OutputDir = Join-Path $projectRoot 'dist' }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$previousPayload = $null
if (!$SkipBuild) {
    Push-Location (Join-Path $projectRoot 'app\WinUninstaller\WebUI')
    try {
        & npm test
        if ($LASTEXITCODE) { throw 'React tests failed.' }
        & npm run build
        if ($LASTEXITCODE) { throw 'React build failed.' }
    } finally { Pop-Location }
    # Build a complete fresh payload: dependency upgrades must not leave obsolete
    # native DLLs from previous builds inside the installer source directory.
    $stagedPayload = Join-Path $projectRoot ('_build\release-stage-' + [Guid]::NewGuid().ToString('N'))
    Assert-WorkspacePath $stagedPayload
    Assert-WorkspacePath $PayloadDir
    # The installer supports Windows x64. Publish the SDK-selected full runtime
    # closure instead of shipping Android/iOS/Linux native assets from NuGet.
    & dotnet publish $projectFile -c Release -r win-x64 --self-contained false -o $stagedPayload -p:SkipLocalWebUiBuild=true -p:NuGetAudit=false
    if ($LASTEXITCODE) { throw '.NET Release publish failed.' }
    if ((Get-Item -LiteralPath (Join-Path $stagedPayload 'Uninstaller77.exe')).VersionInfo.FileVersion -ne "$Version.0") {
        throw 'Staged payload version does not match the requested version.'
    }
    $workspacePrefix = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\') + '\'
    if (!$PayloadDir.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        !$stagedPayload.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Payload replacement is restricted to a directory inside this project.'
    }
    if (Test-Path -LiteralPath $PayloadDir) {
        if (((Get-Item -LiteralPath $PayloadDir).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Payload replacement refuses a reparse-point destination.'
        }
        $previousPayload = Join-Path $projectRoot ('_build\archive\payload-before-' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
        Assert-WorkspacePath $previousPayload
        New-Item -ItemType Directory -Path (Split-Path -Parent $previousPayload) -Force | Out-Null
        if (@(Get-ChildItem -LiteralPath $PayloadDir -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
            throw 'Payload replacement refuses a tree containing reparse points.'
        }
        # Keep a complete recovery package until the new installer compiles.
        Move-Item -LiteralPath $PayloadDir -Destination $previousPayload
    }
    try {
        New-Item -ItemType Directory -Path (Split-Path -Parent $PayloadDir) -Force | Out-Null
        Move-Item -LiteralPath $stagedPayload -Destination $PayloadDir
    } catch {
        if ($previousPayload -and !(Test-Path -LiteralPath $PayloadDir)) {
            Move-Item -LiteralPath $previousPayload -Destination $PayloadDir
        }
        throw
    }
}
$exe = Join-Path $PayloadDir 'Uninstaller77.exe'
$dll = Join-Path $PayloadDir 'Uninstaller77.dll'
$index = Join-Path $PayloadDir 'wwwroot\index.html'
if ((Get-Item -LiteralPath $exe).VersionInfo.FileVersion -ne "$Version.0") { throw 'Payload version does not match installer version.' }
$exeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
$dllHash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
$uiHash = (Get-FileHash -LiteralPath $index -Algorithm SHA256).Hash
$manifest = [ordered]@{ product='Uninstaller 77'; version=$Version; fileVersion="$Version.0"; builtAt=[DateTimeOffset]::Now.ToString('O'); exeSHA256=$exeHash; dllSHA256=$dllHash; indexSHA256=$uiHash; signed=$false }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PayloadDir 'release-manifest.json') -Encoding UTF8
. (Join-Path $PSScriptRoot 'PayloadInventory.ps1')
$inventoryDirectory = Join-Path $projectRoot '_build\installer-inventory'
Assert-WorkspacePath $inventoryDirectory
$inventory = New-InstallerPayloadInventory $PayloadDir $inventoryDirectory
$compiler = $env:INNO_SETUP_COMPILER
if (!$compiler) { $compiler = Join-Path $projectRoot 'tools\InnoSetup\ISCC.exe' }
if (!(Test-Path -LiteralPath $compiler)) {
    $installedCompiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($installedCompiler) { $compiler = $installedCompiler.Source }
}
if (!(Test-Path -LiteralPath $compiler)) {
    throw 'Inno Setup 6.7.3 or newer is required. Install it separately and set INNO_SETUP_COMPILER to the full ISCC.exe path. The compiler is not redistributed with this source repository.'
}
& $compiler /Q "/DAppVersion=$Version" "/DPayloadDir=$PayloadDir" "/DOutputName=$OutputName" "/DSetupOutputDir=$OutputDir" "/DExpectedExeSha=$exeHash" "/DExpectedDllSha=$dllHash" "/DExpectedUiSha=$uiHash" "/DPayloadInstallList=$($inventory.InstallInclude)" "/DPayloadPreflightList=$($inventory.PreflightInclude)" (Join-Path $PSScriptRoot 'Uninstaller77.iss')
if ($LASTEXITCODE) { throw 'Installer compilation failed.' }
$setup = Join-Path $OutputDir "$OutputName.exe"
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
"$setupHash  $OutputName.exe" | Set-Content -LiteralPath (Join-Path $OutputDir "$OutputName.sha256") -Encoding ASCII
Get-FileHash -LiteralPath $setup -Algorithm SHA256 | Format-List
Write-Output "Installer: $setup"
if ($previousPayload) {
    if ($KeepPreviousPayload) {
        Write-Output "Previous complete payload retained by request: $previousPayload"
    } else {
        # Only this invocation's generated backup is eligible. Old archives are
        # handled separately by the reviewed cleanup tool, never a wildcard here.
        $archivePrefix = [IO.Path]::GetFullPath((Join-Path $projectRoot '_build\archive')).TrimEnd('\') + '\'
        $resolvedBackup = [IO.Path]::GetFullPath($previousPayload)
        Assert-WorkspacePath $resolvedBackup
        if (!$resolvedBackup.StartsWith($archivePrefix, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedBackup) -notmatch '^payload-before-\d+\.\d+\.\d+-[a-f0-9]{32}$') {
            throw 'Refusing cleanup of an unexpected backup path.'
        }
        $backupItems = @((Get-Item -LiteralPath $resolvedBackup -Force)) + @(Get-ChildItem -LiteralPath $resolvedBackup -Recurse -Force)
        if (@($backupItems | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
            throw 'Backup cleanup refuses reparse points.'
        }
        foreach ($file in $backupItems | Where-Object { !$_.PSIsContainer }) {
            $handle = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
            $handle.Dispose()
        }
        Remove-Item -LiteralPath $resolvedBackup -Recurse -Force
        Write-Output 'Temporary previous payload removed after successful installer compilation.'
    }
}
