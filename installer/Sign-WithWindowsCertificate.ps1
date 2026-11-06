param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$thumbprint = $env:U77_SIGNING_THUMBPRINT
if ($thumbprint -notmatch '^[a-fA-F0-9]{40}$') {
    throw 'Set U77_SIGNING_THUMBPRINT to the code signing certificate thumbprint in Cert:\CurrentUser\My.'
}
$timestamp = $null
if (![Uri]::TryCreate($env:U77_TIMESTAMP_URL, [UriKind]::Absolute, [ref]$timestamp) -or
    $timestamp.Scheme -notin 'http', 'https' -or $timestamp.UserInfo) {
    throw 'Set U77_TIMESTAMP_URL to the signing provider RFC 3161 timestamp endpoint.'
}
$signTool = (Get-Command signtool.exe -CommandType Application -ErrorAction Stop).Source
$file = Get-Item -LiteralPath $Path
if ($file.PSIsContainer) { throw 'The signing path must be a file.' }
& $signTool sign /sha1 $thumbprint /fd SHA256 /tr $timestamp.AbsoluteUri /td SHA256 $file.FullName
exit $LASTEXITCODE
