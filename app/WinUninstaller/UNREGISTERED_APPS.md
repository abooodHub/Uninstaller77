# Unregistered application file deletion — 1.1.14

The right-click shortcut workflow now performs **permanent deletion after explicit consent**, not retained quarantine. It supports reviewed Electron deployment bundles and dedicated single-executable folders in the current user's profile. It does not invent an uninstall command, change Windows services or Registry entries, or delete parent folders/external application data. Shortcuts outside the reviewed folder are not part of this file scope.

The confirmation names the exact folder, shows its file count/size and explicitly states that Uninstaller 77 cannot restore permanently deleted files. Its bridge request must contain the current review GUID, acknowledged=true and permanent=true. Old 1.1.13 reversible confirmations are rejected. No client file paths or deletion commands are accepted.

For a live deployment, a fresh Registry inventory, scope fingerprint and process/file checks precede an internal same-volume staging rename. Only the signed reviewed file set is deleted, file by file. There is no recursive deletion. Empty directories are removed only when empty; unexpected files are preserved. Completed operations leave no retained application backup; small integrity/audit metadata remains. The displayed size is logical file size, not a claim of secure erasure or an exact NTFS free-space measurement.

If deletion fails partway, actual deleted, remaining and unverified-missing counts are kept separate. The result is not declared complete. The active window can retry remaining approved files after explicit consent. If the app terminates during deletion, remaining files are preserved in LocalAppData/WinUninstaller/PendingRemoval for manual review; there is no automatic destructive resume. A partial deletion is not offered as a complete restorable backup.

## Existing copies saved by 1.1.13

When the original shortcut target is absent (or the shortcut follows the moved executable), the service can identify an older stored copy by its exact original/relocated executable identity, authenticated receipt and full file hashes. A fresh review capability is issued. Multiple matching stored copies are refused; none is guessed. Confirming permanently deletes the selected stored copy rather than moving it into another retained backup. Old backups that have not entered permanent deletion remain restorable through Control Center.

Hermes on this desktop was found in an existing 1.1.13 quarantine copy. Read-only validation identified 1,183 files / 406,014,267 bytes and verified its signed snapshot. The agent did not submit permanent deletion of Hermes. The original desktop deployment folder and the other Hermes agent/data folders are distinct scopes.

## Executed validation

- Full engine: 114 passed / 0 failed, including real owned-fixture permanent deletion, partial failure caused by a file lock, retry, cancellation, stale content, unknown missing files, re-created source folders, legacy stored-copy deletion, retargeted shortcuts and ambiguous backups.
- Real WPF dispatcher/ViewModel test: rejected deletion returns to a usable review, valid permanent fixture deletion completes, and a separate old backup can still be restored.
- React tests: 32 passed. Bilingual SSR checks: 63 passed. Product integrity: 21 passed. Package checks: 45 passed. Lint and Release build completed without warnings/errors.
- Component browser checks exercised consent, irreversible result with no Undo, partial retry and Arabic/English layouts at 1920x1080, 1366x768 and 1280x800. Fixture UI actions perform no real file operations.
- No user program, service or extension was removed by automated testing. No GitHub publication or VM acceptance test occurred. Native UI automation for WinUninstaller.exe is blocked by the computer-use tool's product policy; that restriction was not bypassed.

The historical 1.1.14 installer hash was 381EF47DE362901A2F6AC19D1603C21A395089F8A6578163B9198574E8DCD1D6. Historical evidence is retained in docs/History.zip after project consolidation. The current product is version 1.0.0: dist/Uninstaller77-Setup.exe and dist/app/Uninstaller77.exe. Historical version numbers and hashes describe their original verification, not the current package.

1.1.15 retains this file-removal behavior and adds authenticated registered-EXE job tracking and delayed-registration retry repair. Its current package and test evidence are under _build/verification-broker; the older hash above is historical. UAC acceptance and native visual verification are not established by the automated suite.
