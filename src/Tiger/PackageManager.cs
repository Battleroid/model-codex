using Tiger.Model;

namespace Tiger;

public sealed class TextureEntry
{
    public uint TagHash { get; set; }
    public uint Reference { get; set; }     // entry-table reference -> the pixel-data buffer tag
    public ushort PkgId { get; set; }
    public int Index { get; set; }
    public uint Size { get; set; }
    public string PackageName { get; set; } = "";
    public TigerPackage Package { get; set; } = null!;

    // Filled lazily from the header (PackageManager.LoadHeader).
    public TexHeader? Header { get; set; }

    public string TagId => $"{TagHash:X8}";
    public string DisplayName => TagId;

    /// <summary>Category derived from the package name, e.g. w64_sr_gear_014e -> "sr_gear".</summary>
    public string Category => DeriveCategory(PackageName);

    public static string DeriveCategory(string pkgName)
    {
        string s = pkgName.StartsWith("w64_") ? pkgName[4..] : pkgName;
        // Drop the trailing _<hex id> (and an optional _en/_unp suffix before it).
        int i = s.LastIndexOf('_');
        if (i > 0)
        {
            string tail = s[(i + 1)..];
            if (tail.Length > 0 && tail.All(c => Uri.IsHexDigit(c))) s = s[..i];
        }
        return s.Length == 0 ? pkgName : s;
    }
}

/// <summary>One package as shown in the left panel (Deimos-style: "{pkgId} - {category}").</summary>
public sealed class PackageGroup
{
    public ushort PkgId { get; set; }
    public string Category { get; set; } = "";
    public int ModelCount { get; set; }
    public int EntryCount { get; set; }
    public string Display => $"{PkgId:x4} - {Category}";
}

/// <summary>
/// Indexes Marathon Tiger packages once and exposes everything built on top of them:
/// the universal taghash/hash64 resolver, the texture catalogue (for material preview/export),
/// and the model catalogue (the primary content). Generalised from texture-codex's TextureManager.
/// </summary>
public sealed class PackageManager
{
    public string PackagesDir { get; }
    public List<TextureEntry> Textures { get; } = new();
    public List<ModelEntry> Models { get; } = new();
    public List<PackageGroup> PackageGroups { get; } = new();
    public int PackageCount { get; private set; }

    private readonly Dictionary<ushort, TigerPackage> _byId = new();
    // tag hash -> (package, entry index): the universal resolver used to walk references
    // (texture pixel buffers @0x3C, and model -> vertex/index buffer -> material -> texture).
    private readonly Dictionary<uint, (TigerPackage pkg, int idx)> _byHash = new();
    // global hash64 -> 32-bit taghash, for cross-package references.
    private readonly Dictionary<ulong, uint> _hash64 = new();
    /// <summary>texture taghash -> entry, for group/material lookups.</summary>
    public Dictionary<uint, TextureEntry> ByTag { get; } = new();
    /// <summary>model taghash -> entry.</summary>
    public Dictionary<uint, ModelEntry> ModelByTag { get; } = new();

    public PackageManager(string packagesDir, string oodleDll)
    {
        PackagesDir = packagesDir;
        Oodle.Initialize(oodleDll);
    }

    public IEnumerable<TigerPackage> Packages => _byId.Values;

    /// <summary>Resolve a same-package (or any indexed) 32-bit taghash to its package + entry.</summary>
    public bool TryResolve(uint tagHash, out TigerPackage pkg, out int idx)
    {
        if (_byHash.TryGetValue(tagHash, out var loc)) { pkg = loc.pkg; idx = loc.idx; return true; }
        pkg = null!; idx = 0; return false;
    }

    /// <summary>Resolve a cross-package 64-bit tag to its local 32-bit taghash.</summary>
    public bool TryResolveHash64(ulong hash64, out uint hash32) => _hash64.TryGetValue(hash64, out hash32);

    /// <summary>Read a tag's raw (decrypted/decompressed) bytes by taghash.</summary>
    public byte[]? ReadTag(uint tagHash)
        => _byHash.TryGetValue(tagHash, out var loc) ? loc.pkg.ReadEntry(loc.idx) : null;

    /// <summary>Look up the entry metadata for a taghash (type/subtype/reference/size).</summary>
    public Entry? EntryOf(uint tagHash)
        => _byHash.TryGetValue(tagHash, out var loc) ? loc.pkg.Entries[loc.idx] : null;

    /// <summary>Scan all packages and build the catalogues. Reports progress 0..1.</summary>
    public void Index(Action<double, string>? progress = null)
    {
        string[] files = Directory.GetFiles(PackagesDir, "*.pkg");
        var best = new Dictionary<ushort, (TigerPackage pkg, int count)>();
        int done = 0;
        foreach (string f in files)
        {
            done++;
            TigerPackage pkg;
            try { pkg = new TigerPackage(f); } catch { continue; }
            progress?.Invoke(done / (double)files.Length * 0.6, $"Reading {Path.GetFileName(f)}");
            if (!best.TryGetValue(pkg.PkgId, out var cur) || pkg.Entries.Count > cur.count)
                best[pkg.PkgId] = (pkg, pkg.Entries.Count);
        }

        _byId.Clear(); _byHash.Clear(); _hash64.Clear();
        Textures.Clear(); ByTag.Clear(); Models.Clear(); ModelByTag.Clear(); PackageGroups.Clear();

        // First pass: build the universal taghash + hash64 maps so reference resolution works
        // while we classify entries.
        foreach (var (pkgId, (pkg, _)) in best)
        {
            _byId[pkgId] = pkg;
            for (int i = 0; i < pkg.Entries.Count; i++)
                _byHash[pkg.TagHash(i)] = (pkg, i);
            foreach (var (h64, h32) in pkg.Hash64) _hash64[h64] = h32;
        }

        // Second pass: classify into textures and models.
        int gi = 0;
        foreach (var (pkgId, pkg) in _byId)
        {
            gi++;
            if (gi % 8 == 0) progress?.Invoke(0.6 + gi / (double)_byId.Count * 0.4, $"Indexing {pkg.Name}");
            for (int i = 0; i < pkg.Entries.Count; i++)
            {
                Entry e = pkg.Entries[i];
                if (TigerTexture.IsTextureHeader(e))
                {
                    var te = new TextureEntry
                    {
                        TagHash = pkg.TagHash(i),
                        Reference = e.Reference,
                        PkgId = pkgId,
                        Index = i,
                        Size = e.FileSize,
                        PackageName = pkg.Name,
                        Package = pkg,
                    };
                    Textures.Add(te);
                    ByTag[te.TagHash] = te;
                }
                else if (ModelFormat.IsModelHeader(e) || ModelFormat.IsEntityHeader(e))
                {
                    var me = new ModelEntry
                    {
                        TagHash = pkg.TagHash(i),
                        Reference = e.Reference,
                        PkgId = pkgId,
                        Index = i,
                        Size = e.FileSize,
                        PackageName = pkg.Name,
                        Package = pkg,
                        IsEntity = ModelFormat.IsEntityHeader(e),
                    };
                    Models.Add(me);
                    ModelByTag[me.TagHash] = me;
                }
            }
        }

        BuildPackageGroups();
        PackageCount = best.Count;
        progress?.Invoke(1.0, $"{Models.Count} models, {Textures.Count} textures in {PackageCount} packages");
    }

    /// <summary>One entry per package (pkgId), labelled "{pkgId} - {category}" with its model count.</summary>
    private void BuildPackageGroups()
    {
        var modelsPerPkg = Models.GroupBy(m => m.PkgId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (pkgId, pkg) in _byId)
        {
            PackageGroups.Add(new PackageGroup
            {
                PkgId = pkgId,
                Category = TextureEntry.DeriveCategory(pkg.Name),
                EntryCount = pkg.Entries.Count,
                ModelCount = modelsPerPkg.GetValueOrDefault(pkgId),
            });
        }
        // Only packages that actually contain models are worth showing; sort by category then id.
        PackageGroups.RemoveAll(p => p.ModelCount == 0);
        PackageGroups.Sort((a, b) =>
        {
            int c = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.PkgId.CompareTo(b.PkgId);
        });
    }

    // ---- Texture functionality (material preview + export) ----

    // Tiger classes whose tags are materials that reference textures (found empirically; see Probe).
    private static readonly HashSet<uint> MaterialClasses = new()
    { 0x808031D8, 0x80805350, 0x80808567, 0x8080B9BA, 0x80808490, 0x8080AE01, 0x8080BEF0 };
    private const int ShareCap = 12;   // don't union THROUGH a texture shared by more than this many materials

    /// <summary>Build asset groups: union the same-package textures each material references
    /// (resolving cross-package refs via the hash64 table). Heavy (~material scan) — run off-thread.</summary>
    public AssetGroups BuildGroups(Action<double, string>? progress = null)
    {
        var texPkg = new Dictionary<uint, ushort>(Textures.Count);
        foreach (TextureEntry t in Textures) texPkg[t.TagHash] = t.PkgId;

        // global hash64 -> taghash map (texture targets only)
        var h64 = new Dictionary<ulong, uint>();
        foreach (var pkg in _byId.Values)
            foreach (var (h, h32) in pkg.Hash64)
                if (texPkg.ContainsKey(h32)) h64[h] = h32;

        var parent = new Dictionary<uint, uint>(texPkg.Count);
        foreach (uint t in texPkg.Keys) parent[t] = t;
        uint Find(uint x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(uint a, uint b) { uint ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        // Pass 1: each material's same-package texture set + per-texture material-use count.
        var matSets = new List<List<uint>>();
        var useCount = new Dictionary<uint, int>();
        var candidates = new List<(TigerPackage pkg, Entry e)>();
        foreach (var pkg in _byId.Values)
            foreach (Entry e in pkg.Entries)
                if (e.FileType == 8 && MaterialClasses.Contains(e.Reference) && e.FileSize is >= 8 and <= 65536)
                    candidates.Add((pkg, e));

        int done = 0;
        foreach (var (pkg, e) in candidates)
        {
            if (++done % 4000 == 0) progress?.Invoke(done / (double)candidates.Count, $"Grouping {done}/{candidates.Count}");
            byte[] d;
            try { d = pkg.ReadEntry(e.Index); } catch { continue; }
            var same = new List<uint>();
            var seen = new HashSet<uint>();
            for (int o = 0; o + 4 <= d.Length; o += 4)
            {
                uint v32 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
                uint tex = 0;
                if (texPkg.ContainsKey(v32)) tex = v32;
                else if (o + 8 <= d.Length && h64.TryGetValue(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o)), out uint tt)) tex = tt;
                if (tex != 0 && texPkg[tex] == pkg.PkgId && seen.Add(tex)) same.Add(tex);
            }
            if (same.Count >= 2) { matSets.Add(same); foreach (uint t in same) useCount[t] = useCount.GetValueOrDefault(t) + 1; }
        }

        // Pass 2: union, skipping links through heavily-shared textures (shared masks).
        foreach (var same in matSets)
        {
            uint? anchor = null;
            foreach (uint t in same)
            {
                if (useCount[t] > ShareCap) continue;
                if (anchor is uint a) Union(a, t); else anchor = t;
            }
        }

        // Collect components of size >= 2.
        var byRoot = new Dictionary<uint, List<uint>>();
        foreach (uint t in texPkg.Keys)
        {
            uint r = Find(t);
            if (!byRoot.TryGetValue(r, out var list)) byRoot[r] = list = new();
            list.Add(t);
        }
        // Score grouped textures so each group can be ordered primary-first (sRGB colour, largest area).
        var score = new Dictionary<uint, long>();
        foreach (var list in byRoot.Values)
        {
            if (list.Count < 2) continue;
            foreach (uint t in list)
            {
                if (score.ContainsKey(t)) continue;
                try
                {
                    TexHeader h = TigerTexture.ParseHeader(ReadHeaderBytes(ByTag[t]));
                    long area = (long)h.Width * h.Height;
                    score[t] = (TigerTexture.IsSrgb(h.DxgiFormat) ? 1L << 40 : 0) + area;
                }
                catch { score[t] = 0; }
            }
        }

        var groups = new List<uint[]>();
        foreach (var list in byRoot.Values)
            if (list.Count >= 2)
                groups.Add(list.OrderByDescending(t => score.GetValueOrDefault(t)).ToArray());
        progress?.Invoke(1.0, $"{groups.Count} asset groups");
        return new AssetGroups(groups);
    }

    public byte[] ReadHeaderBytes(TextureEntry t) => t.Package.ReadEntry(t.Index);

    /// <summary>Parse + cache the header for an entry.</summary>
    public TexHeader LoadHeader(TextureEntry t)
    {
        if (t.Header is { } h) return h;
        byte[] tag = ReadHeaderBytes(t);
        TexHeader hd = TigerTexture.ParseHeader(tag);
        t.Header = hd;
        return hd;
    }

    /// <summary>Resolve the pixel bytes for a texture: external buffer (@0x3C) or inline after the header.</summary>
    public byte[]? ReadPixels(TextureEntry t, out TexHeader header)
    {
        byte[] tag = ReadHeaderBytes(t);
        header = TigerTexture.ParseHeader(tag);
        t.Header = header;
        // Prefer the @0x3C "large texture" hash (high-res mips, streamed separately) when present;
        // otherwise the entry-table Reference points to the inline data buffer (e.g. most UI textures).
        if (header.HasExternalBuffer && _byHash.TryGetValue(header.BufferHash, out var loc))
        {
            try { return loc.pkg.ReadEntry(loc.idx); } catch { /* fall through */ }
        }
        if (t.Reference != 0 && t.Reference != 0xFFFFFFFF && _byHash.TryGetValue(t.Reference, out var loc2))
        {
            try { return loc2.pkg.ReadEntry(loc2.idx); } catch { return null; }
        }
        if (tag.Length > TigerTexture.HeaderSize) return tag.AsSpan(TigerTexture.HeaderSize).ToArray();
        return null;
    }

    /// <summary>Decode a texture's top mip to RGBA8. Returns (rgba, width, height) or null.</summary>
    public (byte[] rgba, int width, int height)? Decode(TextureEntry t)
    {
        byte[]? pixels = ReadPixels(t, out TexHeader h);
        if (pixels == null) return null;
        byte[]? rgba = TigerTexture.DecodeRgba(h, pixels);
        return rgba == null ? null : (rgba, h.Width, h.Height);
    }

    /// <summary>Decode a small mip (~target px) for a thumbnail. Returns (rgba, width, height) or null.</summary>
    public (byte[] rgba, int width, int height)? DecodeThumb(TextureEntry t, int target)
    {
        byte[]? pixels = ReadPixels(t, out TexHeader h);
        if (pixels == null) return null;
        byte[]? rgba = TigerTexture.DecodeThumb(h, pixels, target, out int w, out int hh);
        return rgba == null ? null : (rgba, w, hh);
    }

    /// <summary>Build a DDS for raw export. Returns null if pixels can't be resolved.</summary>
    public byte[]? BuildDds(TextureEntry t)
    {
        byte[]? pixels = ReadPixels(t, out TexHeader h);
        return pixels == null ? null : TigerTexture.BuildDds(h, pixels);
    }
}
