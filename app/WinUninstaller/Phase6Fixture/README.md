# Isolated Phase 6 fixture

`Phase6Fixture.exe --install <32-hex-GUID> success|failure` installs a new per-user test app and HKCU uninstall entry under `%LOCALAPPDATA%/WinUninstallerPhase6/<GUID>`. Use a fresh GUID for every installation. The registered `uninstall.exe --uninstall <GUID> success|failure` is a real independent executable: `success` removes its registration and app executable but deliberately leaves its installation directory for residual review; `failure` logs and returns 1603 without removing either.

Build with `python make_icon.py`, `dotnet restore Phase6Fixture.csproj --ignore-failed-sources -p:NuGetAudit=false`, then `dotnet build Phase6Fixture.csproj -c Release --no-restore`. The machine needs the .NET 8 Windows Desktop Runtime. The installer copies the apphost with its original `Phase6Fixture.dll`, `.deps.json` and `.runtimeconfig.json` support files.

`--cleanup <GUID> failure` removes only that fixture's HKCU entry and App directory. Do not run it on any other registration. Its log remains in the GUID root for inspection. This fixture does not require Administrator rights or modify services.
