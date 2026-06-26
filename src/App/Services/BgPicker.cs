using System.Windows.Media;
using WinColor = System.Drawing.Color;
using WinColorDialog = System.Windows.Forms.ColorDialog;
using WinDialogResult = System.Windows.Forms.DialogResult;

namespace ModelCodex.App.Services;

/// <summary>Opens the standard Windows colour picker for choosing a preview background.</summary>
public static class BgPicker
{
    public static Color Pick(Color current)
    {
        using var dlg = new WinColorDialog
        {
            Color = WinColor.FromArgb(current.R, current.G, current.B),
            FullOpen = true,
            AnyColor = true,
        };
        return dlg.ShowDialog() == WinDialogResult.OK
            ? Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B)
            : current;
    }
}
