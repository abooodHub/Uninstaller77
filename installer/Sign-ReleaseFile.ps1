param(
    [Parameter(Mandatory)][string]$ConfigPath,
    [Parameter(Mandatory)][string]$Path,
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$file = Get-Item -LiteralPath $Path
if ($file.PSIsContainer -or [string]::IsNullOrWhiteSpace($config.publisher)) {
    throw 'Signing requires a file and an explicit publisher subject.'
}

if (!$VerifyOnly) {
    # Run the provider in its own process so an exit in the adapter cannot skip verification.
    $powershell = (Get-Process -Id $PID).Path
    & $powershell -NoProfile -NonInteractive -File $config.signerScript -Path $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Signing provider failed for $($file.Name), exit $LASTEXITCODE." }
}

$signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode') {
    throw "Expected a valid embedded Authenticode signature for $($file.Name). Status: $($signature.Status)."
}
if ($signature.SignerCertificate.Subject -eq $signature.SignerCertificate.Issuer) {
    throw "A self-issued certificate cannot sign a distribution build: $($file.Name)."
}
if (![string]::Equals($signature.SignerCertificate.Subject, $config.publisher, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unexpected signing publisher for $($file.Name)."
}
if ($null -eq $signature.TimeStamperCertificate) {
    throw "Missing trusted timestamp on $($file.Name)."
}
Write-Output "Verified Authenticode signature and timestamp: $($file.Name)"
