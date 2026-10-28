# One inventory drives both [Files] and the preflight. Unrelated files already
# in the destination (including a copied, running Setup) are never probed.
function New-InstallerPayloadInventory([string]$PayloadDirectory, [string]$GeneratedDirectory) {
    $root = [IO.Path]::GetFullPath($PayloadDirectory).TrimEnd('\')
    $prefix = $root + '\'
    if (!(Test-Path -LiteralPath $root -PathType Container)) { throw 'Missing installer payload directory.' }
    $items = @((Get-Item -LiteralPath $root -Force)) + @(Get-ChildItem -LiteralPath $root -Recurse -Force)
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Installer inventory refuses reparse points.' }
    $paths = @(foreach ($file in $items | Where-Object { !$_.PSIsContainer } | Sort-Object FullName) {
        $relative = $file.FullName.Substring($prefix.Length)
        if ($relative -match '(^|\\)WebView2\\' -or $file.Name -match '(?i)\.(pdb|log|db|db-shm|db-wal)$') { continue }
        # These values are emitted into Pascal string literals and Inno entries.
        if ($relative -match '[\r\n"{}]' -or [IO.Path]::IsPathRooted($relative) -or $relative -match '(^|\\)\.\.?(\\|$)') { throw "Unsupported payload path: $relative" }
        $relative
    })
    if (!$paths.Count -or $paths -notcontains 'Uninstaller77.exe') { throw 'Installer inventory must include the application EXE.' }
    New-Item -ItemType Directory -Path $GeneratedDirectory -Force | Out-Null
    $installLines = foreach ($relative in $paths) {
        $parent = Split-Path -Parent $relative
        $destination = if ($parent) { '{app}\' + $parent } else { '{app}' }
        'Source: "{#PayloadDir}\' + $relative + '"; DestDir: "' + $destination + '"; Flags: ignoreversion'
    }
    $preflightLines = foreach ($relative in $paths) { "  Files.Add('" + $relative.Replace("'", "''") + "');" }
    $install = Join-Path $GeneratedDirectory 'payload-install-files.iss'
    $preflight = Join-Path $GeneratedDirectory 'payload-preflight-files.iss'
    $installLines | Set-Content -LiteralPath $install -Encoding UTF8
    $preflightLines | Set-Content -LiteralPath $preflight -Encoding UTF8
    return [pscustomobject]@{ Paths=$paths; InstallInclude=$install; PreflightInclude=$preflight }
}
