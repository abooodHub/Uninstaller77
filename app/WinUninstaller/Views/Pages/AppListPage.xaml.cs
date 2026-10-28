using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinUninstaller.ViewModels;

namespace WinUninstaller.Views.Pages;

public partial class AppListPage : UserControl
{
    public AppListPage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is AppListViewModel oldVm) oldVm.PropertyChanged -= ViewModelChanged;
            if (e.NewValue is AppListViewModel vm) vm.PropertyChanged += ViewModelChanged;
        };
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not AppListViewModel vm) return;
        var card = e.PropertyName switch
        {
            nameof(AppListViewModel.IsDetailsOpen) when vm.IsDetailsOpen => DetailsCard,
            nameof(AppListViewModel.IsConfirmOpen) when vm.IsConfirmOpen => ConfirmCard,
            nameof(AppListViewModel.IsProgressOpen) when vm.IsProgressOpen => ProgressCard,
            nameof(AppListViewModel.IsResidualOpen) when vm.IsResidualOpen => ResidualCard,
            _ => null
        };
        if (card == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (vm.ReduceMotion)
            {
                card.BeginAnimation(OpacityProperty, null);
                card.Opacity = 1;
                card.RenderTransform = Transform.Identity;
                return;
            }
            card.Opacity = 0;
            var slide = new TranslateTransform(0, 8);
            card.RenderTransform = slide;
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }, DispatcherPriority.Loaded);
    }
}
