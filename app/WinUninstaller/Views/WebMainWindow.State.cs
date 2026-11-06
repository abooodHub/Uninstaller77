using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinUninstaller.Models;
using WinUninstaller.Services;
using WinUninstaller.ViewModels;

namespace WinUninstaller.Views;

public partial class WebMainWindow
{
    private object BuildState() => new
    {
        revision = ++_stateRevision,
        measurePerformance = _measurePerformance,
        language = _main.LanguageCode,
        notice = _apps.WebNotice,
        noticeIsError = _apps.WebNoticeIsError,
        direction = _main.LayoutDirection == FlowDirection.RightToLeft ? "rtl" : "ltr",
        reduceMotion = _main.ReduceMotion,
        settingsOpen = _main.IsSettingsOpen,
        page = CurrentWebPage,
        activeSidebarItem = _workspacePage ?? _main.ActiveSidebarItem,
        controlPage = new
        {
            historyCount = _history.TotalCount, historyIncludesTests = _history.ShowTestRecords
        },
        extensionsPage = new
        {
            loading = _extensionLoading,
            scanned = _extensionsScanned,
            error = _extensionScanError is null ? null : UiStrings.Get(_main.LanguageCode, _extensionScanError == "Incomplete" ? "extensionScanIncomplete" : "extensionScanFailed"),
            incomplete = _extensionDiagnosticCount > 0,
            diagnosticCount = _extensionDiagnosticCount,
            browsers = _detectedBrowsers,
            items = _extensions.Select(e => new
            {
                id = e.Id,
                browser = e.Browser,
                profile = e.Profile,
                name = e.Name,
                desc = e.Description,
                version = e.Version,
                icon = e.Icon,
                isPolicyManaged = e.IsPolicyManaged,
                installationLocation = e.InstallationLocation,
                permissions = e.Permissions,
                optionalPermissions = e.OptionalPermissions,
                hostPermissions = e.HostPermissions,
                optionalHostPermissions = e.OptionalHostPermissions,
                contentScriptHosts = e.ContentScriptHosts,
                enabledState = e.EnabledState,
                installationSource = e.InstallationSource,
                managementCapability = e.ManagementCapability
            }).ToList()
        },
        navigation = new
        {
            appSection = _main.AppSectionTitle, allApps = _main.AllAppsLabel, recentlyInstalled = _main.RecentAppsLabel,
            largeApps = _main.LargeAppsLabel, storeApps = _main.StoreAppsLabel, auditLogs = _main.AuditLogsLabel,
            history = _main.HistoryLabel, restore = _main.RestoreLabel, settings = _main.SettingsLabel,
            reviewBeforeCleanup = _main.ReviewBeforeCleanupLabel, potentialLeftoversHint = _main.PotentialLeftoversHint
        },
        queueProtectionSettings = new
        {
            enabled = _main.ProtectBeforeUninstall, label = _main.ProtectionSettingLabel, hint = _main.ProtectionSettingHint
        },
        appsPage = new
        {
            title = _apps.PageTitle,
            total = _apps.TotalCount, hasPendingQueueReview = _apps.HasPendingQueueReview,
            filtered = _apps.FilteredCount,
            recent = _apps.RecentCount,
            large = _apps.LargeCount,
            store = _apps.StoreCount,
            bundleware = (int?)null, bundlewareAvailable = false,
            logged = _apps.LoggedCount,
            infrequent = (int?)null, usageAvailable = false,
            windowsApps = _apps.WindowsAppsCount,
            thirdPartyApps = _apps.ThirdPartyAppsCount,
            selected = _apps.SelectedCount,
            isLoading = _apps.IsLoading,
            inventoryState = _apps.InventoryState, inventoryStage = _apps.InventoryStage,
            inventoryError = _apps.InventoryError, inventoryRevision = _apps.InventoryRevision,
            hasConfirmedInventory = _apps.HasConfirmedInventory,
            discoveryDiagnostics = _apps.DiscoveryDiagnostics,
            status = _apps.StatusText,
            category = _apps.CurrentCategory.ToString(),
            sort = _apps.CurrentSort.ToString(),
            search = _apps.SearchText,
            labels = new { _apps.ShownLabel, _apps.RegisteredAppsLabel, _apps.RefreshLabel, _apps.SearchPlaceholder,
                _apps.SelectVisibleLabel, _apps.UninstallSelectedLabel, _apps.SortLabel, _apps.ApplicationColumnLabel,
                _apps.VersionColumnLabel, _apps.SizeColumnLabel, _apps.InstalledColumnLabel, _apps.ActionsColumnLabel,
                _apps.DetailsActionLabel, _apps.UninstallActionLabel, _apps.SortNameLabel,
                _apps.SortSizeLabel, _apps.SortDateLabel, _apps.SortPublisherLabel, _apps.NoMatchesLabel,
                _apps.SearchHintLabel, _apps.ScanAppsLabel, _apps.ScanRegistrationsLabel, _apps.DetailsVersionLabel,
                _apps.ArchitectureLabel, _apps.InstallerEngineLabel, _apps.EstimatedSizeLabel, _apps.InstallDateLabel,
                _apps.InstallLocationLabel, _apps.RegistryKeyLabel, _apps.UninstallCommandLabel,
                _apps.QuietUninstallCommandLabel, _apps.ConfirmUninstallLabel, _apps.UninstallModeLabel,
                _apps.StandardModeLabel, _apps.StandardModeHintLabel, _apps.QuietModeLabel, _apps.QuietModeHintLabel,
                _apps.ReviewLeftoversLabel, _apps.ReviewLeftoversHintLabel, _apps.CancelLabel, _apps.UninstallNowLabel,
                _apps.ResidualTitleLabel, _apps.ResidualSizeLabel, _apps.CheckedLocationsLabel,
                _apps.SelectAllLabel, _apps.DeselectAllLabel, _apps.QuarantineNoticeLabel,
                _apps.SkipResidualLabel, _apps.MoveToQuarantineLabel, _apps.CurrentApplicationLabel,
                _apps.CurrentStageLabel, _apps.ElapsedLabel, _apps.ResultsLabel, _apps.StopAfterCurrentLabel,
                _apps.CloseLabel, _apps.CancelOperationLabel },
            items = CurrentWebPage == "apps" ? _apps.Apps.Select(AppDto).ToArray() : [],
            details = _apps.DetailsApp is null ? null : AppDto(_apps.DetailsApp),
            confirm = _apps.IsConfirmOpen && _apps.ConfirmApp is not null ? AppDto(_apps.ConfirmApp) : null,
            unregistered = new
            {
                open = _apps.IsUnregisteredOpen, busy = _apps.IsUnregisteredBusy, state = _apps.UnregisteredState,
                error = _apps.UnregisteredError, planId = _apps.UnregisteredPlan?.Id,
                deletedFiles = _apps.UnregisteredDeletedFiles, deletionTotal = _apps.UnregisteredPlan?.Files.Count,
                remainingFiles = _apps.UnregisteredDeletion?.RemainingFiles,
                unverifiedMissingFiles = _apps.UnregisteredDeletion?.UnverifiedMissingFiles,
                pendingPath = _apps.UnregisteredDeletion?.PendingPath, canRetryDelete = _apps.UnregisteredDeletion?.CanRetry == true,
                fromBackup = _apps.UnregisteredPlan?.StoredReceipt is not null,
                pendingFolder = _apps.UnregisteredPlan?.StoredReceipt is { } receipt &&
                    Path.GetFileName(Path.GetDirectoryName(receipt)) == "Quarantine" ? "Quarantine" : "PendingRemoval",
                app = _apps.UnregisteredIdentity is null ? null : AppDto(_apps.UnregisteredIdentity),
                root = _apps.UnregisteredPlan?.Root, target = _apps.UnregisteredPlan?.Target, kind = _apps.UnregisteredPlan?.Kind,
                bytes = _apps.UnregisteredPlan?.Bytes, count = _apps.UnregisteredPlan?.Files.Count,
                files = _apps.UnregisteredPlan?.Files.Select(file => new { path = file.RelativePath, bytes = file.Bytes }).Take(200).ToArray()
            },
            quiet = _apps.IsQuietMode, scanResiduals = _apps.AutoScanResiduals,
            operation = new
            {
                open = _apps.IsProgressOpen, isBatch = _apps.IsBatchQueue,
                preview = _apps.IsQueuePreview,
                preparingProtection = _apps.PreparingProtection,
                protectionStage = UiStrings.Get(_main.LanguageCode, "protectionPreparing"),
                protectionWarning = _apps.ProtectionWarning,
                canOpenSystemRestore = _apps.CanOpenSystemRestore,
                openSystemRestore = _apps.OpenSystemRestoreLabel,
                isRunning = _apps.IsBatchQueue ? _apps.IsBatchRunning : _apps.IsProgressOpen,
                title = _apps.ProgressTitle, status = _apps.ProgressStatus, stage = _apps.CurrentStage,
                app = _apps.ProgressApp is null ? null : AppDto(_apps.ProgressApp),
                total = _apps.BatchTotal, processed = _apps.BatchCompleted, percent = _apps.BatchProgressPercent,
                successful = _apps.BatchSuccessful, failed = _apps.BatchFailed, skipped = _apps.BatchSkipped,
                elapsed = _apps.ElapsedTime, state = _apps.BatchState.ToString(), stateLabel = _apps.BatchStateLabel,
                operationState = _apps.OperationState.ToString(), stopRequested = _apps.StopBatchAfterCurrent,
                needsReview = _apps.NeedsQueueReview, canCancelCurrent = _apps.CanCancelCurrent,
                errorDetails = _apps.AttentionDetails, failureCode = _apps.AttentionFailureCode, exitCode = _apps.AttentionExitCode,
                retrying = _apps.VerificationRetrying, remaining = _apps.QueueRemaining,
                queueId = _apps.ReviewQueueId,
                review = new
                {
                    queueId = _apps.ReviewQueueId, recovered = !_apps.IsBatchRunning && _apps.NeedsQueueReview,
                    canAcknowledge = _apps.CanAcknowledgeReview,
                    detectedAt = _apps.ReviewSnapshot?.InterruptionDetectedAt,
                    createdAt = _apps.ReviewSnapshot?.CreatedAt,
                    observation = _apps.ReviewObservation,
                    registrationState = _apps.ReviewRegistrationState,
                    reason = UiStrings.Get(_main.LanguageCode, "queueReviewUncertain"),
                    items = _apps.QueueItems.Select(item => new { item.OperationId, appId = item.App.Id,
                        name = item.App.Name, source = item.App.Source, registryKey = item.App.RegistryKeyPath,
                        registeredCommand = item.App.SafeUninstallCommand,
                        lastState = item.State.ToString(), item.StartedAt, item.FinishedAt, evidence = item.Message, item.ExitCode }).ToArray()
                },
                queueItems = _apps.QueueItems.Select(item => new { item.OperationId, app = AppDto(item.App),
                    state = item.State.ToString(), item.StartedAt, item.FinishedAt, item.Message, item.ExitCode, item.FailureCode }).ToArray(),
                logs = _apps.ProgressLogs.ToArray(), results = _apps.BatchResults.Select(r => new
                { app = AppDto(r.App), state = r.State.ToString(), message = r.Message, label = r.StateLabel }).ToArray()
            },
            residual = new
            {
                open = _apps.IsResidualOpen, cleaning = _apps.IsCleaningResiduals,
                reviewId = _apps.ResidualReviewId, selectionVersion = _apps.ResidualSelectionVersion,
                stage = _apps.ResidualScanStage,
                scannedLocations = _apps.ResidualScannedLocations, size = _apps.TotalResidualSizeDisplay,
                foundSelectable = _apps.ResidualReviewGroups.Any(group => group.Items.Any(item => item.CanClean)),
                groups = _apps.ResidualReviewGroups.Where(group => _apps.IsResidualOpen && group.Items.Count > 0).Select(group => new
                {
                    app = AppDto(group.App), group.Offset, count = group.Items.Count,
                    status = group.Scan.Message(_apps.LanguageCode)
                }).ToArray(),
                summary = _apps.ResidualReviewGroups.Select(group => new
                {
                    id = group.App.Id, name = group.App.Name,
                    status = group.Scan.Message(_apps.LanguageCode),
                    folder = group.Scan.InstallFolder == ResidualInstallFolderState.NotChecked ? null
                        : UiStrings.Get(_apps.LanguageCode, "residualFolder" + group.Scan.InstallFolder),
                    location = group.App.InstallLocation,
                    informational = group.Items.Where(item => !item.CanClean).Select(item => item.Path).ToArray()
                }).ToArray(),
                items = _apps.ResidualItems.Select((item, index) => new
                {
                    index, title = item.Title, path = item.Path, size = item.SizeDisplay,
                    canClean = item.CanClean, selected = item.IsSelected
                }).ToArray()
            }
        },
        historyPage = new
        {
            title = _history.HistoryTitle, description = _history.HistoryDescription,
            restorePointLabel = UiStrings.Get(_main.LanguageCode, "protectionRestorePoint"),
            registryBackupLabel = UiStrings.Get(_main.LanguageCode, "protectionRegistryBackup"),
            showTestRecords = _history.ShowTestRecords, empty = _history.IsEmpty,
            pageNumber = _history.PageNumber, range = _history.PageRange,
            hasOlderPage = _history.HasOlderPage, hasNewerPage = _history.HasNewerPage,
            labels = new { _history.ShowTestRecordsLabel, _history.RefreshLabel, _history.MoreActionsLabel,
                _history.ClearHistoryLabel, _history.ApplicationLabel, _history.InstallerTypeLabel,
                _history.ResultLabel, _history.ResidualStatusLabel, _history.DateTimeLabel,
                _history.ActionsLabel, _history.NoHistoryLabel, _history.HistoryHintLabel,
                _history.DetailsActionLabel, _history.UninstallDateLabel, _history.ExitCodeLabel,
                _history.ResidualCleanupLabel, _history.RetainedIconLabel, _history.ExecutionNotesLabel,
                _history.CloseLabel },
            records = CurrentWebPage == "history" ? _history.Records.Select(item => HistoryDto(item)).ToArray() : [],
            details = _history.SelectedItem is null ? null : HistoryDto(_history.SelectedItem, includeNotes: true)
        }
    };

    private object AppDto(InstalledApp app) => new
    {
        id = app.Id, name = app.Name, publisher = app.Publisher, version = app.Version,
        size = app.SizeDisplay, sizeKb = app.EstimatedSizeKB, installDate = app.DisplayInstallDate,
        architecture = app.Architecture, installerType = app.TypeDisplay, source = app.Source,
        icon = ToDataUri(app.IconImage), selected = app.IsBatchSelected,
        installLocation = app.InstallLocation, registryKey = app.RegistryKeyPath,
        uninstallCommand = app.SafeUninstallCommand, quietUninstallCommand = app.SafeQuietUninstallCommand,
        isBuiltIn = app.IsBuiltInWindowsApp, isThirdParty = app.IsThirdPartyWindowsApp,
        isBundleware = app.IsBundleware, isLogged = app.IsLogged,
        description = app.Description
    };

    private object HistoryDto(HistoryItemViewModel item, bool includeNotes = false) => new
    {
        id = item.Record.Id, name = item.Name, publisher = item.Publisher, version = item.Version,
        date = item.Date, installerType = item.InstallerType, success = item.Success, outcome = item.OutcomeState,
        status = item.StatusText, residualStatus = item.Protection is null ? item.Record.ResidualStatus : item.ResidualStatus, exitCode = item.Record.ObservedExitCode,
        residualPermanent = item.Record.ResidualStatus.StartsWith("ResidualPermanent:", StringComparison.Ordinal) ? item.ResidualStatus : null,
        icon = ToDataUri(item.Icon), notes = includeNotes ? item.Notes : null, test = item.IsLegacyTestRecord,
        cleanedItemsCount = item.Record.CleanedItemsCount, cleanedSizeBytes = item.Record.CleanedSizeBytes
    };

    private string? ToDataUri(ImageSource? source)
    {
        if (source is not BitmapSource bitmap) return null;
        if (_icons.TryGetValue(source, out var cached)) return cached.Value;
        try
        {
            var result = "data:image/png;base64," + Convert.ToBase64String(BitmapEncodingService.EncodePng(bitmap));
            _icons.Add(source, new(result));
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or NotSupportedException)
        {
            // An unavailable icon must not prevent every language/page/state update.
            WriteWebLog("Icon encoding unavailable: " + ex);
            _icons.Add(source, new(""));
            return null;
        }
    }
}
