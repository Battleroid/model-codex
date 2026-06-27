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
    string Info,
    IReadOnlyList<int> Variants,
    int SelectedVariant,
    List<ChannelEdit> ChannelValues,
    List<ChannelValue> UsedChannels);

/// <summary>Shared model preview pipeline: parse geometry, build per-part textured meshes + channels.</summary>
public static class ModelPreview
{
    /// <summary>Max number of editable channel rows shown — see the comment at the population site.</summary>
    public const int MaxEditableChannels = 8;

    public static PreviewData Load(PackageManager mgr, ModelEntry entry, bool textured, uint? overrideAlbedo = null,
        ModelDetail detail = ModelDetail.MostDetailed, int? variant = null, MaterialView view = MaterialView.Shaded,
        bool flat = false)
    {
        var full = ModelParse.Parse(mgr, entry, detail);
        if (full == null || full.VertexCount == 0)
            return new PreviewData(null, new(), new(), "no geometry", Array.Empty<int>(), -1, new(), new());

        // Render only the selected permutation/variant — entities stack every variant on the same
        // geometry, so drawing them all z-fights and the model looks gray/wrong.
        int sel = variant ?? full.DefaultVariant;
        var g = full.WithVariant(sel);

        var parts = ModelSceneBuilder.BuildParts(g, mgr, textured, overrideAlbedo, view, flat);

        var channels = new List<MaterialChannel>();
        var values = new List<ChannelEdit>();
        var used = new List<ChannelValue>();
        var seenMat = new HashSet<uint>();
        var seenTex = new HashSet<uint>();
        var seenChan = new HashSet<uint>();
        foreach (var part in g.Parts)
        {
            if (part.MaterialHash == 0 || !seenMat.Add(part.MaterialHash)) continue;
            foreach (var c in MaterialMap.Channels(mgr, part.MaterialHash))
                if (seenTex.Add(c.TexHash)) channels.Add(c);
            // Object channels the material's shader bytecode references (aggregated across the model's
            // materials), resolved to names where the wordlist has them — like Deimos's Channels panel.
            foreach (uint h in MaterialMap.ObjectChannels(mgr, part.MaterialHash))
                if (seenChan.Add(h)) used.Add(new ChannelValue(ChannelNames.Resolve(h), $"0x{h:X8}"));
            // Editable channel values from the first material only (avoids a huge mixed list).
            // Each cbuffer Vec4 is a drag-scrub row. Slots the shader bytecode drives from a named object
            // channel (see TfxChannels) get that name; the rest are static constants shown as [index].
            // We surface named slots first, then non-zero constants. Cap the count: each row hosts 4 scrub
            // controls in the D3DImage-backed window and past ~12 realized rows the SharpDX viewport stops
            // compositing correctly (renders abstract colour blobs), so MaxEditableChannels stays under that.
            if (values.Count == 0)
            {
                var cb = MaterialMap.ChannelValues(mgr, part.MaterialHash);
                var slotChan = TfxChannels.SlotChannels(mgr, part.MaterialHash);
                var rows = new List<(int slot, string label, bool named, bool nonZero, (float x, float y, float z, float w) v)>();
                for (int i = 0; i < cb.Count; i++)
                {
                    bool named = slotChan.TryGetValue(i, out uint h);
                    var v = cb[i];
                    bool nonZero = v.x != 0 || v.y != 0 || v.z != 0 || v.w != 0;
                    if (!named && !nonZero) continue; // skip empty, unnamed constants
                    string label = named ? ChannelNames.Resolve(h) : $"[{i}]";
                    rows.Add((i, label, named, nonZero, v));
                }
                // Within the row cap, reserve up to a third for named channels (so the names are always
                // represented) and fill the rest with non-zero constants (whose change is visible when
                // dragged). Named slots shown first, then the constants, each in slot order.
                var namedRows = rows.Where(r => r.named).OrderBy(r => r.slot).ToList();
                var otherRows = rows.Where(r => !r.named).OrderByDescending(r => r.nonZero).ThenBy(r => r.slot).ToList();
                int namedQuota = Math.Min(namedRows.Count, MaxEditableChannels / 3);
                var chosen = namedRows.Take(namedQuota)
                    .Concat(otherRows.Take(MaxEditableChannels - namedQuota))
                    .Concat(namedRows.Skip(namedQuota)) // backfill with more named if constants ran out
                    .Take(MaxEditableChannels);
                foreach (var r in chosen)
                    values.Add(new ChannelEdit(r.label, r.v.x, r.v.y, r.v.z, r.v.w));
            }
        }

        var (mn, mx) = g.Bounds();
        var sz = mx - mn;
        string info = $"{g.Parts.Count} parts · {g.VertexCount:N0} verts · {g.TriangleCount:N0} tris · {sz.X:F1}×{sz.Y:F1}×{sz.Z:F1}";
        return new PreviewData(g, parts, channels, info, full.Variants, sel, values, used);
    }

    public static HxMaterial MakeMaterial(byte[]? albedoDds, byte[]? emissiveDds = null, bool unlit = false)
    {
        // Channel-view: show the texture flat/unlit via emissive only (black diffuse so lighting is ignored).
        if (unlit)
        {
            var flat = new PhongMaterial { DiffuseColor = Color.Black, AmbientColor = Color.Black };
            if (albedoDds != null) { flat.EmissiveColor = Color.White; flat.EmissiveMap = new TextureModel(new MemoryStream(albedoDds), true); }
            else flat.EmissiveColor = new Color4(0.5f, 0.5f, 0.5f, 1f);
            return flat;
        }
        // Lit material. AmbientColor lets ambient light contribute; tiny emissive avoids pure-black faces.
        var mat = new PhongMaterial
        {
            // Low flat ambient so the albedo stays saturated (high ambient washed colours out); the rig's
            // ambient light fills shadows. Tiny emissive avoids pure-black faces; gentle spec.
            AmbientColor = new Color4(0.45f, 0.45f, 0.48f, 1f),
            EmissiveColor = new Color4(0.015f, 0.015f, 0.02f, 1f),
            SpecularColor = new Color4(0.06f, 0.06f, 0.07f, 1f),
            SpecularShininess = 14f,
        };
        if (albedoDds != null) { mat.DiffuseColor = Color.White; mat.DiffuseMap = new TextureModel(new MemoryStream(albedoDds), true); }
        else mat.DiffuseColor = new Color4(0.78f, 0.79f, 0.82f, 1f);
        // Best-effort emissive: a mostly-black map that lights up only the genuinely glowing regions.
        if (emissiveDds != null)
        {
            mat.EmissiveColor = Color.White;
            mat.EmissiveMap = new TextureModel(new MemoryStream(emissiveDds), true);
        }
        return mat;
    }

    /// <summary>Build a MeshGeometryModel3D per part (call on the UI thread).</summary>
    public static IEnumerable<MeshGeometryModel3D> ToModels(PreviewData data, bool wireframe = false)
    {
        foreach (var p in data.Parts)
            yield return new MeshGeometryModel3D
            {
                Geometry = p.Geometry,
                Material = MakeMaterial(p.AlbedoDds, p.EmissiveDds, p.Unlit),
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

    /// <summary>Best-effort live channel feedback: multiply textured parts' albedo by the product of the
    /// "tint" channels' current RGB (white-default colour channels). The real shader isn't run, so only
    /// these colour channels visibly affect the model — dragging a scalar channel does nothing here.</summary>
    public static void ApplyTint(ObservableElement3DCollection coll, IEnumerable<ChannelEdit> channels)
    {
        float r = 1, g = 1, b = 1;
        foreach (var c in channels)
            if (c.IsTint) { r *= (float)c.X; g *= (float)c.Y; b *= (float)c.Z; }
        var tint = new Color4(Math.Clamp(r, 0, 2), Math.Clamp(g, 0, 2), Math.Clamp(b, 0, 2), 1f);
        foreach (var e in coll)
            if (e is MeshGeometryModel3D { Material: PhongMaterial pm } && pm.DiffuseMap != null)
                pm.DiffuseColor = tint;
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
