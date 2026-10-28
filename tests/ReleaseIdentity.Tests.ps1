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
Check ($project.Project.PropertyGroup.Version -eq '1.0.0') 'Single product release version is 1.0.0'
foreach($name in @('Uninstaller77.exe','Uninstaller77.dll','Uninstaller77.deps.json','Uninstaller77.runtimeconfig.json')) {
    Check (Test-Path -LiteralPath (Join-Path $PayloadDirectory $name)) ('Renamed runtime artifact exists: ' + $name)
}
foreach($file in @((Join-Path $PayloadDirectory 'Uninstaller77.exe'),(Join-Path $PayloadDirectory 'Uninstaller77.dll'),$Setup)) {
    $version=(Get-Item -LiteralPath $file).VersionInfo
    Check (($version.FileMajorPart -eq 1) -and ($version.FileMinorPart -eq 0) -and ($version.FileBuildPart -eq 0) -and ($version.FilePrivatePart -eq 0)) ('Fixed numeric version equals 1.0.0.0: ' + (Split-Path -Leaf $file))
    Check ($version.ProductName.Trim() -eq 'Uninstaller 77') ('Product identity matches: ' + (Split-Path -Leaf $file))
}
Check (@(Get-ChildItem -LiteralPath $PayloadDirectory -File -Filter 'WinUninstaller*').Count -eq 0) 'No previous assembly names in runtime package'
$manifest=Get-Content -LiteralPath (Join-Path $PayloadDirectory 'release-manifest.json') -Raw | ConvertFrom-Json
Check ($manifest.version -eq '1.0.0' -and $manifest.fileVersion -eq '1.0.0.0') 'Manifest and product versions agree'
$deps=Get-Content -LiteralPath (Join-Path $PayloadDirectory 'Uninstaller77.deps.json') -Raw | ConvertFrom-Json
Check ($null -ne $deps.libraries.'Uninstaller77/1.0.0') 'Runtime dependency graph names the new assembly and version'
$installer=Get-Content -LiteralPath (Join-Path $workspace 'installer\Uninstaller77.iss') -Raw
Check ($installer.Contains('AppId={{B1477720-8D55-4E92-8C77-771177100001}')) 'Existing installation identity retained for upgrades'
Check ($installer.Contains('Name: "english"; MessagesFile: "compiler:Default.isl"') -and !$installer.Contains('Name: "arabic"')) 'Installer remains English only'
Check ($installer.Contains('RetireKnownLegacyRuntime') -and $installer.Contains('FirstUnavailableLegacyRuntime')) 'Renamed upgrade has legacy lock preflight and exact-hash retirement'
Write-Output "RESULT $passed passed / 0 failed"
