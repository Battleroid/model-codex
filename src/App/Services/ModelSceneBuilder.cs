using System.IO;
using HelixToolkit.SharpDX.Core;
using SharpDX;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Tiger;
using Tiger.Model;
using TModel = Tiger.Model.ModelGeometry;

namespace ModelCodex.App.Services;

/// <summary>Per-part render data produced off the UI thread (HelixToolkit Element3D is assembled on it).</summary>
public sealed class PartRender
{
    public MeshGeometry3D Geometry { get; init; } = null!;
    /// <summary>Albedo texture (PNG-encoded), or null for an untextured part.</summary>
    public byte[]? AlbedoDds { get; init; }
    /// <summary>Best-effort emissive map (PNG), or null when the material isn't emissive.</summary>
    public byte[]? EmissiveDds { get; init; }
}

/// <summary>Converts decoded <see cref="Tiger.Model.ModelGeometry"/> into HelixToolkit mesh data,
/// resolving each part's albedo texture when requested.</summary>
public static class ModelSceneBuilder
{
    /// <summary>One merged mesh (no materials) — used where a single geometry is enough.</summary>
    public static MeshGeometry3D BuildMerged(TModel geom)
    {
        var positions = new Vector3Collection();
        var indices = new IntCollection();
        var normals = new Vector3Collection();
        var texcoords = new Vector2Collection();
        int baseV = 0;
        foreach (var part in geom.Parts)
        {
            AppendPart(part, positions, indices, normals, texcoords, ref baseV);
        }
        return Finish(positions, indices, normals, texcoords);
    }

    /// <summary>One mesh per part, each with its resolved albedo texture (PNG) when <paramref name="textured"/>.
    /// If <paramref name="overrideAlbedo"/> is set, that texture is used for every part (channel preview).</summary>
    public static List<PartRender> BuildParts(TModel geom, PackageManager mgr, bool textured, uint? overrideAlbedo = null)
    {
        var result = new List<PartRender>();
        var texCache = new Dictionary<uint, byte[]?>();
        var mapCache = new Dictionary<uint, (byte[]? albedo, byte[]? emissive)>();

        foreach (var part in geom.Parts)
        {
            var positions = new Vector3Collection();
            var indices = new IntCollection();
            var normals = new Vector3Collection();
            var texcoords = new Vector2Collection();
            int baseV = 0;
            AppendPart(part, positions, indices, normals, texcoords, ref baseV);

            byte[]? albedo = null, emissive = null;
            if (overrideAlbedo is uint forced)
            {
                albedo = AlbedoPng(mgr, forced, texCache);
            }
            else if (textured && part.MaterialHash != 0)
            {
                (albedo, emissive) = Maps(mgr, part.MaterialHash, mapCache);
            }

            result.Add(new PartRender
            {
                Geometry = Finish(positions, indices, normals, texcoords),
                AlbedoDds = albedo,
                EmissiveDds = emissive,
            });
        }
        return result;
    }

    /// <summary>Decode one texture to an opaque albedo PNG (cached). The diffuse-map alpha is a mask, not
    /// transparency — forcing it opaque stops HelixToolkit rendering coloured regions see-through. Chains
    /// down through mips because large top mips often live in a separately-streamed buffer that fails.</summary>
    private static byte[]? AlbedoPng(PackageManager mgr, uint texHash, Dictionary<uint, byte[]?> cache)
    {
        if (cache.TryGetValue(texHash, out var cached)) return cached;
        byte[]? png = null;
        try
        {
            if (DecodeRobust(mgr, texHash) is { } d)
            {
                for (int i = 3; i < d.rgba.Length; i += 4) d.rgba[i] = 255;
                png = EncodePng(d.rgba, d.width, d.height);
            }
        }
        catch { }
        cache[texHash] = png;
        return png;
    }

    /// <summary>Resolve a material's albedo + best-effort emissive map (cached per material). Emission is
    /// the gstack texture's green channel run through Marathon's exp2 response curve and modulated by the
    /// albedo (a factual reproduction of the engine's emission math). The curve self-gates: green below
    /// ~0.5 yields zero, so non-emissive materials produce a black (skipped) map and never falsely glow.</summary>
    internal static (byte[]? albedo, byte[]? emissive) Maps(PackageManager mgr, uint materialHash,
        Dictionary<uint, (byte[]?, byte[]?)> cache)
    {
        if (cache.TryGetValue(materialHash, out var hit)) return hit;
        byte[]? albedoPng = null, emissivePng = null;
        try
        {
            if (MaterialMap.Albedo(mgr, materialHash) is uint albedoHash && DecodeRobust(mgr, albedoHash) is { } a)
            {
                var arr = (byte[])a.rgba.Clone();
                for (int i = 3; i < arr.Length; i += 4) arr[i] = 255;
                albedoPng = EncodePng(arr, a.width, a.height);

                if (MaterialMap.Gstack(mgr, materialHash) is uint gHash && DecodeRobust(mgr, gHash) is { } g)
                    emissivePng = BuildEmissive(a.rgba, a.width, a.height, g.rgba, g.width, g.height);
            }
        }
        catch { }
        cache[materialHash] = (albedoPng, emissivePng);
        return (albedoPng, emissivePng);
    }

    /// <summary>Decode a texture, preferring the mips that actually resolve. Large top mips often live in a
    /// separately-streamed buffer that fails, so try the smaller inline mips first (the known-good order),
    /// falling back to the full decode last.</summary>
    private static (byte[] rgba, int width, int height)? DecodeRobust(PackageManager mgr, uint texHash)
    {
        if (!mgr.ByTag.TryGetValue(texHash, out var te)) return null;
        return mgr.DecodeThumb(te, 512) ?? mgr.DecodeThumb(te, 256)
               ?? mgr.DecodeThumb(te, 128) ?? mgr.DecodeThumb(te, 64) ?? mgr.Decode(te);
    }

    // Genuine emission is LOCALIZED (screens, energy lines, indicators — a few % of the atlas). A gstack
    // whose green lights up over a large fraction is really a roughness/cavity/noise map (the heuristic
    // can't tell which texture is which without the shader), so reject it to avoid false "cloud glow".
    private const float MaxEmissiveCoverage = 0.30f;

    /// <summary>Emissive map = curve(gstack.green) * albedo. Returns null when nothing emits or the
    /// emission covers too much area to be real (a misidentified roughness/noise map).</summary>
    private static byte[]? BuildEmissive(byte[] albedo, int aw, int ah, byte[] gstack, int gw, int gh)
    {
        var outRgba = new byte[aw * ah * 4];
        int emitting = 0, total = aw * ah;
        for (int y = 0; y < ah; y++)
        for (int x = 0; x < aw; x++)
        {
            int ai = (y * aw + x) * 4;
            // Nearest-sample the gstack (shares the albedo's UV space, just a different resolution).
            int gx = gw == aw ? x : x * gw / aw;
            int gy = gh == ah ? y : y * gh / ah;
            float gG = gstack[(gy * gw + gx) * 4 + 1] / 255f;
            float t = Math.Clamp(gG * 2f - 1.00784314f, 0f, 1f);
            float intensity = MathF.Pow(2f, t * 13f - 7f) - 0.0078125f;
            if (intensity <= 0.02f) { outRgba[ai + 3] = 255; continue; }
            emitting++;
            outRgba[ai] = (byte)Math.Min(255f, albedo[ai] * intensity);
            outRgba[ai + 1] = (byte)Math.Min(255f, albedo[ai + 1] * intensity);
            outRgba[ai + 2] = (byte)Math.Min(255f, albedo[ai + 2] * intensity);
            outRgba[ai + 3] = 255;
        }
        if (emitting == 0 || (float)emitting / total > MaxEmissiveCoverage) return null;
        return EncodePng(outRgba, aw, ah);
    }

    private static void AppendPart(ModelPart part, Vector3Collection pos, IntCollection idx,
        Vector3Collection nrm, Vector2Collection uv, ref int baseV)
    {
        foreach (var p in part.Positions) pos.Add(new Vector3(p.X, p.Y, p.Z));
        foreach (var n in part.Normals) nrm.Add(new Vector3(n.X, n.Y, n.Z));
        foreach (var t in part.Texcoords) uv.Add(new Vector2(t.X, t.Y));
        foreach (var i in part.Indices) idx.Add(baseV + i);
        baseV += part.Positions.Count;
    }

    private static MeshGeometry3D Finish(Vector3Collection pos, IntCollection idx,
        Vector3Collection nrm, Vector2Collection uv)
    {
        // Decoded Tiger normals are quaternion-packed and unreliable; compute smooth normals explicitly
        // (HelixToolkit's CalculateNormals proved unreliable here — flat/dark shading).
        var acc = new Vector3[pos.Count];
        for (int i = 0; i + 2 < idx.Count; i += 3)
        {
            int a = idx[i], b = idx[i + 1], c = idx[i + 2];
            var fn = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
            acc[a] += fn; acc[b] += fn; acc[c] += fn;
        }
        var normals = new Vector3Collection(pos.Count);
        for (int i = 0; i < acc.Length; i++)
        {
            var v = acc[i];
            if (v.LengthSquared() > 1e-12f) { v.Normalize(); normals.Add(v); }
            else normals.Add(new Vector3(0, 0, 1));
        }
        return new MeshGeometry3D { Positions = pos, Indices = idx, TextureCoordinates = uv, Normals = normals };
    }

    private static byte[] EncodePng(byte[] rgba, int w, int h)
    {
        using var img = Image.LoadPixelData<Rgba32>(rgba, w, h);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }
}
