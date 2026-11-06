param(
    [switch]$SkipBuild,
    [string]$Version = '',
    [string]$PayloadDir = '',
    [string]$OutputName = '',
    [string]$OutputDir = '',
    [string]$WebView2Installer = '',
    [string]$SignerScript = '',
    [string]$SigningPublisher = '',
    [switch]$KeepPreviousPayload
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$signedBuild = ![string]::IsNullOrWhiteSpace($SignerScript) -or ![string]::IsNullOrWhiteSpace($SigningPublisher)
if ($signedBuild) {
    if ([string]::IsNullOrWhiteSpace($SignerScript) -or [string]::IsNullOrWhiteSpace($SigningPublisher)) {
        throw 'Provide both -SignerScript and -SigningPublisher to produce a signed build.'
    }
    $signer = Get-Item -LiteralPath $SignerScript
    if ($signer.PSIsContainer -or $signer.Extension -ne '.ps1') { throw 'The signing provider must be a PowerShell script.' }
    $SignerScript = $signer.FullName
}
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
function Assert-GeneratedDestination([string]$Path, [switch]$AllowDistRoot) {
    Assert-WorkspacePath $Path
    $dist = Join-Path $projectRoot 'dist'
    $build = Join-Path $projectRoot '_build'
    if (($AllowDistRoot -and $Path.Equals($dist, [StringComparison]::OrdinalIgnoreCase)) -or
        $Path.StartsWith($dist + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($build + '\', [StringComparison]::OrdinalIgnoreCase)) { return }
    throw 'Release destinations must be inside dist or _build. The dist root is reserved for installer files.'
}
function Assert-ExistingPayload([string]$Directory) {
    if (!(Test-Path -LiteralPath $Directory)) { return }
    $manifestPath = Join-Path $Directory 'release-manifest.json'
    Assert-WorkspacePath $manifestPath
    if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Existing payload has no Uninstaller 77 release manifest.' }
    $previous = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($previous.product -ne 'Uninstaller 77' -or $previous.version -notmatch '^\d+\.\d+\.\d+$') {
        throw 'Existing payload has an invalid Uninstaller 77 release manifest.'
    }
    $verifiedFiles = @{ 'Uninstaller77.exe' = $previous.exeSHA256; 'Uninstaller77.dll' = $previous.dllSHA256; 'wwwroot\index.html' = $previous.indexSHA256 }
    if ($previous.sbomSHA256) { $verifiedFiles['bom.cdx.json'] = $previous.sbomSHA256 }
    foreach ($entry in $verifiedFiles.GetEnumerator()) {
        $file = Join-Path $Directory $entry.Key
        Assert-WorkspacePath $file
        if ($entry.Value -notmatch '^[a-fA-F0-9]{64}$' -or !(Test-Path -LiteralPath $file -PathType Leaf) -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Value) {
            throw "Existing payload does not match its release manifest: $($entry.Key)."
        }
    }
}
function Assert-BundledDesktopRuntime([string]$Directory, [string]$RuntimeVersion) {
    $config = Get-Content -LiteralPath (Join-Path $Directory 'Uninstaller77.runtimeconfig.json') -Raw | ConvertFrom-Json
    foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        if (!($config.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq $framework -and $_.version -eq $RuntimeVersion })) {
            throw "The payload must include $framework $RuntimeVersion."
        }
    }
    foreach ($file in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $Directory $file) -PathType Leaf)) {
            throw "The self-contained payload is missing $file."
        }
    }
}
$projectFile = Join-Path $projectRoot 'app\WinUninstaller\WinUninstaller.csproj'
if (!$Version) {
    [xml]$projectDefinition = Get-Content -LiteralPath $projectFile -Raw
    $Version = [string]($projectDefinition.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'The project must declare a valid three-part release version.' }
}
if (!$PayloadDir) { $PayloadDir = Join-Path $projectRoot 'dist\app' }
$PayloadDir = [IO.Path]::GetFullPath($PayloadDir).TrimEnd('\')
if (!$OutputName) { $OutputName = 'Uninstaller77-Setup' }
if (!$OutputDir) { $OutputDir = Join-Path $projectRoot 'dist' }
$OutputDir = [IO.Path]::GetFullPath($OutputDir).TrimEnd('\')
Assert-GeneratedDestination $PayloadDir
Assert-GeneratedDestination $OutputDir -AllowDistRoot
if ($OutputDir.Equals($PayloadDir, [StringComparison]::OrdinalIgnoreCase) -or
    $OutputDir.StartsWith($PayloadDir + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Setup output must not be inside the application payload.'
}
Assert-ExistingPayload $PayloadDir
if (!$WebView2Installer) {
    $WebView2Installer = Join-Path $projectRoot '_build\installer-prerequisites\MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
}
if (!(Test-Path -LiteralPath $WebView2Installer -PathType Leaf)) {
    throw 'WebView2 Evergreen Standalone x64 is required to build Setup. See docs/BUILDING.md or supply -WebView2Installer.'
}
$WebView2Installer = (Get-Item -LiteralPath $WebView2Installer -ErrorAction Stop).FullName
$webViewSignature = Get-AuthenticodeSignature -LiteralPath $WebView2Installer
if ($webViewSignature.Status -ne 'Valid' -or
    $webViewSignature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'Microsoft Corporation' -or
    (Get-Item -LiteralPath $WebView2Installer).Length -lt 50MB -or
    (Split-Path -Leaf $WebView2Installer) -ne 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe') {
    throw 'Supply the official Microsoft-signed WebView2 Evergreen Standalone x64 installer.'
}
$webViewHash = (Get-FileHash -LiteralPath $WebView2Installer -Algorithm SHA256).Hash
$desktopRuntimeVersion = '10.0.14'
$stageRoot = Join-Path $projectRoot ('_build\release-stage-' + [Guid]::NewGuid().ToString('N'))
$stagedPayload = Join-Path $stageRoot 'app'
Assert-WorkspacePath $PayloadDir
Assert-WorkspacePath $OutputDir
Assert-WorkspacePath $stageRoot
if ($OutputName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw 'Invalid installer output name.' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
if (Test-Path -LiteralPath $PayloadDir) {
    if (@(Get-ChildItem -LiteralPath $PayloadDir -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw 'Payload replacement refuses a tree containing reparse points.'
    }
}
if ($SkipBuild) {
    Copy-Item -LiteralPath $PayloadDir -Destination $stagedPayload -Recurse
} else {
    Push-Location (Join-Path $projectRoot 'app\WinUninstaller\WebUI')
    try {
        & npm test
        if ($LASTEXITCODE) { throw 'React tests failed.' }
        & npm run build
        if ($LASTEXITCODE) { throw 'React build failed.' }
    } finally { Pop-Location }
    & dotnet publish $projectFile -c Release -r win-x64 --self-contained true -o $stagedPayload "-p:RuntimeFrameworkVersion=$desktopRuntimeVersion" -p:SkipLocalWebUiBuild=true -p:NuGetAudit=false
    if ($LASTEXITCODE) { throw '.NET Release publish failed.' }
}
$exe = Join-Path $stagedPayload 'Uninstaller77.exe'
$dll = Join-Path $stagedPayload 'Uninstaller77.dll'
$index = Join-Path $stagedPayload 'wwwroot\index.html'
Assert-BundledDesktopRuntime $stagedPayload $desktopRuntimeVersion
if ((Get-Item -LiteralPath $exe).VersionInfo.FileVersion -ne "$Version.0") { throw 'Payload version does not match installer version.' }
$innoSigning = @()
if ($signedBuild) {
    $signingHost = (Get-Process -Id $PID).Path
    $signingVerifier = Join-Path $PSScriptRoot 'Sign-ReleaseFile.ps1'
    $signingConfig = Join-Path $stageRoot 'signing.json'
    @{ signerScript = $SignerScript; publisher = $SigningPublisher } |
        ConvertTo-Json | Set-Content -LiteralPath $signingConfig -Encoding UTF8
    foreach ($file in @($exe, $dll)) {
        & $signingHost -NoProfile -NonInteractive -File $signingVerifier -ConfigPath $signingConfig -Path $file
        if ($LASTEXITCODE) { throw 'Application signing failed. No output was promoted.' }
    }
    # Inno expands $q to quotes and $f to the exact temporary file being signed.
    $innoCommand = '$q{0}$q -NoProfile -NonInteractive -File $q{1}$q -ConfigPath $q{2}$q -Path $f' -f
        $signingHost.Replace('$', '$$'), $signingVerifier.Replace('$', '$$'), $signingConfig.Replace('$', '$$')
    $signingToolName = 'Uninstaller77Release_' + [Guid]::NewGuid().ToString('N')
    $innoSigning = @("/DU77SignToolName=$signingToolName", "/S$signingToolName=$innoCommand")
}
$exeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
$dllHash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
$uiHash = (Get-FileHash -LiteralPath $index -Algorithm SHA256).Hash
$sbomPath = Join-Path $stagedPayload 'bom.cdx.json'
$webViewRuntimeVersion = [string](Get-Item -LiteralPath $WebView2Installer).VersionInfo.ProductVersion
& (Join-Path $projectRoot 'tools\New-ReleaseSbom.ps1') -Version $Version -OutputPath $sbomPath `
    -NuGetLock (Join-Path $projectRoot 'app\WinUninstaller\packages.lock.json') `
    -NpmLock (Join-Path $projectRoot 'app\WinUninstaller\WebUI\package-lock.json') `
    -DesktopRuntimeVersion $desktopRuntimeVersion -WebView2RuntimeVersion $webViewRuntimeVersion -WebView2SHA256 $webViewHash | Out-Null
$sbomHash = (Get-FileHash -LiteralPath $sbomPath -Algorithm SHA256).Hash
$manifest = [ordered]@{ product='Uninstaller 77'; version=$Version; fileVersion="$Version.0"; builtAt=[DateTimeOffset]::Now.ToString('O'); exeSHA256=$exeHash; dllSHA256=$dllHash; indexSHA256=$uiHash; sbomSHA256=$sbomHash; desktopRuntimeVersion=$desktopRuntimeVersion; webView2InstallerSHA256=$webViewHash; signed=$signedBuild }
if ($signedBuild) { $manifest['signingPublisher'] = $SigningPublisher }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stagedPayload 'release-manifest.json') -Encoding UTF8
. (Join-Path $PSScriptRoot 'PayloadInventory.ps1')
$inventoryDirectory = Join-Path $stageRoot 'inventory'
Assert-WorkspacePath $inventoryDirectory
$inventory = New-InstallerPayloadInventory $stagedPayload $inventoryDirectory
$compiler = $env:INNO_SETUP_COMPILER
if (!$compiler) { $compiler = Join-Path $projectRoot 'tools\InnoSetup\ISCC.exe' }
if (!(Test-Path -LiteralPath $compiler)) {
    $installedCompiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($installedCompiler) { $compiler = $installedCompiler.Source }
}
if (!(Test-Path -LiteralPath $compiler)) {
    throw 'Inno Setup 6.7.3 or newer is required. Install it separately and set INNO_SETUP_COMPILER to the full ISCC.exe path. The compiler is not redistributed with this source repository.'
}
& $compiler /Q @innoSigning "/DAppVersion=$Version" "/DPayloadDir=$stagedPayload" "/DOutputName=$OutputName" "/DSetupOutputDir=$stageRoot" "/DExpectedExeSha=$exeHash" "/DExpectedDllSha=$dllHash" "/DExpectedUiSha=$uiHash" "/DWebView2Installer=$WebView2Installer" "/DExpectedWebView2Sha=$webViewHash" "/DPayloadInstallList=$($inventory.InstallInclude)" "/DPayloadPreflightList=$($inventory.PreflightInclude)" (Join-Path $PSScriptRoot 'Uninstaller77.iss')
if ($LASTEXITCODE) { throw 'Installer compilation failed.' }
$stagedSetup = Join-Path $stageRoot "$OutputName.exe"
if ($signedBuild) {
    & $signingHost -NoProfile -NonInteractive -File $signingVerifier -ConfigPath $signingConfig -Path $stagedSetup -VerifyOnly
    if ($LASTEXITCODE) { throw 'The completed installer signature could not be verified. No output was promoted.' }
}
$setupHash = (Get-FileHash -LiteralPath $stagedSetup -Algorithm SHA256).Hash
$stagedHash = Join-Path $stageRoot "$OutputName.sha256"
"$setupHash  $OutputName.exe" | Set-Content -LiteralPath $stagedHash -Encoding ASCII
$setup = Join-Path $OutputDir "$OutputName.exe"
$checksum = Join-Path $OutputDir "$OutputName.sha256"
$previousPayload = $null
$archive = Join-Path $projectRoot ('_build\archive\payload-before-' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
Assert-WorkspacePath $archive
$setupBackup = Join-Path $stageRoot 'previous'
New-Item -ItemType Directory -Path $OutputDir, (Split-Path -Parent $PayloadDir), (Split-Path -Parent $archive), $setupBackup -Force | Out-Null
$changes = @(
    @{ Staged=$stagedPayload; Destination=$PayloadDir; Backup=$archive },
    @{ Staged=$stagedSetup; Destination=$setup; Backup=(Join-Path $setupBackup 'setup.exe') },
    @{ Staged=$stagedHash; Destination=$checksum; Backup=(Join-Path $setupBackup 'setup.sha256') }
)
$promoted = [System.Collections.Generic.List[hashtable]]::new()
$backedUp = [System.Collections.Generic.List[hashtable]]::new()
$promotionLock = [IO.File]::Open((Join-Path $projectRoot '_build\release-promotion.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    Assert-ExistingPayload $PayloadDir
    foreach ($change in $changes) {
        Assert-WorkspacePath $change.Destination
        if (Test-Path -LiteralPath $change.Destination) {
            Move-Item -LiteralPath $change.Destination -Destination $change.Backup
            $backedUp.Add($change)
        }
        Move-Item -LiteralPath $change.Staged -Destination $change.Destination
        $promoted.Add($change)
    }
} catch {
    $promotionError = $_
    for ($i = $promoted.Count - 1; $i -ge 0; $i--) {
        $change = $promoted[$i]
        Move-Item -LiteralPath $change.Destination -Destination $change.Staged -ErrorAction Continue
    }
    for ($i = $backedUp.Count - 1; $i -ge 0; $i--) {
        $change = $backedUp[$i]
        if (!(Test-Path -LiteralPath $change.Destination)) {
            Move-Item -LiteralPath $change.Backup -Destination $change.Destination -ErrorAction Continue
        }
    }
    throw "Installer promotion failed. Recovery files, if any, are in $stageRoot and $archive. $promotionError"
} finally { $promotionLock.Dispose() }
if (Test-Path -LiteralPath $archive) { $previousPayload = $archive }
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
