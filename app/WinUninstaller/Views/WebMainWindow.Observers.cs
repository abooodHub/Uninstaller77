using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Threading;
using WinUninstaller.ViewModels;

namespace WinUninstaller.Views;

public partial class WebMainWindow
{
    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e) => ScheduleState();
    private void OnHistoryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                _ = Dispatcher.InvokeAsync(() => OnHistoryPropertyChanged(sender, e), DispatcherPriority.DataBind);
            return;
        }
        if (e.PropertyName == nameof(HistoryViewModel.Records)) AttachCollections();
        ScheduleState();
    }
    private void OnAppsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                _ = Dispatcher.InvokeAsync(() => OnAppsPropertyChanged(sender, e), DispatcherPriority.DataBind);
            return;
        }
        if (e.PropertyName is nameof(AppListViewModel.Apps) or nameof(AppListViewModel.ProgressLogs) or nameof(AppListViewModel.BatchResults) or nameof(AppListViewModel.ResidualItems))
            AttachCollections();
        if (e.PropertyName == nameof(AppListViewModel.HistoryCount) && CurrentWebPage == "control")
            _history.RefreshCount();
        ScheduleState();
    }
    private void AttachCollections()
    {
        DetachCollections();
        Attach(_apps.Apps);
        Attach(_apps.ProgressLogs);
        Attach(_apps.BatchResults);
        Attach(_apps.ResidualItems);
        Attach(_history.Records);
    }
    private void Attach<T>(ObservableCollection<T> collection) where T : class
    {
        _attachedCollections.Add(collection);
        collection.CollectionChanged -= OnCollectionChanged;
        collection.CollectionChanged += OnCollectionChanged;
        foreach (var item in collection)
            if (item is INotifyPropertyChanged notify)
            {
                _attachedItems.Add(notify);
                notify.PropertyChanged -= OnItemPropertyChanged;
                notify.PropertyChanged += OnItemPropertyChanged;
            }
    }
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                _ = Dispatcher.InvokeAsync(() => OnCollectionChanged(sender, e), DispatcherPriority.DataBind);
            return;
        }
        if (e.Action == NotifyCollectionChangedAction.Reset) { AttachCollections(); ScheduleState(); return; }
        if (e.OldItems != null)
            foreach (var item in e.OldItems)
                if (item is INotifyPropertyChanged removed && _attachedItems.Remove(removed))
                    removed.PropertyChanged -= OnItemPropertyChanged;
        if (e.NewItems != null)
            foreach (var item in e.NewItems)
                if (item is INotifyPropertyChanged notify)
                {
                    _attachedItems.Add(notify);
                    notify.PropertyChanged -= OnItemPropertyChanged;
                    notify.PropertyChanged += OnItemPropertyChanged;
                }
        ScheduleState();
    }
    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => ScheduleState();

    private void PushState()
    {
        if (_closed || Browser?.CoreWebView2 == null) return;
        try { Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "state", state = BuildState() }, JsonOptions)); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException ex) { WriteWebLog("State publish failed: " + ex); }
    }
}
