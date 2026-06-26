using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ModelCodex.App.ViewModels;

namespace ModelCodex.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow { Owner = this, DataContext = DataContext };
        win.ShowDialog();
    }

    private void OnExportMenu(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } cm } b)
        {
            cm.PlacementTarget = b;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }
    }

    // Middle-click closes the tab (Deimos/codex convention).
    private void OnTabPressed(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle &&
            (sender as FrameworkElement)?.DataContext is TabItemViewModel tab)
        {
            Vm?.CloseTabCommand.Execute(tab);
            e.Handled = true;
        }
    }

    private void OnCloseTab(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TabItemViewModel tab)
        {
            Vm?.CloseTabCommand.Execute(tab);
            e.Handled = true;
        }
    }
}
