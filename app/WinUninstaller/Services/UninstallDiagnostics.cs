using System.IO;
using System.Text.Json;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

internal static class UninstallDiagnostics
{
    private static readonly object Gate = new();
    internal static void Write(string operationId, InstalledApp app, string phase, object evidence)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller", "Logs");
            Directory.CreateDirectory(folder);
            var line = JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, operationId, processId = Environment.ProcessId,
                version = typeof(UninstallService).Assembly.GetName().Version?.ToString(), appId = app.Id, appName = app.Name,
                source = app.Source, registration = app.RegistryKeyPath, phase, evidence });
            lock (Gate) File.AppendAllText(Path.Combine(folder, "uninstall-operations.jsonl"), line + Environment.NewLine);
        }
        catch { /* Diagnostic I/O must never change an execution or its result. */ }
    }

    internal sealed class Forward<T>(Action<T> observe, IProgress<T>? target) : IProgress<T>
    { public void Report(T value) { observe(value); target?.Report(value); } }
}
