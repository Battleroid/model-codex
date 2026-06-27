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

    /// <summary>Permutations/variants of the selected model (gear/skin colour sets); empty when none.</summary>
    public ObservableCollection<PermutationOption> Permutations { get; } = new();

    /// <summary>Live light rig bound by the viewport (direct children illuminate; ItemsModel3D ones don't).</summary>
    public LightingState Lights { get; } = new();

    [ObservableProperty] private ModelTile? _selectedTile;
    [ObservableProperty] private string _previewInfo = "";
    [ObservableProperty] private bool _textured = true;
    [ObservableProperty] private bool _hideEmpty = true;
    [ObservableProperty] private LightingStyle _lighting = LightingStyle.Lookdev;
    [ObservableProperty] private MaterialView _materialView = MaterialView.Shaded;
    [ObservableProperty] private ModelDetail _detail = ModelDetail.MostDetailed;
    [ObservableProperty] private MColor _previewBg = BgColors.Parse(AppState.Instance.Config.PreviewBg);
    [ObservableProperty] private bool _flatShading = AppState.Instance.Config.FlatShading;

    public static IReadOnlyList<LightingOption> LightingStyles => Services.Lighting.Styles;
    public static IReadOnlyList<MaterialViewOption> MaterialViews { get; } = new[]
    {
        new MaterialViewOption(MaterialView.Shaded, "Shaded"),
        new MaterialViewOption(MaterialView.Albedo, "Albedo"),
        new MaterialViewOption(MaterialView.Normal, "Normal"),
        new MaterialViewOption(MaterialView.Metalness, "Metalness"),
        new MaterialViewOption(MaterialView.Emission, "Emission"),
        new MaterialViewOption(MaterialView.Transmission, "Transmission"),
    };
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

    partial void OnMaterialViewChanged(MaterialView value)
    {
        if (SelectedTile != null) _ = PreviewAsync(SelectedTile.Entry);
    }

    partial void OnFlatShadingChanged(bool value)
    {
        AppState.Instance.SetFlatShading(value);
        if (SelectedTile != null) _ = PreviewAsync(SelectedTile.Entry);
    }

    partial void OnPreviewBgChanged(MColor value) => AppState.Instance.SetPreviewBg(BgColors.ToHex(value));

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
    [ObservableProperty] private int _selectedPermutation = -1;
    public bool HasPermutations => Permutations.Count > 1;
    private uint? _overrideAlbedo;
    private int? _variant;
    private bool _suppressPermReload;

    partial void OnSelectedTileChanged(ModelTile? value)
    {
        OnPropertyChanged(nameof(SelectedModel));
        _overrideAlbedo = null;
        _variant = null; // new model -> back to its default permutation
        _selectedChannel = null; // Channels list rebuilds, clearing the selection visually
        if (value != null) _ = PreviewAsync(value.Entry);
    }

    partial void OnSelectedPermutationChanged(int value)
    {
        if (_suppressPermReload || SelectedTile == null) return;
        _variant = value < 0 ? null : value;
        _ = PreviewAsync(SelectedTile.Entry);
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
            int? variant = _variant;
            var matView = MaterialView;
            bool flat = FlatShading;
            var data = await Task.Run(() => ModelPreview.Load(mgr, entry, textured, over, detail, variant, matView, flat));
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
            // Rebuild the permutation selector only when the variant SET changes (i.e. a new model) —
            // not on a pure permutation switch, which would clear the list and blank the ComboBox display.
            bool sameSet = Permutations.Count == data.Variants.Count
                           && Permutations.Select(p => p.Index).SequenceEqual(data.Variants);
            if (!sameSet)
            {
                _suppressPermReload = true;
                Permutations.Clear();
                for (int i = 0; i < data.Variants.Count; i++)
                    Permutations.Add(new PermutationOption(data.Variants[i], $"Variant {i + 1}"));
                SelectedPermutation = data.SelectedVariant;
                OnPropertyChanged(nameof(HasPermutations));
                _suppressPermReload = false;
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

        int total = 0, hidden = 0;
        foreach (var m in _all.Where(m => m.PkgId == pkg.PkgId).OrderBy(m => m.TagHash))
        {
            total++;
            if (HideEmpty && ThumbnailService.EmptyCache.TryGetValue(m.TagHash, out bool e) && e) { hidden++; continue; }
            VisibleModels.Add(new ModelTile(m));
        }
        ResultCountText = HideEmpty && hidden > 0 ? $"{VisibleModels.Count} models ({hidden} empty hidden)" : $"{VisibleModels.Count} models";
        if (HideEmpty) _ = ScanEmptiesAsync(pkg.PkgId);
    }

    partial void OnHideEmptyChanged(bool value) => ApplyFilter();

    /// <summary>Parse-check the current package's not-yet-known models off-thread and drop empties live.</summary>
    private async Task ScanEmptiesAsync(ushort pkgId)
    {
        if (_mgr is not { } mgr) return;
        var todo = _all.Where(m => m.PkgId == pkgId && !ThumbnailService.EmptyCache.ContainsKey(m.TagHash)).ToList();
        if (todo.Count == 0) return;
        var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        await Task.Run(() =>
        {
            int done = 0;
            Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, m =>
            {
                bool empty;
                try { var g = ModelParse.Parse(mgr, m); empty = g == null || g.VertexCount == 0; } catch { empty = true; }
                ThumbnailService.EmptyCache[m.TagHash] = empty;
                if (System.Threading.Interlocked.Increment(ref done) % 96 == 0)
                    ui.InvokeAsync(DropKnownEmpties, System.Windows.Threading.DispatcherPriority.Background);
            });
        });
        DropKnownEmpties();
    }

    private void DropKnownEmpties()
    {
        if (!HideEmpty) return;
        for (int i = VisibleModels.Count - 1; i >= 0; i--)
            if (ThumbnailService.EmptyCache.TryGetValue(VisibleModels[i].TagHash, out bool e) && e)
                VisibleModels.RemoveAt(i);
        if (SelectedPackage is { } p)
        {
            int total = _all.Count(m => m.PkgId == p.PkgId);
            ResultCountText = $"{VisibleModels.Count} models ({total - VisibleModels.Count} empty hidden)";
        }
    }
}
