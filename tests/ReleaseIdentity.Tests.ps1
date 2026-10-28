param([string]$PayloadDirectory = '', [string]$Setup = '')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if (!$PayloadDirectory) { $PayloadDirectory = Join-Path $workspace 'dist\app' }
if (!$Setup) { $Setup = Join-Path $workspace 'dist\Uninstaller77-Setup.exe' }
$passed = 0
function Check([bool]$condition, [string]$name) {
    if (!$condition) { throw "FAIL $name" }
    $script:passed++; Write-Output "PASS $name"
}
[xml]$project = Get-Content -LiteralPath (Join-Path $workspace 'app\WinUninstaller\WinUninstaller.csproj') -Raw
Check ($project.Project.PropertyGroup.AssemblyName -eq 'Uninstaller77') 'Public assembly name matches product'
$releaseVersion = [string]$project.Project.PropertyGroup.Version
Check ($releaseVersion -match '^1\.0\.\d+$') 'Declared release remains in the 1.0 product line'
$expectedVersion = [Version]($releaseVersion + '.0')
foreach($name in @('Uninstaller77.exe','Uninstaller77.dll','Uninstaller77.deps.json','Uninstaller77.runtimeconfig.json')) {
    Check (Test-Path -LiteralPath (Join-Path $PayloadDirectory $name)) ('Renamed runtime artifact exists: ' + $name)
}
foreach($file in @((Join-Path $PayloadDirectory 'Uninstaller77.exe'),(Join-Path $PayloadDirectory 'Uninstaller77.dll'),$Setup)) {
    $version=(Get-Item -LiteralPath $file).VersionInfo
    Check (($version.FileMajorPart -eq $expectedVersion.Major) -and ($version.FileMinorPart -eq $expectedVersion.Minor) -and ($version.FileBuildPart -eq $expectedVersion.Build) -and ($version.FilePrivatePart -eq $expectedVersion.Revision)) ('Numeric version matches declared release: ' + (Split-Path -Leaf $file))
    Check ($version.ProductName.Trim() -eq 'Uninstaller 77') ('Product identity matches: ' + (Split-Path -Leaf $file))
}
Check (@(Get-ChildItem -LiteralPath $PayloadDirectory -File -Filter 'WinUninstaller*').Count -eq 0) 'No previous assembly names in runtime package'
$manifest=Get-Content -LiteralPath (Join-Path $PayloadDirectory 'release-manifest.json') -Raw | ConvertFrom-Json
Check ($manifest.version -eq $releaseVersion -and $manifest.fileVersion -eq $expectedVersion.ToString()) 'Manifest and product versions agree'
$deps=Get-Content -LiteralPath (Join-Path $PayloadDirectory 'Uninstaller77.deps.json') -Raw | ConvertFrom-Json
Check ($deps.libraries.PSObject.Properties.Name -contains "Uninstaller77/$releaseVersion") 'Runtime dependency graph names the assembly and declared version'
$installer=Get-Content -LiteralPath (Join-Path $workspace 'installer\Uninstaller77.iss') -Raw
Check ($installer.Contains('AppId={{B1477720-8D55-4E92-8C77-771177100001}')) 'Existing installation identity retained for upgrades'
Check ($installer.Contains('Name: "english"; MessagesFile: "compiler:Default.isl"') -and !$installer.Contains('Name: "arabic"')) 'Installer remains English only'
Check ($installer.Contains('RetireKnownLegacyRuntime') -and $installer.Contains('FirstUnavailableLegacyRuntime')) 'Renamed upgrade has legacy lock preflight and exact-hash retirement'
Write-Output "RESULT $passed passed / 0 failed"
