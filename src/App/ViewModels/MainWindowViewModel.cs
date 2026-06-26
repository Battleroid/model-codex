using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModelCodex.App.Services;
using Tiger.Model;

namespace ModelCodex.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    public LibraryViewModel Library { get; } = new();
    public ObservableCollection<TabItemViewModel> Tabs { get; } = new();

    [ObservableProperty] private TabItemViewModel? _selectedTab;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _statusText = "Ready.";
    [ObservableProperty] private bool _indexLoaded;
    [ObservableProperty] private string _gameDirText = "";

    public MainWindowViewModel()
    {
        Tabs.Add(Library);
        SelectedTab = Library;
        Library.OpenRequested = OpenInTab;
        UpdateGameDirText();

        if (AppState.Instance.GameDirValid) _ = LoadIndex();
        else StatusText = "Set the Marathon game folder in Settings, then Load.";
    }

    public void UpdateGameDirText()
    {
        var dir = AppState.Instance.Config.GameDir;
        GameDirText = string.IsNullOrEmpty(dir) ? "No game folder set" : dir!;
    }

    [RelayCommand]
    public async Task LoadIndex()
    {
        if (IsBusy) return;
        if (!AppState.Instance.GameDirValid)
        {
            StatusText = "Marathon game folder is not valid (need packages\\ and bin\\x64\\oo2core_9_win64.dll).";
            return;
        }

        IsBusy = true;
        ProgressValue = 0;
        ProgressText = "Indexing packages…";
        var ui = Dispatcher.CurrentDispatcher;
        try
        {
            var mgr = await Task.Run(() => AppState.Instance.BuildIndex((p, m) =>
                ui.InvokeAsync(() => { ProgressValue = p; ProgressText = m; }, DispatcherPriority.Background)));
            Library.SetManager(mgr);
            IndexLoaded = true;
            StatusText = $"{mgr.Models.Count} models · {mgr.Textures.Count} textures · {mgr.PackageCount} packages";
        }
        catch (Exception ex)
        {
            StatusText = $"Index failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
        }
    }

    public void OpenInTab(ModelEntry e)
    {
        var existing = Tabs.OfType<ModelTabViewModel>().FirstOrDefault(t => t.TagHash == e.TagHash);
        if (existing != null) { SelectedTab = existing; return; }
        var tab = new ModelTabViewModel(e);
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    [RelayCommand]
    private async Task ExportSelectedModel()
    {
        if (Library.SelectedModel is not { } e) { StatusText = "Select a model first."; return; }
        try
        {
            string path = await Task.Run(() => Services.ExportRunner.ExportOne(e));
            StatusText = $"Exported → {path}";
        }
        catch (Exception ex) { StatusText = $"Export failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ExportPackage()
    {
        var list = Library.CurrentPackageModels;
        if (list.Count == 0 || IsBusy) return;
        IsBusy = true; ProgressValue = 0; ProgressText = $"Exporting {list.Count} models…";
        var ui = Dispatcher.CurrentDispatcher;
        try
        {
            int n = await Services.ExportRunner.ExportBulk(list,
                (p, m) => ui.InvokeAsync(() => { ProgressValue = p; ProgressText = m; }, DispatcherPriority.Background),
                CancellationToken.None);
            StatusText = $"Exported {n}/{list.Count} models → {AppState.Instance.EffectiveExportDir}";
        }
        catch (Exception ex) { StatusText = $"Export failed: {ex.Message}"; }
        finally { IsBusy = false; ProgressText = ""; }
    }

    [RelayCommand]
    private void OpenExportFolder()
    {
        string dir = AppState.Instance.EffectiveExportDir;
        System.IO.Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
    }

    [RelayCommand]
    public void CloseTab(TabItemViewModel? tab)
    {
        if (tab is null || !tab.CanClose) return;
        int idx = Tabs.IndexOf(tab);
        bool wasSelected = ReferenceEquals(tab, SelectedTab);
        Tabs.Remove(tab);
        if (wasSelected)
            SelectedTab = Tabs.Count > 0 ? Tabs[Math.Clamp(idx, 0, Tabs.Count - 1)] : Library;
    }
}
