using System.Windows.Media;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ModelCodex.App.Services;

public enum LightingStyle { Lookdev, Studio, Sun, Flat }

/// <summary>How the preview shades a model. Shaded = lit render; the rest show a single material channel
/// flat/unlit (Deimos-style lookdev channels). Metalness/Emission/Transmission are the gstack R/G/B.</summary>
public enum MaterialView { Shaded, Albedo, Normal, Metalness, Emission, Transmission }

/// <summary>Texture shown on grid thumbnails.</summary>
public enum GridTex { Textured, Normal, Untextured }
public sealed record GridTexOption(GridTex Mode, string Name) { public override string ToString() => Name; }

/// <summary>One material channel value (pixel cbuffer Vec4) for the read-only inspector.</summary>
public sealed record ChannelValue(string Index, string Values);

/// <summary>Combo options. ToString returns Name so the closed ComboBox display shows it reliably.</summary>
public sealed record LightingOption(LightingStyle Style, string Name) { public override string ToString() => Name; }
public sealed record DetailOption(Tiger.Model.ModelDetail Detail, string Name) { public override string ToString() => Name; }
public sealed record BgOption(Color Color, string Name) { public override string ToString() => Name; }
public sealed record PermutationOption(int Index, string Name) { public override string ToString() => Name; }
public sealed record MaterialViewOption(MaterialView View, string Name) { public override string ToString() => Name; }

/// <summary>A fixed 4-light rig (1 ambient + 3 directional) for a lighting style.</summary>
public readonly record struct LightRig(
    Color Ambient,
    Color Key, Vector3D KeyDir,
    Color Fill, Vector3D FillDir,
    Color Rim, Vector3D RimDir);

/// <summary>
/// Bindable lighting state. The viewport hosts fixed AmbientLight3D + 3 DirectionalLight3D as direct
/// children bound to these properties (lights nested in ItemsModel3D don't illuminate, so they must be
/// direct viewport children). The lookdev selector calls <see cref="Apply"/>.
/// </summary>
public sealed partial class LightingState : ObservableObject
{
    [ObservableProperty] private Color _ambient;
    [ObservableProperty] private Color _key;
    [ObservableProperty] private Vector3D _keyDir;
    [ObservableProperty] private Color _fill;
    [ObservableProperty] private Vector3D _fillDir;
    [ObservableProperty] private Color _rim;
    [ObservableProperty] private Vector3D _rimDir;

    public LightingState() => Apply(LightingStyle.Lookdev);

    /// <summary>Apply a style's rig, scaling all light intensities by <paramref name="exposure"/>.</summary>
    public void Apply(LightingStyle style, double exposure = 1.0)
    {
        var r = Lighting.Rig(style);
        Ambient = Scale(r.Ambient, exposure); Key = Scale(r.Key, exposure); KeyDir = r.KeyDir;
        Fill = Scale(r.Fill, exposure); FillDir = r.FillDir; Rim = Scale(r.Rim, exposure); RimDir = r.RimDir;
    }

    private static Color Scale(Color c, double e)
    {
        byte S(byte v) => (byte)Math.Clamp(v * e, 0, 255);
        return Color.FromRgb(S(c.R), S(c.G), S(c.B));
    }
}

public static class Lighting
{
    public static readonly IReadOnlyList<LightingOption> Styles = new[]
    {
        new LightingOption(LightingStyle.Lookdev, "Lookdev"),
        new LightingOption(LightingStyle.Studio, "Studio"),
        new LightingOption(LightingStyle.Sun, "Sun"),
        new LightingOption(LightingStyle.Flat, "Flat"),
    };

    public static LightRig Rig(LightingStyle style) => style switch
    {
        LightingStyle.Studio => new LightRig(
            C(55, 57, 64),
            C(245, 245, 245), V(-0.5, -0.6, -0.7),
            C(120, 122, 135), V(0.7, 0.4, -0.2),
            C(150, 150, 165), V(0.1, 0.6, 0.6)),

        LightingStyle.Sun => new LightRig(
            C(95, 102, 118),
            C(255, 250, 235), V(-0.4, -0.5, -0.75),
            C(95, 105, 125), V(0.3, 0.4, 0.5),
            C(0, 0, 0), V(0, 0, 1)),

        LightingStyle.Flat => new LightRig(
            C(225, 225, 230),
            C(70, 70, 74), V(-0.4, -0.5, -0.7),
            C(0, 0, 0), V(0, 0, 1),
            C(0, 0, 0), V(0, 0, 1)),

        _ => new LightRig( // Lookdev — neutral key + soft fill/rim, low flat ambient so colour stays saturated
            C(78, 80, 88),
            C(158, 158, 162), V(-0.45, -0.55, -0.70),
            C(74, 76, 86), V(0.55, 0.45, -0.25),
            C(46, 48, 56), V(0.10, -0.30, 0.80)),
    };

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    private static Vector3D V(double x, double y, double z) => new(x, y, z);
}
