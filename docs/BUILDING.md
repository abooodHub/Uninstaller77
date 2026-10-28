# Build from source

Use Windows x64, the .NET 8 SDK, Node.js 24, and npm. The installed app also requires .NET 8 Desktop Runtime (x64) and Microsoft Edge WebView2 Runtime. Run commands from the checkout root.

## Application and installer

Install Inno Setup 6.7.3 or newer from its official publisher. Compiler binaries are excluded from Git. Set the path to your installed compiler if it is not already on PATH:

```powershell
$env:INNO_SETUP_COMPILER = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
.\Build.ps1
```

Existing local environments also recognize `tools/InnoSetup/ISCC.exe`. The script restores npm dependencies from the lockfile when missing, builds the interface, publishes a fresh Windows x64 payload and compiles the installer.

Outputs:

- `dist/Uninstaller77-Setup.exe`: installer.
- `dist/Uninstaller77-Setup.sha256`: installer SHA256.
- `dist/app/Uninstaller77.exe`: complete runtime package with adjacent DLLs and `wwwroot`.

Close a running copy before replacing the payload. Do not copy a standalone EXE without its adjacent files. For an isolated candidate use the `PayloadDir` and `OutputDir` parameters on `installer/Build-Release.ps1`.

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
