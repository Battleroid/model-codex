using System.Buffers.Binary;

namespace Tiger.Model;

/// <summary>
/// A tiny little-endian cursor over a tag's bytes. The codebase otherwise calls
/// <see cref="BinaryPrimitives"/> at fixed offsets; model parsing reads many sequential
/// fields, so a cursor keeps that readable. Bounds-checked; reads past the end throw.
/// </summary>
public struct TagReader
{
    private readonly byte[] _data;
    public int Position;

    public TagReader(byte[] data, int position = 0) { _data = data; Position = position; }

    public readonly int Length => _data.Length;
    public readonly ReadOnlySpan<byte> Span => _data;
    public readonly bool CanRead(int bytes) => Position + bytes <= _data.Length;

    public void Seek(int position) => Position = position;
    public void Skip(int bytes) => Position += bytes;

    public byte ReadU8() { byte v = _data[Position]; Position += 1; return v; }
    public sbyte ReadS8() { sbyte v = (sbyte)_data[Position]; Position += 1; return v; }

    public ushort ReadU16() { ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Position)); Position += 2; return v; }
    public short ReadS16() { short v = BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(Position)); Position += 2; return v; }
    public uint ReadU32() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Position)); Position += 4; return v; }
    public int ReadS32() { int v = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(Position)); Position += 4; return v; }
    public ulong ReadU64() { ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(Position)); Position += 8; return v; }
    public float ReadF32() { float v = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(Position)); Position += 4; return v; }

    /// <summary>Read a 16-bit signed normalized value in [-1, 1].</summary>
    public float ReadSNorm16() => Math.Max(ReadS16() / 32767f, -1f);

    // Absolute-offset peeks (do not advance the cursor).
    public readonly ushort PeekU16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(at));
    public readonly uint PeekU32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(at));
    public readonly ulong PeekU64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(at));
}
