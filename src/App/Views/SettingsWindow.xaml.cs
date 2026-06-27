using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ModelCodex.App.Services;
using ModelCodex.App.ViewModels;

namespace ModelCodex.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadFromConfig();
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void LoadFromConfig()
    {
        var c = AppState.Instance.Config;
        GameDirBox.Text = c.GameDir ?? "";
        ExportDirBox.Text = c.ExportDir ?? "";
        SelectByTag(FormatBox, c.ExportFormat);
        SelectByTag(LookdevBox, c.Lookdev);
        ExportTexturesBox.IsChecked = c.ExportTextures;
        IsometricBox.IsChecked = c.IsometricByDefault;
        SpinPreviewsBox.IsChecked = c.SpinPreviews;
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if ((item.Tag as string) == tag) { box.SelectedItem = item; return; }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void OnBrowseGame(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the Marathon install folder" };
        if (!string.IsNullOrEmpty(GameDirBox.Text)) dlg.InitialDirectory = GameDirBox.Text;
        if (dlg.ShowDialog(this) == true) GameDirBox.Text = dlg.FolderName;
    }

    private void OnBrowseExport(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the export folder" };
        if (!string.IsNullOrEmpty(ExportDirBox.Text)) dlg.InitialDirectory = ExportDirBox.Text;
        if (dlg.ShowDialog(this) == true) ExportDirBox.Text = dlg.FolderName;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var st = AppState.Instance;
        string? oldGameDir = st.Config.GameDir;

        st.SetGameDir(string.IsNullOrWhiteSpace(GameDirBox.Text) ? null : GameDirBox.Text.Trim());
        st.SetExportDir(string.IsNullOrWhiteSpace(ExportDirBox.Text) ? null : ExportDirBox.Text.Trim());
        st.SetExportFormat(TagOf(FormatBox));
        st.SetLookdev(TagOf(LookdevBox));
        st.SetExportTextures(ExportTexturesBox.IsChecked == true);
        st.SetIsometricByDefault(IsometricBox.IsChecked == true);
        st.SetSpinPreviews(SpinPreviewsBox.IsChecked == true);
        ThumbnailService.SetSpin(st.Config.SpinPreviews);

        Vm?.UpdateGameDirText();
        bool gameDirChanged = !string.Equals(oldGameDir, st.Config.GameDir, StringComparison.OrdinalIgnoreCase);
        Close();

        if (gameDirChanged && st.GameDirValid && Vm is { } vm)
            _ = vm.LoadIndex();
    }
}
