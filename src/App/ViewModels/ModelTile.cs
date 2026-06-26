using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Tiger.Model;

namespace ModelCodex.App.ViewModels;

/// <summary>A grid tile wrapping a model, with a lazily-rendered isometric thumbnail.</summary>
public sealed partial class ModelTile : ObservableObject
{
    public ModelEntry Entry { get; }
    public string TagId => Entry.TagId;
    public uint TagHash => Entry.TagHash;

    [ObservableProperty] private BitmapSource? _thumb;
    [ObservableProperty] private bool _failed;

    /// <summary>Load-once guard flipped when the tile is first realized by virtualization.</summary>
    public bool LoadRequested;

    /// <summary>Decoded geometry + per-part albedo + the base-angle thumbnail, cached for hover re-render.</summary>
    public ModelGeometry? Geometry;
    public IReadOnlyList<TexSample?>? PartTextures;
    public BitmapSource? BaseThumb;
    public bool HoverRendering;

    public ModelTile(ModelEntry entry) => Entry = entry;
}
