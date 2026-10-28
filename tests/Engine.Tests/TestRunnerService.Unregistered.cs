using System.IO;
using System.Text.Json;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class TestRunnerService
{
    private async Task RunUnregisteredTestsAsync(List<TestResult> results, Action<string>? logger)
    {
        // All mutations below are restricted to a fresh owned fixture profile.
        var fixture = Path.Combine(Path.GetTempPath(), "Uninstaller77_Unregistered_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        UnregisteredAppService Service(string profile) => new(profile, Path.Combine(profile, "Quarantine"), AppContext.BaseDirectory);
        string App(string name, bool bundle = true)
        {
            var root = Path.Combine(fixture, name, "Apps", "OwnedApp"); Directory.CreateDirectory(root);
            File.Copy(Environment.ProcessPath!, Path.Combine(root, "OwnedApp.exe"));
            if (bundle)
            {
                Directory.CreateDirectory(Path.Combine(root, "resources"));
                File.WriteAllText(Path.Combine(root, "resources", "app.asar"), "Owned synthetic deployment metadata");
                File.WriteAllText(Path.Combine(root, "icudtl.dat"), "Owned ICU fixture");
                File.WriteAllText(Path.Combine(root, "chrome_100_percent.pak"), "Owned resource fixture");
            }
            return Path.Combine(root, "OwnedApp.exe");
        }
        static void Refused(Action action, string? code = null)
        {
            try { action(); } catch (Exception ex) { if (code is null || ex.Message.Contains(code)) return; throw; }
            throw new Exception("Unsafe/unconfirmed operation was accepted: " + code);
        }
        await RunTest("Unregistered Electron bundle review is read-only and pins exact contents", results, logger, () =>
        {
            var target = App("Review"); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            var service = Service(profile); var plan = service.Prepare(target, []);
            if (plan.Kind != "ElectronBundle" || plan.Files.Count != 4 || !File.Exists(target) || Directory.Exists(Path.Combine(profile, "Quarantine")))
                throw new Exception("Review changed files or guessed a wider root.");
            return "Four owned files fingerprinted; no backup directory or removal created; target parent is the exact scope.";
        });
        await RunTest("Unregistered file isolation and authenticated restore preserve bytes and external data", results, logger, () =>
        {
            var target = App("Roundtrip"); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            var service = Service(profile); var external = Path.Combine(profile, "keep-user-data.txt"); File.WriteAllText(external, "Preserve this owned data");
            var plan = service.Prepare(target, []); var path = service.Quarantine(plan, []);
            if (Directory.Exists(plan.Root) || !service.IsManifest(path) || File.ReadAllText(external) != "Preserve this owned data") throw new Exception("Scope or receipt was wrong.");
            var restored = service.Restore(path);
            if (!File.Exists(target) || restored.Fingerprint != service.Prepare(target, []).Fingerprint || File.ReadAllText(external) != "Preserve this owned data") throw new Exception("Restore changed original file bytes or external data.");
            Refused(() => service.Restore(path), "RestoreOccupied");
            return "Real directory move and restore; original SHA256s verified; occupied target protected; no uninstaller started.";
        });
        await RunTest("A changed deployment after review is never moved", results, logger, () =>
        {
            var target = App("Changed"); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            var service = Service(profile); var plan = service.Prepare(target, []);
            File.WriteAllText(Path.Combine(plan.Root, "added.txt"), "After review");
            Refused(() => service.Quarantine(plan, []), "ChangedScope");
            if (!File.Exists(target)) throw new Exception("Changed files were moved.");
            return "A newly added file invalidated the confirmation fingerprint.";
        });
        await RunTest("Registered identities and a newly registered overlapping scope block unmanaged removal", results, logger, () =>
        {
            var target = App("Registered"); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            var service = Service(profile); var plan = service.Prepare(target, []);
            var registrations = new[] { new InstalledApp { Id = "OwnedRegistration", InstallLocation = plan.Root } };
            Refused(() => service.Prepare(target, registrations), "RegisteredScope");
            Refused(() => service.Quarantine(plan, registrations), "RegisteredScope");
            return "Existing/fresh registration boundaries take priority over unregistered file isolation.";
        });
        await RunTest("Mixed data and unrecognized layouts remain untouched", results, logger, () =>
        {
            var target = App("Mixed"); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(target)!, "User Data"));
            Refused(() => Service(profile).Prepare(target, []), "MixedData");
            var unknown = App("Unknown", false); var unknownProfile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(unknown)))!;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(unknown)!, "unrelated.txt"), "Keep unrelated content");
            Refused(() => Service(unknownProfile).Prepare(unknown, []), "UnknownLayout");
            return "No guessed parent-folder deletion when user data or unrecognized siblings are present.";
        });
        await RunTest("Single-file unregistered applications have an exact isolated folder scope", results, logger, () =>
        {
            var target = App("Single", false); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            var service = Service(profile); var plan = service.Prepare(target, []);
            if (plan.Kind != "SingleExecutable" || plan.Files.Count != 1) throw new Exception("Single executable was not isolated.");
            var receipt = service.Quarantine(plan, []); service.Restore(receipt);
            return "Only one owned executable moved and restored; no sibling/root guessing.";
        });
        await RunTest("Unregistered backup receipt tampering cannot redirect restore", results, logger, () =>
        {
            var target = App("Tamper"); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            var service = Service(profile); var plan = service.Prepare(target, []); var receipt = service.Quarantine(plan, []);
            var original = File.ReadAllText(receipt); using var document = JsonDocument.Parse(original);
            var body = Convert.FromBase64String(document.RootElement.GetProperty("Body").GetString()!);
            body[body.Length / 2] ^= 1;
            File.WriteAllText(receipt, JsonSerializer.Serialize(new { Kind = UnregisteredAppService.ManifestKind, Body = Convert.ToBase64String(body), Signature = document.RootElement.GetProperty("Signature").GetString() }));
            Refused(() => service.Restore(receipt), "InvalidBackup");
            File.WriteAllText(receipt, original); service.Restore(receipt);
            return "Modified receipt failed its integrity signature; untouched receipt restored the owned fixture.";
        });
        await RunTest("Modified backup payload is refused before restore", results, logger, () =>
        {
            var target = App("BackupChanged"); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            var service = Service(profile); var plan = service.Prepare(target, []); var receipt = service.Quarantine(plan, []);
            File.WriteAllText(Path.Combine(profile, "Quarantine", plan.Id, "extra.txt"), "Unexpected backup file");
            Refused(() => service.Restore(receipt), "BackupChanged");
            if (Directory.Exists(plan.Root)) throw new Exception("Changed backup was restored.");
            return "Backup bytes are checked independently of the authenticated receipt.";
        });
        await RunTest("File locks and pre-move cancellation cannot partially remove a deployment", results, logger, () =>
        {
            var target = App("Locked"); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            var service = Service(profile); var plan = service.Prepare(target, []);
            using (var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Refused(() => service.Quarantine(plan, []));
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); Refused(() => service.Quarantine(plan, [], cancel.Token));
            if (!File.Exists(target)) throw new Exception("Blocked/cancelled removal changed source files.");
            return "Locked or pre-cancelled scope stays in its original location; no process termination.";
        });
        await RunTest("Profile roots, external files and non-PE targets are rejected", results, logger, () =>
        {
            var target = App("Scope"); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            var service = Service(profile);
            Refused(() => Service(Path.GetDirectoryName(target)!).Prepare(target, []), "ProtectedScope");
            Refused(() => service.Prepare(Environment.ProcessPath!, []), "ProtectedScope");
            File.WriteAllText(target, "Not a Windows PE executable"); Refused(() => service.Prepare(target, []), "UnsupportedExecutable");
            return "No traversal to a shared profile root, another installation or a fake executable.";
        });
        await RunTest("Linked content cannot expand an unregistered application scope", results, logger, () =>
        {
            var target = App("Linked"); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            var root = Path.GetDirectoryName(target)!; var link = Path.Combine(root, "linked-content");
            var external = Path.Combine(profile, "KeepOutside"); Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "keep.txt"), "Owned external data");
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c mklink /J \"{link}\" \"{external}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("Owned junction fixture could not be created: " + process.StandardError.ReadToEnd());
            try
            {
                Refused(() => Service(profile).Prepare(target, []), "LinkedPath");
                if (File.ReadAllText(Path.Combine(external, "keep.txt")) != "Owned external data") throw new Exception("Linked data changed.");
            }
            finally
            {
                if (!Path.GetFullPath(link).StartsWith(Path.GetFullPath(fixture) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0) throw new Exception("Unsafe fixture link cleanup.");
                Directory.Delete(link, recursive: false); // Unlink this owned junction only; never follow its target.
            }
            return "A real NTFS junction was rejected without touching its external target.";
        });
        await RunTest("Unregistered file outcomes never become successful uninstall history", results, logger, () =>
        {
            foreach (var state in new[] { "FilesQuarantined", "FilesRestored", "FilesDeleted", "FileDeletionIncomplete" })
            {
                var id = _db.AddRecord(new() { AppName = "Owned file operation", Success = false, Outcome = state, InstallerType = "Unregistered files" });
                var record = _db.GetHistory().First(item => item.Id == id);
                if (record.Success || HistoryOutcome.Normalize(record.Success, record.Outcome) != state) throw new Exception("File operation was presented as a successful uninstall.");
            }
            return "File isolation/restore survive SQLite roundtrip with distinct outcomes and Success=false.";
        });
        await RunTest("Unregistered bridge actions accept only a review identity and explicit consent", results, logger, () =>
        {
            var id = Guid.NewGuid().ToString("N");
            using var valid = JsonDocument.Parse(JsonSerializer.Serialize(new { planId = id, acknowledged = true, permanent = true }));
            if (UnregisteredAppService.ReadAction(valid.RootElement, true) != id) throw new Exception("Valid consent was rejected.");
            foreach (var json in new[] { "{}", "{\"planId\":\"wrong\",\"acknowledged\":true}",
                JsonSerializer.Serialize(new { planId=id, acknowledged=true }), JsonSerializer.Serialize(new { planId=id, acknowledged=true, permanent=false }),
                JsonSerializer.Serialize(new { planId=id, acknowledged=false }), JsonSerializer.Serialize(new { planId=id, acknowledged=true, path=fixture }),
                "{\"planId\":\""+id+"\",\"planId\":\""+id+"\",\"acknowledged\":true}", JsonSerializer.Serialize(new { planId=id, acknowledged="true" }) })
            { using var bad = JsonDocument.Parse(json); Refused(() => UnregisteredAppService.ReadAction(bad.RootElement, true), "InvalidReview"); }
            using var undo = JsonDocument.Parse(JsonSerializer.Serialize(new { planId=id }));
            if (UnregisteredAppService.ReadAction(undo.RootElement, false) != id) throw new Exception("Undo identity was rejected.");
            Refused(() => UnregisteredAppService.ReadAction(valid.RootElement, false), "InvalidReview");
            return "No client paths, stale reversible confirmations, false permanent-deletion consent, duplicate keys or wrong consent types accepted.";
        });
        await RunTest("Permanent application-file removal deletes the reviewed bytes without retaining a backup", results, logger, () =>
        {
            var target = App("Permanent"); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            var service = Service(profile); var plan = service.Prepare(target, []);
            var external = Path.Combine(profile, "keep.txt"); File.WriteAllText(external, "Keep external data");
            var deletion = service.DeletePermanently(plan, []);
            if (!deletion.Complete || deletion.DeletedFiles != plan.Files.Count || deletion.DeletedBytes != plan.Bytes || deletion.RemainingFiles != 0 ||
                Directory.Exists(plan.Root) || Directory.Exists(Path.Combine(profile, "PendingRemoval", plan.Id)) || Directory.Exists(Path.Combine(profile, "Quarantine")) || File.ReadAllText(external) != "Keep external data")
                throw new Exception("Permanent removal retained application files or changed external data.");
            return "Real owned application bytes permanently deleted, deployment directory absent, no retained application backup, external data intact.";
        });
        await RunTest("Incomplete permanent deletion reports actual counts and can retry the remaining approved files", results, logger, () =>
        {
            var target = App("PartialPermanent"); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            var service = Service(profile); var plan = service.Prepare(target, []); FileStream? locked = null;
            UnregisteredAppService.DeletionResult deletion;
            try
            {
                deletion = service.DeletePermanently(plan, [], new InlineDeletionProgress(value =>
                {
                    if (value.DeletedFiles == 1 && locked is null) locked = new FileStream(Path.Combine(profile, "PendingRemoval", plan.Id, plan.Files[1].RelativePath), FileMode.Open, FileAccess.Read, FileShare.None);
                }));
                if (deletion.Complete || !deletion.CanRetry || deletion.DeletedFiles != 1 || deletion.RemainingFiles != plan.Files.Count - 1) throw new Exception("Partial deletion was misreported.");
                Refused(() => new UnregisteredAppService(profile, Path.Combine(profile, "PendingRemoval"), AppContext.BaseDirectory).Restore(Path.Combine(profile, "PendingRemoval", plan.Id + ".json")), "PermanentDeletionStarted");
            }
            finally { locked?.Dispose(); }
            var retry = service.RetryPermanentDeletion(plan.Id, deletion.DeletedPaths);
            if (!retry.Complete || retry.DeletedFiles != plan.Files.Count || retry.DeletedBytes != plan.Bytes || Directory.Exists(plan.Root)) throw new Exception("Remaining-file retry failed.");
            return "A real file lock caused partial deletion, not success; cumulative verified counts survived retry; partial backup restore was blocked.";
        });
        await RunTest("Changed or cancelled permanent deletion leaves the reviewed source intact", results, logger, () =>
        {
            var target = App("PermanentCancel"); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            var service = Service(profile); var plan = service.Prepare(target, []);
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); Refused(() => service.DeletePermanently(plan, [], ct: cancel.Token));
            File.WriteAllText(Path.Combine(plan.Root, "new.txt"), "Not reviewed"); Refused(() => service.DeletePermanently(plan, []), "ChangedScope");
            if (!File.Exists(target) || File.ReadAllText(Path.Combine(plan.Root, "new.txt")) != "Not reviewed") throw new Exception("Source changed before permanent commit.");
            return "Pre-cancellation and stale review fingerprints perform no irreversible operation.";
        });
        await RunTest("Files disappearing outside permanent deletion are not counted as our successful deletions", results, logger, () =>
        {
            var target = App("PermanentMissing"); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            var service = Service(profile); var plan = service.Prepare(target, []); FileStream? locked = null;
            var deletion = service.DeletePermanently(plan, [], new InlineDeletionProgress(value =>
            {
                if (value.DeletedFiles == 1 && locked is null) locked = new FileStream(Path.Combine(profile, "PendingRemoval", plan.Id, plan.Files[1].RelativePath), FileMode.Open, FileAccess.Read, FileShare.None);
            }));
            locked?.Dispose();
            File.Delete(Path.Combine(profile, "PendingRemoval", plan.Id, plan.Files[2].RelativePath)); // Owned fixture simulating another actor.
            var retry = service.RetryPermanentDeletion(plan.Id, deletion.DeletedPaths);
            if (retry.Complete || retry.DeletedFiles != 1 || retry.UnverifiedMissingFiles != 1 || retry.RemainingFiles != plan.Files.Count - 2) throw new Exception("Unobserved removal became fabricated success.");
            return "External absence is separate from verified deletions; changed remaining scope is not purged.";
        });
        await RunTest("A recreated original application folder is preserved and prevents permanent-removal success", results, logger, () =>
        {
            var target = App("PermanentRecreated"); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            var service = Service(profile); var plan = service.Prepare(target, []);
            var deletion = service.DeletePermanently(plan, [], new InlineDeletionProgress(value =>
            {
                if (value.DeletedFiles == plan.Files.Count) { Directory.CreateDirectory(plan.Root); File.WriteAllText(Path.Combine(plan.Root, "keep-new.txt"), "New installation data"); }
            }));
            if (deletion.Complete || deletion.CanRetry || !deletion.Errors.Contains("UnregisteredApp:SourceReappeared") || File.ReadAllText(Path.Combine(plan.Root, "keep-new.txt")) != "New installation data")
                throw new Exception("A new unreviewed source folder was deleted or hidden.");
            return "Only the old approved files were deleted; another actor's recreated folder remained intact and triggered review.";
        });
        await RunTest("A missing shortcut target can review and permanently delete its authenticated legacy quarantine copy", results, logger, () =>
        {
            var target = App("DeleteLegacy"); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            var service = Service(profile); var original = service.Prepare(target, []); var receipt = service.Quarantine(original, []);
            var stored = service.Prepare(target, []);
            var followedShortcut = service.Prepare(Path.Combine(stored.Root, Path.GetFileName(target)), []);
            if (followedShortcut.StoredReceipt != receipt || followedShortcut.Target != target) throw new Exception("A shortcut following the moved executable lost its original signed identity.");
            if (stored.StoredReceipt != receipt || stored.Root == original.Root || stored.Id == original.Id) throw new Exception("Legacy backup did not receive a fresh deletion capability.");
            var deleted = service.DeletePermanently(stored, []);
            if (!deleted.Complete || deleted.DeletedFiles != original.Files.Count || Directory.Exists(stored.Root) || Directory.Exists(original.Root)) throw new Exception("Legacy stored bytes were retained.");
            Refused(() => service.Restore(receipt), "PermanentDeletionStarted");
            return "Exact target identity plus receipt signature and full file hashes matched the older backup; its files were really deleted and not offered for restore.";
        });
        await RunTest("Multiple legacy quarantine copies never trigger a guessed permanent deletion", results, logger, () =>
        {
            var target = App("LegacyAmbiguous"); var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)))!;
            var service = Service(profile); var first = service.Prepare(target, []); service.Quarantine(first, []);
            App("LegacyAmbiguous");
            // Prepare the re-created real source, not the previous stored copy.
            var second = service.Prepare(target, []); service.Quarantine(second, []);
            Refused(() => service.Prepare(target, []), "AmbiguousBackup");
            if (!Directory.Exists(Path.Combine(profile, "Quarantine", first.Id)) || !Directory.Exists(Path.Combine(profile, "Quarantine", second.Id))) throw new Exception("An ambiguous backup was removed.");
            return "Both stored copies remained intact; duplicate target identities require manual review.";
        });
    }

    private sealed class InlineDeletionProgress(Action<UnregisteredAppService.DeletionProgress> report) : IProgress<UnregisteredAppService.DeletionProgress>
    { public void Report(UnregisteredAppService.DeletionProgress value) => report(value); }
}
