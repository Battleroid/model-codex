using CommunityToolkit.Mvvm.ComponentModel;

namespace ModelCodex.App.Services;

/// <summary>An editable material channel value (one pixel-shader cbuffer Vec4). Drag-scrub fields bind to
/// X/Y/Z/W. A channel whose default is a white (1,1,1) colour is treated as a tint: its current RGB
/// multiplies the albedo in the live preview (a best-effort stand-in for running the real shader).</summary>
public sealed partial class ChannelEdit : ObservableObject
{
    public string Label { get; }
    public double DefX { get; }
    public double DefY { get; }
    public double DefZ { get; }
    public double DefW { get; }
    /// <summary>True when the default is ~(1,1,1) — i.e. a colour tint we can approximate on the albedo.</summary>
    public bool IsTint { get; }

    /// <summary>Invoked whenever any component changes (the VM recomputes + applies the albedo tint).</summary>
    public Action? Changed { get; set; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _z;
    [ObservableProperty] private double _w;

    public ChannelEdit(string label, float x, float y, float z, float w)
    {
        Label = label;
        _x = DefX = x; _y = DefY = y; _z = DefZ = z; _w = DefW = w;
        static bool One(float v) => v is > 0.9f and < 1.1f;
        IsTint = One(x) && One(y) && One(z);
    }

    partial void OnXChanged(double value) => Changed?.Invoke();
    partial void OnYChanged(double value) => Changed?.Invoke();
    partial void OnZChanged(double value) => Changed?.Invoke();
    partial void OnWChanged(double value) => Changed?.Invoke();

    public void Reset() { X = DefX; Y = DefY; Z = DefZ; W = DefW; }
}
