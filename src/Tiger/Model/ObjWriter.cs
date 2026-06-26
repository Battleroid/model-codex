using System.Globalization;
using System.Text;

namespace Tiger.Model;

/// <summary>Minimal Wavefront OBJ writer — used by the Probe harness to eyeball geometry in Blender.</summary>
public static class ObjWriter
{
    public static string ToObj(ModelGeometry geom, string name = "model")
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine($"# model-codex export: {name}");
        int vbase = 1;
        for (int pi = 0; pi < geom.Parts.Count; pi++)
        {
            var part = geom.Parts[pi];
            sb.AppendLine($"o part_{pi}_mat_{part.MaterialHash:X8}");
            foreach (var v in part.Positions) sb.AppendLine($"v {v.X.ToString(ci)} {v.Y.ToString(ci)} {v.Z.ToString(ci)}");
            foreach (var t in part.Texcoords) sb.AppendLine($"vt {t.X.ToString(ci)} {t.Y.ToString(ci)}");
            foreach (var n in part.Normals) sb.AppendLine($"vn {n.X.ToString(ci)} {n.Y.ToString(ci)} {n.Z.ToString(ci)}");
            for (int i = 0; i + 2 < part.Indices.Count; i += 3)
            {
                int a = vbase + part.Indices[i], b = vbase + part.Indices[i + 1], c = vbase + part.Indices[i + 2];
                sb.AppendLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
            }
            vbase += part.Positions.Count;
        }
        return sb.ToString();
    }
}
