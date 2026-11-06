param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][string]$NuGetLock,
    [Parameter(Mandatory)][string]$NpmLock,
    [Parameter(Mandatory)][string]$DesktopRuntimeVersion,
    [Parameter(Mandatory)][string]$WebView2RuntimeVersion,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$WebView2SHA256
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'SBOM product version must have three numeric parts.' }
foreach ($path in @($NuGetLock, $NpmLock)) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "SBOM input is missing: $path" }
}

function Convert-IntegrityHash([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $encoded = if ($Value.StartsWith('sha512-')) { $Value.Substring(7) } else { $Value }
    try { return [Convert]::ToHexString([Convert]::FromBase64String($encoded)) }
    catch { throw 'A dependency lock contains an invalid SHA-512 integrity value.' }
}

$components = [System.Collections.Generic.List[object]]::new()
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
function Add-Component([string]$Type, [string]$Name, [string]$ComponentVersion, [string]$Purl, [string]$Sha512 = '') {
    if (!$seen.Add($Purl)) { return }
    $component = [ordered]@{ type=$Type; name=$Name; version=$ComponentVersion; purl=$Purl }
    if ($Sha512) { $component['hashes'] = @([ordered]@{ alg='SHA-512'; content=$Sha512 }) }
    $components.Add([pscustomobject]$component)
}

$nuget = Get-Content -LiteralPath $NuGetLock -Raw | ConvertFrom-Json
$framework = $nuget.dependencies.PSObject.Properties | Select-Object -First 1
if (!$framework) { throw 'NuGet lock has no target framework.' }
foreach ($package in $framework.Value.PSObject.Properties) {
    $resolved = [string]$package.Value.resolved
    if (!$resolved) { throw "NuGet lock entry has no resolved version: $($package.Name)" }
    Add-Component 'library' $package.Name $resolved ("pkg:nuget/{0}@{1}" -f [Uri]::EscapeDataString($package.Name), [Uri]::EscapeDataString($resolved)) (Convert-IntegrityHash ([string]$package.Value.contentHash))
}

$npm = Get-Content -LiteralPath $NpmLock -Raw | ConvertFrom-Json -AsHashtable
foreach ($entry in $npm['packages'].GetEnumerator()) {
    if (!$entry.Key -or $entry.Key -eq '') { continue }
    $name = $entry.Key -replace '^.*node_modules/', ''
    $resolved = [string]$entry.Value['version']
    if (!$resolved -or !$name) { continue }
    $purlName = $name.Replace('@', '%40').Replace('/', '%2F')
    Add-Component 'library' $name $resolved ("pkg:npm/{0}@{1}" -f $purlName, [Uri]::EscapeDataString($resolved)) (Convert-IntegrityHash ([string]$entry.Value['integrity']))
}

Add-Component 'framework' 'Microsoft.NETCore.App' $DesktopRuntimeVersion "pkg:generic/Microsoft.NETCore.App@$DesktopRuntimeVersion"
Add-Component 'framework' 'Microsoft.WindowsDesktop.App' $DesktopRuntimeVersion "pkg:generic/Microsoft.WindowsDesktop.App@$DesktopRuntimeVersion"
$webView = [ordered]@{
    type='framework'; name='Microsoft Edge WebView2 Runtime'; version=$WebView2RuntimeVersion
    purl=("pkg:generic/Microsoft%20Edge%20WebView2%20Runtime@{0}" -f [Uri]::EscapeDataString($WebView2RuntimeVersion))
    hashes=@([ordered]@{ alg='SHA-256'; content=$WebView2SHA256.ToUpperInvariant() })
}
if ($seen.Add($webView.purl)) { $components.Add([pscustomobject]$webView) }

$sbom = [ordered]@{
    bomFormat='CycloneDX'; specVersion='1.5'; serialNumber=('urn:uuid:' + [Guid]::NewGuid().ToString())
    version=1
    metadata=[ordered]@{
        timestamp=[DateTimeOffset]::UtcNow.ToString('O')
        component=[ordered]@{ type='application'; name='Uninstaller 77'; version=$Version; purl="pkg:generic/Uninstaller%2077@$Version" }
        tools=@([ordered]@{ vendor='Uninstaller 77'; name='New-ReleaseSbom.ps1'; version='1' })
    }
    components=@($components | Sort-Object purl)
}
$parent = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$sbom | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Output $OutputPath
