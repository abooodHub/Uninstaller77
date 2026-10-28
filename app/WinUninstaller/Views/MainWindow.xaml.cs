using System.Windows;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinUninstaller.ViewModels;

namespace WinUninstaller.Views;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel oldVm) { oldVm.PropertyChanged -= MainViewModelChanged; oldVm.AppList.PropertyChanged -= AppListChanged; }
            if (e.NewValue is MainViewModel vm) { vm.PropertyChanged += MainViewModelChanged; vm.AppList.PropertyChanged += AppListChanged; }
        };
        SourceInitialized += (_, _) =>
        {
            try
            {
                var dark = 1;
                DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
            }
            catch { /* Older Windows versions keep their system title bar theme. */ }
        };
    }

    private void MainViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentViewModel)) return;
        AnimatePage(sender as MainViewModel);
    }

    private void AppListChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppListViewModel.CurrentCategory))
            AnimatePage(DataContext as MainViewModel);
    }

    private void AnimatePage(MainViewModel? vm)
    {
        if (vm == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (vm.ReduceMotion)
            {
                MainContentHost.BeginAnimation(OpacityProperty, null);
                MainContentHost.Opacity = 1;
                MainContentHost.RenderTransform = Transform.Identity;
                return;
            }
            MainContentHost.Opacity = 0;
            var slide = new TranslateTransform(0, 9);
            MainContentHost.RenderTransform = slide;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            var move = new DoubleAnimation(9, 0, TimeSpan.FromMilliseconds(190)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            MainContentHost.BeginAnimation(OpacityProperty, fade);
            slide.BeginAnimation(TranslateTransform.YProperty, move);
        }, DispatcherPriority.Loaded);
    }
}
