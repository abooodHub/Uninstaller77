using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public enum HealthCheckStatus { NotScanned, Running, CompletedNoFindings, CompletedWithFindings, Unsupported, Failed, Cancelled, Partial, Healthy, AttentionRequired, ScanIncomplete }
public enum HealthConfidence { Confirmed, Potential }
public enum HealthRisk { Informational, Low, Medium, High }

public sealed record HealthFinding(string Id, string Category, string Title, string? ApplicationId, string? ApplicationName,
    string Source, string Evidence, HealthConfidence Confidence, string Explanation, string Recommendation, HealthRisk Risk);
public sealed record HealthCheckOutput(int ScannedItems, IReadOnlyList<HealthFinding> Findings, IReadOnlyList<string> Diagnostics, int ErrorCount = 0);
public sealed record HealthCheckDefinition(string Key, string TitleEn, string TitleAr, string DescriptionEn, string DescriptionAr,
    string IconName, bool Supported, Func<IReadOnlyList<InstalledApp>, CancellationToken, Task<HealthCheckOutput>>? Execute = null,
    TimeSpan? Timeout = null);
public sealed record HealthCheckResult(string Key, string TitleEn, string TitleAr, string DescriptionEn, string DescriptionAr,
    string IconName, HealthCheckStatus Status, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt,
    double DurationMilliseconds, int ScannedItems, int ConfirmedFindings, int PotentialFindings, int ErrorCount,
    IReadOnlyList<HealthFinding> Findings, IReadOnlyList<string> Diagnostics)
{
    public bool IsSupported => Status != HealthCheckStatus.Unsupported;
    public int FindingCount => Findings.Count;
}
public sealed record HealthScanReport(HealthCheckStatus Status, DateTimeOffset StartedAt, DateTimeOffset EndedAt,
    double DurationMilliseconds, int ScannedItems, int SupportedChecks, int UnsupportedChecks,
    int ConfirmedFindings, int PotentialFindings, int ErrorCount, double CpuPercentOfMachine,
    long MemoryDeltaBytes, IReadOnlyList<HealthCheckResult> Checks, string? Error);

/// <summary>Read-only scan orchestration; a clean result requires completed supported checks.</summary>
public static class SoftwareHealthEngine
{
    public static IReadOnlyList<HealthCheckDefinition> CreateChecks() => new[]
    {
        Unsupported("leftovers", "Uninstallation Leftovers", "بقايا إزالة التثبيت", "No verified uninstall-history identity is available for safe residual ownership checks.", "لا تتوفر هوية موثقة من سجل الإزالة لفحص ملكية المخلفات بأمان.", "FileText"),
        Unsupported("outdated", "Outdated Software", "برامج قديمة", "No trusted version comparison source is configured.", "لا يتوفر مصدر موثوق لمقارنة الإصدارات.", "CloudDownload"),
        Unsupported("redundant", "Redundant Software Files", "ملفات البرامج الزائدة", "No application-owned disposable cache rules are configured.", "لا توجد قواعد موثقة لملفات مؤقتة مملوكة للتطبيق.", "Layers"),
        Unsupported("hibernation", "Software Hibernation", "إسبات البرامج", "Per-application hibernation is unavailable.", "إسبات التطبيقات غير متاح.", "Moon"),
        Unsupported("permissions", "Software Permissions", "أذونات البرامج", "Windows permission data is not yet associated with verified application identities.", "لم تُربط أذونات Windows بعد بهويات تطبيقات موثقة.", "Lock"),
        Unsupported("notifications", "Disturbing Notifications", "الإشعارات", "Notification frequency is unavailable and cannot be inferred from application names.", "بيانات تكرار الإشعارات غير متاحة ولا يمكن استنتاجها من أسماء التطبيقات.", "BellOff"),
        new("stubborn", "Uninstall Problems", "مشكلات إلغاء التثبيت", "Read-only validation of registered uninstall commands; no command is executed.", "تحقق للقراءة فقط من أوامر الإزالة المسجلة؛ لا يُشغّل أي أمر.", "Wrench", true, ScanUninstallEntriesAsync, TimeSpan.FromSeconds(20)),
        Unsupported("packages", "Installation Files", "ملفات التثبيت", "No user-approved scan location has been selected.", "لم يحدد المستخدم موقعًا للفحص.", "Package"),
        Unsupported("malicious", "Malware / Extensions", "البرامج الضارة والإضافات", "Security classification is not supported; extension inventory is observational only.", "التصنيف الأمني غير مدعوم؛ جرد الإضافات للملاحظة فقط.", "ShieldAlert")
    };

    private static HealthCheckDefinition Unsupported(string key, string en, string ar, string descEn, string descAr, string icon) => new(key, en, ar, descEn, descAr, icon, false);

    public static Task<HealthScanReport> ScanAsync(IEnumerable<InstalledApp> apps, CancellationToken cancellationToken = default,
        IProgress<HealthCheckResult>? progress = null, IReadOnlyList<HealthCheckDefinition>? definitions = null,
        int maxParallelism = 3)
    {
        var inventory = apps.Where(a => a.Source.Equals("Registry", StringComparison.OrdinalIgnoreCase)).ToArray();
        var checks = definitions ?? CreateChecks();
        var start = DateTimeOffset.Now;
        return RunAsync(inventory, checks, start, cancellationToken, progress, Math.Clamp(maxParallelism, 1, 4));
    }

    private static async Task<HealthScanReport> RunAsync(IReadOnlyList<InstalledApp> inventory,
        IReadOnlyList<HealthCheckDefinition> definitions, DateTimeOffset started,
        CancellationToken callerToken, IProgress<HealthCheckResult>? progress, int maxParallelism)
    {
        var stopwatch = Stopwatch.StartNew();
        using var current = Process.GetCurrentProcess();
        var cpuBefore = current.TotalProcessorTime;
        var memoryBefore = current.PrivateMemorySize64;
        var results = new HealthCheckResult[definitions.Count];
        using var gate = new SemaphoreSlim(maxParallelism);
        var tasks = definitions.Select((definition, index) => ExecuteOne(definition, index)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        stopwatch.Stop(); current.Refresh();
        var ended = DateTimeOffset.Now;
        var cpu = Math.Clamp((current.TotalProcessorTime - cpuBefore).TotalMilliseconds /
            Math.Max(1, stopwatch.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100, 0, 100);
        var all = results.ToArray();
        var errors = all.Sum(x => x.ErrorCount);
        var completed = all.Any(x => x.Status is HealthCheckStatus.CompletedNoFindings or HealthCheckStatus.CompletedWithFindings);
        var interrupted = all.Any(x => x.Status is HealthCheckStatus.Failed or HealthCheckStatus.Partial);
        var cancelled = callerToken.IsCancellationRequested || all.Any(x => x.Status == HealthCheckStatus.Cancelled);
        var hasFindings = all.Sum(x => x.Findings.Count) > 0;
        var status = cancelled ? HealthCheckStatus.Cancelled : interrupted ? completed || hasFindings ? (hasFindings ? HealthCheckStatus.AttentionRequired : HealthCheckStatus.ScanIncomplete) : HealthCheckStatus.Failed :
            hasFindings ? HealthCheckStatus.AttentionRequired : all.Any(x => x.Status == HealthCheckStatus.Unsupported) ? HealthCheckStatus.ScanIncomplete : HealthCheckStatus.Healthy;
        return new(status, started, ended, stopwatch.Elapsed.TotalMilliseconds, all.Sum(x => x.ScannedItems),
            all.Count(x => x.Status != HealthCheckStatus.Unsupported), all.Count(x => x.Status == HealthCheckStatus.Unsupported),
            all.Sum(x => x.ConfirmedFindings), all.Sum(x => x.PotentialFindings), errors, cpu,
            current.PrivateMemorySize64 - memoryBefore, all, null);

        async Task ExecuteOne(HealthCheckDefinition definition, int index)
        {
            var checkStart = DateTimeOffset.Now;
            if (!definition.Supported || definition.Execute is null)
            {
                results[index] = Result(definition, HealthCheckStatus.Unsupported, checkStart, DateTimeOffset.Now, 0, 0, [], ["No verified data source or safe detection rule is configured."]);
                progress?.Report(results[index]); return;
            }
            if (callerToken.IsCancellationRequested)
            {
                results[index] = Result(definition, HealthCheckStatus.Cancelled, checkStart, DateTimeOffset.Now, 0, 0, [], ["Cancelled before check start."]);
                progress?.Report(results[index]); return;
            }
            try { await gate.WaitAsync(callerToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                results[index] = Result(definition, HealthCheckStatus.Cancelled, checkStart, DateTimeOffset.Now, 0, 0, [], ["Cancelled before check start."]);
                progress?.Report(results[index]); return;
            }
            try
            {
                results[index] = Result(definition, HealthCheckStatus.Running, checkStart, null, 0, 0, [], []);
                progress?.Report(results[index]);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
                var limit = definition.Timeout ?? TimeSpan.FromSeconds(30);
                timeout.CancelAfter(limit);
                try
                {
                    var output = await definition.Execute(inventory, timeout.Token).WaitAsync(limit, callerToken).ConfigureAwait(false);
                    var state = output.ErrorCount > 0 ? output.Findings.Count > 0 ? HealthCheckStatus.Partial : HealthCheckStatus.Failed :
                        output.Findings.Count > 0 ? HealthCheckStatus.CompletedWithFindings : HealthCheckStatus.CompletedNoFindings;
                    results[index] = Result(definition, state, checkStart, DateTimeOffset.Now, output.ScannedItems, output.ErrorCount, output.Findings, output.Diagnostics);
                }
                catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
                { results[index] = Result(definition, HealthCheckStatus.Cancelled, checkStart, DateTimeOffset.Now, 0, 0, [], ["Cancelled by user."]); }
                catch (Exception ex)
                {
                    var timedOut = timeout.IsCancellationRequested;
                    results[index] = Result(definition, HealthCheckStatus.Failed, checkStart, DateTimeOffset.Now, 0, 1, [], [timedOut ? "Check timed out." : $"Check failed: {ex.GetType().Name}: {ex.Message}"]);
                }
            }
            finally { gate.Release(); progress?.Report(results[index]); }
        }
    }

    private static HealthCheckResult Result(HealthCheckDefinition definition, HealthCheckStatus status,
        DateTimeOffset? start, DateTimeOffset? end, int scanned, int errors, IReadOnlyList<HealthFinding> findings,
        IReadOnlyList<string> diagnostics) => new(definition.Key, definition.TitleEn, definition.TitleAr,
        definition.DescriptionEn, definition.DescriptionAr, definition.IconName, status, start, end,
        start.HasValue && end.HasValue ? (end.Value - start.Value).TotalMilliseconds : 0, scanned,
        findings.Count(x => x.Confidence == HealthConfidence.Confirmed), findings.Count(x => x.Confidence == HealthConfidence.Potential),
        errors, findings, diagnostics);

    private static Task<HealthCheckOutput> ScanUninstallEntriesAsync(IReadOnlyList<InstalledApp> inventory, CancellationToken ct) => Task.Run(async () =>
    {
        var findings = new List<HealthFinding>(); var diagnostics = new List<string>(); var scanned = 0; var errors = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in inventory)
        {
            ct.ThrowIfCancellationRequested(); scanned++;
            if (scanned % 16 == 0) await Task.Delay(5, ct);
            try
            {
                if (app.IsSystemComponent || app.InstallerType == InstallType.Msix || string.IsNullOrWhiteSpace(app.RegistryKeyPath)) continue;
                var identity = $"{app.RegistryHive}|{app.RegistryKeyPath}|{app.Id}";
                if (!seen.Add(identity))
                { findings.Add(Find(app, "ConflictingRegistration", HealthConfidence.Potential, "The same uninstall registry identity appeared more than once.", identity, "Review the duplicate registration; no change was made.", HealthRisk.Low)); continue; }
                if (string.IsNullOrWhiteSpace(app.UninstallString))
                { findings.Add(Find(app, "MissingUninstallCommand", HealthConfidence.Confirmed, "This Registry uninstall entry has no UninstallString value.", $"{app.RegistryKeyPath}\\UninstallString = <missing>", "Review the registration or use the application's verified repair or removal option.", HealthRisk.Medium)); continue; }
                var parsed = Parse(app.UninstallString);
                if (!parsed.Supported)
                { findings.Add(Find(app, "UnsupportedUninstallCommand", HealthConfidence.Potential, "The registered command is outside the safe direct-executable parser.", $"{app.RegistryKeyPath}\\UninstallString; executable token could not be validated", "Review the command in application details; it was not executed.", HealthRisk.Low)); continue; }
                if (parsed.Executable is { } exe && !File.Exists(exe))
                    findings.Add(Find(app, "MissingUninstaller", HealthConfidence.Confirmed, "The executable path in the registered uninstall command does not exist.", $"{app.RegistryKeyPath}\\UninstallString -> {exe}", "Check whether the application has moved or use its official repair process.", HealthRisk.Medium));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors++; diagnostics.Add($"{app.Id}: {ex.GetType().Name}: {ex.Message}"); }
        }
        return new HealthCheckOutput(scanned, findings, diagnostics, errors);
    }, ct);

    private static (bool Supported, string? Executable) Parse(string command)
    {
        var value = Environment.ExpandEnvironmentVariables(command.Trim());
        if (value.Length == 0 || value.IndexOfAny(['\r', '\n', '\0']) >= 0) return (false, null);
        string token;
        if (value[0] == '"')
        {
            var end = value.IndexOf('"', 1);
            if (end < 2 || (end + 1 < value.Length && !char.IsWhiteSpace(value[end + 1]))) return (false, null);
            token = value[1..end];
        }
        else
        {
            var exeEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            var msiEnd = value.IndexOf(".msi", StringComparison.OrdinalIgnoreCase);
            var end = exeEnd < 0 ? msiEnd : msiEnd < 0 ? exeEnd : Math.Min(exeEnd, msiEnd);
            if (end < 0 || (end + 4 < value.Length && !char.IsWhiteSpace(value[end + 4]))) return (false, null);
            token = value[..(end + 4)].Trim();
        }
        if (token.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) return (false, null);
        if (token.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            return (true, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"));
        if (!token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(token)) return (false, null);
        return (true, Path.GetFullPath(token));
    }

    private static HealthFinding Find(InstalledApp app, string kind, HealthConfidence confidence, string why,
        string evidence, string recommendation, HealthRisk risk)
    {
        var source = $"{app.RegistryHive}|{app.RegistryKeyPath}|{app.Id}|{kind}|{evidence}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..24];
        return new(id, "stubborn", kind, app.Id, app.Name, "Windows Registry uninstall registration", evidence,
            confidence, why, recommendation, risk);
    }
}
