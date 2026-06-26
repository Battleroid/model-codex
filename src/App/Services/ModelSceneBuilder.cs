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
    /// <summary>Albedo texture as a DDS blob, or null for an untextured part.</summary>
    public byte[]? AlbedoDds { get; init; }
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
        // Decode the albedo, force it OPAQUE (the diffuse-map alpha is a mask, not transparency — leaving
        // it makes HelixToolkit render coloured regions see-through, so the model looks washed/gray), then
        // PNG-encode. This mirrors MIDA's RemoveAlpha step.
        byte[]? Tex(uint texHash)
        {
            if (texCache.TryGetValue(texHash, out var cached)) return cached;
            byte[]? png = null;
            try
            {
                // The large mips live in a separately-streamed buffer that often fails to resolve; only the
                // small inline mips decode reliably (that's why the CPU thumbnail works but the preview was
                // gray). Chain down to a mip that actually decodes.
                if (mgr.ByTag.TryGetValue(texHash, out var te) &&
                    (mgr.DecodeThumb(te, 512) ?? mgr.DecodeThumb(te, 128) ?? mgr.Decode(te)) is { } d)
                {
                    for (int i = 3; i < d.rgba.Length; i += 4) d.rgba[i] = 255;
                    png = EncodePng(d.rgba, d.width, d.height);
                }
            }
            catch { }
            texCache[texHash] = png;
            return png;
        }

        foreach (var part in geom.Parts)
        {
            var positions = new Vector3Collection();
            var indices = new IntCollection();
            var normals = new Vector3Collection();
            var texcoords = new Vector2Collection();
            int baseV = 0;
            AppendPart(part, positions, indices, normals, texcoords, ref baseV);

            byte[]? tex = null;
            if (overrideAlbedo is uint forced) tex = Tex(forced);
            else if (textured && part.MaterialHash != 0 && MaterialMap.Albedo(mgr, part.MaterialHash) is uint texHash)
                tex = Tex(texHash);

            result.Add(new PartRender { Geometry = Finish(positions, indices, normals, texcoords), AlbedoDds = tex });
        }
        return result;
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
