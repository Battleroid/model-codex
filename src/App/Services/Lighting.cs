using System.Windows.Media;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ModelCodex.App.Services;

public enum LightingStyle { Lookdev, Studio, Sun, Flat }

/// <summary>Combo options. ToString returns Name so the closed ComboBox display shows it reliably.</summary>
public sealed record LightingOption(LightingStyle Style, string Name) { public override string ToString() => Name; }
public sealed record DetailOption(Tiger.Model.ModelDetail Detail, string Name) { public override string ToString() => Name; }
public sealed record BgOption(Color Color, string Name) { public override string ToString() => Name; }

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

    public void Apply(LightingStyle style)
    {
        var r = Lighting.Rig(style);
        Ambient = r.Ambient; Key = r.Key; KeyDir = r.KeyDir;
        Fill = r.Fill; FillDir = r.FillDir; Rim = r.Rim; RimDir = r.RimDir;
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

        _ => new LightRig( // Lookdev — bright, even, readable
            C(140, 142, 150),
            C(190, 192, 200), V(-0.45, -0.55, -0.70),
            C(150, 152, 165), V(0.55, 0.45, -0.25),
            C(130, 132, 142), V(0.10, -0.30, 0.80)),
    };

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    private static Vector3D V(double x, double y, double z) => new(x, y, z);
}
