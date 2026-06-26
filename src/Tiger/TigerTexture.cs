using System.Buffers.Binary;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace Tiger;

/// <summary>Parsed Marathon texture header (Tiger engine, Beyond-Light era layout).
/// Offsets confirmed empirically against retail packages (see Probe):
///   0x00 u32 dataSize · 0x04 u16 dxgiFormat · 0x20 u16 magic(0xCAFE)
///   0x22 u16 width · 0x24 u16 height · 0x26 u16 depth · 0x28 u16 arraySize · 0x3C u32 bufferHash.</summary>
public struct TexHeader
{
    public uint DataSize;
    public ushort DxgiFormat;
    public ushort Width;
    public ushort Height;
    public ushort Depth;
    public ushort ArraySize;
    public byte BitsPerPixel;        // @0x2C
    public byte MipCount;            // @0x2D
    public uint BufferHash;          // tag hash of the large pixel buffer; 0xFFFFFFFF if none

    public bool HasExternalBuffer => BufferHash != 0 && BufferHash != 0xFFFFFFFF;
    public bool IsCubemap => ArraySize == 6;
    public string FormatName => TigerTexture.FormatName(DxgiFormat);
    public string ColorSpace => TigerTexture.IsSrgb(DxgiFormat) ? "sRGB" : "Linear";

    /// <summary>2D / Cubemap / Volume (N) / Array (N).</summary>
    public string Kind =>
        ArraySize == 6 ? "Cubemap" :
        Depth > 1 ? $"Volume · {Depth}" :
        ArraySize > 1 ? $"Array · {ArraySize}" : "2D";

    /// <summary>Decoded (uncompressed) memory footprint of the top slice, all mips.</summary>
    public string DecodedSize
    {
        get
        {
            long bytes = (long)Width * Height * 4;
            return bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.#} MB" : $"{bytes / 1024.0:0.#} KB";
        }
    }
}

/// <summary>Turns Tiger texture header tags + pixel buffers into RGBA / DDS.</summary>
public static class TigerTexture
{
    public const ushort Magic = 0xCAFE;     // at header offset 0x20
    public const int HeaderSize = 0x40;

    /// <summary>True if these entry type/subtype are texture headers (32/1, 32/2, 32/3).</summary>
    public static bool IsTextureHeader(Entry e) =>
        e.FileType == 32 && (e.FileSubType == 1 || e.FileSubType == 2 || e.FileSubType == 3);

    public static TexHeader ParseHeader(byte[] tag)
    {
        if (tag.Length < HeaderSize) throw new InvalidDataException("texture header too small");
        return new TexHeader
        {
            DataSize = BinaryPrimitives.ReadUInt32LittleEndian(tag.AsSpan(0x00)),
            DxgiFormat = BinaryPrimitives.ReadUInt16LittleEndian(tag.AsSpan(0x04)),
            Width = BinaryPrimitives.ReadUInt16LittleEndian(tag.AsSpan(0x22)),
            Height = BinaryPrimitives.ReadUInt16LittleEndian(tag.AsSpan(0x24)),
            Depth = BinaryPrimitives.ReadUInt16LittleEndian(tag.AsSpan(0x26)),
            ArraySize = BinaryPrimitives.ReadUInt16LittleEndian(tag.AsSpan(0x28)),
            BitsPerPixel = tag[0x2C],
            MipCount = tag[0x2D],
            BufferHash = BinaryPrimitives.ReadUInt32LittleEndian(tag.AsSpan(0x3C)),
        };
    }

    /// <summary>Sanity gate: valid magic, known format, plausible dimensions.</summary>
    public static bool TryProbe(byte[] tag, out TexHeader h)
    {
        h = default;
        if (tag.Length < HeaderSize) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(tag.AsSpan(0x20)) != Magic) return false;
        h = ParseHeader(tag);
        if (h.Width is < 1 or > 16384 || h.Height is < 1 or > 16384) return false;
        return Formats.ContainsKey(h.DxgiFormat);
    }

    // ---- decode ----

    /// <summary>Decode the top mip (slice 0) to RGBA8. Returns null for unsupported formats.</summary>
    public static byte[]? DecodeRgba(TexHeader h, byte[] pixels) =>
        DecodeCore(h.DxgiFormat, pixels, h.Width, h.Height);

    /// <summary>Decode a small mip near <paramref name="target"/> px for thumbnails — much cheaper
    /// than decoding the full top mip of a large texture. Falls back to mip 0 for single-mip data.</summary>
    public static byte[]? DecodeThumb(TexHeader h, byte[] pixels, int target, out int outW, out int outH)
    {
        ushort f = h.DxgiFormat;
        int w = h.Width, hh = h.Height;
        long off = 0, chosenOff = 0;
        int mw = w, mh = hh, cw = w, ch = hh;
        bool found = false;
        // Walk the mip chain (mip 0 = largest, at the front); pick the smallest mip whose
        // longest side is still >= target, so downscaling to target stays crisp.
        while (true)
        {
            long sz = MipBytes(mw, mh, f);
            if (sz <= 0 || off + sz > pixels.Length) break;
            if (Math.Max(mw, mh) >= target) { chosenOff = off; cw = mw; ch = mh; found = true; }
            else { if (!found) { chosenOff = off; cw = mw; ch = mh; found = true; } break; }
            off += sz;
            if (mw == 1 && mh == 1) break;
            mw = Math.Max(1, mw / 2); mh = Math.Max(1, mh / 2);
        }
        if (!found) { outW = w; outH = hh; return DecodeCore(f, pixels, w, hh); }   // mip0 didn't fit; try whole
        long msz = MipBytes(cw, ch, f);
        if (chosenOff + msz > pixels.Length) { outW = w; outH = hh; return DecodeCore(f, pixels, w, hh); }
        byte[] slice = pixels.AsSpan((int)chosenOff, (int)msz).ToArray();
        outW = cw; outH = ch;
        return DecodeCore(f, slice, cw, ch);
    }

    /// <summary>Bytes for one mip at (w,h) in the given format.</summary>
    static long MipBytes(int w, int h, ushort f)
    {
        if (BcFormat(f) != null) return (long)BlocksX(w) * BlocksY(h) * BlockBytes(f);
        int bpp = Bpp(f);
        return bpp == 0 ? -1 : (long)w * h * bpp;
    }

    static int Bpp(ushort f) => f switch
    {
        28 or 29 or 87 or 88 or 34 => 4,
        10 => 8, 2 => 16, 49 => 2, 61 => 1,
        _ => 0,
    };

    /// <summary>Decode RGBA8 from data starting at a mip boundary. Returns null for unsupported formats.</summary>
    static byte[]? DecodeCore(ushort f, byte[] pixels, int w, int hh)
    {
        switch (f)
        {
            case 28 or 29:                         // R8G8B8A8_UNORM/_SRGB
                return CopyRaw(pixels, w * hh * 4);
            case 87 or 88:                         // B8G8R8A8 -> RGBA
            {
                int need = w * hh * 4;
                var o = new byte[need];
                int n = Math.Min(need, pixels.Length);
                for (int i = 0; i + 3 < n; i += 4)
                { o[i] = pixels[i + 2]; o[i + 1] = pixels[i + 1]; o[i + 2] = pixels[i]; o[i + 3] = pixels[i + 3]; }
                return o;
            }
            case 61:                               // R8_UNORM -> grayscale
            {
                var o = new byte[w * hh * 4];
                int n = Math.Min(w * hh, pixels.Length);
                for (int i = 0; i < n; i++) { o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = pixels[i]; o[i * 4 + 3] = 255; }
                return o;
            }
            case 49:                               // R8G8_UNORM (e.g. normal XY) -> RG, B=0
            {
                var o = new byte[w * hh * 4];
                int n = Math.Min(w * hh * 2, pixels.Length);
                for (int i = 0; i + 1 < n; i += 2)
                { int p = i / 2 * 4; o[p] = pixels[i]; o[p + 1] = pixels[i + 1]; o[p + 2] = 0; o[p + 3] = 255; }
                return o;
            }
            case 10:                               // R16G16B16A16_FLOAT (HDR) -> tonemapped RGBA
            {
                var o = new byte[w * hh * 4];
                int count = w * hh, avail = pixels.Length / 8;
                for (int i = 0; i < count && i < avail; i++)
                {
                    int s = i * 8, d = i * 4;
                    o[d] = Tone(Half(pixels, s)); o[d + 1] = Tone(Half(pixels, s + 2));
                    o[d + 2] = Tone(Half(pixels, s + 4)); o[d + 3] = (byte)Math.Clamp(Half(pixels, s + 6) * 255f, 0, 255);
                }
                return o;
            }
            case 34:                               // R16G16_FLOAT -> RG, B=0
            {
                var o = new byte[w * hh * 4];
                int count = w * hh, avail = pixels.Length / 4;
                for (int i = 0; i < count && i < avail; i++)
                {
                    int s = i * 4, d = i * 4;
                    o[d] = Tone(Half(pixels, s)); o[d + 1] = Tone(Half(pixels, s + 2)); o[d + 2] = 0; o[d + 3] = 255;
                }
                return o;
            }
        }

        CompressionFormat? cf = BcFormat(f);
        if (cf == null) return null;

        int blocks = BlocksX(w) * BlocksY(hh) * BlockBytes(f);
        if (pixels.Length < blocks) return null;
        byte[] mip0 = pixels.Length == blocks ? pixels : pixels.AsSpan(0, blocks).ToArray();

        var dec = new BcDecoder();
        if (cf == CompressionFormat.Bc6U || cf == CompressionFormat.Bc6S)
        {
            ColorRgbFloat[] hdr = dec.DecodeRawHdr(mip0, w, hh, cf.Value);
            var o = new byte[w * hh * 4];
            for (int i = 0; i < hdr.Length && i < w * hh; i++)
            { o[i * 4] = Tone(hdr[i].r); o[i * 4 + 1] = Tone(hdr[i].g); o[i * 4 + 2] = Tone(hdr[i].b); o[i * 4 + 3] = 255; }
            return o;
        }
        ColorRgba32[] px = dec.DecodeRaw(mip0, w, hh, cf.Value);
        var buf = new byte[w * hh * 4];
        bool bc4 = f is 80 or 81;                  // single channel -> show as grayscale
        for (int i = 0; i < px.Length && i < w * hh; i++)
        {
            byte r = px[i].r;
            if (bc4) { buf[i * 4] = buf[i * 4 + 1] = buf[i * 4 + 2] = r; buf[i * 4 + 3] = 255; }
            else { buf[i * 4] = r; buf[i * 4 + 1] = px[i].g; buf[i * 4 + 2] = px[i].b; buf[i * 4 + 3] = px[i].a; }
        }
        return buf;
    }

    static float Half(byte[] b, int off) =>
        (float)BitConverter.UInt16BitsToHalf((ushort)(b[off] | (b[off + 1] << 8)));

    static byte[] CopyRaw(byte[] src, int need)
    {
        var o = new byte[need];
        Array.Copy(src, o, Math.Min(need, src.Length));
        return o;
    }

    static byte Tone(float v) { v = v / (1f + v); return (byte)Math.Clamp(MathF.Pow(Math.Max(0, v), 1f / 2.2f) * 255f, 0, 255); }

    // ---- DDS (DX10) for raw export ----

    public static byte[] BuildDds(TexHeader h, byte[] pixels)
    {
        ushort f = h.DxgiFormat;
        int w = h.Width, hh = h.Height;
        bool bc = f >= 70 && f <= 99;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0x20534444u);                          // 'DDS '
        bw.Write(124u);
        uint flags = 0x1 | 0x2 | 0x4 | 0x1000;          // CAPS|HEIGHT|WIDTH|PIXELFORMAT
        flags |= bc ? 0x80000u : 0x8u;                  // LINEARSIZE : PITCH
        bw.Write(flags);
        bw.Write((uint)hh); bw.Write((uint)w);
        bw.Write((uint)(bc ? BlocksX(w) * BlocksY(hh) * BlockBytes(f) : w * 4));
        bw.Write(0u);                                   // depth
        bw.Write(1u);                                   // mipcount (we wrap the whole buffer; viewers read mip0)
        for (int i = 0; i < 11; i++) bw.Write(0u);
        bw.Write(32u);                                  // pixelformat size
        bw.Write(0x4u);                                 // DDPF_FOURCC
        bw.Write(0x30315844u);                          // 'DX10'
        bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u);
        bw.Write(0x1000u);                              // caps TEXTURE
        bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u);
        bw.Write((uint)f);                              // dxgiFormat
        bw.Write(3u);                                   // TEXTURE2D
        bw.Write(h.IsCubemap ? 0x4u : 0u);              // miscFlag
        bw.Write((uint)Math.Max(1, h.IsCubemap ? h.ArraySize / 6 : 1));
        bw.Write(0u);
        bw.Write(pixels);
        return ms.ToArray();
    }

    // ---- format tables ----

    static int BlocksX(int w) => Math.Max(1, (w + 3) / 4);
    static int BlocksY(int h) => Math.Max(1, (h + 3) / 4);
    static int BlockBytes(ushort f) => f is 71 or 72 or 80 or 81 ? 8 : 16;   // BC1/BC4 = 8, rest = 16

    static CompressionFormat? BcFormat(ushort f) => f switch
    {
        71 or 72 => CompressionFormat.Bc1,
        74 or 75 => CompressionFormat.Bc2,
        77 or 78 => CompressionFormat.Bc3,
        80 or 81 => CompressionFormat.Bc4,
        83 or 84 => CompressionFormat.Bc5,
        95 => CompressionFormat.Bc6U,
        96 => CompressionFormat.Bc6S,
        98 or 99 => CompressionFormat.Bc7,
        _ => null,
    };

    public static bool IsSupported(ushort f) => Formats.ContainsKey(f) &&
        (f is 28 or 29 or 87 or 88 or 61 or 49 or 10 or 34 || BcFormat(f) != null);

    public static string FormatName(ushort f) => Formats.GetValueOrDefault(f, $"DXGI_{f}");

    /// <summary>sRGB formats are colour/albedo maps (vs linear normal/data maps).</summary>
    public static bool IsSrgb(ushort f) => f is 29 or 72 or 75 or 78 or 88 or 99;

    public static readonly Dictionary<ushort, string> Formats = new()
    {
        [2] = "R32G32B32A32_FLOAT", [10] = "R16G16B16A16_FLOAT",
        [28] = "R8G8B8A8_UNORM", [29] = "R8G8B8A8_SRGB",
        [34] = "R16G16_FLOAT", [49] = "R8G8_UNORM", [61] = "R8_UNORM",
        [87] = "B8G8R8A8_UNORM", [88] = "B8G8R8A8_SRGB",
        [71] = "BC1_UNORM", [72] = "BC1_SRGB",
        [74] = "BC2_UNORM", [75] = "BC2_SRGB",
        [77] = "BC3_UNORM", [78] = "BC3_SRGB",
        [80] = "BC4_UNORM", [81] = "BC4_SNORM",
        [83] = "BC5_UNORM", [84] = "BC5_SNORM",
        [95] = "BC6H_UF16", [96] = "BC6H_SF16",
        [98] = "BC7_UNORM", [99] = "BC7_SRGB",
    };
}
