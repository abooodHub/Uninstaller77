param(
    [switch]$Execute,
    [switch]$ReleaseFinalize,
    [string]$ReportDirectory = ''
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$prefix = $workspace + '\'
if (!$ReportDirectory) { $ReportDirectory = Join-Path $workspace ('_build\cleanup-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
if (!$ReportDirectory.StartsWith((Join-Path $workspace '_build') + '\', [StringComparison]::OrdinalIgnoreCase) -and
    !$ReportDirectory.StartsWith((Join-Path $workspace 'reports') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Cleanup evidence must stay inside the project build/reports directories.'
}
function Assert-LocalTree([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (!$resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Outside workspace: $resolved" }
    # Check every ancestor as well as every descendant. Never follow junctions.
    $cursor = $resolved
    while ($cursor -ne $workspace) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point: $cursor" }
        }
        $cursor = Split-Path -Parent $cursor
    }
    if (Test-Path -LiteralPath $resolved -PathType Container) {
        $links = @(Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
        if ($links.Count) { throw "Cleanup refuses a tree containing reparse points: $resolved" }
    }
    return $resolved
}
Assert-LocalTree $ReportDirectory | Out-Null
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$targets = [Collections.Generic.List[object]]::new()
function Add-Target([string]$Relative, [string]$Reason, [bool]$Generated = $true) {
    $path = Assert-LocalTree (Join-Path $workspace $Relative)
    if (Test-Path -LiteralPath $path) { $targets.Add([pscustomobject]@{ Path=$path; Reason=$Reason; Generated=$Generated }) }
}
# Fixed, reviewable output roots. No recursive search for every directory named bin.
foreach ($relative in @(
    'app\WinUninstaller\bin', 'app\WinUninstaller\obj',
    'tests\Engine.Tests\bin', 'tests\Engine.Tests\obj',
    'tests\ProductIntegrity.Tests\bin', 'tests\ProductIntegrity.Tests\obj',
    'tests\ControlCenter.Tests\bin', 'tests\ControlCenter.Tests\obj',
    '_build\AuditCandidate', '_build\Phase13', '_build\Phase14', '_build\Phase14_1', '_build\Phase14_1_Details',
    '_build\cleanup-build-validation',
    'dist\Phase11', 'dist\Phase12', 'dist\Phase13', 'dist\Phase14', 'dist\Phase14_1', 'dist\Phase14_1_Details',
    'app\WinUninstaller\WebUI\node_modules\.vite', 'app\WinUninstaller\WebUI\node_modules\.vite-temp',
    'app\WinUninstaller\WebUI\node_modules\.tmp'
)) { Add-Target $relative 'Reproducible build output or compiler cache' }
if ($ReleaseFinalize) {
    # Explicit final-release roots. Never enumerate arbitrary bin folders or
    # touch the canonical runtime, installer, user data or source fixtures.
    foreach ($relative in @(
        'app\WinUninstaller\WebUI\node_modules',
        'tools\DriveDiagnostics\bin', 'tools\DriveDiagnostics\obj',
        'dist\verified-drive-fix', 'dist\verified-patch', 'dist\DriveDiagnostics',
        '_build\archive'
    )) { Add-Target $relative 'Reproducible dependencies or superseded release/diagnostic package' }
}
$archive = Join-Path $workspace '_build\archive'
$rollback = $null
if (!$ReleaseFinalize -and (Test-Path -LiteralPath $archive)) {
    Assert-LocalTree $archive | Out-Null
    $rollback = Get-ChildItem -LiteralPath $archive -Directory -Filter 'setup-before-*' |
        Where-Object { (Test-Path -LiteralPath (Join-Path $_.FullName 'Uninstaller77-Setup.exe')) -and (Test-Path -LiteralPath (Join-Path $_.FullName 'Uninstaller77-Setup.sha256')) } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($rollback) {
        $expected = ((Get-Content -LiteralPath (Join-Path $rollback.FullName 'Uninstaller77-Setup.sha256') -Raw).Trim() -split '\s+')[0]
        if ((Get-FileHash -LiteralPath (Join-Path $rollback.FullName 'Uninstaller77-Setup.exe')).Hash -ne $expected) { throw 'Rollback installer hash mismatch.' }
    }
    foreach ($directory in Get-ChildItem -LiteralPath $archive -Directory) {
        if ($rollback -and $directory.FullName -eq $rollback.FullName) { continue }
        if ($directory.Name -match '^(payload-before-\d+\.\d+\.\d+-[a-f0-9]{32}|setup-before-.+|nested-generated-app-20261026)$') {
            Add-Target $directory.FullName.Substring($prefix.Length) 'Superseded generated package; latest installer rollback retained'
        }
    }
}
# Confirmed unreferenced scaffold/assets and obsolete one-use migration scripts.
foreach ($relative in @(
    'tests\retired\Scaffold', 'tools\innosetup-6.7.3.exe', 'dist\VM-FullTests.lnk',
    'app\WinUninstaller\WebUI\public\favicon.svg', 'app\WinUninstaller\WebUI\public\icons.svg',
    'app\WinUninstaller\WebUI\src\assets\favicon.svg', 'app\WinUninstaller\WebUI\src\assets\u77.svg',
    '_build\prune_grid_and_fix_handoff.py', '_build\prune_retired_entrypoints.py',
    '_build\retire_removed_features.py', '_build\summarize_code_audit.py'
)) { Add-Target $relative 'Unused scaffold, redundant tool installer, obsolete shortcut or completed migration' $false }
# Collapse overlapping cache roots before measuring/deleting their parent.
$minimalTargets = @($targets | Where-Object {
    $candidate = $_.Path
    @($targets | Where-Object { $candidate -ne $_.Path -and $candidate.StartsWith($_.Path + '\', [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0
})
$targets = [Collections.Generic.List[object]]::new()
foreach ($target in $minimalTargets) { $targets.Add($target) }
$skippedLocked = [Collections.Generic.List[object]]::new()
if ($Execute -and $ReleaseFinalize) {
    $available = [Collections.Generic.List[object]]::new()
    foreach ($target in $targets) {
        try {
            $probeFiles = @(if (Test-Path -LiteralPath $target.Path -PathType Container) { Get-ChildItem -LiteralPath $target.Path -Recurse -Force -File } else { Get-Item -LiteralPath $target.Path })
            foreach ($file in $probeFiles) {
                $pin = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
                $pin.Dispose()
            }
            $available.Add($target)
        } catch { $skippedLocked.Add([pscustomobject]@{ Path=$target.Path; Reason=$_.Exception.Message }) }
    }
    $targets = $available
}
$protectedRoots = @('dist\app', 'dist\current', 'VMTestShare', '_build\audit-snapshot-20261026', 'reports') | ForEach-Object { Join-Path $workspace $_ }
foreach ($target in $targets) {
    foreach ($protected in $protectedRoots) {
        if ($target.Path -eq $protected -or $target.Path.StartsWith($protected + '\', [StringComparison]::OrdinalIgnoreCase) -or $protected.StartsWith($target.Path + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Protected path: $($target.Path)" }
    }
    $files = @(if (Test-Path -LiteralPath $target.Path -PathType Container) { Get-ChildItem -LiteralPath $target.Path -Recurse -Force -File } else { Get-Item -LiteralPath $target.Path })
    foreach ($file in $files) {
        if ($file.Extension -in @('.db', '.sqlite', '.sqlite3', '.db-wal', '.db-shm') -or $file.FullName -match '\\Fixtures?\\') { throw "Protected data/fixture in candidate: $($file.FullName)" }
        $dependencyRoot = Join-Path $workspace 'app\WinUninstaller\WebUI\node_modules'
        # npm supplies its own .ps1 launchers and dependency source files. This
        # exception is restricted to the exact reproducible dependency root.
        if ($target.Generated -and $target.Path -ne $dependencyRoot -and ($file.Extension -in @('.ps1', '.py', '.csproj') -or ($file.Extension -eq '.cs' -and $file.FullName -notmatch '\\obj\\'))) { throw "Unexpected source in generated candidate: $($file.FullName)" }
    }
}
$before = @(Get-ChildItem -LiteralPath $workspace -Recurse -Force -File)
$before | Select-Object @{n='Path';e={$_.FullName.Substring($prefix.Length)}},Length | Export-Csv -LiteralPath (Join-Path $ReportDirectory 'BEFORE.csv') -NoTypeInformation -Encoding UTF8
$manifest = @(foreach ($target in $targets) {
    $files = @(if (Test-Path -LiteralPath $target.Path -PathType Container) { Get-ChildItem -LiteralPath $target.Path -Recurse -Force -File } else { Get-Item -LiteralPath $target.Path })
    foreach ($file in $files) {
        [pscustomobject]@{Path=$file.FullName.Substring($prefix.Length); Bytes=$file.Length; SHA256=(Get-FileHash -LiteralPath $file.FullName).Hash; Root=$target.Path; Reason=$target.Reason}
    }
})
$manifest | Export-Csv -LiteralPath (Join-Path $ReportDirectory 'DELETION_MANIFEST.csv') -NoTypeInformation -Encoding UTF8
ConvertTo-Json -InputObject @($targets.ToArray()) -Depth 4 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'TARGETS.json') -Encoding UTF8
$deletedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest) { [void]$deletedNames.Add((Join-Path $workspace $entry.Path)) }
# Hash retained sources, packages, fixtures and historical evidence before mutation.
$retained = @(foreach ($file in $before) {
    if (!$deletedNames.Contains($file.FullName) -and !$file.FullName.StartsWith($ReportDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
        [pscustomobject]@{Path=$file.FullName; Bytes=$file.Length; SHA256=(Get-FileHash -LiteralPath $file.FullName).Hash}
    }
})
$retained | Export-Csv -LiteralPath (Join-Path $ReportDirectory 'RETAINED_HASHES.csv') -NoTypeInformation -Encoding UTF8
if ($Execute) {
    # Preflight all files before deleting anything; never stop a process to release locks.
    foreach ($entry in $manifest) {
        $stream = [IO.File]::Open((Join-Path $workspace $entry.Path), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        $stream.Dispose()
    }
    foreach ($target in $targets) {
        Assert-LocalTree $target.Path | Out-Null
        Remove-Item -LiteralPath $target.Path -Recurse -Force
    }
    foreach ($file in $retained) {
        if (!(Test-Path -LiteralPath $file.Path) -or (Get-FileHash -LiteralPath $file.Path).Hash -ne $file.SHA256) { throw "Retained file changed: $($file.Path)" }
    }
}
$after = @(Get-ChildItem -LiteralPath $workspace -Recurse -Force -File)
$after | Select-Object @{n='Path';e={$_.FullName.Substring($prefix.Length)}},Length | Export-Csv -LiteralPath (Join-Path $ReportDirectory 'AFTER.csv') -NoTypeInformation -Encoding UTF8
$summary = [ordered]@{
    Executed=[bool]$Execute; BeforeBytes=($before | Measure-Object Length -Sum).Sum;
    AfterBytes=($after | Measure-Object Length -Sum).Sum; CandidateBytes=[long]($manifest | Measure-Object Bytes -Sum).Sum;
    DeletedFiles=$(if ($Execute) { @($manifest).Count } else { 0 }); CandidateRoots=$targets.Count;
    Rollback=$(if ($rollback) {$rollback.FullName} else {$null}); RetainedHashChecks=@($retained).Count;
    Reports=$ReportDirectory; Unit='Logical file bytes; not NTFS allocated clusters'; ArchivedBytes=0
    SkippedLockedRoots=@($skippedLocked.ToArray())
}
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportDirectory 'SUMMARY.json') -Encoding UTF8
$summary | ConvertTo-Json
