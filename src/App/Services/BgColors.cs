using System.Globalization;
using MColor = System.Windows.Media.Color;

namespace ModelCodex.App.Services;

/// <summary>Parse/format the persisted preview background colour ("#RRGGBB").</summary>
public static class BgColors
{
    private static readonly MColor Default = MColor.FromRgb(0x10, 0x10, 0x14);

    public static MColor Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Default;
        var s = hex.TrimStart('#');
        if (s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
            return MColor.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return Default;
    }

    public static string ToHex(MColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
