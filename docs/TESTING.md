# Validation scope — 1.0.0

The Windows desktop-user regression run recorded on 2026-10-28 passed **174 engine tests, 0 failures**. It exercised live engine sources with uniquely owned Registry/filesystem fixtures and actual child executables. No real user application or extension was removed.

| Other executed local checks | Passed | Failed |
| --- | ---: | ---: |
| React presentation | 42 | 0 |
| Arabic / English component rendering (SSR) | 77 | 0 |
| Product integrity and native SQLite | 21 | 0 |
| Package and image integrity | 45 | 0 |
| Assembly / installer identity | 18 | 0 |

The full runner includes Windows job/broker tracking, browser handoff fixtures, shortcut identity, MSI/EXE classification, per-item diagnostics, stop/review behavior and isolated quarantine/restore tests. Preserved historical Software Health and Install Monitor test sources are linked by the test project; those features are excluded from the shipping assembly and UI.

```powershell
dotnet run --project tests/Engine.Tests/Engine.Tests.csproj -c Release -- --report-dir _build/engine-tests

dotnet run --project tests/ProductIntegrity.Tests/ProductIntegrity.Tests.csproj -c Release -p:SkipLocalWebUiBuild=true -p:NuGetAudit=false
```

Use the authorized ordinary Windows desktop-user context. Do not substitute sandbox restrictions with passes. The default full suite does not request Administrator/UAC acceptance. It reads inventory, but destructive fixtures are restricted to their unique identities and folders. Real-browser tests use separate temporary profiles.

## CI and practical limits

CI runs React/SSR contracts, builds the Windows app and engine runner, and executes product-integrity checks. Compiling the full runner is not described as executing its desktop/process tests on the hosted runner.

SSR is not a native visual/resize test. The desktop app was launched and WebView2/React startup was observed; a complete Windows VM installation/UAC acceptance matrix was not independently executed. User-reported VM checks are separate from recorded fixtures. Third-party uninstallers can reject removal, require interaction or leave a restart pending.

Detailed local logs and user-specific historical evidence are excluded from public Git. Do not upload raw diagnostics or SQLite data in issues.
