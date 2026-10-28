# Exercises Windows mutex/file-sharing behavior with owned fixtures only.
# This does not launch Setup or establish end-to-end upgrade success.
$ErrorActionPreference = 'Stop'
$fixtureRoot = Join-Path (Split-Path -Parent $PSScriptRoot) ('_build\InstallerPreflight_' + [Guid]::NewGuid().ToString('N'))
$mutexName = 'Local\Uninstaller77.InstallerFixture.' + [Guid]::NewGuid().ToString('N')
$script:passed = 0
$handles = [Collections.Generic.List[IDisposable]]::new()
function Assert-Check([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw "FAIL $Name" }
    $script:passed++
    Write-Output "PASS $Name"
}
function Mutex-Exists([string]$Name) {
    $probe = $null
    try { return [Threading.Mutex]::TryOpenExisting($Name, [ref]$probe) }
    finally { if ($probe) { $probe.Dispose() } }
}
try {
    Assert-Check (!(Mutex-Exists $mutexName)) 'No running fixture gives no in-use marker'
    $first = [Threading.Mutex]::new($false, $mutexName); $handles.Add($first)
    $second = [Threading.Mutex]::new($false, $mutexName); $handles.Add($second)
    Assert-Check (Mutex-Exists $mutexName) 'Multiple instances share the same in-use marker'
    $first.Dispose()
    Assert-Check (Mutex-Exists $mutexName) 'Closing one instance keeps another instance protected'
    $second.Dispose()
    Assert-Check (!(Mutex-Exists $mutexName)) 'Marker clears only after all instances close'

    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $fixture = Join-Path $fixtureRoot 'owned-payload.dll'
    [IO.File]::WriteAllBytes($fixture, [byte[]](17,29,43,71,113))
    $before = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash
    $reader = [IO.File]::Open($fixture, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $handles.Add($reader)
    $blocked = $false
    try {
        $probe = [IO.File]::Open($fixture, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $probe.Dispose()
    } catch [IO.IOException] { $blocked = $true }
    Assert-Check $blocked 'Exclusive preflight rejects payload held open by another reader'
    $reader.Dispose()
    $probe = [IO.File]::Open($fixture, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $probe.Dispose()
    Assert-Check $true 'Exclusive preflight accepts payload after the reader closes'
    Assert-Check ($before -eq (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash) 'Preflight leaves existing bytes unchanged'
    Write-Output "$script:passed Passed / 0 Failed (Windows primitives; not native Setup E2E)"
} finally {
    foreach ($handle in $handles) { $handle.Dispose() }
    # Delete only the one owned fixture file and its now-empty, unique directory.
    $ownedFile = Join-Path $fixtureRoot 'owned-payload.dll'
    if ([IO.File]::Exists($ownedFile)) { [IO.File]::Delete($ownedFile) }
    if ([IO.Directory]::Exists($fixtureRoot)) { [IO.Directory]::Delete($fixtureRoot, $false) }
}
