namespace Tiger;

/// <summary>Asset groups: sets of textures that belong to one material/asset (color + normal +
/// data + colour variants), derived by unioning the same-package textures each material references.</summary>
public sealed class AssetGroups
{
    public List<uint[]> Groups { get; }                       // each group: texture taghashes (>=2)
    public IReadOnlyDictionary<uint, int> GroupOf { get; }     // taghash -> group index

    public AssetGroups(List<uint[]> groups)
    {
        Groups = groups;
        var m = new Dictionary<uint, int>();
        for (int i = 0; i < groups.Count; i++)
            foreach (uint t in groups[i]) m[t] = i;
        GroupOf = m;
    }

    public static AssetGroups Empty { get; } = new(new List<uint[]>());
}
