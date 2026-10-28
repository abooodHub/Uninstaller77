# Validation scope — 1.0.1

The Windows desktop-user regression run recorded on 2026-10-28 passed **174 engine tests, 0 failures**. It exercised live engine sources with uniquely owned Registry/filesystem fixtures and actual child executables. No real user application or extension was removed.

| Other executed local checks | Passed | Failed |
| --- | ---: | ---: |
| React presentation | 42 | 0 |
| Arabic / English component rendering (SSR) | 79 | 0 |
| Product integrity and native SQLite | 28 | 0 |
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

## Product website and gallery update

The 1.0.1 check rerun passed 174 engine tests, 42 React tests, 79 SSR contracts, 28 product/SQLite checks, 45 package/image checks and 18 installer/release identity checks. The 7 Windows installer-preflight primitive checks passed; this is not native Setup end-to-end acceptance.

The new website action accepts no payload properties. It opens only the fixed HTTPS product address using the default browser, without executable paths, launch arguments or elevation. Product-integrity tests reject supplied URLs, paths, arguments and duplicate URL fields, and confirm that the public website is not a trusted WebView command source. The new Settings button is covered by Arabic/English rendering checks. A live native click of the button was not automated because native application control was unavailable under product policy.

The website gallery uses eight original saved captures. Older builds and fixture previews are explicitly identified; these are not claimed as new screenshots of every v1.0.1 screen. Gallery image decoding, filtering, keyboard navigation and 390px mobile RTL/LTR layout were checked in a browser. The supplied YouTube video was observed playing through the click-to-load embedded player during local preview.
