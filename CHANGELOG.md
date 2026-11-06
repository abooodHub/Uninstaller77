# Changelog

## Unreleased

- Split focus management, application/browser icons, and virtual-list rendering out of the 1,300-line React entry point without changing bundle size or host behavior.
- Add a typed 52-operation host allowlist and a cross-language audit that rejects drift between the C# WebView protocol, TypeScript contract, and static UI calls.
- Add a pinned weekly/PR CodeQL workflow for C# and TypeScript plus dependency-review rejection for new High/Critical vulnerable dependencies.
- Enable deterministic .NET builds and repository-wide analyzers, with explicit CI-enforced reliability rules for Win32 return values, cancellation propagation, and Unicode P/Invoke declarations.
- Split the WebView bridge and AppList presentation/dialog responsibilities into focused partial files while preserving bindings, bridge operations, and engine behavior.
- Move React host contracts into a dedicated module, add a bilingual Error Boundary, and trap/restore keyboard focus in modal dialogs and drawers.
- Enforce a production web bundle budget and a 10,000-row virtualization contract; the current candidate remains within JS, CSS, gzip, and startup limits.
- Build and verify an isolated self-contained 1.0.3 installer candidate without replacing `dist`; its payload, manifest hashes, CycloneDX SBOM, preflight, identity, and signing-policy contracts all pass.
- Upgrade `Microsoft.Data.Sqlite` from 8.0.0 to 10.0.14, centralize NuGet versions, commit per-project lock files, require locked/audited CI restores, and fail CI on High/Critical NuGet or npm findings.
- Generate a CycloneDX 1.5 release SBOM from the NuGet and npm lock files, include dependency integrity hashes and runtime/WebView2 identities, and bind the SBOM SHA-256 into the release manifest.
- Record SQLite `user_version`, refuse newer unsupported schemas, and create a durable consistent backup before any legacy or drift-repair migration; an injected pre-commit failure proves transactional rollback.
- Reject duplicate JSON fields at any depth, excessive JSON node counts, control-bearing bridge request IDs, and query/fragment variants of the trusted WebView origin before dispatch.
- Preserve permanent residual-deletion outcomes as distinct `Deleted`, `Changed`, `Inaccessible`, and `Retained` evidence; cover changed bytes, NTFS hard links, sharing violations, and unreviewed input with real filesystem fixtures.
- Revalidate the exact Registry uninstall command after pre-launch housekeeping and immediately before process creation; an engine-owned fixture proves that a changed command launches neither the original nor replacement registration.
- Recover interrupted atomic queue writes from `queue.json.tmp`, archive both temporary and previous primary evidence, force journal bytes to disk before replacement, and refuse new saves until the interruption is reviewed.
- Add a staged defect-remediation plan with explicit P0–P3 priorities, safety invariants, release gates, and per-issue completion criteria.
- Make the one-way placeholder image converter reject reverse writes with `Binding.DoNothing` instead of throwing `NotImplementedException`.
- Contain and diagnose WebView callback failures during dispatcher shutdown or browser recovery instead of allowing an `async void` event exception to terminate WPF.
- Enforce web linting, warning-free .NET builds, Control Center protocol tests, and project-cleanup safety tests in CI, with run concurrency cancellation and a finite timeout.
- Update `CommunityToolkit.Mvvm` from 8.4.0 to 8.4.2 across the application and engine test project.
- Update `lucide-react` from 1.61.0 to 1.68.0 with lint, contract, build, and server-render audits.
- Update the WebView2 SDK from 1.0.4129.50 to stable 1.0.4314.35, verified against the native recovery flow and a complete temporary installer package.
- Require WebView2 Runtime 156.0.4314.42 or newer during Setup; older registrations now run the verified bundled prerequisite and are rechecked before payload replacement.
- Remove unused native UI resources and retired Install Monitor CSS selectors while preserving the live fallback interface.
- Preserve release archives during normal finalization; deleting them now requires the separate explicit `-DeleteArchive` cleanup option.
- Split WebView state/observers and residual-cleanup logic into focused partial classes without changing the command allowlist, JSON contract, or cleanup behavior.
- Make read-only verification of a completed browser handoff retry immediately instead of spending half of its retry window on a second settling delay.
- Isolate native WebView2 UI acceptance data from the user's normal browser profile and keep the test bound to the currently active recovered browser instance.
- Keep Authenticode signer inspection warning-free on .NET 10 with a narrowly scoped compatibility suppression; .NET does not yet provide a non-obsolete PE signer extraction API.

- Streamline the active uninstall dialog and scan for leftovers after the queue ends. Review findings grouped by verified removal, then optionally confirm permanent deletion of selected, revalidated files from recorded install directories.
- Keep empty or informational-only scans in the uninstall results and explain whether the recorded install folder was missing, unavailable, or checked. Check exact versionless names for informational findings.

- Add optional pre-uninstall protection for each confirmed queue. It attempts a Windows restore point and backs up only the selected uninstall registry branches as `.reg` files.
- Record each protection outcome in History, including disabled steps and reasons for failures, and continue the queue when protection is unavailable.
- Add Arabic and English protection controls and a post-queue link to Windows System Restore. A restore point may not fully recover an application.

## 1.0.1 — 2026-10-28

- Add a translated Product website button in Settings, opening the fixed HTTPS product page in the default browser.
- Reject website action payloads containing custom URLs, paths or arguments.
- Expand the bilingual website with eight contextualized original captures, category filters, keyboard-accessible full-image viewing and README previews.
- Keep previous development screenshots explicitly identified; current browser inventory remains read-only.

## 1.0.0 — 2026-10-28

- Windows x64 application inventory for Registry programs and current-user MSIX/AppX packages.
- Sequential queues with distinct progress, successful, failed and skipped results.
- Retained per-app failure diagnostics and observed exit codes.
- EXE commands preserved under GUID-shaped Registry keys, including the observed Google Drive layout.
- Read-only verification and review of uncertain completion, plus stop-after-current-operation control.
- Reviewed residual quarantine / restore and identity-checked orphaned desktop-shortcut cleanup.
- Read-only browser extension inventory by browser and profile.
- English / Arabic UI, CSV export, local history and Explorer shortcut integration.
- English installer with payload preflight and compatible installation-data identity.
- MIT source release, dependency notices and bilingual product website.

Browser extension mutation, software-health remediation, install monitoring, force uninstall, file shredding and system restore are not exposed as available product features in this release.
