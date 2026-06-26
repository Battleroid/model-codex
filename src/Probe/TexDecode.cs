using System.Buffers.Binary;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Probe;

/// <summary>Phase-0 texture decode helpers: DXGI -> RGBA via BCnEncoder, PNG + DDS output.</summary>
public static class TexDecode
{
    // DXGI format -> BCnEncoder CompressionFormat (BCn), or null for raw paths handled separately.
    static CompressionFormat? BcFormat(ushort dxgi) => dxgi switch
    {
        71 or 72 => CompressionFormat.Bc1,        // BC1_UNORM / _SRGB
        74 or 75 => CompressionFormat.Bc2,
        77 or 78 => CompressionFormat.Bc3,
        80 or 81 => CompressionFormat.Bc4,
        83 or 84 => CompressionFormat.Bc5,
        95 or 96 => CompressionFormat.Bc6U,       // BC6H (HDR)
        98 or 99 => CompressionFormat.Bc7,
        _ => null,
    };

    static int BcBlockBytes(ushort dxgi) => dxgi switch
    {
        71 or 72 or 80 or 81 => 8,                // BC1, BC4 = 8 bytes/block
        _ => 16,                                  // BC2/3/5/6/7 = 16 bytes/block
    };

    /// <summary>Decode the top mip to RGBA8 (4 bytes/px). Returns null if format unsupported.</summary>
    public static byte[]? DecodeTopMip(ushort dxgi, byte[] data, int w, int h)
    {
        // Uncompressed paths.
        if (dxgi is 28 or 29)                      // R8G8B8A8_UNORM/_SRGB
        {
            int need = w * h * 4;
            var outp = new byte[need];
            Array.Copy(data, outp, Math.Min(need, data.Length));
            return outp;
        }
        if (dxgi is 87 or 88)                      // B8G8R8A8 -> swizzle to RGBA
        {
            int need = w * h * 4;
            var outp = new byte[need];
            int n = Math.Min(need, data.Length);
            for (int i = 0; i + 3 < n; i += 4)
            {
                outp[i + 0] = data[i + 2]; outp[i + 1] = data[i + 1];
                outp[i + 2] = data[i + 0]; outp[i + 3] = data[i + 3];
            }
            return outp;
        }
        if (dxgi is 61)                            // R8_UNORM -> grayscale
        {
            int need = w * h;
            var outp = new byte[w * h * 4];
            for (int i = 0; i < Math.Min(need, data.Length); i++)
            { outp[i * 4] = outp[i * 4 + 1] = outp[i * 4 + 2] = data[i]; outp[i * 4 + 3] = 255; }
            return outp;
        }

        var cf = BcFormat(dxgi);
        if (cf == null) return null;

        // Slice exactly the top mip's worth of block data.
        int blocksX = Math.Max(1, (w + 3) / 4), blocksY = Math.Max(1, (h + 3) / 4);
        int mipBytes = blocksX * blocksY * BcBlockBytes(dxgi);
        if (data.Length < mipBytes) return null;
        var mip = data.AsSpan(0, mipBytes).ToArray();

        var dec = new BcDecoder();
        if (cf == CompressionFormat.Bc6U)
        {
            // HDR -> simple Reinhard tonemap to 8-bit for preview.
            ColorRgbFloat[] hdr = dec.DecodeRawHdr(mip, w, h, CompressionFormat.Bc6U);
            var outp = new byte[w * h * 4];
            for (int i = 0; i < hdr.Length && i < w * h; i++)
            {
                outp[i * 4 + 0] = Tone(hdr[i].r); outp[i * 4 + 1] = Tone(hdr[i].g);
                outp[i * 4 + 2] = Tone(hdr[i].b); outp[i * 4 + 3] = 255;
            }
            return outp;
        }
        ColorRgba32[] px = dec.DecodeRaw(mip, w, h, cf.Value);
        var buf = new byte[w * h * 4];
        for (int i = 0; i < px.Length && i < w * h; i++)
        { buf[i * 4] = px[i].r; buf[i * 4 + 1] = px[i].g; buf[i * 4 + 2] = px[i].b; buf[i * 4 + 3] = px[i].a; }
        return buf;
    }

    static byte Tone(float v) { v = v / (1f + v); return (byte)Math.Clamp(MathF.Pow(v, 1f / 2.2f) * 255f, 0, 255); }

    public static void WritePng(string path, byte[] rgba, int w, int h)
    {
        using var img = Image.LoadPixelData<Rgba32>(rgba, w, h);
        img.SaveAsPng(path);
    }

    /// <summary>Minimal DDS (DX10 header) wrapping the raw buffer — for external cross-check.</summary>
    public static byte[] BuildDds(ushort dxgi, int w, int h, byte[] data)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0x20534444u);            // 'DDS '
        bw.Write(124u);                   // dwSize
        bw.Write(0x1007u | 0x80000u);     // CAPS|HEIGHT|WIDTH|PIXELFORMAT | LINEARSIZE
        bw.Write((uint)h); bw.Write((uint)w);
        int blocksX = Math.Max(1, (w + 3) / 4), blocksY = Math.Max(1, (h + 3) / 4);
        bool bc = dxgi >= 70;
        bw.Write((uint)(bc ? blocksX * blocksY * BcBlockBytes(dxgi) : w * 4)); // pitch/linear size
        bw.Write(0u);                     // depth
        bw.Write(1u);                     // mipcount
        for (int i = 0; i < 11; i++) bw.Write(0u);   // reserved
        // pixel format (32 bytes): DX10 FourCC
        bw.Write(32u);                    // pf size
        bw.Write(0x4u);                   // DDPF_FOURCC
        bw.Write(0x30315844u);            // 'DX10'
        bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u);
        bw.Write(0x1000u);                // caps TEXTURE
        bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u);
        // DX10 header (20 bytes)
        bw.Write((uint)dxgi);             // dxgiFormat
        bw.Write(3u);                     // D3D11_RESOURCE_DIMENSION_TEXTURE2D
        bw.Write(0u);                     // miscFlag
        bw.Write(1u);                     // arraySize
        bw.Write(0u);                     // miscFlags2
        bw.Write(data);
        return ms.ToArray();
    }
}
