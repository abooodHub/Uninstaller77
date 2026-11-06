# Build from source

Use Windows x64, the .NET 10 SDK, Node.js 24, and npm. The 1.0.3 candidate bundles the .NET Desktop Runtime and the Microsoft WebView2 Evergreen Standalone x64 installer. Run commands from the checkout root.

## Application and installer

Install Inno Setup 6.7.3 or newer from its official publisher. Compiler binaries are excluded from Git. Set the path to your installed compiler if it is not already on PATH:

```powershell
$env:INNO_SETUP_COMPILER = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
New-Item -ItemType Directory -Force '_build\installer-prerequisites' | Out-Null
curl.exe --fail --location --proto '=https' --proto-redir '=https' 'https://go.microsoft.com/fwlink/?linkid=2124701' --output '_build\installer-prerequisites\MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
if ($LASTEXITCODE) { throw 'WebView2 download failed' }
.\Build.ps1
```

Existing local environments also recognize `tools/InnoSetup/ISCC.exe`. NuGet versions are centralized in `Directory.Packages.props`; each build project has a committed `packages.lock.json`, while npm uses only `package-lock.json`. CI restores NuGet in locked mode and audits all transitive NuGet/npm dependencies, failing on High or Critical findings. The release script restores npm dependencies from the lockfile when missing, builds the interface, publishes a fresh Windows x64 payload and compiles the installer.

The build verifies Microsoft's signature on the standalone installer. You can supply a predownloaded copy with `-WebView2Installer` on `installer/Build-Release.ps1`. Setup requires WebView2 Runtime 156.0.4314.42 or newer; an older or missing registration runs the bundled Evergreen installer and is rechecked before payload replacement. Refresh the bundled .NET patch and WebView2 installer when preparing a new public release. The candidate targets native x64 Windows. ARM64 requires separate validation and packaging.

Outputs:

- `dist/Uninstaller77-Setup.exe`: installer.
- `dist/Uninstaller77-Setup.sha256`: installer SHA256.
- `dist/app/Uninstaller77.exe`: complete runtime package with adjacent DLLs and `wwwroot`.
- `dist/app/bom.cdx.json`: CycloneDX 1.5 SBOM for locked NuGet/npm and bundled runtime prerequisites; its SHA-256 is recorded in `release-manifest.json`.

Close a running copy before replacing the payload. Do not copy a standalone EXE without its adjacent files. For an isolated candidate use the `PayloadDir` and `OutputDir` parameters on `installer/Build-Release.ps1` under `dist` or `_build`. Replacing an existing payload requires its release manifest and matching application and UI hashes.

## Signed distribution build

Pass a signing-provider script and the exact Authenticode certificate subject. The provider receives one argument, `-Path`, and must sign that file in place with a trusted Authenticode certificate and timestamp. Keep credentials in the provider or its secret store. The repository and staged signing configuration require no private key or password.

```powershell
.\Build.ps1 -SignerScript 'C:\Signing\Sign-Uninstaller77.ps1' -SigningPublisher 'CN=Your verified publisher'
```

For a certificate available in the Windows personal certificate store, put Windows SDK SignTool on PATH and use the included provider. Set the exact certificate thumbprint and your provider's RFC 3161 endpoint.

```powershell
$env:U77_SIGNING_THUMBPRINT = 'Your certificate thumbprint'
$env:U77_TIMESTAMP_URL = 'Your RFC 3161 timestamp URL'
$publisher = (Get-Item "Cert:\CurrentUser\My\$env:U77_SIGNING_THUMBPRINT").Subject
.\Build.ps1 -SignerScript '.\installer\Sign-WithWindowsCertificate.ps1' -SigningPublisher $publisher
```

The build signs the application and its assembly before calculating payload hashes. Inno signs Setup, its temporary executables and the uninstaller during compilation. The script verifies the final Setup before generating its checksum or promoting any output. Distribution signing rejects self-issued certificates. A build without signing arguments remains unsigned. Run signed VM acceptance with `-ExpectedSigningPublisher` to check the installed files and uninstaller on a clean guest.

## Web interface only

```powershell
cd app/WinUninstaller/WebUI
npm ci
npm test
npm run build
node tests/render-audit.mjs
```

The React UI uses the version declared in the C# project. The public assembly is `Uninstaller77`; internal namespaces and the existing AppData identity remain `WinUninstaller` for compatibility.

## Product website

`docs/site/` is buildless HTML/CSS/JavaScript with local images. Local preview:

```powershell
python -m http.server 8787 --bind 127.0.0.1 --directory docs/site
```

The Pages workflow uploads only that directory. It does not publish source, local logs or build folders as website files.
