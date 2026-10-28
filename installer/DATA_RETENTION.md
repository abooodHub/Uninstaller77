# Uninstaller 77 installation and data retention

The per-user installer uses a stable AppId and installs application files under
`%LocalAppData%\Programs\Uninstaller 77`. The user may choose another location.
Upgrades reuse the previous installation directory and shortcut selection.

Existing persisted data remains under `%LocalAppData%\WinUninstaller`; this internal
storage name and C# assembly identity are deliberately retained for compatibility.
Setup and Uninstall do not delete this directory, SQLite, history, logs, settings,
cached application icons, queue journals or quarantined files. There is no automatic
cleanup task. A separate, explicit data-removal policy would be required to erase it.

The setup requires .NET 8 Desktop Runtime x64 and WebView2 and checks their installed
files before copying payload files. It does not change runtimes or Windows services.
Installer failure logs are created by Inno Setup in the Windows TEMP directory;
test invocations additionally pass a specific guest-local `/LOG=` destination.

The wizard step indicator reflects the actual setup page. It is decorative navigation
feedback and is separate from Inno Setup's genuine file-copy progress. Its motion
respects Windows client animation preferences and the existing app Reduce Motion setting.
The output is unsigned unless a signing step with a valid certificate is added.
