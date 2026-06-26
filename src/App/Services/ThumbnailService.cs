using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModelCodex.App.ViewModels;
using Tiger;
using Tiger.Model;

namespace ModelCodex.App.Services;

/// <summary>
/// Renders grid-tile thumbnails: each tile shows the actual 3D model in isometric, textured with its
/// albedo, rasterized on the CPU off the UI thread. Hovering re-renders at a cursor-derived angle so the
/// tile subtly orbits. Geometry + per-part textures are cached on the tile so hover only re-rasterizes.
/// </summary>
public static class ThumbnailService
{
    public const int ThumbSize = 184;
    private const int TexMip = 128; // small albedo mip for UV sampling
    private static readonly SemaphoreSlim Gate = new(Math.Max(2, Environment.ProcessorCount / 2));
    private static readonly ConcurrentDictionary<uint, BitmapSource> ThumbCache = new();
    private static readonly ConcurrentDictionary<uint, TexSample?> TexCache = new();
    /// <summary>tag -> has no decodable geometry (populated as thumbnails/scans parse).</summary>
    public static readonly ConcurrentDictionary<uint, bool> EmptyCache = new();

    private static readonly (byte, byte, byte) Bg = (28, 30, 36);
    private static readonly (byte, byte, byte) Face = (170, 174, 182);

    public static void Request(ModelTile tile)
    {
        if (tile.LoadRequested) return;
        tile.LoadRequested = true;
        if (ThumbCache.TryGetValue(tile.TagHash, out var cached)) { tile.BaseThumb = cached; tile.Thumb = cached; }
        _ = LoadAsync(tile);
    }

    private static async Task LoadAsync(ModelTile tile)
    {
        PackageManager? mgr = AppState.Instance.Manager;
        if (mgr == null) return;
        await Gate.WaitAsync();
        try
        {
            var (geom, tex, bmp) = await Task.Run<(ModelGeometry?, IReadOnlyList<TexSample?>?, BitmapSource?)>(() =>
            {
                try
                {
                    var g = ModelParse.Parse(mgr, tile.Entry);
                    bool empty = g == null || g.VertexCount == 0;
                    EmptyCache[tile.TagHash] = empty;
                    if (empty) return (null, null, null);
                    var textures = ResolvePartTextures(mgr, g);
                    var src = ToBitmap(IsoThumbnail.Render(g, ThumbSize, Bg, Face,
                        IsoThumbnail.DefaultAzimuth, IsoThumbnail.DefaultElevation, textures));
                    return (g, textures, src);
                }
                catch { return (null, null, null); }
            });

            if (bmp != null) ThumbCache[tile.TagHash] = bmp;
            var app = Application.Current;
            if (app == null) return;
            await app.Dispatcher.InvokeAsync(() =>
            {
                tile.Geometry = geom; tile.PartTextures = tex;
                if (bmp != null) { tile.BaseThumb = bmp; tile.Thumb = bmp; } else tile.Failed = true;
            });
        }
        finally { Gate.Release(); }
    }

    /// <summary>Per-part albedo as small decoded mips (cached globally by texture hash).</summary>
    private static IReadOnlyList<TexSample?> ResolvePartTextures(PackageManager mgr, ModelGeometry geom)
    {
        var list = new List<TexSample?>(geom.Parts.Count);
        foreach (var part in geom.Parts)
        {
            TexSample? sample = null;
            if (part.MaterialHash != 0 && MaterialMap.Albedo(mgr, part.MaterialHash) is uint th)
            {
                if (!TexCache.TryGetValue(th, out sample))
                {
                    try
                    {
                        if (mgr.ByTag.TryGetValue(th, out var te) && mgr.DecodeThumb(te, TexMip) is { } d)
                            sample = new TexSample(d.rgba, d.width, d.height);
                    }
                    catch { sample = null; }
                    TexCache[th] = sample;
                }
            }
            list.Add(sample);
        }
        return list;
    }

    public static void Hover(ModelTile tile, double fx, double fy)
    {
        var geom = tile.Geometry;
        if (geom == null || tile.HoverRendering) return;
        tile.HoverRendering = true;
        float az = IsoThumbnail.DefaultAzimuth + (float)fx * 0.6f;
        float el = IsoThumbnail.DefaultElevation - (float)fy * 0.45f;
        var tex = tile.PartTextures;
        _ = Task.Run(() =>
        {
            BitmapSource? src = null;
            try { src = ToBitmap(IsoThumbnail.Render(geom, ThumbSize, Bg, Face, az, el, tex)); } catch { }
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (src != null) tile.Thumb = src;
                tile.HoverRendering = false;
            });
        });
    }

    public static void ResetHover(ModelTile tile)
    {
        if (tile.BaseThumb != null) tile.Thumb = tile.BaseThumb;
    }

    private static BitmapSource ToBitmap(byte[] rgba)
    {
        for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]); // RGBA->BGRA
        var src = BitmapSource.Create(ThumbSize, ThumbSize, 96, 96, PixelFormats.Bgra32, null, rgba, ThumbSize * 4);
        src.Freeze();
        return src;
    }
}
