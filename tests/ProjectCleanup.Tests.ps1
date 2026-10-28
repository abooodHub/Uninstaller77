$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $root ('_build\cleanup-safety-' + [Guid]::NewGuid().ToString('N'))
$fixturePrefix = [IO.Path]::GetFullPath((Join-Path $root '_build')).TrimEnd('\') + '\'
if (![IO.Path]::GetFullPath($fixture).StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test fixture path.' }
New-Item -ItemType Directory -Path (Join-Path $fixture 'tools') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'tools\Clean-Project.ps1') -Destination (Join-Path $fixture 'tools\Clean-Project.ps1')
$cleaner = Join-Path $fixture 'tools\Clean-Project.ps1'
$passed = 0
function Put([string]$Relative, [string]$Content = 'owned fixture') {
    $path = Join-Path $fixture $Relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, $Content)
    return $path
}
function Check([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw "FAIL $Name" }
    $script:passed++
    Write-Output "PASS $Name"
}
function Expect-Rejection([string]$Name) {
    $caught = $false
    try { & $cleaner -Execute | Out-Null } catch { $caught = $true }
    Check $caught $Name
}
try {
    $generated = Put 'app\WinUninstaller\bin\Release\fake.dll'
    $protected = @(
        (Put 'dist\app\WinUninstaller.exe'), (Put 'dist\Uninstaller77-Setup.exe'),
        (Put 'VMTestShare\Fixture\owned.txt'), (Put 'tests\retired\Engineering\Required.cs'),
        (Put 'app\WinUninstaller\Phase6Fixture\Program.cs'), (Put '_build\audit-snapshot-20261026\source.cs'),
        (Put 'reports\HISTORICAL\evidence.txt'), (Put 'dist\current\app\WinUninstaller.exe')
    )
    & $cleaner | Out-Null
    Check (Test-Path -LiteralPath $generated) 'Default plan preserves generated output'
    & $cleaner -Execute | Out-Null
    Check (!(Test-Path -LiteralPath $generated)) 'Execute removes the complete owned output tree'
    Check (@($protected | Where-Object { !(Test-Path -LiteralPath $_) }).Count -eq 0) 'Source, current package, snapshots, fixtures and historical evidence survive'
    $repeated = (& $cleaner -Execute) | ConvertFrom-Json
    Check ($repeated.DeletedFiles -eq 0 -and $repeated.CandidateBytes -eq 0) 'Repeated cleanup with no candidates reports zero deletions'

    $generated = Put 'app\WinUninstaller\bin\Release\fake.dll'
    $database = Put 'tests\Engine.Tests\obj\user.sqlite'
    Expect-Rejection 'Persistent database in a generated candidate aborts before any deletion'
    Check (Test-Path -LiteralPath $generated) 'Database rejection leaves all candidate trees intact'
    Remove-Item -LiteralPath $database
    $source = Put 'tests\Engine.Tests\bin\Required.cs'
    Expect-Rejection 'Unexpected source in an output tree blocks deletion'
    Remove-Item -LiteralPath $source

    $locked = Put 'tests\Engine.Tests\bin\locked.dll'
    $handle = [IO.File]::Open($locked, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try { Expect-Rejection 'A locked output aborts cleanup without stopping its owner' } finally { $handle.Dispose() }
    Check (Test-Path -LiteralPath $generated) 'Lock rejection occurs before the first recursive deletion'
    & $cleaner -Execute | Out-Null

    $destination = Join-Path $fixture 'VMTestShare'
    $junction = Join-Path $fixture 'app\WinUninstaller\bin'
    New-Item -ItemType Junction -Path $junction -Target $destination | Out-Null
    try { Expect-Rejection 'Junction candidates are rejected without touching the target' } finally { Remove-Item -LiteralPath $junction -Force }
    Check (Test-Path -LiteralPath $protected[2]) 'Junction target fixture remains intact'

    $caught = $false
    try { & $cleaner -ReportDirectory (Join-Path $fixture '..\outside-reports') | Out-Null } catch { $caught = $true }
    Check $caught 'Evidence path outside the project reports directory is rejected'
    $dependency = Put 'app\WinUninstaller\WebUI\node_modules\owned-package\index.js'
    $cache = Put 'app\WinUninstaller\WebUI\node_modules\.vite\cache.js'
    $launcher = Put 'app\WinUninstaller\WebUI\node_modules\.bin\owned.ps1' 'Owned npm launcher fixture'
    $duplicate = Put 'dist\verified-drive-fix\app\owned.dll'
    $old = Put '_build\archive\before-drive-promotion-owned\app\old.dll'
    $finalized = (& $cleaner -ReleaseFinalize -Execute) | ConvertFrom-Json
    Check (@($dependency,$cache,$launcher,$duplicate,$old | Where-Object {Test-Path -LiteralPath $_}).Count -eq 0) 'Release finalization removes complete duplicate packages and reproducible dependencies'
    Check ($finalized.DeletedFiles -eq 5) 'Nested caches and npm script launchers are counted exactly once'
    Check (@($protected | Where-Object { !(Test-Path -LiteralPath $_) }).Count -eq 0) 'Release finalization preserves canonical runtime, installer, sources and historical fixtures'
    Write-Output "RESULT: $passed passed, 0 failed"
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing fixture cleanup outside workspace.' }
    if (@(Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Refusing cleanup of fixture with remaining junctions.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
