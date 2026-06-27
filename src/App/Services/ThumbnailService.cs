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

    // Grid render settings (set from the library toolbar; changing any clears the rendered cache).
    private static (byte, byte, byte) Bg = (28, 30, 36);
    private static (byte, byte, byte) Face = (170, 174, 182);
    private static GridTex View = GridTex.Textured;

    public static void SetBg(System.Windows.Media.Color c) => Bg = (c.R, c.G, c.B);
    public static void SetLighting(LightingStyle s) => Face = s switch
    {
        LightingStyle.Flat => (208, 208, 212),
        LightingStyle.Studio => (192, 193, 198),
        LightingStyle.Sun => (200, 196, 182),
        _ => (170, 174, 182), // Lookdev
    };
    public static void SetView(GridTex v) => View = v;
    /// <summary>Drop rendered thumbnails (call after a settings change) so tiles re-render with new settings.</summary>
    public static void ClearRendered() => ThumbCache.Clear();

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
                    var parsed = ModelParse.Parse(mgr, tile.Entry);
                    bool empty = parsed == null || parsed.VertexCount == 0;
                    EmptyCache[tile.TagHash] = empty;
                    if (empty) return (null, null, null);
                    // Show only the default permutation; otherwise stacked variants z-fight and grey out.
                    var g = parsed.WithVariant(parsed.DefaultVariant);
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
        if (View == GridTex.Untextured) { foreach (var _ in geom.Parts) list.Add(null); return list; }
        TexSample? primary = null;
        foreach (var part in geom.Parts)
        {
            TexSample? sample = null;
            uint? texHash = View == GridTex.Normal
                ? (part.MaterialHash != 0 ? MaterialMap.Normal(mgr, part.MaterialHash) : null)
                : (part.MaterialHash != 0 ? MaterialMap.Albedo(mgr, part.MaterialHash) : null);
            if (texHash is uint th)
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
            primary ??= sample;
            list.Add(sample);
        }
        // Textureless stub-material parts fall back to the model's primary albedo (matches the preview).
        if (primary != null)
            for (int i = 0; i < list.Count; i++) list[i] ??= primary;
        return list;
    }

    public static void Hover(ModelTile tile, double fx, double fy)
    {
        var geom = tile.Geometry;
        if (geom == null) return;
        tile.Hovering = true; // pause auto-spin for this tile while the cursor drives it
        if (tile.HoverRendering) return;
        tile.HoverRendering = true;
        // Orbit around the model's *current* position (its paused spin angle) so spinning tiles don't
        // jump when grabbed; when spin is off SpinAngle is 0, giving the original default-angle orbit.
        float az = IsoThumbnail.DefaultAzimuth + tile.SpinAngle + (float)fx * 0.6f;
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
        tile.Hovering = false;
        // When spinning, leave the thumbnail where it is — the spin timer resumes it from SpinAngle.
        if (SpinEnabled) return;
        if (tile.BaseThumb != null) tile.Thumb = tile.BaseThumb;
    }

    // ===== Slow auto-spin of visible tiles (opt-in via Settings) =====
    private static readonly HashSet<ModelTile> Spinning = new();
    private static System.Windows.Threading.DispatcherTimer? _spinTimer;
    public static bool SpinEnabled { get; private set; }

    /// <summary>Track a realized tile so it participates in auto-spin (no-op cost when spin is off).</summary>
    public static void RegisterSpin(ModelTile tile) { lock (Spinning) Spinning.Add(tile); EnsureSpinTimer(); }
    public static void UnregisterSpin(ModelTile tile) { lock (Spinning) Spinning.Remove(tile); }

    /// <summary>Turn slow auto-spin on/off globally. When off, tiles snap back to their base angle.</summary>
    public static void SetSpin(bool on)
    {
        SpinEnabled = on;
        if (on) EnsureSpinTimer();
        else
        {
            _spinTimer?.Stop();
            ModelTile[] tiles; lock (Spinning) tiles = Spinning.ToArray();
            foreach (var t in tiles) { t.SpinAngle = 0; if (t.BaseThumb != null) t.Thumb = t.BaseThumb; }
        }
    }

    private static void EnsureSpinTimer()
    {
        if (!SpinEnabled || Application.Current == null) return;
        if (_spinTimer == null)
        {
            _spinTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(45) };
            _spinTimer.Tick += (_, _) => SpinTick();
        }
        if (!_spinTimer.IsEnabled) _spinTimer.Start();
    }

    private static void SpinTick()
    {
        ModelTile[] tiles; lock (Spinning) tiles = Spinning.ToArray();
        if (tiles.Length == 0) { _spinTimer?.Stop(); return; }
        const float step = 0.025f; // radians per tick; ~45ms ticks → smooth, ~full revolution every ~11s
        foreach (var tile in tiles)
        {
            var geom = tile.Geometry;
            // Skip until geometry loads, while a frame is in flight, or while the cursor is driving it.
            if (geom == null || tile.HoverRendering || tile.Hovering) continue;
            tile.SpinAngle += step; // per-tile angle so it resumes where a hover paused it
            float az = IsoThumbnail.DefaultAzimuth + tile.SpinAngle;
            tile.HoverRendering = true;
            var tex = tile.PartTextures;
            _ = Task.Run(() =>
            {
                BitmapSource? src = null;
                try { src = ToBitmap(IsoThumbnail.Render(geom, ThumbSize, Bg, Face, az, IsoThumbnail.DefaultElevation, tex)); } catch { }
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    if (src != null && SpinEnabled && !tile.Hovering) tile.Thumb = src;
                    tile.HoverRendering = false;
                });
            });
        }
    }

    private static BitmapSource ToBitmap(byte[] rgba)
    {
        for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]); // RGBA->BGRA
        var src = BitmapSource.Create(ThumbSize, ThumbSize, 96, 96, PixelFormats.Bgra32, null, rgba, ThumbSize * 4);
        src.Freeze();
        return src;
    }
}
