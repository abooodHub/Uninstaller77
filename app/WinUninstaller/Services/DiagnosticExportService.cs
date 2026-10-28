using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace WinUninstaller.Services;

/// <summary>Local-only diagnosis. No export, deployment, removal, or queue resume.</summary>
public static class DiagnosticExportService
{
    public static async Task<string> CollectAsync()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller");
        var folder = Path.Combine(data, "Diagnostics", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(folder);
        var processes = new List<object>();
        foreach (var process in Process.GetProcessesByName("WinUninstaller"))
        {
            using (process)
                try { processes.Add(new { process.Id, path = process.MainModule?.FileName, version = process.MainModule?.FileVersionInfo.FileVersion }); }
                catch (Exception ex) { processes.Add(new { process.Id, error = ex.Message }); }
        }
        File.WriteAllText(Path.Combine(folder, "environment.json"), JsonSerializer.Serialize(new
        { timestamp = DateTimeOffset.UtcNow, os = Environment.OSVersion.ToString(), collector = Environment.ProcessPath, processes }, new JsonSerializerOptions { WriteIndented = true }));
        var details = new List<string>();
        var log = Path.Combine(data, "Logs", "webview.log");
        if (File.Exists(log))
        {
            var errors = File.ReadLines(log).Where(line => line.Contains("operation=reviewQueue", StringComparison.Ordinal) ||
                line.Contains("operation=stopBatch", StringComparison.Ordinal) || line.Contains("operation=retryQueueVerification", StringComparison.Ordinal)).TakeLast(8).ToArray();
            File.WriteAllLines(Path.Combine(folder, "queue-handler-errors.txt"), errors);
            details.AddRange(errors.Select(line => line[..Math.Min(line.Length, 400)]));
        }
        var journal = Path.Combine(data, "Queue", "queue.json");
        if (File.Exists(journal))
        {
            var snapshot = JsonSerializer.Deserialize<QueueJournalSnapshot>(File.ReadAllText(journal));
            var item = snapshot?.Items.FirstOrDefault(entry => entry.State == UninstallOperationState.NeedsUserAttention);
            if (item is not null)
            {
                var query = item.App.Source == "MSIX" || item.App.InstallerType == Models.InstallType.Msix
                    ? await MsixDeploymentService.QueryAsync(item.App.Id) : null;
                File.WriteAllText(Path.Combine(folder, "affected-operation.json"), JsonSerializer.Serialize(new
                { item.OperationId, appId = item.App.Id, item.App.Source, item.State, item.ExitCode, item.Message, query }, new JsonSerializerOptions { WriteIndented = true }));
                details.Add(item.App.Id + "\n" + item.Message);
                if (query is not null) details.Add(query.FailureCode + ": " + query.Error + "\nState: " + query.Verification);
            }
        }
        return "Local diagnostics: " + folder + "\n\n" + string.Join("\n\n", details);
    }
}
