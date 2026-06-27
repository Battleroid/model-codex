using System.IO;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.Wpf.SharpDX;
using ModelCodex.App.Services;
using Tiger;
using Tiger.Model;
using Camera = HelixToolkit.Wpf.SharpDX.Camera;
using OrthographicCamera = HelixToolkit.Wpf.SharpDX.OrthographicCamera;
using PerspectiveCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using MColor = System.Windows.Media.Color;

namespace ModelCodex.App.ViewModels;

/// <summary>A model opened in its own tab: full interactive inspector (orbit camera, perspective/iso
/// toggle, textured/wireframe/grid toggles, channels). Reuses the shared <see cref="ModelPreview"/>.</summary>
public sealed partial class ModelTabViewModel : TabItemViewModel
{
    public ModelEntry Entry { get; }
    public uint TagHash => Entry.TagHash;
    public string Category { get; }

    public EffectsManager EffectsManager { get; } = new DefaultEffectsManager();
    public ObservableElement3DCollection PreviewModels { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<MaterialChannel> Channels { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<ChannelEdit> ChannelValues { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<ChannelValue> UsedChannels { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<PermutationOption> Permutations { get; } = new();

    [ObservableProperty] private Camera _camera;
    [ObservableProperty] private string _previewInfo = "Loading…";
    [ObservableProperty] private bool _textured = true;
    [ObservableProperty] private bool _isometric;
    [ObservableProperty] private bool _wireframe;
    [ObservableProperty] private bool _showGrid = true;
    [ObservableProperty] private LightingStyle _lighting = LightingStyle.Lookdev;
    [ObservableProperty] private MaterialView _materialView = MaterialView.Shaded;
    [ObservableProperty] private ModelDetail _detail = ModelDetail.MostDetailed;
    [ObservableProperty] private double _exposure = 1.0;
    [ObservableProperty] private MColor _previewBg = BgColors.Parse(AppState.Instance.Config.PreviewBg);
    [ObservableProperty] private bool _flatShading = AppState.Instance.Config.FlatShading;

    public LightingState Lights { get; } = new();
    public static IReadOnlyList<LightingOption> LightingStyles => Services.Lighting.Styles;
    public static IReadOnlyList<MaterialViewOption> MaterialViews => LibraryViewModel.MaterialViews;
    public static IReadOnlyList<DetailOption> DetailLevels => LibraryViewModel.DetailLevels;
    public static IReadOnlyList<BgOption> BgOptions => LibraryViewModel.BgOptions;

    [ObservableProperty] private string _exportStatus = "";
    [ObservableProperty] private int _selectedPermutation = -1;
    public bool HasPermutations => Permutations.Count > 1;

    private ModelGeometry? _geom;
    private PreviewData? _lastData;
    private int? _variant;
    private bool _suppressPermReload;

    partial void OnSelectedPermutationChanged(int value)
    {
        if (_suppressPermReload) return;
        _variant = value < 0 ? null : value;
        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task Export()
    {
        try
        {
            ExportStatus = "Exporting…";
            string path = await Task.Run(() => ExportRunner.ExportOne(Entry));
            ExportStatus = $"Exported → {path}";
        }
        catch (Exception ex) { ExportStatus = $"Export failed: {ex.Message}"; }
    }

    public ModelTabViewModel(ModelEntry entry)
    {
        Entry = entry;
        Title = entry.TagId;
        Category = entry.Category;
        _isometric = AppState.Instance.Config.IsometricByDefault;
        _textured = true;
        _camera = MakeCamera(_isometric);
        _ = LoadAsync();
    }

    private static Camera MakeCamera(bool iso) => iso
        ? new OrthographicCamera { UpDirection = new Vector3D(0, 0, 1), NearPlaneDistance = -1e6, FarPlaneDistance = 1e6 }
        : new PerspectiveCamera { UpDirection = new Vector3D(0, 0, 1), NearPlaneDistance = 0.01, FarPlaneDistance = 1e6, FieldOfView = 45 };

    private async Task LoadAsync()
    {
        var mgr = AppState.Instance.Manager;
        if (mgr == null) { PreviewInfo = "no index"; return; }
        bool textured = Textured;
        var detail = Detail;
        int? variant = _variant;
        var matView = MaterialView;
        bool flat = FlatShading;
        try
        {
            var data = await Task.Run(() => ModelPreview.Load(mgr, Entry, textured, null, detail, variant, matView, flat));
            _geom = data.Geometry;
            _lastData = data;
            ModelPreview.Populate(PreviewModels, data, Wireframe);
            Channels.Clear();
            foreach (var c in data.Channels) Channels.Add(c);
            ChannelValues.Clear();
            foreach (var v in data.ChannelValues) { v.Changed = OnChannelEdited; ChannelValues.Add(v); }
            UsedChannels.Clear();
            foreach (var v in data.UsedChannels) UsedChannels.Add(v);
            // Rebuild the permutation list only when the variant set changes (keeps the selection stable).
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
            FrameCamera();
        }
        catch (Exception ex) { PreviewInfo = $"error: {ex.Message}"; }
    }

    private void FrameCamera()
    {
        if (_geom == null) return;
        if (Camera is PerspectiveCamera pc) ModelPreview.Frame(pc, _geom);
        else if (Camera is OrthographicCamera oc) FrameOrtho(oc);
    }

    private void FrameOrtho(OrthographicCamera oc)
    {
        var (mn, mx) = _geom!.Bounds();
        var center = new SharpDX.Vector3((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, (mn.Z + mx.Z) / 2);
        var ext = mx - mn;
        float radius = Math.Max(ext.X, Math.Max(ext.Y, ext.Z)); if (radius <= 0) radius = 1;
        var dir = SharpDX.Vector3.Normalize(new SharpDX.Vector3(1f, -1f, 0.7f));
        var pos = center + dir * radius * 2.2f;
        oc.Position = new Point3D(pos.X, pos.Y, pos.Z);
        oc.LookDirection = new Vector3D(center.X - pos.X, center.Y - pos.Y, center.Z - pos.Z);
        oc.UpDirection = new Vector3D(0, 0, 1);
        oc.Width = radius * 1.6;
    }

    partial void OnIsometricChanged(bool value) { Camera = MakeCamera(value); FrameCamera(); }
    partial void OnTexturedChanged(bool value) => _ = LoadAsync();
    partial void OnDetailChanged(ModelDetail value) => _ = LoadAsync();
    partial void OnMaterialViewChanged(MaterialView value) => _ = LoadAsync();
    partial void OnFlatShadingChanged(bool value) { AppState.Instance.SetFlatShading(value); _ = LoadAsync(); }
    partial void OnPreviewBgChanged(MColor value) => AppState.Instance.SetPreviewBg(BgColors.ToHex(value));

    partial void OnLightingChanged(LightingStyle value) => Lights.Apply(value, Exposure);
    partial void OnExposureChanged(double value) => Lights.Apply(Lighting, value);
    private void OnChannelEdited() => ModelPreview.ApplyTint(PreviewModels, ChannelValues);

    partial void OnWireframeChanged(bool value)
    {
        foreach (var e in PreviewModels)
            if (e is MeshGeometryModel3D m) m.RenderWireframe = value;
    }
}
