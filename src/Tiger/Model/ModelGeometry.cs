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

    public int TriangleCount => Indices.Count / 3;
}

/// <summary>A fully decoded static model: a set of parts plus an overall bounding box.</summary>
public sealed class ModelGeometry
{
    public List<ModelPart> Parts { get; } = new();

    public int VertexCount => Parts.Sum(p => p.Positions.Count);
    public int TriangleCount => Parts.Sum(p => p.TriangleCount);

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
