using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WinUninstaller.Models;
using WinUninstaller.ViewModels;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace WinUninstaller.Services;

public partial class TestRunnerService
{
    private sealed class FailedResidualFixture : ResidualScanService
    {
        protected override List<ResidualItem> CollectResiduals(InstalledApp app, CancellationToken token, IProgress<ScanProgress>? progress)
            => throw new IOException("Phase11 isolated scan failure");
    }

    private async Task RunPhase11TestsAsync(List<TestResult> results, Action<string>? logger)
    {
        static InstalledApp Identity() => new() { Id = Guid.NewGuid().ToString("N"), Name = "Phase11_" + Guid.NewGuid().ToString("N"),
            RegistryHive = "HKCU", RegistryKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Phase11_" + Guid.NewGuid().ToString("N") };

        await RunTest("Phase11 legacy SQLite migration preserves evidence and unresolved outcomes", results, logger, () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "U77_Phase11_Migration_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try
            {
                var path = Path.Combine(folder, "legacy.db");
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
                {
                    connection.Open(); using var command = connection.CreateCommand();
                    command.CommandText = """
                        CREATE TABLE UninstallHistory (Id INTEGER PRIMARY KEY AUTOINCREMENT, AppId TEXT NOT NULL, AppName TEXT NOT NULL,
                          Publisher TEXT, Version TEXT, CachedIconPath TEXT, UninstallDate TEXT NOT NULL, ExitCode INTEGER, Success INTEGER, Notes TEXT);
                        INSERT INTO UninstallHistory VALUES (1,'legacy-id','Legacy fixture','publisher','1.0','','2026-01-01 12:00:00',-3,0,'original evidence');
                        """;
                    command.ExecuteNonQuery();
                }
                var database = new DatabaseService(path); var original = database.GetHistory().Single();
                if (original.AppId != "legacy-id" || original.Notes != "original evidence" || original.Success || original.ExitCode != -3 || original.Outcome != "") throw new Exception("Legacy record was rewritten during additive migration.");
                var id = database.AddRecord(new() { AppName = "Unresolved fixture", Success = false, Outcome = "NeedsAttention", Notes = "still unknown" });
                var unresolved = database.GetHistory().Single(record => record.Id == id);
                if (unresolved.Success || HistoryOutcome.Normalize(unresolved.Success, unresolved.Outcome) != "NeedsAttention" || HistoryOutcome.Normalize(false, "Succeeded") != "NeedsAttention" || HistoryOutcome.Normalize(false, "") != "LegacyUnverified") throw new Exception("Unresolved or contradictory outcome became success/failure.");
                if (UiStrings.Get("ar", "outcomeNeedsAttention") != "غير محسوم" || UiStrings.Get("en", "outcomeNotStarted") != "Not started") throw new Exception("History outcomes were not localized.");
                return "Additive Outcome column retained old record fields; unknown and contradictory results stayed unresolved, and legacy unsuccessful records stayed unverified.";
            }
            finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        });

        await RunTest("Phase11 frozen ICO decoder frames encode across thread boundaries", results, logger, async () =>
        {
            var ready = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var producer = new Thread(() =>
            {
                try
                {
                    var pixels = Enumerable.Repeat((byte)180, 32 * 32 * 4).ToArray();
                    var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 128);
                    var png = BitmapEncodingService.EncodePng(bitmap);
                    using var ico = new MemoryStream(); using var writer = new BinaryWriter(ico);
                    writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
                    writer.Write((byte)32); writer.Write((byte)32); writer.Write((byte)0); writer.Write((byte)0);
                    writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(png.Length); writer.Write(22); writer.Write(png); writer.Flush(); ico.Position = 0;
                    var decoder = new IconBitmapDecoder(ico, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    var frame = decoder.Frames[0]; frame.Freeze(); ready.TrySetResult(frame);
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true };
            producer.SetApartmentState(ApartmentState.STA); producer.Start();
            var foreignFrame = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var encoded = BitmapEncodingService.EncodePng(foreignFrame);
            using var pngStream = new MemoryStream(encoded);
            var decoded = new PngBitmapDecoder(pngStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            if (decoded.PixelWidth != 32 || decoded.PixelHeight != 32) throw new Exception("Pixel copy lost icon dimensions.");
            return "A frozen ICO frame created on a dedicated STA thread was encoded and decoded on a different thread without accessing decoder metadata.";
        });

        await RunTest("Phase11 unsupported MSIX never reports clean", results, logger, async () =>
        {
            var app = Identity(); app.Source = "MSIX";
            var scan = await new FailedResidualFixture().ScanResidualsAsync(app);
            if (scan.State != ResidualScanState.Unsupported || scan.Completed || scan.Items.Count != 0 || scan.StorageStatus.Contains("Clean")) throw new Exception("Unsupported scan became clean or ran its scanner.");
            if (scan.Message("ar") != "لم يتم فحص المخلفات — هذا النوع غير مدعوم حاليًا" || scan.Message("en") != "Residual scan not supported for this package type.") throw new Exception("Unsupported scan messages do not match the contract.");
            return "Unsupported bypasses the scanner, has its own persistent state and exact Arabic/English messages.";
        });
        await RunTest("Phase11 completed empty scan is distinct from unsupported", results, logger, async () =>
        {
            var scan = await new ResidualScanService().ScanResidualsAsync(Identity());
            if (scan.State != ResidualScanState.CompletedNoFindings || !scan.Completed || scan.Items.Count != 0) throw new Exception("A completed empty scan was not classified explicitly.");
            return "Actual read-only scan of unique absent fixture locations completed with no findings.";
        });
        await RunTest("Phase11 scan failure preserves error instead of empty success", results, logger, async () =>
        {
            var scan = await new FailedResidualFixture().ScanResidualsAsync(Identity());
            if (scan.State != ResidualScanState.Failed || scan.Completed || !scan.Error.Contains("isolated scan failure")) throw new Exception("Scan exception was lost or became success.");
            return "Injected I/O failure became Failed, with original error and no completion claim.";
        });
        await RunTest("Phase11 cancelled and unidentified scans are not completed", results, logger, async () =>
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var cancelled = await new FailedResidualFixture().ScanResidualsAsync(Identity(), cancel.Token);
            var absentIdentity = await new ResidualScanService().ScanResidualsAsync(new InstalledApp());
            if (cancelled.State != ResidualScanState.Cancelled || absentIdentity.State != ResidualScanState.NotScanned) throw new Exception("Cancelled or unidentified scan became completed.");
            return "Pre-cancellation never enters the scanner; missing installation identity remains NotScanned.";
        });
        await RunTest("Phase11 residual states persist without rewriting legacy history", results, logger, () =>
        {
            var legacy = _db.AddRecord(new() { AppName = "Phase11 legacy fixture", ResidualStatus = "Legacy evidence unchanged", Notes = "original" });
            foreach (var state in Enum.GetValues<ResidualScanState>())
            {
                var scan = new ResidualScanResult(state, []);
                var id = _db.AddRecord(new() { AppName = "Phase11 " + state, ResidualStatus = scan.StorageStatus });
                _db.AppendRecordNote(id, "read-only fixture evidence");
                if (_db.GetHistory().Single(record => record.Id == id).ResidualStatus != scan.StorageStatus || scan.Message("ar").StartsWith("residualScan")) throw new Exception("Scan state or localization was lost.");
            }
            if (_db.GetHistory().Single(record => record.Id == legacy).ResidualStatus != "Legacy evidence unchanged") throw new Exception("Legacy history was rewritten.");
            return "All seven states round-trip in the existing TEXT column; legacy values remain untouched.";
        });
        await RunTest("Phase11 unknown usage is unavailable, not infrequent", results, logger, () =>
        {
            var old = Identity(); old.InstallDate = "19900101";
            if (InventoryIntegrity.UsageClassificationAvailable || AppListViewModel.QueryApps([old], AppCategory.Infrequent, SortMode.NameAsc, "").Count != 0) throw new Exception("Age was used as usage evidence.");
            return "Old installation dates do not produce infrequently-used applications.";
        });
        await RunTest("Phase11 unrelated same-day registrations are not bundleware", results, logger, () =>
        {
            var names = Enumerable.Range(0, 2).Select(_ => "Uninstaller_Test_Phase11_" + Guid.NewGuid().ToString("N")).ToArray();
            const string parent = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";
            try
            {
                foreach (var name in names)
                {
                    using var key = Registry.CurrentUser.CreateSubKey(parent + name);
                    key.SetValue("DisplayName", name); key.SetValue("InstallDate", "20261025");
                    key.SetValue("UninstallString", @"C:\Phase11_NonexistentFixture\uninstall.exe");
                }
                var found = new RegistryDiscoveryService().DiscoverApps(false).Where(app => names.Contains(app.Name)).ToArray();
                if (found.Length != 2 || found.Any(app => app.IsBundleware) || InventoryIntegrity.BundleClassificationAvailable || AppListViewModel.QueryApps(found, AppCategory.Bundleware, SortMode.NameAsc, "").Count != 0) throw new Exception("Same-day registration became bundle evidence.");
                return "Two actual unique HKCU fixture registrations with the same date remained unrelated; no uninstaller ran.";
            }
            finally { foreach (var name in names) Registry.CurrentUser.DeleteSubKeyTree(parent + name, throwOnMissingSubKey: false); }
        });
        await RunTest("Phase11 historical snapshots never create installed applications", results, logger, () =>
        {
            var historyId = _db.AddLoggedInstallation("Removed snapshot fixture", "", "[]", "[]", "historical evidence");
            var current = Identity();
            var result = InventoryIntegrity.CurrentRegistrations([current, new() { Id = "historical", Name = "Removed snapshot fixture", Source = "Logged" }]);
            if (result.Count != 1 || result[0].Id != current.Id || !_db.GetLoggedInstallations().Any(record => record.Id == historyId)) throw new Exception("History was promoted to inventory or erased.");
            return "Only current discovery survives inventory composition; the historical database record is preserved.";
        });
        await RunTest("Phase11 snapshot linking uses identity across rename upgrade and removal", results, logger, () =>
        {
            var current = Identity(); current.Name = "Renamed fixture"; current.Version = "2.0";
            var snapshot = new DatabaseService.LoggedInstallation(1, "Original fixture name", "", "2026-01-01", "[]", JsonSerializer.Serialize(new[] { current.RegistryKeyPath.Replace("HKCU\\", "HKEY_CURRENT_USER\\") }), "observed only");
            var sameNameOtherIdentity = Identity(); sameNameOtherIdentity.Name = snapshot.AppName;
            var matches = InstallMonitorService.MatchCurrentRegistrations(snapshot, [current, sameNameOtherIdentity]);
            if (matches.Count != 1 || matches[0].Version != "2.0" || matches[0].Id != current.Id || InstallMonitorService.MatchCurrentRegistrations(snapshot, [sameNameOtherIdentity]).Count != 0 || InstallMonitorService.MatchCurrentRegistrations(snapshot with { TrackedRegistryJson = "[]" }, [sameNameOtherIdentity]).Count != 0) throw new Exception("Snapshot linking used a name or stale installation.");
            return "Stable registration matched the current renamed/upgraded identity; removal and name-only records had no current match.";
        });
        await RunTest("Phase11 unresolved acknowledgment preserves unknown evidence and archives", results, logger, () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "U77_Phase11_Review_" + Guid.NewGuid().ToString("N"));
            try
            {
                var journal = new QueueJournal(directory); var now = DateTimeOffset.UtcNow;
                var items = new[] { new QueueJournalItem("operation-1", Identity(), UninstallOperationState.Running, now, null, "original evidence", 0), new QueueJournalItem("operation-2", Identity(), UninstallOperationState.Idle, null, null) };
                File.WriteAllText(Path.Combine(directory, "queue.json"), JsonSerializer.Serialize(new { QueueId = "legacy-queue", CreatedAt = now, Active = true, StopRequested = false, Items = items, UnknownLegacyEvidence = "preserve these original bytes" }));
                if (journal.Read()!.SchemaVersion != 1) throw new Exception("Legacy schema was not recognized.");
                journal.RecordReview("legacy-queue", "RecoveryDetected", "detected, not actual interruption time");
                var pending = journal.RecordReview("legacy-queue", "KeepPending", "keep original evidence");
                if (!QueueJournal.NeedsReview(pending)) throw new Exception("Keeping pending unblocked the queue.");
                var acknowledged = journal.RecordReview("legacy-queue", "AcknowledgedUnresolved", "explicit user acknowledgment, outcome unknown");
                if (QueueJournal.NeedsReview(acknowledged) || acknowledged.Active || !acknowledged.StopRequested || acknowledged.Items[0].State != UninstallOperationState.Running || acknowledged.Items[0].FinishedAt != null || acknowledged.Items[1].StartedAt != null || acknowledged.Items[0].Message != "original evidence") throw new Exception("Acknowledgment changed the outcome or launched a waiting item.");
                journal.Save(new("new-manual-queue", now, false, false, []));
                var evidence = Directory.GetFiles(Path.Combine(directory, "Evidence")).Select(File.ReadAllText).ToArray();
                if (!evidence.Any(text => text.Contains("AcknowledgedUnresolved") && text.Contains("original evidence"))) throw new Exception("Acknowledged evidence was lost when a new queue was saved.");
                if (!evidence.Any(text => text.Contains("UnknownLegacyEvidence") && text.Contains("preserve these original bytes"))) throw new Exception("Unrecognized original evidence fields were lost.");
                return "Legacy schema read; keep remained blocking; acknowledgment preserved original states and never started the next item; archived evidence survived replacement.";
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        await RunTest("Phase11 review rejects stale identity and active execution lease", results, logger, () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "U77_Phase11_Lease_" + Guid.NewGuid().ToString("N"));
            try
            {
                var journal = new QueueJournal(directory); journal.Save(new("queue", DateTimeOffset.UtcNow, true, false, []));
                var staleRejected = false; try { journal.RecordReview("other", "AcknowledgedUnresolved", ""); } catch (InvalidOperationException) { staleRejected = true; }
                var leaseRejected = false;
                using (journal.AcquireExecutionLease()) try { journal.RecordReview("queue", "AcknowledgedUnresolved", ""); } catch (IOException) { leaseRejected = true; }
                if (!staleRejected || !leaseRejected || journal.Read()!.AcknowledgedUnresolvedAt is not null) throw new Exception("Review overlapped active execution or accepted the wrong identity.");
                return "Stale queue IDs and active execution locks cannot be acknowledged.";
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        await RunTest("Phase11 read-only review does not turn absence into success", results, logger, async () =>
        {
            var observation = await QueueReviewVerifier.ObserveAsync(Identity());
            if (observation.State != "Absent" || !observation.Evidence.Contains("unresolved")) throw new Exception("Read-only observation implied historical success.");
            return "Unique absent fixture registration observed without launching any uninstaller; historical result remains unresolved.";
        });
        await RunTest("Phase11 CSV export preserves filtered scope and escapes cells", results, logger, () =>
        {
            var selected = Identity(); selected.Name = "match,\"quoted\"\nname"; var other = Identity(); other.Name = "excluded";
            var filtered = AppListViewModel.QueryApps([selected, other], AppCategory.All, SortMode.NameAsc, "match");
            var rows = CsvExportService.FilteredResults(filtered);
            if (rows.Count != 2 || !rows[1].Contains("\"\"quoted\"\"") || rows[1].Contains("excluded")) throw new Exception("CSV escaping or scope was wrong.");
            foreach (var formula in new[] { "=SUM(1,2)", "+cmd", "-1+1", "@SUM(1)", "  =1+1", "\ttext", "\rtext", "\ntext", "\uFEFF=1" })
                if (!CsvExportService.Cell(formula).StartsWith("\"'", StringComparison.Ordinal)) throw new Exception("Formula protection missed leading whitespace/control characters.");
            return "Only filtered results exported; commas, quotes, newlines and spreadsheet formula prefixes were handled.";
        });
    }
}
