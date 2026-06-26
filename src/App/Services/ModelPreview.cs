using System.IO;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.Wpf.SharpDX;
using SharpDX;
using Tiger;
using Tiger.Model;
using HxMaterial = HelixToolkit.Wpf.SharpDX.Material;

namespace ModelCodex.App.Services;

/// <summary>Off-thread parse + scene-build result, shared by the library preview and the model tab.</summary>
public sealed record PreviewData(
    ModelGeometry? Geometry,
    List<PartRender> Parts,
    List<MaterialChannel> Channels,
    string Info);

/// <summary>Shared model preview pipeline: parse geometry, build per-part textured meshes + channels.</summary>
public static class ModelPreview
{
    public static PreviewData Load(PackageManager mgr, ModelEntry entry, bool textured, uint? overrideAlbedo = null,
        ModelDetail detail = ModelDetail.MostDetailed)
    {
        var g = ModelParse.Parse(mgr, entry, detail);
        if (g == null || g.VertexCount == 0)
            return new PreviewData(null, new(), new(), "no geometry");

        var parts = ModelSceneBuilder.BuildParts(g, mgr, textured, overrideAlbedo);

        var channels = new List<MaterialChannel>();
        var seenMat = new HashSet<uint>();
        var seenTex = new HashSet<uint>();
        foreach (var part in g.Parts)
        {
            if (part.MaterialHash == 0 || !seenMat.Add(part.MaterialHash)) continue;
            foreach (var c in MaterialMap.Channels(mgr, part.MaterialHash))
                if (seenTex.Add(c.TexHash)) channels.Add(c);
        }

        var (mn, mx) = g.Bounds();
        var sz = mx - mn;
        string info = $"{g.Parts.Count} parts · {g.VertexCount:N0} verts · {g.TriangleCount:N0} tris · {sz.X:F1}×{sz.Y:F1}×{sz.Z:F1}";
        return new PreviewData(g, parts, channels, info);
    }

    public static HxMaterial MakeMaterial(byte[]? albedoPng)
    {
        // Lit material. AmbientColor lets ambient light contribute; tiny emissive avoids pure-black faces.
        var mat = new PhongMaterial
        {
            AmbientColor = new Color4(0.40f, 0.40f, 0.43f, 1f),
            EmissiveColor = new Color4(0.03f, 0.03f, 0.04f, 1f),
            SpecularColor = new Color4(0.10f, 0.10f, 0.11f, 1f),
            SpecularShininess = 12f,
        };
        if (albedoPng != null) { mat.DiffuseColor = Color.White; mat.DiffuseMap = new TextureModel(new MemoryStream(albedoPng)); }
        else mat.DiffuseColor = new Color4(0.78f, 0.79f, 0.82f, 1f);
        return mat;
    }

    /// <summary>Build a MeshGeometryModel3D per part (call on the UI thread).</summary>
    public static IEnumerable<MeshGeometryModel3D> ToModels(PreviewData data, bool wireframe = false)
    {
        foreach (var p in data.Parts)
            yield return new MeshGeometryModel3D
            {
                Geometry = p.Geometry,
                Material = MakeMaterial(p.AlbedoPng),
                CullMode = SharpDX.Direct3D11.CullMode.Back,
                RenderWireframe = wireframe,
                WireframeColor = System.Windows.Media.Colors.Lime,
            };
    }

    /// <summary>Populate a viewport collection with the model's per-part meshes (lights are direct
    /// viewport children, set via <see cref="LightingState"/>).</summary>
    public static void Populate(ObservableElement3DCollection coll, PreviewData data, bool wireframe = false)
    {
        coll.Clear();
        foreach (var mesh in ToModels(data, wireframe)) coll.Add(mesh);
    }

    /// <summary>Frame a camera on the model's bounds (Z-up, iso-ish vantage).</summary>
    public static void Frame(PerspectiveCamera cam, ModelGeometry geom)
    {
        var (mn, mx) = geom.Bounds();
        var center = new Vector3((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, (mn.Z + mx.Z) / 2);
        var ext = mx - mn;
        float radius = Math.Max(ext.X, Math.Max(ext.Y, ext.Z));
        if (radius <= 0) radius = 1;
        var dir = Vector3.Normalize(new Vector3(1f, -1f, 0.7f));
        var pos = center + dir * radius * 2.2f;
        cam.Position = new System.Windows.Media.Media3D.Point3D(pos.X, pos.Y, pos.Z);
        cam.LookDirection = new System.Windows.Media.Media3D.Vector3D(center.X - pos.X, center.Y - pos.Y, center.Z - pos.Z);
        cam.UpDirection = new System.Windows.Media.Media3D.Vector3D(0, 0, 1);
    }
}
