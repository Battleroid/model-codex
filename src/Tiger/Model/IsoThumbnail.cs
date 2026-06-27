using System.Numerics;

namespace Tiger.Model;

/// <summary>A decoded texture (small mip) for thumbnail UV sampling.</summary>
public readonly record struct TexSample(byte[] Rgba, int W, int H)
{
    public (byte r, byte g, byte b) At(float u, float v)
    {
        if (W <= 0 || H <= 0) return (170, 174, 182);
        int x = (int)(Frac(u) * W); if (x >= W) x = W - 1; if (x < 0) x = 0;
        int y = (int)(Frac(v) * H); if (y >= H) y = H - 1; if (y < 0) y = 0;
        int i = (y * W + x) * 4;
        return (Rgba[i], Rgba[i + 1], Rgba[i + 2]);
    }
    private static float Frac(float x) { x -= MathF.Floor(x); return x < 0 ? x + 1 : x; }
}

/// <summary>
/// CPU isometric rasterizer for grid thumbnails — projects a model orthographically from a fixed iso
/// vantage, shades by surface normal, and (optionally) samples each part's albedo texture by UV.
/// No GPU, fully off-thread. Returns RGBA8 (size*size*4).
/// </summary>
public static class IsoThumbnail
{
    public const float DefaultAzimuth = 0.785398f;   // 45°
    public const float DefaultElevation = 0.5235988f; // 30°

    /// <summary>Render at <paramref name="size"/>, anti-aliased by supersampling: rasterize at
    /// size×<paramref name="supersample"/> then box-downsample. ss=1 disables AA.</summary>
    public static byte[] Render(ModelGeometry geom, int size,
        (byte r, byte g, byte b) bg, (byte r, byte g, byte b) face,
        float azimuth = DefaultAzimuth, float elevation = DefaultElevation,
        IReadOnlyList<TexSample?>? partTextures = null, int supersample = 2)
    {
        int ss = Math.Clamp(supersample, 1, 4);
        if (ss == 1) return RenderCore(geom, size, bg, face, azimuth, elevation, partTextures);
        int hi = size * ss;
        byte[] big = RenderCore(geom, hi, bg, face, azimuth, elevation, partTextures);
        return Downsample(big, hi, size, ss);
    }

    private static byte[] Downsample(byte[] src, int srcSize, int dstSize, int ss)
    {
        var dst = new byte[dstSize * dstSize * 4];
        int n = ss * ss;
        for (int y = 0; y < dstSize; y++)
        for (int x = 0; x < dstSize; x++)
        {
            int r = 0, g = 0, b = 0, a = 0;
            for (int dy = 0; dy < ss; dy++)
            for (int dx = 0; dx < ss; dx++)
            {
                int si = (((y * ss) + dy) * srcSize + (x * ss + dx)) * 4;
                r += src[si]; g += src[si + 1]; b += src[si + 2]; a += src[si + 3];
            }
            int di = (y * dstSize + x) * 4;
            dst[di] = (byte)(r / n); dst[di + 1] = (byte)(g / n); dst[di + 2] = (byte)(b / n); dst[di + 3] = (byte)(a / n);
        }
        return dst;
    }

    private static byte[] RenderCore(ModelGeometry geom, int size,
        (byte r, byte g, byte b) bg, (byte r, byte g, byte b) face,
        float azimuth, float elevation,
        IReadOnlyList<TexSample?>? partTextures)
    {
        var img = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            img[i * 4] = bg.r; img[i * 4 + 1] = bg.g; img[i * 4 + 2] = bg.b; img[i * 4 + 3] = 255;
        }
        var depth = new float[size * size];
        Array.Fill(depth, float.MinValue); // keep the NEAREST fragment (largest depth-along-view)

        var (mn, mx) = geom.Bounds();
        var center = (mn + mx) * 0.5f;
        float extent = Math.Max(mx.X - mn.X, Math.Max(mx.Y - mn.Y, mx.Z - mn.Z));
        if (extent <= 0) return img;

        float az = azimuth, el = Math.Clamp(elevation, 0.08f, 1.49f);
        var f = Vector3.Normalize(new Vector3(MathF.Cos(el) * MathF.Cos(az), MathF.Cos(el) * MathF.Sin(az), MathF.Sin(el)));
        var right = Vector3.Normalize(Vector3.Cross(f, new Vector3(0, 0, 1)));
        var up = Vector3.Cross(right, f);
        var light = Vector3.Normalize(new Vector3(-0.35f, -0.45f, 0.82f));

        float margin = size * 0.10f, span = size - 2 * margin;
        float scale = span / (extent * 1.15f);

        (float sx, float sy, float d) Project(Vector3 p)
        {
            var q = p - center;
            return (size * 0.5f + Vector3.Dot(q, right) * scale, size * 0.5f - Vector3.Dot(q, up) * scale, Vector3.Dot(q, f));
        }

        for (int pi = 0; pi < geom.Parts.Count; pi++)
        {
            var part = geom.Parts[pi];
            var pos = part.Positions;
            var uvs = part.Texcoords;
            TexSample? tex = partTextures != null && pi < partTextures.Count ? partTextures[pi] : null;

            for (int t = 0; t + 2 < part.Indices.Count; t += 3)
            {
                int ia = part.Indices[t], ib = part.Indices[t + 1], ic = part.Indices[t + 2];
                if (ia >= pos.Count || ib >= pos.Count || ic >= pos.Count) continue;
                var pa = Project(pos[ia]); var pb = Project(pos[ib]); var pc = Project(pos[ic]);

                var gn = Vector3.Cross(pos[ib] - pos[ia], pos[ic] - pos[ia]);
                if (gn.LengthSquared() < 1e-12f) continue;
                gn = Vector3.Normalize(gn);
                float lit = 0.30f + 0.70f * Math.Max(0f, Math.Abs(Vector3.Dot(gn, light)));

                Vector2 ua = UV(uvs, ia), ub = UV(uvs, ib), uc = UV(uvs, ic);
                Raster(img, depth, size, pa, pb, pc, ua, ub, uc, lit, face, tex);
            }
        }
        return img;
    }

    private static Vector2 UV(List<Vector2> uvs, int i) => i < uvs.Count ? uvs[i] : Vector2.Zero;

    private static void Raster(byte[] img, float[] depth, int size,
        (float sx, float sy, float d) a, (float sx, float sy, float d) b, (float sx, float sy, float d) c,
        Vector2 ua, Vector2 ub, Vector2 uc, float lit, (byte r, byte g, byte b) face, TexSample? tex)
    {
        int minX = Math.Max(0, (int)MathF.Floor(Math.Min(a.sx, Math.Min(b.sx, c.sx))));
        int maxX = Math.Min(size - 1, (int)MathF.Ceiling(Math.Max(a.sx, Math.Max(b.sx, c.sx))));
        int minY = Math.Max(0, (int)MathF.Floor(Math.Min(a.sy, Math.Min(b.sy, c.sy))));
        int maxY = Math.Min(size - 1, (int)MathF.Ceiling(Math.Max(a.sy, Math.Max(b.sy, c.sy))));
        float area = Edge(a, b, c);
        if (MathF.Abs(area) < 1e-6f) return;

        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            var p = ((float)x + 0.5f, (float)y + 0.5f, 0f);
            float w0 = Edge(b, c, p), w1 = Edge(c, a, p), w2 = Edge(a, b, p);
            bool inside = (w0 >= 0 && w1 >= 0 && w2 >= 0) || (w0 <= 0 && w1 <= 0 && w2 <= 0);
            if (!inside) continue;
            float l0 = w0 / area, l1 = w1 / area, l2 = w2 / area;
            float d = l0 * a.d + l1 * b.d + l2 * c.d;
            int idx = y * size + x;
            if (d <= depth[idx]) continue; // keep nearest
            depth[idx] = d;

            byte br, bg2, bb;
            if (tex is { } ts)
            {
                float u = l0 * ua.X + l1 * ub.X + l2 * uc.X;
                float v = l0 * ua.Y + l1 * ub.Y + l2 * uc.Y;
                // Parser texcoords use the export/glTF V convention; flip V so the CPU thumbnail samples
                // the same way the HelixToolkit preview does (see ModelSceneBuilder.AppendPart).
                var (cr, cg, cbl) = ts.At(u, 1f - v);
                br = cr; bg2 = cg; bb = cbl;
            }
            else { br = face.r; bg2 = face.g; bb = face.b; }

            img[idx * 4] = (byte)(br * lit);
            img[idx * 4 + 1] = (byte)(bg2 * lit);
            img[idx * 4 + 2] = (byte)(bb * lit);
            img[idx * 4 + 3] = 255;
        }
    }

    private static float Edge((float sx, float sy, float d) a, (float sx, float sy, float d) b, (float sx, float sy, float d) c)
        => (b.sx - a.sx) * (c.sy - a.sy) - (b.sy - a.sy) * (c.sx - a.sx);
}
