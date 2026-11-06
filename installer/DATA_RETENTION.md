# Uninstaller 77 installation and data retention

The per-user installer uses a stable AppId and installs application files under
`%LocalAppData%\Programs\Uninstaller 77`. The user may choose another location.
Upgrades reuse the previous installation directory and shortcut selection.

Existing persisted data remains under `%LocalAppData%\WinUninstaller`; this internal
storage name and C# assembly identity are deliberately retained for compatibility.
Setup and Uninstall do not delete this directory, SQLite, history, logs, settings,
cached application icons, queue journals or quarantined files. There is no automatic
cleanup task. A separate, explicit data-removal policy would be required to erase it.

The 1.0.3 candidate carries the .NET 10 Desktop Runtime x64 in the application payload
and bundles the WebView2 Evergreen Standalone x64 prerequisite. Setup verifies the
prerequisite and payload before copying application files. It does not replace the
system .NET runtime or change Windows services.
Installer failure logs are created by Inno Setup in the Windows TEMP directory;
test invocations additionally pass a specific guest-local `/LOG=` destination.

The wizard step indicator reflects the actual setup page. It is decorative navigation
feedback and is separate from Inno Setup's genuine file-copy progress. Its motion
respects Windows client animation preferences and the existing app Reduce Motion setting.
The output is unsigned unless a signing step with a valid certificate is added.
