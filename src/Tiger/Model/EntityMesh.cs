using System.Buffers.Binary;
using System.Numerics;

namespace Tiger.Model;

/// <summary>
/// Clean-room parser for Marathon entity/dynamic models. Resolves an SEntity (class 0x8080BAAD) to its
/// SEntityModel geometry (class 0x8080881C) by walking the reference graph, then parses meshes/parts and
/// the shared vertex/index buffers. Offsets confirmed against retail bytes via Probe `entity`; MIDA used
/// only as format documentation.
/// </summary>
public static class EntityMesh
{
    public const uint ClassEntity = 0x8080BAAD;
    public const uint ClassEntityModel = 0x8080881C;

    // SEntityModel offsets.
    private const int EM_Meshes = 0x10;            // DynamicArray<SEntityModelMesh> (0x80-byte elements)
    private const int EM_ModelScale = 0xA0;        // Vector4 (xyz used)
    private const int EM_ModelTranslation = 0xB0;  // Vector4
    private const int EM_TexcoordScale = 0xC0;     // Vector2
    private const int EM_TexcoordTranslation = 0xC8;

    // SEntityModelMesh offsets (0x80 bytes).
    private const int MESH_Vertices1 = 0x00; // positions (+normal)
    private const int MESH_Vertices2 = 0x04; // texcoords (+normal)
    private const int MESH_Indices = 0x10;
    private const int MESH_Parts = 0x20;     // DynamicArray<SD1878080> (0x28-byte elements)

    private static (long count, int dataOffset) DynArray(byte[] d, int f)
    {
        long count = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(f));
        long rel = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(f + 8));
        return (count, f + 0x18 + (int)rel);
    }

    // Material classes seen on entity external materials (texture-codex set + entity-specific 0x8080BAF8).
    private static readonly HashSet<uint> MaterialClasses = new()
    { 0x808031D8, 0x8080BAF8, 0x80805350, 0x80808567, 0x8080B9BA, 0x80808490, 0x8080AE01, 0x8080BEF0 };

    /// <summary>Find the SEntityModel geometry tag + its model resource (BFS over references).</summary>
    public static (uint? model, uint resource) ResolveModel(PackageManager mgr, uint entityHash)
    {
        var visited = new HashSet<uint> { entityHash };
        var queue = new Queue<(uint hash, int depth)>();
        queue.Enqueue((entityHash, 0));
        while (queue.Count > 0)
        {
            var (h, depth) = queue.Dequeue();
            byte[]? d = mgr.ReadTag(h);
            if (d == null) continue;
            for (int o = 0; o + 4 <= d.Length; o += 4)
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
                if ((v & 0xFF000000u) != 0x80000000u) continue;
                Entry? e = mgr.EntryOf(v);
                if (e == null) continue;
                if (e.Reference == ClassEntityModel) return (v, h); // h = the model resource
                if (visited.Add(v) && depth < 5) queue.Enqueue((v, depth + 1));
            }
        }
        return (null, 0);
    }

    /// <summary>The entity's external material list (the longest run of material tags in the model resource).
    /// Parts whose direct material is empty index into this by their variant.</summary>
    private static List<uint> ExternalMaterials(PackageManager mgr, uint resourceHash)
    {
        var list = new List<uint>();
        byte[]? d = mgr.ReadTag(resourceHash);
        if (d == null) return list;
        int bestStart = -1, bestLen = 0, curStart = -1, curLen = 0;
        for (int o = 0; o + 4 <= d.Length; o += 4)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
            Entry? e = mgr.EntryOf(v);
            bool isMat = e != null && e.FileType == 8 && MaterialClasses.Contains(e.Reference);
            if (isMat) { if (curStart < 0) { curStart = o; curLen = 0; } curLen++; if (curLen > bestLen) { bestLen = curLen; bestStart = curStart; } }
            else { curStart = -1; curLen = 0; }
        }
        for (int k = 0; k < bestLen; k++)
            list.Add(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(bestStart + k * 4)));
        return list;
    }

    public static ModelGeometry? Parse(PackageManager mgr, uint entityHash, ModelDetail detail = ModelDetail.MostDetailed)
    {
        var (model, resource) = ResolveModel(mgr, entityHash);
        if (model is not uint m) return null;
        var ext = ExternalMaterials(mgr, resource);
        return ParseModel(mgr, m, ext, detail);
    }

    public static ModelGeometry? ParseModel(PackageManager mgr, uint modelHash, List<uint> externalMats, ModelDetail detail)
    {
        byte[]? d = mgr.ReadTag(modelHash);
        if (d == null || d.Length < 0xD0) return null;

        var (meshCount, meshOff) = DynArray(d, EM_Meshes);
        Vector3 scale = ReadVec3(d, EM_ModelScale);
        Vector3 trans = ReadVec3(d, EM_ModelTranslation);
        var texScale = new Vector2(ReadF32(d, EM_TexcoordScale), ReadF32(d, EM_TexcoordScale + 4));
        var texTrans = new Vector2(ReadF32(d, EM_TexcoordTranslation), ReadF32(d, EM_TexcoordTranslation + 4));

        var geom = new ModelGeometry();
        for (int mi = 0; mi < meshCount; mi++)
        {
            int mb = meshOff + mi * 0x80;
            if (mb + 0x80 > d.Length) break;

            uint v1 = ReadU32(d, mb + MESH_Vertices1);
            uint v2 = ReadU32(d, mb + MESH_Vertices2);
            uint ibHash = ReadU32(d, mb + MESH_Indices);
            var (partCount, partOff) = DynArray(d, mb + MESH_Parts);

            IndexBuffer? ib = IndexBuffer.Load(mgr, ibHash);
            VertexBuffer? vb1 = v1 != 0 ? VertexBuffer.Load(mgr, v1) : null;
            VertexBuffer? vb2 = v2 != 0 ? VertexBuffer.Load(mgr, v2) : null;
            if (ib == null || vb1 == null) continue;

            VertexBuffer? posBuf = vb1.ProvidesPosition ? vb1 : (vb2?.ProvidesPosition == true ? vb2 : vb1);
            VertexBuffer? uvBuf = vb1.ProvidesTexcoord ? vb1 : (vb2?.ProvidesTexcoord == true ? vb2 : null);

            for (int pi = 0; pi < partCount; pi++)
            {
                int pe = partOff + pi * 0x28;
                if (pe + 0x28 > d.Length) break;

                uint rawMat = ReadU32(d, pe + 0x00);
                short vsi = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(pe + 0x04));
                var prim = (PrimitiveType)(sbyte)d[pe + 0x06];
                uint indexOffset = ReadU32(d, pe + 0x08);
                uint indexCount = ReadU32(d, pe + 0x0C);
                byte lod = d[pe + 0x21];
                if (indexCount == 0) continue;

                // Direct material when present; else the external material selected by this part's variant.
                bool directValid = rawMat != 0 && rawMat != 0xFFFFFFFF && mgr.EntryOf(rawMat)?.FileType == 8;
                uint matHash = directValid ? rawMat
                    : externalMats.Count > 0 ? externalMats[Math.Clamp((int)vsi, 0, externalMats.Count - 1)] : 0;

                bool highest = lod == 0 || (lod & 1) == 1;
                if (detail == ModelDetail.MostDetailed && !highest) continue;
                if (detail == ModelDetail.LeastDetailed && highest) continue;

                var tris = ib.ReadTriangles(indexOffset, indexCount, prim);
                if (tris.Count == 0) continue;

                var part = new ModelPart { MaterialIndex = pi, MaterialHash = matHash, DetailLevel = lod };
                var local = new Dictionary<uint, int>();
                int Local(uint gv)
                {
                    if (local.TryGetValue(gv, out int li)) return li;
                    li = part.Positions.Count; local[gv] = li;
                    Vector3 p = Vector3.Zero; Vector3 nrm = Vector3.UnitZ; Vector2 uv = Vector2.Zero;
                    if (posBuf != null && posBuf.Decode((int)gv, out var pp, out var pn, out _))
                    {
                        if (pp is Vector3 pv) p = pv * scale + trans;
                        if (pn is Vector3 nv) nrm = nv;
                    }
                    if (uvBuf != null && uvBuf.Decode((int)gv, out _, out _, out var uu) && uu is Vector2 uvv)
                        uv = new Vector2(uvv.X * texScale.X + texTrans.X, 1f - (uvv.Y * texScale.Y + texTrans.Y));
                    part.Positions.Add(p); part.Normals.Add(nrm); part.Texcoords.Add(uv);
                    return li;
                }
                foreach (var (a, b, c) in tris)
                {
                    part.Indices.Add(Local(a)); part.Indices.Add(Local(b)); part.Indices.Add(Local(c));
                }
                if (part.Positions.Count > 0) geom.Parts.Add(part);
            }
        }
        return geom;
    }

    private static uint ReadU32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
    private static float ReadF32(byte[] d, int o) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o));
    private static Vector3 ReadVec3(byte[] d, int o) => new(ReadF32(d, o), ReadF32(d, o + 4), ReadF32(d, o + 8));
}
