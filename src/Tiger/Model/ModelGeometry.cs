using System.Numerics;

namespace Tiger.Model;

/// <summary>One drawable part of a model: a triangle list over its own vertex arrays.</summary>
public sealed class ModelPart
{
    public List<Vector3> Positions { get; } = new();
    public List<Vector2> Texcoords { get; } = new();
    public List<Vector3> Normals { get; } = new();
    /// <summary>Flattened, 0-based triangle indices (3 per triangle) into this part's arrays.</summary>
    public List<int> Indices { get; } = new();

    public int MaterialIndex { get; set; } = -1;
    public uint MaterialHash { get; set; }
    public int DetailLevel { get; set; }
    /// <summary>Permutation/variant this part belongs to (-1 = no variant: always drawn). Entities expose
    /// the same geometry several times, one part per variant, each with its own material — the gear/skin
    /// "permutations". Only the selected variant should be drawn; otherwise they z-fight and look wrong.</summary>
    public int Variant { get; set; } = -1;

    public int TriangleCount => Indices.Count / 3;
}

/// <summary>A fully decoded static model: a set of parts plus an overall bounding box.</summary>
public sealed class ModelGeometry
{
    public List<ModelPart> Parts { get; } = new();

    public int VertexCount => Parts.Sum(p => p.Positions.Count);
    public int TriangleCount => Parts.Sum(p => p.TriangleCount);

    /// <summary>Distinct variant/permutation indices present (sorted). Empty when the model has no variants.</summary>
    public IReadOnlyList<int> Variants =>
        Parts.Where(p => p.Variant >= 0).Select(p => p.Variant).Distinct().OrderBy(v => v).ToList();

    /// <summary>The variant shown by default (the lowest index), or -1 when the model has no variants.</summary>
    public int DefaultVariant => Variants.Count > 0 ? Variants[0] : -1;

    /// <summary>Parts to draw for a given variant: the variant's own parts plus any non-variant (always-on)
    /// parts. When the model has no variants, every part is returned.</summary>
    public IEnumerable<ModelPart> PartsForVariant(int variant) =>
        Parts.Where(p => p.Variant < 0 || p.Variant == variant);

    /// <summary>A lightweight view of this model containing only the given variant's parts (shares the same
    /// ModelPart objects, so switching permutations is cheap and needs no re-decode).</summary>
    public ModelGeometry WithVariant(int variant)
    {
        if (Variants.Count == 0) return this;
        var g = new ModelGeometry();
        g.Parts.AddRange(PartsForVariant(variant));
        return g;
    }

    public (Vector3 min, Vector3 max) Bounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var part in Parts)
            foreach (var v in part.Positions)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        if (Parts.All(p => p.Positions.Count == 0)) return (Vector3.Zero, Vector3.Zero);
        return (min, max);
    }
}
