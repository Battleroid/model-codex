namespace Tiger.Model;

/// <summary>
/// A model header tag — the grid/preview unit, analogous to <see cref="TextureEntry"/>.
/// Geometry-derived fields (part/vertex counts, bounds) are filled lazily once parsed.
/// </summary>
public sealed class ModelEntry
{
    public uint TagHash { get; set; }
    public uint Reference { get; set; }     // entry-table class/reference hash
    public ushort PkgId { get; set; }
    public int Index { get; set; }
    public uint Size { get; set; }
    public string PackageName { get; set; } = "";
    public TigerPackage Package { get; set; } = null!;

    /// <summary>True for entity/dynamic models (class 0x8080BAAD); false for static meshes (0x80808635).</summary>
    public bool IsEntity { get; set; }

    // Filled lazily once the geometry is parsed (Phase 1).
    public int PartCount { get; set; }
    public int VertexCount { get; set; }
    public int TriangleCount { get; set; }
    public bool Parsed { get; set; }
    public bool ParseFailed { get; set; }

    public string TagId => $"{TagHash:X8}";
    public string DisplayName => TagId;

    /// <summary>Category derived from the package name, e.g. w64_sr_gear_014e -> "sr_gear".</summary>
    public string Category => TextureEntry.DeriveCategory(PackageName);
}
