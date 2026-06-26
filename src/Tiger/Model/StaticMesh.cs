using System.Buffers.Binary;
using System.Numerics;

namespace Tiger.Model;

/// <summary>Which LOD/detail parts to include. Highest detail = DetailLevel 0 or odd (Tiger convention).</summary>
public enum ModelDetail { MostDetailed, LeastDetailed, All }

/// <summary>
/// Clean-room parser for Marathon static models. Walks
/// SStaticMesh (class 0x80808635) -> SStaticMeshData (0x80808620) -> parts/buffers -> raw
/// vertex/index data, producing a <see cref="ModelGeometry"/>. Offsets were confirmed against retail
/// bytes via the Probe harness (see Probe `model`/`mesh`); MIDA was used only as format documentation.
/// </summary>
public static class StaticMesh
{
    public const uint ClassStaticMesh = 0x80808635;
    public const uint ClassStaticMeshData = 0x80808620;

    // SStaticMesh field offsets.
    private const int SM_StaticData = 0x08;   // u32 ref -> SStaticMeshData
    private const int SM_Materials = 0x10;    // DynamicArray<u32 material hash>

    // SStaticMeshData field offsets.
    private const int SD_MaterialAssignments = 0x08; // DynamicArray, 6-byte elements
    private const int SD_Parts = 0x18;               // DynamicArray, 0xC-byte elements
    private const int SD_Meshes = 0x28;              // DynamicArray, 0x10-byte elements (buffer groups)
    private const int SD_ModelTransform = 0x40;      // Vector4 (xyz=offset, w=scale)
    private const int SD_TexcoordScale = 0x50;       // float
    private const int SD_TexcoordTranslation = 0x54; // Vector2

    /// <summary>DynamicArray: count = u64@F, relOffset = u64@F+8, data starts at F + 0x18 + relOffset.</summary>
    private static (long count, int dataOffset) DynArray(byte[] d, int f)
    {
        long count = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(f));
        long rel = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(f + 8));
        return (count, f + 0x18 + (int)rel);
    }

    private static bool IsHighestDetail(byte lod) => lod == 0 || (lod & 1) == 1;

    public static ModelGeometry? Parse(PackageManager mgr, uint staticMeshHash, ModelDetail detail = ModelDetail.MostDetailed)
    {
        byte[]? sm = mgr.ReadTag(staticMeshHash);
        if (sm == null || sm.Length < 0x20) return null;

        uint staticDataHash = BinaryPrimitives.ReadUInt32LittleEndian(sm.AsSpan(SM_StaticData));
        // Materials array (each element a 4-byte material taghash).
        var (matCount, matOff) = DynArray(sm, SM_Materials);
        var materialHashes = new List<uint>();
        for (int i = 0; i < matCount && matOff + i * 4 + 4 <= sm.Length; i++)
            materialHashes.Add(BinaryPrimitives.ReadUInt32LittleEndian(sm.AsSpan(matOff + i * 4)));

        byte[]? sd = mgr.ReadTag(staticDataHash);
        if (sd == null || sd.Length < 0x60) return null;

        var (maCount, maOff) = DynArray(sd, SD_MaterialAssignments);
        var (partCount, partOff) = DynArray(sd, SD_Parts);
        var (meshCount, meshOff) = DynArray(sd, SD_Meshes);
        if (meshCount < 1) return new ModelGeometry();

        // Transform: position = raw * scale + offset; scale = ModelTransform.W, offset = ModelTransform.XYZ.
        Vector4 mt = ReadVec4(sd, SD_ModelTransform);
        float scale = mt.W;
        var offset = new Vector3(mt.X, mt.Y, mt.Z);
        float texScale = BinaryPrimitives.ReadSingleLittleEndian(sd.AsSpan(SD_TexcoordScale));
        var texTrans = new Vector2(
            BinaryPrimitives.ReadSingleLittleEndian(sd.AsSpan(SD_TexcoordTranslation)),
            BinaryPrimitives.ReadSingleLittleEndian(sd.AsSpan(SD_TexcoordTranslation + 4)));

        // Buffer group 0 (the only one Marathon static meshes use).
        int b = meshOff;
        uint indexHash = BinaryPrimitives.ReadUInt32LittleEndian(sd.AsSpan(b + 0x00));
        uint v0Hash = BinaryPrimitives.ReadUInt32LittleEndian(sd.AsSpan(b + 0x04));
        uint v1Hash = BinaryPrimitives.ReadUInt32LittleEndian(sd.AsSpan(b + 0x08));

        IndexBuffer? ib = IndexBuffer.Load(mgr, indexHash);
        VertexBuffer? vb0 = v0Hash != 0 ? VertexBuffer.Load(mgr, v0Hash) : null;
        VertexBuffer? vb1 = v1Hash != 0 ? VertexBuffer.Load(mgr, v1Hash) : null;
        if (ib == null || vb0 == null) return null;

        // Pick which buffer supplies positions vs texcoords.
        VertexBuffer? posBuf = vb0.ProvidesPosition ? vb0 : (vb1?.ProvidesPosition == true ? vb1 : vb0);
        VertexBuffer? uvBuf = vb0.ProvidesTexcoord ? vb0 : (vb1?.ProvidesTexcoord == true ? vb1 : null);

        var geom = new ModelGeometry();

        // Each material assignment -> a part drawn with one material.
        for (int i = 0; i < maCount; i++)
        {
            int ma = maOff + i * 6;
            if (ma + 6 > sd.Length) break;
            ushort partIndex = BinaryPrimitives.ReadUInt16LittleEndian(sd.AsSpan(ma));
            if (partIndex >= partCount) continue;

            int pe = partOff + partIndex * 0x0C;
            if (pe + 0x0C > sd.Length) continue;
            uint indexOffset = BinaryPrimitives.ReadUInt32LittleEndian(sd.AsSpan(pe + 0x00));
            uint indexCount = BinaryPrimitives.ReadUInt32LittleEndian(sd.AsSpan(pe + 0x04));
            byte lod = sd[pe + 0x0A];
            var prim = (PrimitiveType)(sbyte)sd[pe + 0x0B];
            if (indexCount == 0) continue;

            // LOD filter — by default keep only the highest-detail parts (avoids overlapping LODs).
            bool highest = IsHighestDetail(lod);
            if (detail == ModelDetail.MostDetailed && !highest) continue;
            if (detail == ModelDetail.LeastDetailed && highest) continue;

            var tris = ib.ReadTriangles(indexOffset, indexCount, prim);
            if (tris.Count == 0) continue;

            var part = new ModelPart
            {
                MaterialIndex = i,
                MaterialHash = i < materialHashes.Count ? materialHashes[i] : 0,
                DetailLevel = lod,
            };

            // Remap the part's global vertex indices to a compact local range.
            var local = new Dictionary<uint, int>();
            int Local(uint gv)
            {
                if (local.TryGetValue(gv, out int li)) return li;
                li = part.Positions.Count;
                local[gv] = li;

                Vector3 p = Vector3.Zero; Vector2 uv = Vector2.Zero; Vector3 nrm = Vector3.UnitZ;
                if (posBuf != null && posBuf.Decode((int)gv, out var pp, out var pn, out _))
                {
                    if (pp is Vector3 pv) p = pv * scale + offset;
                    if (pn is Vector3 nv) nrm = nv;
                }
                if (uvBuf != null && uvBuf.Decode((int)gv, out _, out _, out var uu) && uu is Vector2 uvv)
                    uv = new Vector2(uvv.X * texScale + texTrans.X, 1f - (uvv.Y * texScale + texTrans.Y));

                part.Positions.Add(p);
                part.Normals.Add(nrm);
                part.Texcoords.Add(uv);
                return li;
            }

            foreach (var (a, bb, c) in tris)
            {
                part.Indices.Add(Local(a));
                part.Indices.Add(Local(bb));
                part.Indices.Add(Local(c));
            }
            if (part.Positions.Count > 0) geom.Parts.Add(part);
        }

        return geom;
    }

    private static Vector4 ReadVec4(byte[] d, int o) => new(
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o)),
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o + 4)),
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o + 8)),
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o + 12)));
}
