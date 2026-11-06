# Validation scope — 1.0.3 candidate

The Windows desktop-user regression run recorded on 2026-11-06 passed **194 engine tests, 0 failures**. It exercised live engine sources with uniquely owned Registry/filesystem fixtures and actual child executables. No real user application or extension was removed.

| Other executed local checks | Passed | Failed |
| --- | ---: | ---: |
| React presentation | 42 | 0 |
| Arabic / English component rendering (SSR) | 92 | 0 |
| Product integrity and native SQLite | 39 | 0 |
| Control Center protocol and reporting | 38 | 0 |
| Package and image integrity | 50 | 0 |
| Assembly / installer identity and manifest hashes | 25 | 0 |
| Installer payload and WebView2 preflight | 13 | 0 |
| Release destination safety | 6 | 0 |
| Project cleanup safety | 17 | 0 |
| Release signing policy | 5 | 0 |
| CycloneDX SBOM generation | 1 | 0 |

The full runner includes Windows job/broker tracking, browser handoff fixtures, shortcut identity, MSI/EXE classification, per-item diagnostics, stop/review behavior and isolated quarantine/restore tests. Preserved historical Software Health and Install Monitor test sources are linked by the test project; those features are excluded from the shipping assembly and UI.

Locked restores passed for the application, engine, ProductIntegrity, and Control Center projects after upgrading to `Microsoft.Data.Sqlite` 10.0.14. NuGet application/engine scans and both production-only and complete npm audits reported no known vulnerabilities. The SBOM contract produced 83 unique components with dependency integrity hashes.

The isolated unsigned candidate at `_build/candidate-20261106` was freshly published and compiled without replacing the canonical `dist` installer. Its Release identity, executable/UI/SBOM manifest hashes, installer primitives, actual Inno payload preflight, and signing-policy contracts all passed. It is test evidence only and must not be published as a production release. CodeQL and dependency review are configured in `.github/workflows/security.yml`; hosted execution remains GitHub-side evidence rather than a locally claimed pass.

On 2026-11-06 the candidate was also reported to install and run successfully in a Windows VM. This is a user-observed smoke result, not a substitute for the scripted disposable-guest upgrade, uninstall, offline-runtime, UAC, and signed-publisher matrix below.

The web candidate passed the Error Boundary and dialog-semantics render contracts. The production bundle passed the enforced budget at approximately 383 KB JavaScript (118 KB gzip), 103 KB CSS (20 KB gzip), and 138 KB startup gzip; virtualized rendering remains bounded through 10,000 rows.

```powershell
dotnet run --project tests/Engine.Tests/Engine.Tests.csproj -c Release -- --report-dir _build/engine-tests

dotnet run --project tests/ProductIntegrity.Tests/ProductIntegrity.Tests.csproj -c Release -p:SkipLocalWebUiBuild=true -p:NuGetAudit=false
```

Use the authorized ordinary Windows desktop-user context. Do not substitute sandbox restrictions with passes. The default full suite does not request Administrator/UAC acceptance. It reads inventory, but destructive fixtures are restricted to their unique identities and folders. Real-browser tests use separate temporary profiles.

## CI and practical limits

CI runs web lint and React contracts, builds the Windows app and engine runner with warnings treated as errors, and executes product-integrity, Control Center protocol, and project-cleanup safety checks. Compiling the full engine runner is not described as executing its desktop/process tests on the hosted runner.

SSR is not a native visual/resize test. The desktop app was launched and WebView2/React startup was observed; a complete Windows VM installation/UAC acceptance matrix was not independently executed. User-reported VM checks are separate from recorded fixtures. Third-party uninstallers can reject removal, require interaction or leave a restart pending.

Detailed local logs and user-specific historical evidence are excluded from public Git. Do not upload raw diagnostics or SQLite data in issues.

## Product website and gallery update

The 1.0.1 check rerun passed 174 engine tests, 42 React tests, 79 SSR contracts, 28 product/SQLite checks, 45 package/image checks and 18 installer/release identity checks. The 7 Windows installer-preflight primitive checks passed; this is not native Setup end-to-end acceptance.

The new website action accepts no payload properties. It opens only the fixed HTTPS product address using the default browser, without executable paths, launch arguments or elevation. Product-integrity tests reject supplied URLs, paths, arguments and duplicate URL fields, and confirm that the public website is not a trusted WebView command source. The new Settings button is covered by Arabic/English rendering checks. A live native click of the button was not automated because native application control was unavailable under product policy.

The website gallery uses eight original saved captures. Older builds and fixture previews are explicitly identified; these are not claimed as new screenshots of every v1.0.1 screen. Gallery image decoding, filtering, keyboard navigation and 390px mobile RTL/LTR layout were checked in a browser. The supplied YouTube video was observed playing through the click-to-load embedded player during local preview.

## Installer acceptance for the 1.0.3 candidate

Run `tests/InstallerAcceptance.ps1` in a disposable VMware clone with a clean, non-elevated Windows user profile. Set the clone's `guestinfo.u77InstallerAcceptance` VMX property to a unique lowercase 32-character hex token and supply it as `-GuestToken`. The runner requires `-DisposableGuest`, the candidate file, its version, and its verified SHA256. For an upgrade scenario also pass the original `-Baseline` installer and `-BaselineSHA256`. Use a separate clean profile or restore the disposable lab state for each scenario.

The runner verifies the candidate installed into its own directory, checks shortcut targets and WebView2/React startup, and removes its own test installation after verifying it. It checks that profile sentinels survive and writes `result.json` and installer logs to its guest-local run directory. `-EvidenceDirectory` optionally copies that run's evidence to a dedicated host share. A pre-existing product registration or shortcut stops the run before installation.

Record whether the WebView2 Runtime was present before Setup. A successful offline run with WebView2 already registered tests the skip path. Exercise the bundled-install path separately on an offline guest with the Runtime absent. Preserve logs from the reported laptop failure as a distinct reproduction case.

## Real per-user MSI lifecycle

From a non-elevated PowerShell 7 session in the repository root, pass a local WiX CLI (tested with 5.0.2).

```powershell
pwsh -NoProfile -File tests/MsiUserAcceptance.ps1 -WixPath C:\path\to\wix.exe
```

The test builds and installs a uniquely identified x64 MSI under LocalAppData. The engine verifies the owned registration and both ownership markers before launching Windows Installer. The test checks the observed exit code, registration and payload removal, and a repeat request using the stale catalog entry. `install.log` and `msi-result.json` are written to the run directory printed by the script.

## Desktop QA and release destination checks

Use a Debug build for the optional visual QA and synthetic UI fixtures. Release builds exclude the screenshot runners and synthetic queue entry points. The Release product-integrity suite checks the actual assembly for those exclusions.

Run `pwsh -NoProfile -File tests/ReleaseDestination.Tests.ps1` to exercise rejected release paths and modified payloads with isolated sentinels. The test stops before publish or installer compilation.

Run `pwsh -NoProfile -File tests/LegacyRuntime.Tests.ps1 -LegacyPayload C:\path\to\old\app` only with the original extracted `WinUninstaller` runtime. It compares the historical files against the installer's exact-hash allowlist before exercising retirement on copies.
