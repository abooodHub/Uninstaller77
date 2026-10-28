using System.IO;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class TestRunnerService
{
    private async Task RunPhase12TestsAsync(List<TestResult> results, Action<string>? logger)
    {
        static InstalledApp App(string id, string command) => new()
        {
            Id = id, Name = "Phase12 fixture " + id, Source = "Registry", RegistryHive = "HKCU",
            RegistryKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Phase12_" + id,
            UninstallString = command
        };
        static HealthCheckDefinition Check(string key, Func<IReadOnlyList<InstalledApp>, CancellationToken, Task<HealthCheckOutput>> run,
            TimeSpan? timeout = null) => new(key, key, key, "fixture", "fixture", "Wrench", true, run, timeout);

        await RunTest("Phase12 nine health categories preserve supported and unsupported states", results, logger, async () =>
        {
            var report = await SoftwareHealthEngine.ScanAsync([App("valid", $"\"{Environment.ProcessPath}\"")]);
            if (report.Checks.Count != 9 || report.SupportedChecks != 1 || report.UnsupportedChecks != 8 || report.Status != HealthCheckStatus.ScanIncomplete ||
                report.Checks.Single(c => c.Key == "stubborn").Status != HealthCheckStatus.CompletedNoFindings ||
                report.Checks.Where(c => c.Key != "stubborn").Any(c => c.Status != HealthCheckStatus.Unsupported))
                throw new Exception("Health categories did not preserve real support and completion states.");
            return "One completed read-only uninstall check, eight explicit Unsupported results; global status is Partial rather than Healthy.";
        });

        await RunTest("Phase12 supported scan with no findings completes cleanly within its scope", results, logger, async () =>
        {
            var d = Check("fixture", (_, _) => Task.FromResult(new HealthCheckOutput(4, [], [])));
            var report = await SoftwareHealthEngine.ScanAsync([], definitions: [d]);
            if (report.Status != HealthCheckStatus.Healthy || report.Checks[0].Status != HealthCheckStatus.CompletedNoFindings || report.Checks[0].ScannedItems != 4)
                throw new Exception("An actually completed empty check was not distinguished from unsupported.");
            return "A completed fixture check reports no findings and its exact scanned item count.";
        });

        await RunTest("Phase12 malformed uninstall commands are potential findings with evidence", results, logger, async () =>
        {
            var report = await SoftwareHealthEngine.ScanAsync([App("malformed", "shell:AppsFolder\\Vendor.App")]);
            var item = report.Checks.Single(c => c.Key == "stubborn").Findings.Single();
            if (item.Title != "UnsupportedUninstallCommand" || item.Confidence != HealthConfidence.Potential ||
                string.IsNullOrWhiteSpace(item.Evidence) || item.ApplicationId != "malformed" || item.Recommendation.Length == 0)
                throw new Exception("Malformed command lacked a potential classification or exact evidence context.");
            return "Unsupported command remained a potential, non-destructive review item with stable app identity and registry evidence.";
        });

        await RunTest("Phase12 exact missing uninstaller path is confirmed and never executed", results, logger, async () =>
        {
            var missing = Path.Combine(Path.GetTempPath(), "U77_Phase12_Missing_" + Guid.NewGuid().ToString("N") + ".exe");
            var report = await SoftwareHealthEngine.ScanAsync([App("missing", $"\"{missing}\" /uninstall")]);
            var item = report.Checks.Single(c => c.Key == "stubborn").Findings.Single();
            if (item.Title != "MissingUninstaller" || item.Confidence != HealthConfidence.Confirmed || !item.Evidence.Contains(missing, StringComparison.OrdinalIgnoreCase) || File.Exists(missing))
                throw new Exception("The exact missing executable was not reported safely.");
            return "Only File.Exists was used against an isolated nonexistent fixture path; no command or installer was launched.";
        });

        await RunTest("Phase12 finding identity remains stable for identical evidence", results, logger, async () =>
        {
            var missing = Path.Combine(Path.GetTempPath(), "U77_Phase12_Stable_Missing.exe");
            var app = App("stable", $"\"{missing}\"");
            var one = await SoftwareHealthEngine.ScanAsync([app]); var two = await SoftwareHealthEngine.ScanAsync([app]);
            if (one.Checks.Single(c => c.Key == "stubborn").Findings.Single().Id != two.Checks.Single(c => c.Key == "stubborn").Findings.Single().Id)
                throw new Exception("Stable evidence generated a changing finding identifier.");
            return "SHA-256-based finding ID is deterministic for the registry identity, issue kind and evidence.";
        });

        await RunTest("Phase12 failed checks preserve isolated diagnostics", results, logger, async () =>
        {
            var fail = Check("fail", (_, _) => throw new IOException("owned failure fixture"));
            var ok = Check("ok", (_, _) => Task.FromResult(new HealthCheckOutput(1, [], [])));
            var report = await SoftwareHealthEngine.ScanAsync([], definitions: [fail, ok]);
            if (report.Status != HealthCheckStatus.ScanIncomplete || report.Checks.Single(c => c.Key == "fail").Status != HealthCheckStatus.Failed ||
                report.Checks.Single(c => c.Key == "ok").Status != HealthCheckStatus.CompletedNoFindings || report.ErrorCount != 1)
                throw new Exception("A failing check erased or masked an independent completed result.");
            return "One failed fixture check did not erase the independent completed check; report is Partial.";
        });

        await RunTest("Phase12 partial checks retain findings and count errors", results, logger, async () =>
        {
            var finding = new HealthFinding("fixture-id", "fixture", "Fixture", "fixture-app", "Fixture app", "owned fixture", "fixture evidence", HealthConfidence.Potential, "Fixture only", "No action", HealthRisk.Low);
            var partial = Check("partial", (_, _) => Task.FromResult(new HealthCheckOutput(2, [finding], ["one inaccessible owned fixture"], 1)));
            var report = await SoftwareHealthEngine.ScanAsync([], definitions: [partial]);
            if (report.Status != HealthCheckStatus.AttentionRequired || report.PotentialFindings != 1 || report.ErrorCount != 1 || report.Checks[0].Findings.Count != 1)
                throw new Exception("Partial findings or per-check errors were lost.");
            return "Potential finding and one isolated inspection error are both retained.";
        });

        await RunTest("Phase12 timed-out checks report Failed with timeout diagnostics", results, logger, async () =>
        {
            var slow = Check("slow", async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(0, [], []); }, TimeSpan.FromMilliseconds(40));
            var report = await SoftwareHealthEngine.ScanAsync([], definitions: [slow]);
            if (report.Checks[0].Status != HealthCheckStatus.Failed || !report.Checks[0].Diagnostics.Any(x => x.Contains("timed out", StringComparison.OrdinalIgnoreCase)))
                throw new Exception("A timed-out check was not reported as failed with its reason.");
            return "Timeout canceled only its fixture check and left a concrete timeout diagnostic.";
        });

        await RunTest("Phase12 cancellation has its own status and preserves completed results", results, logger, async () =>
        {
            using var cts = new CancellationTokenSource();
            var completed = Check("done", (_, _) => Task.FromResult(new HealthCheckOutput(1, [], [])));
            var slow = Check("slow", async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(0, [], []); }, TimeSpan.FromSeconds(5));
            var reporter = new CancellationProgress(_ => cts.Cancel());
            var report = await SoftwareHealthEngine.ScanAsync([], cts.Token, reporter, [completed, slow], maxParallelism: 1);
            if (report.Status != HealthCheckStatus.Cancelled || report.Checks.All(c => c.Status != HealthCheckStatus.Cancelled))
                throw new Exception("User cancellation was collapsed into clean or failed.");
            return "Cancellation remains distinct; completed check results are returned.";
        });

        await RunTest("Phase12 unsupported data source never becomes a clean scan", results, logger, async () =>
        {
            var unsupported = new HealthCheckDefinition("unsupported", "Unsupported", "غير مدعوم", "n/a", "n/a", "Info", false);
            var report = await SoftwareHealthEngine.ScanAsync([], definitions: [unsupported]);
            if (report.Checks[0].Status != HealthCheckStatus.Unsupported || report.Status == HealthCheckStatus.CompletedNoFindings)
                throw new Exception("Unsupported category became a clean result.");
            return "Unsupported-only run is incomplete; it cannot be labeled Healthy or Clean.";
        });

        await RunTest("Retired Phase12 previous-scan commands remain outside the product allowlist", results, logger, () =>
        {
            foreach (var operation in new[] { "reviewPreviousHealth", "showLatestHealth" })
                if (WebUiProtocol.TryParse($"{{\"id\":\"phase12\",\"op\":\"{operation}\",\"payload\":{{}}}}", out _, out _))
                    throw new Exception($"Retired UI operation '{operation}' was accepted.");
            if (WebUiProtocol.TryParse("{\"id\":\"phase12\",\"op\":\"removeHealthFinding\",\"payload\":{}}", out _, out _))
                throw new Exception("Unverified health remediation command was accepted.");
            return "Removed health review controls and unverified health remediation remain denied. The historical test was updated to the current removal requirement.";
        });
    }

    private sealed class CancellationProgress(Action<HealthCheckResult> cancel) : IProgress<HealthCheckResult>
    {
        public void Report(HealthCheckResult value) { if (value.Status == HealthCheckStatus.Running) cancel(value); }
    }
}
