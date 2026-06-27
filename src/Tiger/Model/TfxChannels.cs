using System.Buffers.Binary;

namespace Tiger.Model;

/// <summary>Interprets a material's pixel-shader TFX bytecode to discover which constant-buffer slots are
/// written ("PopOutput") from an object channel — i.e. which cbuffer Vec4s carry a *named* channel value
/// rather than a static literal. Lets the inspector label those editable slots with their channel name.
///
/// Marathon's TFX opcode table is the documented Tiger set shifted by a fixed +0x0E (verified empirically:
/// with this shift every one of the ~103k material bytecodes walks op+operand exactly to its end; no other
/// shift does). The bytecode is a little stack machine — PushObjectChannelVector pushes a named channel,
/// arithmetic ops combine the stack, and PopOutput &lt;slot&gt; stores the top expression into cbuffer slot N
/// (clearing the stack). We track, per stack entry, which channel hashes contributed, so a PopOutput tells
/// us "slot N is driven by channel H".</summary>
public static class TfxChannels
{
    private const int Shift = 0x0E;

    // Operand byte-count per *documented* opcode value (everything else = 0 operand bytes).
    private static readonly Dictionary<byte, int> DocWidth = Build();
    private static Dictionary<byte, int> Build()
    {
        var w = new Dictionary<byte, int>();
        foreach (byte b in new byte[] { 0x22,0x34,0x35,0x36,0x37,0x38,0x39,0x3a,0x3b,0x43,0x44,0x45,0x46,0x47,0x48,0x49,0x4a,0x4b,0x4c,0x4d,0x4f,0x50 }) w[b] = 1;
        foreach (byte b in new byte[] { 0x3c,0x3d,0x3e,0x3f,0x40,0x41,0x52,0x53,0x54 }) w[b] = 2;
        w[0x4e] = 4; // PushObjectChannelVector (u32 hash)
        return w;
    }

    // Stack effect (pop,push) per documented opcode; default (0,0).
    private static readonly Dictionary<byte, (int pop, int push)> DocEffect = BuildEffect();
    private static Dictionary<byte, (int, int)> BuildEffect()
    {
        var e = new Dictionary<byte, (int, int)>();
        foreach (byte b in new byte[] { 0x01,0x02,0x03,0x04,0x05,0x06,0x08,0x09,0x0a,0x0b,0x0c,0x0d,0x0e,0x0f }) e[b] = (2, 1);
        foreach (byte b in new byte[] { 0x07,0x15,0x16,0x17,0x18,0x19,0x1a,0x1d,0x1e,0x1f,0x20,0x21,0x22,0x23,0x27,0x28,0x29,0x2a,0x2b,0x35,0x36,0x37,0x38,0x39,0x3a,0x3b }) e[b] = (1, 1);
        foreach (byte b in new byte[] { 0x10,0x11,0x12,0x13 }) e[b] = (3, 1);
        e[0x14] = (2, 0); e[0x2c] = (1, 0); e[0x2d] = (4, 0); e[0x2e] = (5, 1);
        e[0x34] = (0, 1);                                   // PushConstantVec4
        foreach (byte b in new byte[] { 0x3c,0x3d,0x40 }) e[b] = (0, 1); // extern float/vec4/u32
        e[0x3e] = (0, 4);                                   // extern mat4 pushes 4
        e[0x42] = (0, 1); e[0x4c] = (0, 1); e[0x50] = (0, 1); // ops that push a constant vector
        e[0x49] = (1, 0); e[0x51] = (1, 0);
        foreach (byte b in new byte[] { 0x52,0x53,0x54 }) e[b] = (0, 1); // tex dims/tile params
        return e;
    }

    private static int WidthFor(byte b)
    {
        int dv = b - Shift;
        return dv is >= 0 and <= 255 ? DocWidth.GetValueOrDefault((byte)dv, 0) : 0;
    }

    /// <summary>Map of cbuffer Vec4 slot index -> the object-channel hash that drives it (FNV-1, resolve
    /// names via <see cref="ChannelNames"/>). Slots not present are static constants. Empty on any error.</summary>
    public static Dictionary<int, uint> SlotChannels(PackageManager mgr, uint materialHash)
    {
        var result = new Dictionary<int, uint>();
        byte[]? d = mgr.ReadTag(materialHash);
        int f = 0x278 + 0x20; // SMaterialShader.TFX_Bytecode DynamicArray<u8>
        if (d == null || f + 0x18 > d.Length) return result;
        long count = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(f));
        long rel = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(f + 8));
        int off = f + 0x18 + (int)rel;
        if (count is < 1 or > 0x100000 || off < 0 || off + (int)count > d.Length) return result;
        var bc = d.AsSpan(off, (int)count);

        byte objChan = (byte)(0x4e + Shift), pushFromOut = (byte)(0x43 + Shift), popOut = (byte)(0x44 + Shift),
             popOutMat4 = (byte)(0x45 + Shift), pushTemp = (byte)(0x46 + Shift), popTemp = (byte)(0x47 + Shift),
             globChan = (byte)(0x4f + Shift);

        var stack = new List<HashSet<uint>>();
        var temp = new Dictionary<int, HashSet<uint>>();
        var outProv = new Dictionary<int, HashSet<uint>>();

        HashSet<uint> Pop() { if (stack.Count == 0) return new(); var v = stack[^1]; stack.RemoveAt(stack.Count - 1); return v; }
        HashSet<uint> PopN(int n) { var u = new HashSet<uint>(); for (int k = 0; k < n; k++) u.UnionWith(Pop()); return u; }

        try
        {
            int i = 0;
            while (i < bc.Length)
            {
                byte b = bc[i];
                int wdt = WidthFor(b);
                if (b == objChan)
                {
                    uint hash = BinaryPrimitives.ReadUInt32BigEndian(bc.Slice(i + 1)); // channel hashes are big-endian
                    stack.Add(new HashSet<uint> { hash });
                }
                else if (b == globChan) stack.Add(new HashSet<uint>());
                else if (b == pushFromOut) stack.Add(new HashSet<uint>(outProv.GetValueOrDefault(bc[i + 1]) ?? new()));
                else if (b == pushTemp) stack.Add(new HashSet<uint>(temp.GetValueOrDefault(bc[i + 1]) ?? new()));
                else if (b == popTemp) temp[bc[i + 1]] = Pop();
                else if (b == popOut)
                {
                    int slot = bc[i + 1]; var prov = Pop(); outProv[slot] = prov;
                    if (prov.Count > 0 && !result.ContainsKey(slot)) result[slot] = prov.First();
                    stack.Clear();
                }
                else if (b == popOutMat4) { int slot = bc[i + 1]; var prov = PopN(4); for (int q = 0; q < 4; q++) outProv[slot + q] = prov; stack.Clear(); }
                else
                {
                    var (pp, ps) = DocEffect.GetValueOrDefault((byte)(b - Shift), (0, 0));
                    var u = PopN(pp);
                    for (int q = 0; q < ps; q++) stack.Add(new HashSet<uint>(u));
                }
                i += 1 + wdt;
            }
        }
        catch { /* best-effort: malformed bytecode just yields whatever we resolved so far */ }
        return result;
    }
}
