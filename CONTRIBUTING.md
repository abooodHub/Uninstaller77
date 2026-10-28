# Contributing

Build instructions: `docs/BUILDING.md`. Test scope: `docs/TESTING.md`.

Use isolated owned fixtures for any uninstall, Registry write, cleanup or restoration test. Do not uninstall user software, alter real services/drivers, mutate real browser profiles or force-kill vendor uninstallers. Administrator/UAC acceptance is a separate explicit check.

Add regressions that test outcomes rather than implementation statements. Separate queue completion from removal verification, preserve per-item diagnostics and retain uncertainty when completion cannot be established. Do not delete Registry entries to manufacture success.

Keep English/Arabic text aligned, including RTL/LTR and technical paths. Removed features may remain in historical tests, but must not silently re-enter the shipping assembly or WebView2 protocol.

State which checks actually ran. Redact local accounts, inventory and logs. Build outputs, SQLite files and private diagnostics do not belong in a commit.
