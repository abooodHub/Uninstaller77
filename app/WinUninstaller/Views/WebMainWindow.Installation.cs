using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using WinUninstaller.Services;

namespace WinUninstaller.Views;

public partial class WebMainWindow
{
    private CancellationTokenSource? _installationFinish;
    private CancellationTokenSource? _installationCancel;
    private InstallationReport? _activeInstallation;
    private InstallationReport[] _installationLogs = [];
    private bool _installationLogsRead;

    private void LoadInstallationLogs()
    {
        if (_installationLogsRead) return;
        try
        {
            _installationLogs = _apps.InstallationLogs.Recent(error => _apps.SetWebNotice(error, true));
            _installationLogsRead = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { _apps.SetWebNotice(ex.Message, true); }
    }

    private Task ChooseInstallationScopeAsync(string registryScope)
    {
        var picker = new OpenFolderDialog { Title = UiStrings.Get(_main.LanguageCode, "monitorChooseDirectory") };
        if (picker.ShowDialog(this) == true && !_closed)
            _ = ExecuteUiOperationAsync(() => RecordInstallationAsync(picker.FolderName, registryScope));
        return Task.CompletedTask;
    }

    private async Task RecordInstallationAsync(string root, string registryScope)
    {
        _apps.RequireMaintenanceIdle();
        using var finish = new CancellationTokenSource();
        using var cancel = new CancellationTokenSource();
        _installationFinish = finish;
        _installationCancel = cancel;
        _activeInstallation = null;
        _apps.IsInstallationRecording = true;
        ScheduleState();
        try
        {
            var recorder = new InstallationRecorder(_apps.InstallationLogs);
            await Task.Run(() => recorder.RecordAsync(root, registryScope, report =>
                Dispatcher.Invoke(() => { _activeInstallation = report; ScheduleState(); }, DispatcherPriority.Background), finish.Token, cancel.Token));
        }
        finally
        {
            _installationFinish = null;
            _installationCancel = null;
            _apps.IsInstallationRecording = false;
            _installationLogsRead = false;
            LoadInstallationLogs();
        }
    }

    private Task ExportInstallationReportAsync(InstallationReport report)
    {
        var picker = new SaveFileDialog
        {
            Title = UiStrings.Get(_main.LanguageCode, "monitorExportTitle"),
            Filter = "JSON (*.json)|*.json",
            FileName = "Uninstaller77-Installation-" + report.Id + ".json",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (picker.ShowDialog(this) == true)
        {
            File.WriteAllBytes(picker.FileName, JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true }));
            _apps.SetWebNotice(UiStrings.Get(_main.LanguageCode, "monitorExported"));
        }
        return Task.CompletedTask;
    }

    private object InstallationPageDto() => new
    {
        busy = _installationFinish is not null,
        finishing = _installationFinish?.IsCancellationRequested == true,
        cancelling = _installationCancel?.IsCancellationRequested == true,
        active = _activeInstallation is null ? null : InstallationReportDto(_activeInstallation),
        reports = _installationLogs.Where(report => report.Id != _activeInstallation?.Id).Take(10).Select(InstallationReportDto).ToArray()
    };

    private static object InstallationReportDto(InstallationReport report) => new
    {
        report.Id, report.Root, report.RegistryScope, report.State, report.StartedAt, report.FinishedAt,
        report.BaselineFiles, report.BaselineKeys, program = report.Registration?.Name,
        count = report.Changes.Length, linkedCount = report.Changes.Count(change => change.Association == "RegistrationLinked"), issues = report.Issues,
        changes = report.Changes.OrderByDescending(change => change.At).Take(80).Select(change => new
        { change.Kind, change.Change, change.Path, change.At, change.Phase, change.Association, change.View }).ToArray()
    };
}
