using System.Buffers.Binary;

namespace Tiger.Model;

/// <summary>A texture channel referenced by a material (resolved by scanning the material tag).</summary>
public sealed record MaterialChannel(uint TexHash, int Width, int Height, string Format, bool Srgb)
{
    public string TagId => $"{TexHash:X8}";
}

/// <summary>
/// Resolves a static-mesh material (a type-8 class tag) to its texture set by scanning the tag bytes
/// for same-package 32-bit texture refs and cross-package 64-bit refs (via the hash64 table) — the same
/// technique texture-codex uses for asset grouping. Picks the albedo (sRGB, largest area) for shading.
/// </summary>
public static class MaterialMap
{
    /// <summary>Distinct texture taghashes this material references, in file order.</summary>
    public static List<uint> TextureRefs(PackageManager mgr, uint materialHash)
    {
        var refs = new List<uint>();
        byte[]? d = mgr.ReadTag(materialHash);
        if (d == null) return refs;
        var seen = new HashSet<uint>();
        for (int o = 0; o + 4 <= d.Length; o += 4)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
            uint tex = 0;
            if (mgr.ByTag.ContainsKey(v)) tex = v;
            else if (o + 8 <= d.Length && mgr.TryResolveHash64(BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o)), out uint h32)
                     && mgr.ByTag.ContainsKey(h32)) tex = h32;
            if (tex != 0 && seen.Add(tex)) refs.Add(tex);
        }
        return refs;
    }

    /// <summary>Resolve a material's channels with header metadata (for the channels panel).</summary>
    public static List<MaterialChannel> Channels(PackageManager mgr, uint materialHash)
    {
        var list = new List<MaterialChannel>();
        foreach (uint t in TextureRefs(mgr, materialHash))
        {
            if (!mgr.ByTag.TryGetValue(t, out var te)) continue;
            try
            {
                var h = mgr.LoadHeader(te);
                list.Add(new MaterialChannel(t, h.Width, h.Height, h.FormatName, TigerTexture.IsSrgb(h.DxgiFormat)));
            }
            catch { /* skip undecodable header */ }
        }
        return list;
    }

    /// <summary>Pick the albedo/diffuse texture: the first sRGB (colour) texture in material order;
    /// else the largest sRGB; else the first texture. Marathon materials are dye/channel-packed, so the
    /// first sRGB slot is the closest thing to a base colour.</summary>
    public static uint? Albedo(PackageManager mgr, uint materialHash)
    {
        var refs = TextureRefs(mgr, materialHash);
        uint firstSrgb = 0, largestSrgb = 0, first = 0; long largestArea = -1;
        foreach (uint t in refs)
        {
            if (!mgr.ByTag.TryGetValue(t, out var te)) continue;
            if (first == 0) first = t;
            try
            {
                var h = mgr.LoadHeader(te);
                if (TigerTexture.IsSrgb(h.DxgiFormat))
                {
                    if (firstSrgb == 0) firstSrgb = t;
                    long area = (long)h.Width * h.Height;
                    if (area > largestArea) { largestArea = area; largestSrgb = t; }
                }
            }
            catch { /* skip */ }
        }
        uint pick = firstSrgb != 0 ? firstSrgb : (largestSrgb != 0 ? largestSrgb : first);
        return pick == 0 ? null : pick;
    }

    /// <summary>Pick the "gstack" packed map (metalness/emission/transmission) for best-effort emissive:
    /// the largest non-sRGB texture that isn't the normal map or a tiny/detail map. Heuristic — Marathon
    /// has no fixed slot semantics (the pixel shader decides), so this is a reasonable approximation.</summary>
    public static uint? Gstack(PackageManager mgr, uint materialHash)
    {
        uint best = 0; long bestArea = 0; uint albedo = Albedo(mgr, materialHash) ?? 0;
        foreach (uint t in TextureRefs(mgr, materialHash))
        {
            if (t == albedo || !mgr.ByTag.TryGetValue(t, out var te)) continue;
            try
            {
                var h = mgr.LoadHeader(te);
                if (TigerTexture.IsSrgb(h.DxgiFormat)) continue;          // colour map, not a data pack
                if (h.Width <= 8 || h.Height <= 8) continue;              // dummy/detail
                if (IsNormalMap(mgr, te)) continue;                       // tangent-space normal
                long area = (long)h.Width * h.Height;
                if (area > bestArea) { bestArea = area; best = t; }
            }
            catch { }
        }
        return best == 0 ? null : best;
    }

    /// <summary>The material's tangent-space normal map, if present (largest one detected).</summary>
    public static uint? Normal(PackageManager mgr, uint materialHash)
    {
        uint best = 0; long bestArea = 0;
        foreach (uint t in TextureRefs(mgr, materialHash))
        {
            if (!mgr.ByTag.TryGetValue(t, out var te) || !IsNormalMap(mgr, te)) continue;
            try { var h = mgr.LoadHeader(te); long a = (long)h.Width * h.Height; if (a > bestArea) { bestArea = a; best = t; } }
            catch { }
        }
        return best == 0 ? null : best;
    }

    /// <summary>A tangent-space normal map reads ~(128,128,255): flat blue. Sample a tiny mip to detect it.</summary>
    private static bool IsNormalMap(PackageManager mgr, TextureEntry te)
    {
        try
        {
            if (mgr.DecodeThumb(te, 16) is not { } d || d.rgba.Length < 16) return false;
            long r = 0, g = 0, b = 0; int n = d.rgba.Length / 4;
            for (int i = 0; i < d.rgba.Length; i += 4) { r += d.rgba[i]; g += d.rgba[i + 1]; b += d.rgba[i + 2]; }
            int ar = (int)(r / n), ag = (int)(g / n), ab = (int)(b / n);
            return ab > 200 && Math.Abs(ar - 128) < 40 && Math.Abs(ag - 128) < 40;
        }
        catch { return false; }
    }
}
