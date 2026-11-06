$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root ('_build\sbom-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    $webView = Join-Path $work 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
    [IO.File]::WriteAllBytes($webView, [Text.Encoding]::UTF8.GetBytes('owned SBOM fixture'))
    $hash = (Get-FileHash -LiteralPath $webView -Algorithm SHA256).Hash
    $output = Join-Path $work 'bom.cdx.json'
    & (Join-Path $root 'tools\New-ReleaseSbom.ps1') -Version 1.0.3 -OutputPath $output `
        -NuGetLock (Join-Path $root 'app\WinUninstaller\packages.lock.json') `
        -NpmLock (Join-Path $root 'app\WinUninstaller\WebUI\package-lock.json') `
        -DesktopRuntimeVersion 10.0.14 -WebView2RuntimeVersion 156.0.4314.42 -WebView2SHA256 $hash | Out-Null
    $bom = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    if ($bom.bomFormat -ne 'CycloneDX' -or $bom.specVersion -ne '1.5' -or $bom.metadata.component.version -ne '1.0.3') { throw 'Invalid SBOM envelope.' }
    $components = @($bom.components)
    foreach ($expected in @(
        @{Name='Microsoft.Data.Sqlite'; Version='10.0.14'; Algorithm='SHA-512'},
        @{Name='lucide-react'; Version='1.68.0'; Algorithm='SHA-512'},
        @{Name='Microsoft.NETCore.App'; Version='10.0.14'; Algorithm=''},
        @{Name='Microsoft Edge WebView2 Runtime'; Version='156.0.4314.42'; Algorithm='SHA-256'}
    )) {
        $component = $components | Where-Object { $_.name -eq $expected.Name -and $_.version -eq $expected.Version } | Select-Object -First 1
        if (!$component -or ($expected.Algorithm -and !($component.hashes | Where-Object alg -eq $expected.Algorithm))) {
            throw "SBOM omitted or failed to hash $($expected.Name) $($expected.Version)."
        }
    }
    if (@($components.purl | Group-Object | Where-Object Count -gt 1).Count) { throw 'SBOM contains duplicate package identities.' }
    Write-Output "PASS CycloneDX SBOM contains $($components.Count) unique locked NuGet/npm/runtime components with dependency integrity hashes."
} finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
