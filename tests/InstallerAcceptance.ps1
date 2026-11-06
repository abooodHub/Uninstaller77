param(
    [Parameter(Mandatory)][string]$Candidate,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][string]$ExpectedSHA256,
    [Parameter(Mandatory)][string]$GuestToken,
    [string]$ExpectedSigningPublisher,
    [string]$Baseline,
    [string]$BaselineSHA256,
    [string]$EvidenceDirectory,
    [switch]$DisposableGuest
)

$ErrorActionPreference = 'Stop'
function Read-NumericFileVersion([string]$Path) {
    $info = (Get-Item -LiteralPath $Path).VersionInfo
    return [version]::new($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
}

$computer = Get-CimInstance Win32_ComputerSystem
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (!$DisposableGuest -or $computer.Manufacturer -notmatch 'VMware' -or
    $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Use an explicitly disposable VMware guest and a non-elevated PowerShell session.'
}
$vmTools = Join-Path $env:ProgramFiles 'VMware\VMware Tools\vmtoolsd.exe'
if (!(Test-Path -LiteralPath $vmTools -PathType Leaf)) { throw 'VMware Tools is required to identify the disposable guest.' }
$configuredToken = (& $vmTools --cmd 'info-get guestinfo.u77InstallerAcceptance' | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $GuestToken -notmatch '^[a-f0-9]{32}$' -or $configuredToken -cne $GuestToken) {
    throw 'The VMware guest does not match the disposable test clone.'
}
if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$' -or $ExpectedSHA256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw 'Supply the candidate release version and its full SHA256.'
}
$candidatePath = (Get-Item -LiteralPath $Candidate).FullName
if ((Get-FileHash -LiteralPath $candidatePath).Hash -ne $ExpectedSHA256 -or
    (Read-NumericFileVersion $candidatePath) -ne [version]"$ExpectedVersion.0") {
    throw 'Candidate installer identity does not match.'
}
if ($Baseline) {
    $baselinePath = (Get-Item -LiteralPath $Baseline).FullName
    $baselineVersion = Read-NumericFileVersion $baselinePath
    if ($baselineVersion.ToString() -notin '1.0.0.0','1.0.1.0' -or $BaselineSHA256 -notmatch '^[0-9a-fA-F]{64}$' -or
        (Get-FileHash -LiteralPath $baselinePath).Hash -ne $BaselineSHA256) {
        throw 'Use an original 1.0.0 or 1.0.1 baseline installer with its verified SHA256.'
    }
}

$registryKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{B1477720-8D55-4E92-8C77-771177100001}_is1'
if ((Test-Path -LiteralPath $registryKey) -or
    (Test-Path -LiteralPath $registryKey.Replace('HKCU:', 'HKLM:')) -or
    (Test-Path -LiteralPath $registryKey.Replace('HKCU:\Software', 'HKLM:\Software\WOW6432Node'))) {
    throw 'An existing Uninstaller 77 registration was found. Use a clean disposable profile.'
}
if (Get-Process Uninstaller77,WinUninstaller -ErrorAction SilentlyContinue) {
    throw 'An Uninstaller 77 process is active. Use an idle disposable profile.'
}

$runDirectory = Join-Path $env:LOCALAPPDATA ('U77InstallerAcceptance\' + [Guid]::NewGuid().ToString('N'))
$installDirectory = Join-Path $runDirectory 'Installed'
$programShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Uninstaller 77.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Uninstaller 77.lnk'
foreach ($shortcutDirectory in @((Split-Path -Parent $programShortcut), (Split-Path -Parent $desktopShortcut))) {
    foreach ($name in 'Uninstaller 77.lnk','Uninstaller77.lnk','WinUninstaller.lnk') {
        if (Test-Path -LiteralPath (Join-Path $shortcutDirectory $name)) {
            throw 'An existing product shortcut was found. Use a clean disposable profile.'
        }
    }
}

New-Item -ItemType Directory -Path $runDirectory | Out-Null
$profileDirectory = Join-Path $env:LOCALAPPDATA 'WinUninstaller'
$webViewLog = Join-Path $profileDirectory 'Logs\webview.log'
$evidence = [ordered]@{
    version = $ExpectedVersion
    sha256 = $ExpectedSHA256
    os = [Environment]::OSVersion.Version.ToString()
    webView2Before = [ordered]@{
        machine = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -Name pv -ErrorAction SilentlyContinue).pv
        user = (Get-ItemProperty -LiteralPath 'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -Name pv -ErrorAction SilentlyContinue).pv
    }
    scenario = if ($Baseline) { 'upgrade' } else { 'fresh' }
    completed = @()
    failure = $null
}

function Invoke-OwnedSetup([string]$Path, [string]$Log, [string[]]$Options) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + $Log + '"')) + $Options
    $process = Start-Process -FilePath $Path -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        if (!$process.WaitForExit(240000)) { throw "Setup timed out. See $Log" }
        if ($process.ExitCode -ne 0) { throw "Setup returned $($process.ExitCode). See $Log" }
    } finally { $process.Dispose() }
}

function Assert-InstalledVersion([string]$Version) {
    $entry = Get-ItemProperty -LiteralPath $registryKey
    $installedExe = Join-Path $installDirectory 'Uninstaller77.exe'
    if ($entry.DisplayVersion -ne $Version -or
        $entry.InstallLocation.TrimEnd('\') -ne $installDirectory -or
        (Read-NumericFileVersion $installedExe) -ne [version]"$Version.0") {
        throw "Unexpected installed path or version for $Version."
    }
}

function Assert-SignedByExpectedPublisher([string]$Path, [string]$Publisher) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode' -or
        !$signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -ne $Publisher) {
        throw "The artifact does not have the expected trusted signature and timestamp: $Path"
    }
}

$sentinelName = 'installer-acceptance-' + [Guid]::NewGuid().ToString('N') + '.txt'
$sentinels = foreach ($folder in 'Logs','Quarantine') {
    $directory = Join-Path $profileDirectory $folder
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $path = Join-Path $directory $sentinelName
    [IO.File]::WriteAllText($path, $sentinelName)
    $path
}

function Assert-Sentinels {
    foreach ($path in $sentinels) {
        if (!(Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path) -ne $sentinelName) {
            throw 'The installation lifecycle changed the profile test data.'
        }
    }
}

try {
    if ($ExpectedSigningPublisher) { Assert-SignedByExpectedPublisher $candidatePath $ExpectedSigningPublisher }
    $destination = '/DIR="' + $installDirectory + '"'
    if ($Baseline) {
        Invoke-OwnedSetup $baselinePath (Join-Path $runDirectory 'baseline.log') @($destination, '/TASKS=desktopicon')
        Assert-InstalledVersion ($baselineVersion.ToString(3))
        $evidence.completed += 'Baseline installation'
    }
    $installOptions = @('/TASKS=desktopicon')
    if (!$Baseline) { $installOptions += $destination }
    Invoke-OwnedSetup $candidatePath (Join-Path $runDirectory 'candidate.log') $installOptions
    Assert-InstalledVersion $ExpectedVersion
    Assert-Sentinels
    $evidence.completed += 'Candidate installation and data retention'

    $shell = New-Object -ComObject WScript.Shell
    $installedExe = Join-Path $installDirectory 'Uninstaller77.exe'
    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    $manifest = Get-Content -LiteralPath (Join-Path $installDirectory 'release-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.signed -or $ExpectedSigningPublisher) {
        if (!$ExpectedSigningPublisher -or !$manifest.signed -or $manifest.signingPublisher -ne $ExpectedSigningPublisher) {
            throw 'Pass the expected signing publisher for a signed candidate and verify it matches the packaged manifest.'
        }
        foreach ($artifact in @($installedExe, (Join-Path $installDirectory 'Uninstaller77.dll'), $uninstaller)) {
            Assert-SignedByExpectedPublisher $artifact $ExpectedSigningPublisher
        }
        $evidence.completed += 'Trusted timestamped signatures on Setup, application, assembly and installed uninstaller'
    }
    foreach ($shortcut in $programShortcut,$desktopShortcut) {
        if (!(Test-Path -LiteralPath $shortcut) -or $shell.CreateShortcut($shortcut).TargetPath -ne $installedExe) {
            throw "Incorrect shortcut: $shortcut"
        }
    }
    $evidence.completed += 'Start menu and desktop shortcut targets'

    $priorLog = if (Test-Path -LiteralPath $webViewLog) { [IO.File]::ReadAllText($webViewLog) } else { '' }
    Start-Process -FilePath $programShortcut
    $ready = $false
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        Start-Sleep -Milliseconds 500
        if (!(Test-Path -LiteralPath $webViewLog)) { continue }
        $newLog = [IO.File]::ReadAllText($webViewLog)
        if (!$newLog.StartsWith($priorLog)) { throw 'The existing WebView2 log changed during the test.' }
        $newLog = $newLog.Substring($priorLog.Length)
        if ($newLog.Contains("ProcessPath=$installedExe;") -and
            $newLog.Contains('WebView2 navigation completed; success=True') -and
            $newLog.Contains('UI request received: op=getState') -and
            ([version]$ExpectedVersion -lt [version]'1.0.3' -or $newLog.Contains('React desktop bridge ready.'))) {
            $ready = $true
            break
        }
    }
    if (!$ready) { throw 'The installed WebView2/React interface did not become ready.' }
    $evidence.completed += 'Launch from Start menu and WebView2/React readiness'
    $running = @(Get-Process Uninstaller77 -ErrorAction SilentlyContinue | Where-Object Path -EQ $installedExe)
    if ($running.Count -ne 1 -or !$running[0].CloseMainWindow() -or !$running[0].WaitForExit(15000)) {
        throw 'The owned test instance did not close gracefully.'
    }

    Assert-InstalledVersion $ExpectedVersion
    Invoke-OwnedSetup $uninstaller (Join-Path $runDirectory 'uninstall.log') @()
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if (!(Test-Path -LiteralPath $registryKey) -and !(Test-Path -LiteralPath $installedExe)) { break }
        Start-Sleep -Milliseconds 500
    }
    if (Test-Path -LiteralPath $registryKey) { throw 'Product registration remains after uninstall.' }
    if (Test-Path -LiteralPath $installedExe) { throw 'Product executable remains after uninstall.' }
    Assert-Sentinels
    $evidence.completed += 'Product uninstall and profile data retention'
} catch {
    $evidence.failure = $_.Exception.Message
    throw
} finally {
    $resultPath = Join-Path $runDirectory 'result.json'
    $evidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resultPath -Encoding UTF8
    if ($EvidenceDirectory) {
        try {
            $evidenceRun = Join-Path $EvidenceDirectory (Split-Path -Leaf $runDirectory)
            New-Item -ItemType Directory -Path $evidenceRun | Out-Null
            Copy-Item -LiteralPath $resultPath -Destination $evidenceRun
            Get-ChildItem -LiteralPath $runDirectory -Filter '*.log' -File |
                Copy-Item -Destination $evidenceRun
        } catch {
            Write-Warning 'Evidence export failed. The guest-local result is still available.'
        }
    }
    Write-Host "Acceptance evidence: $runDirectory"
}
