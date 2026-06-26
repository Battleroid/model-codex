using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.Wpf.SharpDX;
using ModelCodex.App.Services;
using SharpDX;
using Tiger;
using Tiger.Model;
using Camera = HelixToolkit.Wpf.SharpDX.Camera;
using HxMaterial = HelixToolkit.Wpf.SharpDX.Material;
using MeshGeometry3D = HelixToolkit.SharpDX.Core.MeshGeometry3D;
using PerspectiveCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using MColor = System.Windows.Media.Color;

namespace ModelCodex.App.ViewModels;

/// <summary>The pinned "home" tab: left package/category list, middle model grid, right preview.</summary>
public sealed partial class LibraryViewModel : TabItemViewModel
{
    public override bool CanClose => false;

    /// <summary>Left panel: individual packages with model counts.</summary>
    public ObservableCollection<PackageGroup> Packages { get; } = new();

    /// <summary>Middle grid tiles (lazily-rendered isometric thumbnails).</summary>
    public ObservableCollection<ModelTile> VisibleModels { get; } = new();

    [ObservableProperty] private PackageGroup? _selectedPackage;
    [ObservableProperty] private string _resultCountText = "";
    [ObservableProperty] private string _emptyMessage = "Load models to begin.";

    // ---- Right-panel live preview ----
    public EffectsManager EffectsManager { get; } = new DefaultEffectsManager();
    public Camera Camera { get; } = new PerspectiveCamera { UpDirection = new Vector3D(0, 0, 1), FarPlaneDistance = 1e6, NearPlaneDistance = 0.01 };

    /// <summary>One MeshGeometryModel3D per part, each with its resolved material/texture.</summary>
    public ObservableElement3DCollection PreviewModels { get; } = new();
    /// <summary>Texture channels of the selected model's materials (for the channels panel).</summary>
    public ObservableCollection<MaterialChannel> Channels { get; } = new();

    /// <summary>Live light rig bound by the viewport (direct children illuminate; ItemsModel3D ones don't).</summary>
    public LightingState Lights { get; } = new();

    [ObservableProperty] private ModelTile? _selectedTile;
    [ObservableProperty] private string _previewInfo = "";
    [ObservableProperty] private bool _textured = true;
    [ObservableProperty] private LightingStyle _lighting = LightingStyle.Lookdev;
    [ObservableProperty] private ModelDetail _detail = ModelDetail.MostDetailed;
    [ObservableProperty] private MColor _previewBg = MColor.FromRgb(0x10, 0x10, 0x14);

    public static IReadOnlyList<LightingOption> LightingStyles => Services.Lighting.Styles;
    public static IReadOnlyList<DetailOption> DetailLevels { get; } = new[]
    {
        new DetailOption(ModelDetail.MostDetailed, "Most detail"),
        new DetailOption(ModelDetail.LeastDetailed, "Least detail"),
        new DetailOption(ModelDetail.All, "All detail"),
    };
    public static IReadOnlyList<BgOption> BgOptions { get; } = new[]
    {
        new BgOption(MColor.FromRgb(0x10, 0x10, 0x14), "Dark"),
        new BgOption(MColor.FromRgb(0x00, 0x00, 0x00), "Black"),
        new BgOption(MColor.FromRgb(0x3a, 0x3a, 0x40), "Gray"),
        new BgOption(MColor.FromRgb(0xc6, 0xc7, 0xcb), "Light"),
        new BgOption(MColor.FromRgb(0x1e, 0x3a, 0x5a), "Blue"),
        new BgOption(MColor.FromRgb(0x2e, 0x12, 0x3a), "Purple"),
    };
    public ModelEntry? SelectedModel => SelectedTile?.Entry;

    private PreviewData? _lastData;

    partial void OnLightingChanged(LightingStyle value) => Lights.Apply(value);

    partial void OnTexturedChanged(bool value)
    {
        if (SelectedTile != null) _ = PreviewAsync(SelectedTile.Entry);
    }

    partial void OnDetailChanged(ModelDetail value)
    {
        if (SelectedTile != null) _ = PreviewAsync(SelectedTile.Entry);
    }

    /// <summary>Models currently shown in the grid (the selected package), for bulk export.</summary>
    public IReadOnlyList<ModelEntry> CurrentPackageModels => VisibleModels.Select(v => v.Entry).ToList();

    public async Task ExportEntryAsync(ModelEntry entry)
    {
        try
        {
            PreviewInfo = "Exporting…";
            string path = await Task.Run(() => ExportRunner.ExportOne(entry));
            PreviewInfo = $"Exported → {path}";
        }
        catch (Exception ex) { PreviewInfo = $"Export failed: {ex.Message}"; }
    }

    /// <summary>Callback up to the window to open a model in its own tab (assigned by MainWindowViewModel).</summary>
    public Action<ModelEntry>? OpenRequested { get; set; }

    private PackageManager? _mgr;
    private List<ModelEntry> _all = new();
    private int _previewToken;

    public LibraryViewModel() { Title = "Library"; }

    public void SetManager(PackageManager mgr)
    {
        _mgr = mgr;
        _all = mgr.Models;

        Packages.Clear();
        foreach (var p in mgr.PackageGroups) Packages.Add(p);

        SelectedPackage = Packages.Count > 0 ? Packages[0] : null;
        EmptyMessage = mgr.Models.Count == 0 ? "No models found in this install." : "";
        ApplyFilter();
    }

    partial void OnSelectedPackageChanged(PackageGroup? value) => ApplyFilter();

    [ObservableProperty] private MaterialChannel? _selectedChannel;
    private uint? _overrideAlbedo;

    partial void OnSelectedTileChanged(ModelTile? value)
    {
        OnPropertyChanged(nameof(SelectedModel));
        _overrideAlbedo = null;
        _selectedChannel = null; // Channels list rebuilds, clearing the selection visually
        if (value != null) _ = PreviewAsync(value.Entry);
    }

    partial void OnSelectedChannelChanged(MaterialChannel? value)
    {
        if (value == null) return;
        _overrideAlbedo = value.TexHash;
        if (SelectedTile != null) _ = PreviewAsync(SelectedTile.Entry);
    }

    private async Task PreviewAsync(ModelEntry entry)
    {
        if (_mgr == null) return;
        var mgr = _mgr;
        bool textured = Textured;
        int token = ++_previewToken;
        try
        {
            uint? over = _overrideAlbedo;
            var detail = Detail;
            var data = await Task.Run(() => ModelPreview.Load(mgr, entry, textured, over, detail));
            if (token != _previewToken) return; // a newer selection won

            if (data.Geometry is { } g)
            {
                entry.PartCount = g.Parts.Count; entry.VertexCount = g.VertexCount;
                entry.TriangleCount = g.TriangleCount; entry.Parsed = true;
            }

            _lastData = data;
            ModelPreview.Populate(PreviewModels, data);
            // Only rebuild the channel list when not previewing a specific channel (keeps selection stable).
            if (_overrideAlbedo == null)
            {
                Channels.Clear();
                foreach (var c in data.Channels) Channels.Add(c);
            }
            PreviewInfo = data.Info;
            if (data.Geometry is { } geom && Camera is PerspectiveCamera pc) ModelPreview.Frame(pc, geom);
        }
        catch (Exception ex)
        {
            if (token == _previewToken) { PreviewModels.Clear(); PreviewInfo = $"parse error: {ex.Message}"; }
        }
    }

    private void ApplyFilter()
    {
        VisibleModels.Clear();
        if (_mgr == null || SelectedPackage is not { } pkg) { ResultCountText = ""; return; }

        foreach (var m in _all.Where(m => m.PkgId == pkg.PkgId).OrderBy(m => m.TagHash))
            VisibleModels.Add(new ModelTile(m));
        ResultCountText = $"{VisibleModels.Count} models";
    }
}
