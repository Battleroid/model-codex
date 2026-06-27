using System.Windows;
using System.Windows.Controls;
using ModelCodex.App.Services;
using ModelCodex.App.ViewModels;

namespace ModelCodex.App.Views;

public partial class ModelTabView : UserControl
{
    public ModelTabView() => InitializeComponent();

    private void OnPickBg(object sender, RoutedEventArgs e)
    {
        if (DataContext is ModelTabViewModel vm) vm.PreviewBg = BgPicker.Pick(vm.PreviewBg);
    }

    // The perspective camera's FieldOfView change doesn't trigger a redraw on its own; force one.
    private void OnFovChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => Viewport?.InvalidateRender();
}
