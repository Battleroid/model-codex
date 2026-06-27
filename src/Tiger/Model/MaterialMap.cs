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
    // SMaterial (class 0x808031D8): pixel shader sub-struct @ 0x278; its Textures DynamicArray @ +0x8.
    // STextureTag (0x18 bytes): TextureIndex u32 @ 0x0; same-package Texture u32 @ 0x8 (0xFFFFFFFF when
    // unbound); cross-package Texture Tag64 @ 0x10. Register order == binding order; slot 0 is the albedo.
    private const int MAT_PixelShader = 0x278;

    /// <summary>The pixel shader's constant-buffer Vec4 values — the material "channels" (named in Deimos
    /// via external metadata we don't have, so exposed here as raw indexed values). Read-only inspection.</summary>
    public static List<(float x, float y, float z, float w)> ChannelValues(PackageManager mgr, uint materialHash)
    {
        var list = new List<(float, float, float, float)>();
        byte[]? d = mgr.ReadTag(materialHash);
        int f = MAT_PixelShader + 0x50; // SMaterialShader.CBuffers DynamicArray<Vec4>
        if (d == null || f + 0x18 > d.Length) return list;
        long count = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(f));
        long rel = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(f + 8));
        int off = f + 0x18 + (int)rel;
        if (count is < 0 or > 512 || off < 0 || off + (int)count * 16 > d.Length) return list;
        for (int k = 0; k < count; k++)
        {
            int e = off + k * 16;
            list.Add((
                BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(e)),
                BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(e + 4)),
                BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(e + 8)),
                BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(e + 12))));
        }
        return list;
    }

    /// <summary>The pixel shader's bound textures in register order: (TextureIndex, textureHash). This is
    /// the authoritative texture set — slot 0 is albedo, slot 1 normal, etc. Empty if none are bound.</summary>
    public static List<(int index, uint tex)> PixelTextures(PackageManager mgr, uint materialHash)
    {
        var list = new List<(int, uint)>();
        byte[]? d = mgr.ReadTag(materialHash);
        int f = MAT_PixelShader + 0x8;
        if (d == null || f + 0x18 > d.Length) return list;
        long count = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(f));
        long rel = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(f + 8));
        int off = f + 0x18 + (int)rel;
        if (count is < 0 or > 64) return list;
        for (int k = 0; k < count; k++)
        {
            int e = off + k * 0x18;
            if (e + 0x18 > d.Length) break;
            int idx = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(e));
            uint tex32 = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(e + 0x8));
            uint tex = mgr.ByTag.ContainsKey(tex32) ? tex32
                : mgr.TryResolveHash64(BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(e + 0x10)), out uint h32)
                  && mgr.ByTag.ContainsKey(h32) ? h32 : 0;
            if (tex != 0) list.Add((idx, tex));
        }
        return list;
    }

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

    /// <summary>The pixel-shader texture bound at a given register slot (0 = albedo, 1 = normal, …).</summary>
    public static uint? SlotTexture(PackageManager mgr, uint materialHash, int slot)
    {
        var px = PixelTextures(mgr, materialHash);
        foreach (var (idx, tex) in px) if (idx == slot) return tex;
        return slot < px.Count ? px[slot].tex : (uint?)null;
    }

    /// <summary>Pick the albedo/diffuse texture. The authoritative source is the pixel shader's register 0
    /// (how the engine binds albedo); fall back to a byte-scan heuristic (first usable sRGB, skipping tiny
    /// dummies) only when the material has no bound pixel textures.</summary>
    public static uint? Albedo(PackageManager mgr, uint materialHash)
    {
        if (SlotTexture(mgr, materialHash, 0) is uint slot0) return slot0;

        var refs = TextureRefs(mgr, materialHash);
        uint firstSrgb = 0, largestSrgb = 0, first = 0; long largestArea = -1;
        foreach (uint t in refs)
        {
            if (!mgr.ByTag.TryGetValue(t, out var te)) continue;
            if (first == 0) first = t;
            try
            {
                var h = mgr.LoadHeader(te);
                if (h.Width <= 8 || h.Height <= 8) continue; // skip dummy/detail
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

    /// <summary>The material's tangent-space normal map. Pixel register 1 is the engine's normal slot;
    /// fall back to detecting a flat-blue map among the references.</summary>
    public static uint? Normal(PackageManager mgr, uint materialHash)
    {
        if (SlotTexture(mgr, materialHash, 1) is uint slot1 && mgr.ByTag.TryGetValue(slot1, out var s1)
            && IsNormalMap(mgr, s1)) return slot1;

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
