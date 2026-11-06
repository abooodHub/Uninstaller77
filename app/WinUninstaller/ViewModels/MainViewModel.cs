using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows;
using WinUninstaller.Services;

namespace WinUninstaller.ViewModels;

public enum Page { AppList, History }

public partial class MainViewModel : ObservableObject
{
    private readonly AppListViewModel _appList;
    private readonly HistoryViewModel _history;
    private readonly ResidualScanService _residuals;
    private bool _loadingPreferences;
    public bool UseWebUi { get; set; }

    [ObservableProperty]
    private Page _currentPage = Page.AppList;

    [ObservableProperty]
    private ObservableObject _currentViewModel;

    [ObservableProperty]
    private string _activeSidebarItem = "AllApps";

    [ObservableProperty]
    private string _languageCode = "en";

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private bool _reduceMotion;

    [ObservableProperty] private bool _protectBeforeUninstall;
    public string ProtectionSettingLabel => UiStrings.Get(LanguageCode, "protectionSetting");
    public string ProtectionSettingHint => UiStrings.Get(LanguageCode, "protectionHint");

    public FlowDirection LayoutDirection => LanguageCode == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    public string AppSectionTitle => UiStrings.Get(LanguageCode, "apps");
    public string AllAppsLabel => UiStrings.Get(LanguageCode, "allApps");
    public string RecentAppsLabel => UiStrings.Get(LanguageCode, "recentApps");
    public string LargeAppsLabel => UiStrings.Get(LanguageCode, "largeApps");
    public string StoreAppsLabel => UiStrings.Get(LanguageCode, "storeApps");
    public string HistoryLabel => UiStrings.Get(LanguageCode, "history");
    public string RestoreLabel => UiStrings.Get(LanguageCode, "restore");
    public string SettingsLabel => UiStrings.Get(LanguageCode, "settings");
    public string SettingsTitle => UiStrings.Get(LanguageCode, "settingsTitle");
    public string LanguageLabel => UiStrings.Get(LanguageCode, "language");
    public string ManagementSubtitle => UiStrings.Get(LanguageCode, "managementSubtitle");
    public string AuditLogsLabel => UiStrings.Get(LanguageCode, "auditLogs");
    public string ReviewBeforeCleanupLabel => UiStrings.Get(LanguageCode, "reviewBeforeCleanup");
    public string PotentialLeftoversHint => UiStrings.Get(LanguageCode, "potentialLeftoversHint");
    public string CloseLabel => UiStrings.Get(LanguageCode, "close");

    partial void OnLanguageCodeChanged(string value)
    {
        _appList.LanguageCode = value;
        _history.LanguageCode = value;
        OnPropertyChanged(nameof(LayoutDirection));
        OnPropertyChanged(nameof(AppSectionTitle)); OnPropertyChanged(nameof(AllAppsLabel));
        OnPropertyChanged(nameof(RecentAppsLabel)); OnPropertyChanged(nameof(LargeAppsLabel));
        OnPropertyChanged(nameof(StoreAppsLabel)); OnPropertyChanged(nameof(HistoryLabel));
        OnPropertyChanged(nameof(RestoreLabel)); OnPropertyChanged(nameof(SettingsLabel));
        OnPropertyChanged(nameof(SettingsTitle)); OnPropertyChanged(nameof(LanguageLabel));
        OnPropertyChanged(nameof(ManagementSubtitle)); OnPropertyChanged(nameof(AuditLogsLabel));
        OnPropertyChanged(nameof(ReviewBeforeCleanupLabel)); OnPropertyChanged(nameof(PotentialLeftoversHint)); OnPropertyChanged(nameof(CloseLabel));
        OnPropertyChanged(nameof(ProtectionSettingLabel)); OnPropertyChanged(nameof(ProtectionSettingHint));
        if (_registerExplorer) ShellUninstallContextMenuService.Register(value);
        SavePreferences();
    }

    partial void OnReduceMotionChanged(bool value)
    {
        _appList.ReduceMotion = value;
        SavePreferences();
    }

    partial void OnProtectBeforeUninstallChanged(bool value)
    {
        _appList.ProtectBeforeUninstall = value;
        SavePreferences();
    }

    public AppListViewModel AppList => _appList;
    public HistoryViewModel History => _history;

    public MainViewModel(AppListViewModel appList, HistoryViewModel history, ResidualScanService residuals, string? isolatedProfile = null)
    {
        _appList = appList;
        _history = history;
        _residuals = residuals;
        _registerExplorer = isolatedProfile is null;
        _preferencesPath = Path.Combine(isolatedProfile ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller"), "settings.json");
        _currentViewModel = appList;
        LoadPreferences();
        _appList.ReduceMotion = ReduceMotion;
        if (_registerExplorer) ShellUninstallContextMenuService.Register(LanguageCode);
    }

    [RelayCommand]
    public void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    public void CloseSettings() => IsSettingsOpen = false;

    private readonly bool _registerExplorer;
    private readonly string _preferencesPath;

    private void LoadPreferences()
    {
        _loadingPreferences = true;
        try
        {
            if (!File.Exists(_preferencesPath)) return;
            var values = ReadPreferences(_preferencesPath);
            LanguageCode = values.Language;
            ReduceMotion = values.ReduceMotion;
            ProtectBeforeUninstall = values.ProtectBeforeUninstall;
        }
        catch { /* Invalid preferences fall back to English defaults. */ }
        finally { _loadingPreferences = false; }
    }

    private void SavePreferences()
    {
        if (_loadingPreferences) return;
        var temporary = _preferencesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_preferencesPath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { language = LanguageCode, reduceMotion = ReduceMotion, protectBeforeUninstall = ProtectBeforeUninstall }));
            File.Move(temporary, _preferencesPath, overwrite: true);
        }
        catch { /* A read-only profile must not prevent use of the app. */ }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    internal static (string Language, bool ReduceMotion, bool ProtectBeforeUninstall) ReadPreferences(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > 16_384) throw new JsonException("Preferences are too large.");
        var bytes = new byte[16_385];
        var count = input.ReadAtLeast(bytes, 16_385, throwOnEndOfStream: false);
        if (count > 16_384) throw new JsonException("Preferences are too large.");
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(bytes.AsSpan(0, count)) ?? throw new JsonException("Invalid preferences.");
        var language = values.TryGetValue("language", out var lang) ? lang.GetString() : "en";
        if (language is not ("en" or "ar")) throw new JsonException("Unsupported language.");
        var motion = values.TryGetValue("reduceMotion", out var value) && value.GetBoolean();
        var protection = values.TryGetValue("protectBeforeUninstall", out var protect) && protect.GetBoolean();
        return (language, motion, protection);
    }

    [RelayCommand]
    private async Task RestoreQuarantineAsync()
    {
        _appList.RequireMaintenanceIdle();
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinUninstaller", "Quarantine");
        Directory.CreateDirectory(directory);
        var picker = new OpenFileDialog
        {
            Title = UiStrings.Get(LanguageCode, "restorePickerTitle"),
            InitialDirectory = directory,
            Filter = UiStrings.Get(LanguageCode, "restorePickerFilter")
        };
        if (picker.ShowDialog() != true) return;
        if (_appList.IsUnregisteredBackup(picker.FileName))
        { await _appList.RestoreUnregisteredAsync(picker.FileName); return; }
        await _appList.RestoreResidualBackupAsync(picker.FileName);
    }

    internal async Task ReviewPendingDeletionAsync()
    {
        _appList.RequireMaintenanceIdle();
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinUninstaller", "PendingRemoval");
        var picker = new OpenFileDialog
        {
            Title = UiStrings.Get(LanguageCode, "pendingDeletionPickerTitle"),
            InitialDirectory = directory,
            Filter = UiStrings.Get(LanguageCode, "restorePickerFilter")
        };
        if (picker.ShowDialog() == true) await _appList.ReviewPendingDeletionAsync(picker.FileName);
    }

    [RelayCommand]
    public void NavigateTo(string target)
    {
        ActiveSidebarItem = target;

        switch (target)
        {
            case "History":
                CurrentPage = Page.History;
                CurrentViewModel = _history;
                _history.LoadHistory();
                break;

            case "RecentApps":
            case "Recent":
                CurrentPage = Page.AppList;
                CurrentViewModel = _appList;
                _appList.SetCategory("Recent");
                break;

            case "LargeApps":
            case "Large":
                CurrentPage = Page.AppList;
                CurrentViewModel = _appList;
                _appList.SetCategory("Large");
                break;

            case "Bundleware":
                CurrentPage = Page.AppList;
                CurrentViewModel = _appList;
                _appList.SetCategory("Bundleware");
                break;

            case "LoggedApps":
            case "Logged":
                CurrentPage = Page.AppList;
                CurrentViewModel = _appList;
                _appList.SetCategory("Logged");
                break;

            case "InfrequentApps":
            case "Infrequent":
                CurrentPage = Page.AppList;
                CurrentViewModel = _appList;
                _appList.SetCategory("Infrequent");
                break;

            case "WindowsApps":
                CurrentPage = Page.AppList;
                CurrentViewModel = _appList;
                _appList.SetCategory("WindowsApps");
                break;

            case "ThirdPartyApps":
                CurrentPage = Page.AppList;
                CurrentViewModel = _appList;
                _appList.SetCategory("ThirdPartyApps");
                break;

            case "StoreApps":
            case "Store":
                CurrentPage = Page.AppList;
                CurrentViewModel = _appList;
                _appList.SetCategory("Store");
                break;

            case "AllApps":
            default:
                CurrentPage = Page.AppList;
                CurrentViewModel = _appList;
                _appList.SetCategory("All");
                break;
        }
    }
}
