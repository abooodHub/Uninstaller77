param([Parameter(Mandatory)] [string]$PayloadDirectory)

$ErrorActionPreference = 'Stop'
$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$workspace = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'ProductIntegrity.Tests/ProductIntegrity.Tests.csproj'
& dotnet build $project -c Release -p:SkipLocalWebUiBuild=true
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$root = Join-Path $workspace ('_build/native-protection-ui-' + [guid]::NewGuid().ToString('N'))
$runner = Join-Path $root 'runner'
[IO.Directory]::CreateDirectory($runner) | Out-Null
$testOutput = Join-Path $PSScriptRoot 'ProductIntegrity.Tests/bin/Release/net10.0-windows'
Get-ChildItem -LiteralPath $testOutput | Copy-Item -Destination $runner -Recurse
$shippedAssembly = Join-Path $payload 'Uninstaller77.dll'
$runnerAssembly = Join-Path $runner 'Uninstaller77.dll'
Copy-Item -LiteralPath $shippedAssembly -Destination $runnerAssembly -Force
Copy-Item -LiteralPath (Join-Path $payload 'wwwroot') -Destination $runner -Recurse
foreach ($locale in 'ar','en') {
    $relative = "Resources/Strings.$locale.json"
    Copy-Item -LiteralPath (Join-Path $payload $relative) -Destination (Join-Path $runner $relative) -Force
}
if ((Get-FileHash -LiteralPath $shippedAssembly).Hash -ne (Get-FileHash -LiteralPath $runnerAssembly).Hash) {
    throw 'The staged test did not preserve the shipped assembly.'
}

& dotnet (Join-Path $runner 'ProductIntegrity.Tests.dll') --native-protection-ui $root
exit $LASTEXITCODE
