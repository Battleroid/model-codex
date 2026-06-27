using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ModelCodex.App.Services;
using ModelCodex.App.ViewModels;

namespace ModelCodex.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    // Lazy-load a tile's thumbnail when its container is realized (Loaded) or recycled (DataContextChanged).
    private void OnTileLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModelTile tile })
        {
            ThumbnailService.Request(tile);
            ThumbnailService.RegisterSpin(tile);
        }
    }

    private void OnTileUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModelTile tile })
            ThumbnailService.UnregisterSpin(tile);
    }

    private void OnTileBound(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ModelTile old) ThumbnailService.UnregisterSpin(old);
        if (e.NewValue is ModelTile tile)
        {
            ThumbnailService.Request(tile);
            ThumbnailService.RegisterSpin(tile);
        }
    }

    // Copy a tile's hash id to the clipboard (from the context menu or by clicking the id label).
    private static void CopyHash(ModelTile tile)
    {
        try { Clipboard.SetText(tile.TagId); } catch { }
    }

    private void OnCtxCopyHash(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ModelTile t) CopyHash(t);
    }

    private void OnHashClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ModelTile t) { CopyHash(t); e.Handled = true; }
    }

    // Hovering a tile orbits the model slightly based on cursor position.
    private void OnTileHover(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModelTile tile } fe && fe.ActualWidth > 0)
        {
            var p = e.GetPosition(fe);
            double fx = Math.Clamp(p.X / fe.ActualWidth * 2 - 1, -1, 1);
            double fy = Math.Clamp(p.Y / fe.ActualHeight * 2 - 1, -1, 1);
            ThumbnailService.Hover(tile, fx, fy);
        }
    }

    private void OnTileHoverEnd(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModelTile tile })
            ThumbnailService.ResetHover(tile);
    }

    private void OnModelActivated(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is LibraryViewModel vm &&
            (e.OriginalSource as FrameworkElement)?.DataContext is ModelTile t)
            vm.OpenRequested?.Invoke(t.Entry);
    }

    private void OnOpenSelected(object sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel { SelectedModel: { } m } vm)
            vm.OpenRequested?.Invoke(m);
    }

    private void OnPickBg(object sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm) vm.PreviewBg = BgPicker.Pick(vm.PreviewBg);
    }

    // Right-click context menu on a tile (DataContext inherited from the tile).
    private void OnCtxOpen(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ModelTile t && DataContext is LibraryViewModel vm)
            vm.OpenRequested?.Invoke(t.Entry);
    }

    private void OnCtxExport(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ModelTile t && DataContext is LibraryViewModel vm)
            _ = vm.ExportEntryAsync(t.Entry);
    }
}
