namespace Tiger.Model;

/// <summary>
/// Classification of Tiger model/geometry tags. The exact (FileType, FileSubType) and reference
/// class hashes are pinned in Phase 1 via the Probe RE harness (`hist` / `find` / `model`), then
/// the predicates below are tightened. Until then <see cref="IsModelHeader"/> returns false so the
/// catalogue indexes cleanly with an empty model set.
/// </summary>
public static class ModelFormat
{
    /// <summary>
    /// Static/render model header class hashes. Confirmed against retail bytes:
    /// 0x80808635 = SStaticMesh (the top-level static model; 54,399 in the retail catalogue).
    /// Entity/dynamic models are a follow-up (different class).
    /// </summary>
    public static readonly HashSet<uint> ModelClasses = new() { StaticMesh.ClassStaticMesh };
    public static readonly HashSet<uint> EntityClasses = new() { EntityMesh.ClassEntity };

    /// <summary>True if this entry is a static/render model header.</summary>
    public static bool IsModelHeader(Entry e) => e.FileType == 8 && ModelClasses.Contains(e.Reference);

    /// <summary>True if this entry is an entity/dynamic model header.</summary>
    public static bool IsEntityHeader(Entry e) => e.FileType == 8 && EntityClasses.Contains(e.Reference);
}

/// <summary>Unified parse entry-point: dispatches static vs entity geometry.</summary>
public static class ModelParse
{
    public static ModelGeometry? Parse(PackageManager mgr, ModelEntry entry, ModelDetail detail = ModelDetail.MostDetailed)
        => entry.IsEntity
            ? EntityMesh.Parse(mgr, entry.TagHash, detail)
            : StaticMesh.Parse(mgr, entry.TagHash, detail);
}
