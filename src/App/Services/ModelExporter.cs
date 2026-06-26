using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Tiger;
using Tiger.Model;
using AiContext = Assimp.AssimpContext;
using AiFace = Assimp.Face;
using AiMaterial = Assimp.Material;
using AiMesh = Assimp.Mesh;
using AiNode = Assimp.Node;
using AiScene = Assimp.Scene;
using AiPrimitiveType = Assimp.PrimitiveType;
using AiVector3D = Assimp.Vector3D;
using AiTextureSlot = Assimp.TextureSlot;

namespace ModelCodex.App.Services;

/// <summary>
/// Exports a decoded model to glTF (.glb), OBJ+MTL, STL, or FBX, with each part's resolved albedo
/// texture. glTF embeds textures (SharpGLTF); OBJ/FBX reference sibling PNGs; STL is geometry-only.
/// </summary>
public static class ModelExporter
{
    private sealed class PartData
    {
        public List<Vector3> Pos = new();
        public List<Vector3> Nrm = new();
        public List<Vector2> Uv = new();
        public List<int> Idx = new();
        public byte[]? AlbedoPng;
        public uint AlbedoHash;
    }

    /// <summary>Export a model; returns the written file path.</summary>
    public static string Export(PackageManager mgr, ModelGeometry geom, string baseName, string outDir,
        string format, bool textures)
    {
        Directory.CreateDirectory(outDir);
        var parts = Extract(mgr, geom, textures);
        return format.ToLowerInvariant() switch
        {
            "obj" => WriteObj(parts, baseName, outDir),
            "stl" => WriteStl(parts, baseName, outDir),
            "fbx" => WriteFbx(parts, baseName, outDir),
            _ => WriteGlb(parts, baseName, outDir),
        };
    }

    private static List<PartData> Extract(PackageManager mgr, ModelGeometry geom, bool textures)
    {
        var pngCache = new Dictionary<uint, byte[]?>();
        var list = new List<PartData>();
        foreach (var part in geom.Parts)
        {
            var pd = new PartData { Pos = part.Positions, Nrm = SmoothNormals(part.Positions, part.Indices), Uv = part.Texcoords, Idx = part.Indices };
            if (textures && part.MaterialHash != 0 && MaterialMap.Albedo(mgr, part.MaterialHash) is uint th)
            {
                pd.AlbedoHash = th;
                if (!pngCache.TryGetValue(th, out var png))
                {
                    png = null;
                    try { if (mgr.ByTag.TryGetValue(th, out var te) && mgr.Decode(te) is { } d) png = EncodePng(d.rgba, d.width, d.height); }
                    catch { }
                    pngCache[th] = png;
                }
                pd.AlbedoPng = png;
            }
            list.Add(pd);
        }
        return list;
    }

    // ---- glTF (.glb), textures embedded ----
    private static string WriteGlb(List<PartData> parts, string baseName, string outDir)
    {
        var scene = new SceneBuilder();
        var matCache = new Dictionary<uint, MaterialBuilder>();
        int i = 0;
        foreach (var p in parts)
        {
            var mat = MaterialFor(p, matCache);
            var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1>($"part_{i++}");
            var prim = mesh.UsePrimitive(mat);
            for (int t = 0; t + 2 < p.Idx.Count; t += 3)
            {
                var a = Vtx(p, p.Idx[t]); var b = Vtx(p, p.Idx[t + 1]); var c = Vtx(p, p.Idx[t + 2]);
                prim.AddTriangle(a, b, c);
            }
            scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        }
        var model = scene.ToGltf2();
        string path = Path.Combine(outDir, baseName + ".glb");
        model.SaveGLB(path);
        return path;
    }

    private static MaterialBuilder MaterialFor(PartData p, Dictionary<uint, MaterialBuilder> cache)
    {
        if (p.AlbedoPng == null) return new MaterialBuilder("default").WithDoubleSide(true).WithMetallicRoughnessShader();
        if (cache.TryGetValue(p.AlbedoHash, out var m)) return m;
        m = new MaterialBuilder($"mat_{p.AlbedoHash:X8}")
            .WithDoubleSide(true)
            .WithMetallicRoughnessShader()
            .WithBaseColor(ImageBuilder.From(new MemoryImage(p.AlbedoPng)));
        cache[p.AlbedoHash] = m;
        return m;
    }

    private static (VertexPositionNormal, VertexTexture1) Vtx(PartData p, int i)
    {
        var pos = p.Pos[i];
        var nrm = i < p.Nrm.Count ? p.Nrm[i] : Vector3.UnitZ;
        var uv = i < p.Uv.Count ? p.Uv[i] : Vector2.Zero;
        return (new VertexPositionNormal(pos, nrm), new VertexTexture1(uv));
    }

    /// <summary>Recompute per-vertex smooth normals (decoded Tiger normals are quaternion-packed and unreliable).</summary>
    private static List<Vector3> SmoothNormals(List<Vector3> pos, List<int> idx)
    {
        var n = new Vector3[pos.Count];
        for (int t = 0; t + 2 < idx.Count; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            var fn = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
            n[a] += fn; n[b] += fn; n[c] += fn;
        }
        var list = new List<Vector3>(pos.Count);
        foreach (var v in n)
            list.Add(v.LengthSquared() > 1e-12f && float.IsFinite(v.X) ? Vector3.Normalize(v) : Vector3.UnitZ);
        return list;
    }

    // ---- OBJ + MTL + sibling PNGs ----
    private static string WriteObj(List<PartData> parts, string baseName, string outDir)
    {
        var ci = CultureInfo.InvariantCulture;
        var obj = new StringBuilder($"# model-codex\nmtllib {baseName}.mtl\n");
        var mtl = new StringBuilder();
        var written = new HashSet<uint>();
        int vbase = 1;
        for (int pi = 0; pi < parts.Count; pi++)
        {
            var p = parts[pi];
            string matName = p.AlbedoHash != 0 ? $"mat_{p.AlbedoHash:X8}" : $"mat_{pi}";
            if (written.Add(p.AlbedoHash == 0 ? (uint)(0x1000000 + pi) : p.AlbedoHash))
            {
                mtl.Append($"newmtl {matName}\nKd 0.8 0.8 0.8\n");
                if (p.AlbedoPng != null)
                {
                    string tex = $"{p.AlbedoHash:X8}.png";
                    File.WriteAllBytes(Path.Combine(outDir, tex), p.AlbedoPng);
                    mtl.Append($"map_Kd {tex}\n");
                }
                mtl.Append('\n');
            }
            obj.Append($"o part_{pi}\nusemtl {matName}\n");
            foreach (var v in p.Pos) obj.Append($"v {v.X.ToString(ci)} {v.Y.ToString(ci)} {v.Z.ToString(ci)}\n");
            foreach (var t in p.Uv) obj.Append($"vt {t.X.ToString(ci)} {t.Y.ToString(ci)}\n");
            foreach (var n in p.Nrm) obj.Append($"vn {n.X.ToString(ci)} {n.Y.ToString(ci)} {n.Z.ToString(ci)}\n");
            for (int t = 0; t + 2 < p.Idx.Count; t += 3)
            {
                int a = vbase + p.Idx[t], b = vbase + p.Idx[t + 1], c = vbase + p.Idx[t + 2];
                obj.Append($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}\n");
            }
            vbase += p.Pos.Count;
        }
        string path = Path.Combine(outDir, baseName + ".obj");
        File.WriteAllText(path, obj.ToString());
        File.WriteAllText(Path.Combine(outDir, baseName + ".mtl"), mtl.ToString());
        return path;
    }

    // ---- Binary STL (geometry only) ----
    private static string WriteStl(List<PartData> parts, string baseName, string outDir)
    {
        string path = Path.Combine(outDir, baseName + ".stl");
        using var fs = new FileStream(path, FileMode.Create);
        using var bw = new BinaryWriter(fs);
        bw.Write(new byte[80]);
        int triCount = parts.Sum(p => p.Idx.Count / 3);
        bw.Write((uint)triCount);
        foreach (var p in parts)
            for (int t = 0; t + 2 < p.Idx.Count; t += 3)
            {
                var a = p.Pos[p.Idx[t]]; var b = p.Pos[p.Idx[t + 1]]; var c = p.Pos[p.Idx[t + 2]];
                var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                if (!float.IsFinite(n.X)) n = Vector3.UnitZ;
                WriteVec(bw, n); WriteVec(bw, a); WriteVec(bw, b); WriteVec(bw, c);
                bw.Write((ushort)0);
            }
        return path;
    }

    private static void WriteVec(BinaryWriter bw, Vector3 v) { bw.Write(v.X); bw.Write(v.Y); bw.Write(v.Z); }

    // ---- FBX via Assimp; textures written as sibling PNGs ----
    private static string WriteFbx(List<PartData> parts, string baseName, string outDir)
    {
        var scene = new AiScene { RootNode = new AiNode("root") };
        var written = new HashSet<uint>();
        for (int pi = 0; pi < parts.Count; pi++)
        {
            var p = parts[pi];
            var mesh = new AiMesh($"part_{pi}", AiPrimitiveType.Triangle) { MaterialIndex = scene.MaterialCount };
            foreach (var v in p.Pos) mesh.Vertices.Add(new AiVector3D(v.X, v.Y, v.Z));
            foreach (var n in p.Nrm) mesh.Normals.Add(new AiVector3D(n.X, n.Y, n.Z));
            foreach (var t in p.Uv) mesh.TextureCoordinateChannels[0].Add(new AiVector3D(t.X, t.Y, 0));
            mesh.UVComponentCount[0] = 2;
            for (int t = 0; t + 2 < p.Idx.Count; t += 3)
                mesh.Faces.Add(new AiFace(new[] { p.Idx[t], p.Idx[t + 1], p.Idx[t + 2] }));

            var mat = new AiMaterial { Name = $"mat_{pi}" };
            if (p.AlbedoPng != null)
            {
                string tex = $"{p.AlbedoHash:X8}.png";
                if (written.Add(p.AlbedoHash)) File.WriteAllBytes(Path.Combine(outDir, tex), p.AlbedoPng);
                mat.TextureDiffuse = new AiTextureSlot(tex, Assimp.TextureType.Diffuse, 0,
                    Assimp.TextureMapping.FromUV, 0, 1f, Assimp.TextureOperation.Multiply,
                    Assimp.TextureWrapMode.Wrap, Assimp.TextureWrapMode.Wrap, 0);
            }
            scene.Materials.Add(mat);
            scene.Meshes.Add(mesh);
            var node = new AiNode($"part_{pi}");
            node.MeshIndices.Add(scene.MeshCount - 1);
            scene.RootNode.Children.Add(node);
        }
        string path = Path.Combine(outDir, baseName + ".fbx");
        using var ctx = new AiContext();
        ctx.ExportFile(scene, path, "fbx");
        return path;
    }

    private static byte[] EncodePng(byte[] rgba, int w, int h)
    {
        using var img = Image.LoadPixelData<Rgba32>(rgba, w, h);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }
}
