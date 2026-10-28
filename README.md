<p align="center"><img src="docs/site/assets/logo.png" width="112" height="112" alt="Uninstaller 77"></p>
<h1 align="center">Uninstaller 77</h1>
<p align="center">Windows application management, with a clear record of every uninstall attempt.</p>
<p align="center"><b>1.0.1 Â· Windows 10 / 11 x64 Â· Arabic & English Â· MIT</b></p>
<p align="center"><a href="https://github.com/abooodHub/Uninstaller77/releases/download/v1.0.1/Uninstaller77-Setup.exe">Download installer</a> Â· <a href="https://abooodhub.github.io/Uninstaller77/">Product website</a> Â· <a href="README.ar.md">Ø§Ù„Ø¹Ø±Ø¨ÙŠØ©</a></p>

## Features

- **Application inventory:** search and sort Registry programs and current-user MSIX/AppX packages, with available publisher, version, size and uninstall-command information.
- **Sequential queues:** process the applications you selected one at a time, with elapsed time, current stage and separate successful, failed and skipped counts.
- **Per-app results:** retain observed exit codes and failure diagnostics on the correct application, including after a later item completes.
- **Reviewed cleanup:** inspect supported leftovers after verified removal; quarantine eligible reviewed folders and restore them using recovery manifests.
- **Browser inventory:** inspect Chrome, Edge and Brave extensions by browser and profile, with permissions and detected management policies. This feature is read-only.
- **Desktop workflow:** Arabic RTL / English LTR, CSV export, local history, and an Explorer context-menu action for application shortcuts.

[▶ Watch the product video](https://youtu.be/bUDDOJ_9fTo)

## Screenshots

[Explore the interactive gallery: 8 captures with filters and full-size viewing](https://abooodhub.github.io/Uninstaller77/#gallery)

<table>
<tr>
<td width="50%"><a href="docs/site/assets/programs-ar.jpg"><img src="docs/site/assets/programs-ar.jpg" width="480" alt="Programs · development capture"></a><br>Programs · development capture</td>
<td width="50%"><a href="docs/site/assets/windows-apps.png"><img src="docs/site/assets/windows-apps.png" width="480" alt="Windows packages · development capture"></a><br>Windows packages · development capture</td>
</tr>
<tr>
<td width="50%"><a href="docs/site/assets/extensions.png"><img src="docs/site/assets/extensions.png" width="480" alt="Extensions · development capture"></a><br>Extensions · development capture</td>
<td width="50%"><a href="docs/site/assets/queue.png"><img src="docs/site/assets/queue.png" width="480" alt="Queue · fixture preview"></a><br>Queue · fixture preview</td>
</tr>
<tr>
<td width="50%"><a href="docs/site/assets/results.png"><img src="docs/site/assets/results.png" width="480" alt="Results · fixture preview"></a><br>Results · fixture preview</td>
<td width="50%"><a href="docs/site/assets/history.png"><img src="docs/site/assets/history.png" width="480" alt="History · user test session"></a><br>History · user test session</td>
</tr>
</table>

*Original development and test captures. Some show earlier navigation or controls that were later removed; current browser inventory is read-only. Queue and result previews use owned fixtures. The gallery identifies the context of each capture.*

## Install

1. Install **.NET 8 Desktop Runtime (x64)** and **Microsoft Edge WebView2 Runtime** if they are missing.
2. Download [Uninstaller77-Setup.exe](https://github.com/abooodHub/Uninstaller77/releases/download/v1.0.1/Uninstaller77-Setup.exe) from the release.
3. Close any running copy before upgrading, then run Setup. The installer is English; choose Arabic or English in the app.

Setup checks prerequisites before installing application files. They are not bundled. Some vendor uninstallers require a Windows UAC prompt or their own confirmation window.

Check the release asset against [SHA256SUMS.txt](https://github.com/abooodHub/Uninstaller77/releases/download/v1.0.1/SHA256SUMS.txt):

```powershell
Get-FileHash .\Uninstaller77-Setup.exe -Algorithm SHA256
```

## How uninstall results work

The app launches the registered vendor uninstaller and checks removal evidence. A queue reaching **100%** means its items have been processed; it does not mean every application was removed. A stop request prevents the next item from starting after the current operation; it does not force-kill the vendor uninstaller.

The v1.0.1 engine preserves EXE uninstall commands registered under GUID-shaped keys, including the observed Google Drive layout. Third-party uninstallers can still reject an operation or require user input. Protected Windows packages and unsupported cleanup locations are not promised to be removable.

## Build and verify

See [BUILDING.md](docs/BUILDING.md) for a clean checkout and [TESTING.md](docs/TESTING.md) for validation scope.

```powershell
.\Build.ps1

dotnet run --project .\tests\Engine.Tests\Engine.Tests.csproj -c Release -- --report-dir .\_build\engine-tests
```

The application uses WPF, WebView2 and a bundled React/CSS interface. The static product page is in `docs/site/`. Local logs, databases, installers, dependency caches and historical evidence are excluded from Git.

## License and contributions

[MIT](LICENSE). Third-party components and browser marks retain their own terms; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Contributions should follow [CONTRIBUTING.md](CONTRIBUTING.md). Please redact personal paths and logs before opening an issue.
