<p align="center"><img src="docs/site/assets/logo.png" width="112" height="112" alt="Uninstaller 77"></p>
<h1 align="center">Uninstaller 77</h1>
<p align="center">Windows application management, with a clear record of every uninstall attempt.</p>
<p align="center"><b>1.0.0 Â· Windows 10 / 11 x64 Â· Arabic & English Â· MIT</b></p>
<p align="center"><a href="https://github.com/abooodHub/Uninstaller77/releases/download/v1.0.0/Uninstaller77-Setup.exe">Download installer</a> Â· <a href="https://abooodhub.github.io/Uninstaller77/">Product website</a> Â· <a href="README.ar.md">Ø§Ù„Ø¹Ø±Ø¨ÙŠØ©</a></p>

## Features

- **Application inventory:** search and sort Registry programs and current-user MSIX/AppX packages, with available publisher, version, size and uninstall-command information.
- **Sequential queues:** process the applications you selected one at a time, with elapsed time, current stage and separate successful, failed and skipped counts.
- **Per-app results:** retain observed exit codes and failure diagnostics on the correct application, including after a later item completes.
- **Reviewed cleanup:** inspect supported leftovers after verified removal; quarantine eligible reviewed folders and restore them using recovery manifests.
- **Browser inventory:** inspect Chrome, Edge and Brave extensions by browser and profile, with permissions and detected management policies. This feature is read-only.
- **Desktop workflow:** Arabic RTL / English LTR, CSV export, local history, and an Explorer context-menu action for application shortcuts.

<p align="center"><img src="docs/site/assets/history.png" width="1000" alt="Arabic history interface showing uninstall outcomes"></p>

*User-supplied screenshot from a test session. The history keeps earlier attempts, including failures, after an update.*

## Install

1. Install **.NET 8 Desktop Runtime (x64)** and **Microsoft Edge WebView2 Runtime** if they are missing.
2. Download [Uninstaller77-Setup.exe](https://github.com/abooodHub/Uninstaller77/releases/download/v1.0.0/Uninstaller77-Setup.exe) from the release.
3. Close any running copy before upgrading, then run Setup. The installer is English; choose Arabic or English in the app.

Setup checks prerequisites before installing application files. They are not bundled. Some vendor uninstallers require a Windows UAC prompt or their own confirmation window.

Check the release asset against [SHA256SUMS.txt](https://github.com/abooodHub/Uninstaller77/releases/download/v1.0.0/SHA256SUMS.txt):

```powershell
Get-FileHash .\Uninstaller77-Setup.exe -Algorithm SHA256
```

## How uninstall results work

The app launches the registered vendor uninstaller and checks removal evidence. A queue reaching **100%** means its items have been processed; it does not mean every application was removed. A stop request prevents the next item from starting after the current operation; it does not force-kill the vendor uninstaller.

The v1.0.0 engine preserves EXE uninstall commands registered under GUID-shaped keys, including the observed Google Drive layout. Third-party uninstallers can still reject an operation or require user input. Protected Windows packages and unsupported cleanup locations are not promised to be removable.

## Build and verify

See [BUILDING.md](docs/BUILDING.md) for a clean checkout and [TESTING.md](docs/TESTING.md) for validation scope.

```powershell
.\Build.ps1

dotnet run --project .\tests\Engine.Tests\Engine.Tests.csproj -c Release -- --report-dir .\_build\engine-tests
```

The application uses WPF, WebView2 and a bundled React/CSS interface. The static product page is in `docs/site/`. Local logs, databases, installers, dependency caches and historical evidence are excluded from Git.

## License and contributions

[MIT](LICENSE). Third-party components and browser marks retain their own terms; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Contributions should follow [CONTRIBUTING.md](CONTRIBUTING.md). Please redact personal paths and logs before opening an issue.
