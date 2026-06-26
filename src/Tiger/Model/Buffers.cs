using System.Buffers.Binary;
using System.Numerics;

namespace Tiger.Model;

/// <summary>
/// An index buffer: a small header tag (type 32/6) carrying {Is32Bit, DataSize}, whose
/// entry-table Reference points at the raw index data tag (type 40/6).
/// </summary>
public sealed class IndexBuffer
{
    public bool Is32Bit { get; }
    public long DataSize { get; }
    private readonly byte[] _data;

    private IndexBuffer(bool is32, long size, byte[] data) { Is32Bit = is32; DataSize = size; _data = data; }

    public static IndexBuffer? Load(PackageManager mgr, uint headerHash)
    {
        Entry? he = mgr.EntryOf(headerHash);
        byte[]? hb = mgr.ReadTag(headerHash);
        if (he == null || hb == null || hb.Length < 0x10) return null;
        bool is32 = hb[0x01] != 0;
        long size = BinaryPrimitives.ReadInt64LittleEndian(hb.AsSpan(0x08));
        byte[]? data = mgr.ReadTag(he.Reference);
        if (data == null) return null;
        return new IndexBuffer(is32, size, data);
    }

    /// <summary>Read <paramref name="count"/> indices starting at <paramref name="offset"/>, as a triangle list.</summary>
    public List<(uint a, uint b, uint c)> ReadTriangles(uint offset, uint count, PrimitiveType prim)
    {
        return prim == PrimitiveType.TriangleStrip ? ReadStrip(offset, count) : ReadList(offset, count);
    }

    private uint Idx(int i) => Is32Bit
        ? BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(i * 4))
        : BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(i * 2));

    private int Capacity => Is32Bit ? _data.Length / 4 : _data.Length / 2;

    private List<(uint, uint, uint)> ReadList(uint offset, uint count)
    {
        var tris = new List<(uint, uint, uint)>();
        for (uint i = 0; i + 3 <= count; i += 3)
        {
            int b = (int)(offset + i);
            if (b + 2 >= Capacity) break;
            tris.Add((Idx(b), Idx(b + 1), Idx(b + 2)));
        }
        return tris;
    }

    // Triangle strip with Bungie degenerate-restart sentinel (0xFFFF / 0xFFFFFFFF) and alternating winding.
    private List<(uint, uint, uint)> ReadStrip(uint offset, uint count)
    {
        uint restart = Is32Bit ? 0xFFFFFFFFu : 0xFFFFu;
        var tris = new List<(uint, uint, uint)>();
        int triInStrip = 0;
        int end = (int)Math.Min(offset + count, (uint)Capacity);
        for (int p = (int)offset; p + 2 < end; p++)
        {
            uint i1 = Idx(p), i2 = Idx(p + 1), i3 = Idx(p + 2);
            if (i3 == restart || i2 == restart || i1 == restart) { triInStrip = 0; continue; }
            if (i1 != i2 && i2 != i3 && i1 != i3)
                tris.Add((triInStrip & 1) == 0 ? (i1, i2, i3) : (i2, i1, i3));
            triInStrip++;
        }
        return tris;
    }
}

/// <summary>
/// A vertex buffer: a 12-byte header tag (type 32/4) = SVertexHeader {DataSize, Stride, Type, 0xDEADBEEF},
/// whose entry-table Reference points at the raw vertex data tag (type 40/4). Decoded by (Type, Stride).
/// </summary>
public sealed class VertexBuffer
{
    public uint DataSize { get; }
    public short Stride { get; }
    public short Type { get; }
    private readonly byte[] _data;

    private VertexBuffer(uint dataSize, short stride, short type, byte[] data)
    { DataSize = dataSize; Stride = stride; Type = type; _data = data; }

    public static VertexBuffer? Load(PackageManager mgr, uint headerHash)
    {
        Entry? he = mgr.EntryOf(headerHash);
        byte[]? hb = mgr.ReadTag(headerHash);
        if (he == null || hb == null || hb.Length < 0x0C) return null;
        uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(hb.AsSpan(0x00));
        short stride = BinaryPrimitives.ReadInt16LittleEndian(hb.AsSpan(0x04));
        short type = BinaryPrimitives.ReadInt16LittleEndian(hb.AsSpan(0x06));
        byte[]? data = mgr.ReadTag(he.Reference);
        if (data == null || stride <= 0) return null;
        return new VertexBuffer(dataSize, stride, type, data);
    }

    public int VertexCount => Stride > 0 ? _data.Length / Stride : 0;

    private float SNorm(int at) => Math.Max(BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(at)) / 32767f, -1f);

    /// <summary>
    /// Decode the fields this buffer contributes for vertex <paramref name="index"/>. Positions and
    /// texcoords are in normalized space (caller applies the model scale/offset and texcoord transform).
    /// Returns false for an unsupported (Type, Stride) so the caller can flag it.
    /// </summary>
    public bool Decode(int index, out Vector3? pos, out Vector3? normal, out Vector2? uv)
    {
        pos = null; normal = null; uv = null;
        int o = index * Stride;
        if (o < 0 || o + Stride > _data.Length) return true; // out of range -> contribute nothing

        if (Type == 0 || Type == 1)
        {
            switch (Stride)
            {
                case 0x04: // texcoord (2x i16)
                    uv = new Vector2(SNorm(o), SNorm(o + 2));
                    return true;
                case 0x0C: // normal (4x i16) + texcoord (2x i16)
                    normal = new Vector3(SNorm(o), SNorm(o + 2), SNorm(o + 4));
                    uv = new Vector2(SNorm(o + 8), SNorm(o + 10));
                    return true;
                case 0x10: // position (4x i16) + quaternion normal (4x i16)
                    pos = new Vector3(SNorm(o), SNorm(o + 2), SNorm(o + 4));
                    normal = new Vector3(SNorm(o + 8), SNorm(o + 10), SNorm(o + 12));
                    return true;
                case 0x18: // position + normal + tangent (all 4x i16)
                    pos = new Vector3(SNorm(o), SNorm(o + 2), SNorm(o + 4));
                    normal = new Vector3(SNorm(o + 8), SNorm(o + 10), SNorm(o + 12));
                    return true;
            }
        }
        return false; // unsupported layout
    }

    public bool ProvidesPosition => (Type is 0 or 1) && Stride is 0x10 or 0x18;
    public bool ProvidesTexcoord => (Type is 0 or 1) && Stride is 0x04 or 0x0C;
}

/// <summary>Bungie primitive topology values found in static-mesh parts.</summary>
public enum PrimitiveType : sbyte
{
    Triangles = 3,
    TriangleStrip = 5,
}
