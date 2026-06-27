using System.Buffers.Binary;
using SixLabors.ImageSharp;
using Tiger;
using Tiger.Model;
using Probe;

// Texture Codex — Phase 0 discovery harness.
// Identify the texture FileType/FileSubType, confirm header offsets, decode samples.

string PKG_DIR = @"A:\Steam\steamapps\common\Marathon\packages";
string OODLE = @"A:\Steam\steamapps\common\Marathon\bin\x64\oo2core_9_win64.dll";
string OUT = Path.Combine(Path.GetTempPath(), "mara_texprobe");
Directory.CreateDirectory(OUT);

string cmd = args.Length > 0 ? args[0] : "hist";
Oodle.Initialize(OODLE);

// Load best package per pkg_id (largest entry table), mirroring PackageManager.Index.
Console.Error.WriteLine("Loading packages...");
string[] files = Directory.GetFiles(PKG_DIR, "*.pkg");
var best = new Dictionary<ushort, TigerPackage>();
var bestCount = new Dictionary<ushort, int>();
int loaded = 0;
foreach (string f in files)
{
    TigerPackage pkg;
    try { pkg = new TigerPackage(f); } catch { continue; }
    loaded++;
    if (!bestCount.TryGetValue(pkg.PkgId, out int c) || pkg.Entries.Count > c)
    {
        best[pkg.PkgId] = pkg;
        bestCount[pkg.PkgId] = pkg.Entries.Count;
    }
}
Console.Error.WriteLine($"Loaded {loaded} files -> {best.Count} unique packages.");

if (cmd == "hist")
{
    // (type,subtype) -> count, size stats, and per-package-name tallies.
    var count = new Dictionary<(byte, byte), long>();
    var sizeMin = new Dictionary<(byte, byte), uint>();
    var sizeMax = new Dictionary<(byte, byte), uint>();
    var sizeSum = new Dictionary<(byte, byte), double>();
    var pkgNames = new Dictionary<(byte, byte), Dictionary<string, int>>();

    foreach (var pkg in best.Values)
    {
        foreach (Entry e in pkg.Entries)
        {
            var k = (e.FileType, e.FileSubType);
            count[k] = count.GetValueOrDefault(k) + 1;
            sizeSum[k] = sizeSum.GetValueOrDefault(k) + e.FileSize;
            if (!sizeMin.TryGetValue(k, out uint mn) || e.FileSize < mn) sizeMin[k] = e.FileSize;
            if (!sizeMax.TryGetValue(k, out uint mx) || e.FileSize > mx) sizeMax[k] = e.FileSize;
            if (!pkgNames.TryGetValue(k, out var pn)) pkgNames[k] = pn = new();
            pn[pkg.Name] = pn.GetValueOrDefault(pkg.Name) + 1;
        }
    }

    Console.WriteLine($"\n{"type/sub",-10} {"count",10} {"avgSize",12} {"minSize",10} {"maxSize",12}   topPackages");
    Console.WriteLine(new string('-', 110));
    foreach (var k in count.Keys.OrderByDescending(k => count[k]))
    {
        double avg = sizeSum[k] / count[k];
        string top = string.Join(", ", pkgNames[k].OrderByDescending(p => p.Value).Take(3).Select(p => $"{p.Key}({p.Value})"));
        string tag = $"{k.Item1}/{k.Item2}";
        Console.WriteLine($"{tag,-10} {count[k],10} {avg,12:N0} {sizeMin[k],10:N0} {sizeMax[k],12:N0}   {top}");
    }
}
else if (cmd == "tex")
{
    // Known DXGI formats we care about for textures.
    var dxgi = KnownDxgi();
    // Candidate fixed-size header types to evaluate against the D2 "Beyond Light" offset layout.
    var candidates = new (byte t, byte s)[] { (32, 1), (32, 2), (32, 3), (32, 4), (32, 6), (32, 7), (33, 0), (33, 1), (33, 6), (34, 1), (34, 3), (24, 0) };

    foreach (var (t, s) in candidates)
    {
        var entries = new List<(TigerPackage pkg, Entry e)>();
        foreach (var pkg in best.Values)
            foreach (Entry e in pkg.Entries)
                if (e.FileType == t && e.FileSubType == s) entries.Add((pkg, e));
        if (entries.Count == 0) continue;

        int sample = Math.Min(400, entries.Count);
        int plausible = 0, knownFmt = 0, both = 0;
        var fmtHist = new Dictionary<ushort, int>();
        var samples = new List<string>();
        for (int i = 0; i < sample; i++)
        {
            var (pkg, e) = entries[i * entries.Count / sample];
            byte[] d;
            try { d = pkg.ReadEntry(e.Index); } catch { continue; }
            if (d.Length < 0x40) continue;
            ushort fmt = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x04));
            ushort w = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x22));
            ushort h = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x24));
            bool dimOk = w is >= 1 and <= 16384 && h is >= 1 and <= 16384;
            bool fmtOk = dxgi.ContainsKey(fmt);
            if (dimOk) plausible++;
            if (fmtOk) { knownFmt++; fmtHist[fmt] = fmtHist.GetValueOrDefault(fmt) + 1; }
            if (dimOk && fmtOk) both++;
            if (samples.Count < 4)
                samples.Add($"      idx={e.Index} sz={e.FileSize} fmt={fmt}({(fmtOk ? dxgi[fmt] : "?")}) w={w} h={h}  head={Convert.ToHexString(d.AsSpan(0, Math.Min(64, d.Length)))}");
        }
        Console.WriteLine($"\n=== {t}/{s}  ({entries.Count} entries, sampled {sample}) ===");
        Console.WriteLine($"  plausibleDims={plausible}/{sample}  knownFmt={knownFmt}/{sample}  BOTH={both}/{sample}");
        if (fmtHist.Count > 0)
            Console.WriteLine($"  formats: {string.Join(", ", fmtHist.OrderByDescending(p => p.Value).Take(8).Select(p => $"{dxgi[p.Key]}={p.Value}"))}");
        foreach (var ln in samples) Console.WriteLine(ln);
    }
}

else if (cmd == "mgr")
{
    // Validate the production PackageManager + TigerTexture layer end to end.
    var mgr = new PackageManager(PKG_DIR, OODLE);
    mgr.Index((p, m) => { if (p >= 1) Console.Error.WriteLine(m); });
    Console.WriteLine($"Indexed {mgr.Textures.Count} textures across {mgr.PackageCount} packages.");
    var cats = mgr.Textures.GroupBy(t => t.Category).OrderByDescending(g => g.Count()).Take(15);
    Console.WriteLine("Top categories: " + string.Join(", ", cats.Select(g => $"{g.Key}({g.Count()})")));

    int want = args.Length > 1 ? int.Parse(args[1]) : 12;
    int ok = 0;
    var seen = new HashSet<ushort>();
    foreach (var t in mgr.Textures)
    {
        if (ok >= want) break;
        TexHeader h;
        try { h = mgr.LoadHeader(t); } catch { continue; }
        if (!TigerTexture.IsSupported(h.DxgiFormat)) continue;
        if (seen.Contains(h.DxgiFormat) && ok > 6) continue;
        var dec = mgr.Decode(t);
        if (dec == null) continue;
        var (rgba, w, hh) = dec.Value;
        TexDecode.WritePng(Path.Combine(OUT, $"mgr_{t.TagId}_{h.FormatName}_{w}x{hh}.png"), rgba, w, hh);
        seen.Add(h.DxgiFormat); ok++;
        Console.WriteLine($"  OK {t.TagId} {h.FormatName,-16} {w}x{hh}  cat={t.Category} pkg={t.PackageName}");
    }
    Console.WriteLine($"\nDecoded {ok} via PackageManager -> {OUT}");
}
else if (cmd == "meta")
{
    // Validate extra header fields: bpp @0x2C, mipcount @0x2D, depth @0x26, arraySize @0x28.
    var mgr = new PackageManager(PKG_DIR, OODLE);
    mgr.Index((p, m) => { });
    int sample = 0, cubes = 0, arrays = 0, volumes = 0, bppOk = 0, mipOk = 0, total = 0;
    foreach (var t in mgr.Textures)
    {
        byte[] d; try { d = mgr.ReadHeaderBytes(t); } catch { continue; }
        if (d.Length < 0x40 || BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x20)) != 0xCAFE) continue;
        ushort w = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x22));
        ushort h = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x24));
        ushort depth = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x26));
        ushort arr = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x28));
        byte bpp = d[0x2C], mips = d[0x2D];
        total++;
        if (arr == 6) cubes++; else if (arr > 1) arrays++;
        if (depth > 1) volumes++;
        int expMips = (int)Math.Floor(Math.Log2(Math.Max(1, (int)Math.Max((int)w, (int)h)))) + 1;
        if (mips == expMips) mipOk++;
        if (bpp is 4 or 8 or 16 or 32 or 1 or 2 or 24 or 64 or 128) bppOk++;
        if (sample < 14)
        {
            ushort fmt = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x04));
            Console.WriteLine($"  {t.TagId} {TigerTexture.FormatName(fmt),-16} {w}x{h} depth={depth} arr={arr} bpp={bpp} mips={mips} (exp {expMips})");
            sample++;
        }
    }
    Console.WriteLine($"\ntotal={total} bppPlausible={bppOk} mipMatches={mipOk} ({100.0*mipOk/total:F0}%)");
    Console.WriteLine($"cubemaps(arr=6)={cubes} arrays(arr>1)={arrays} volumes(depth>1)={volumes}");
}
else if (cmd == "names")
{
    // Parse the named-tag table (size@0x78, offset@0x7C; table at offset+0x30; entry = u32 hash,
    // u32 class, u64 name_offset -> NullString at entryStart+8+name_offset). Across all packages,
    // report total named tags, which classes, and whether any name a texture.
    var texSet = new HashSet<uint>();
    foreach (var pkg in best.Values)
        foreach (Entry e in pkg.Entries)
            if (e.FileType == 32 && e.FileSubType is 1 or 2 or 3) texSet.Add(pkg.TagHash(e.Index));

    int totalNamed = 0, namedTextures = 0, examples = 0;
    var classHist = new Dictionary<uint, int>();
    foreach (string f in files)
    {
        byte[] all;
        try { all = File.ReadAllBytes(f); } catch { continue; }
        if (all.Length < 0x130 || BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(0)) != 53) continue;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(0x78));
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(0x7C));
        if (size == 0 || offset == 0) continue;
        long tableStart = (long)offset + 0x30;
        for (int i = 0; i < size; i++)
        {
            long es = tableStart + i * 16;
            if (es + 16 > all.Length) break;
            uint hash = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan((int)es));
            uint cls = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan((int)es + 4));
            ulong nameOff = BinaryPrimitives.ReadUInt64LittleEndian(all.AsSpan((int)es + 8));
            long np = es + 8 + (long)nameOff;
            if (np < 0 || np >= all.Length) continue;
            int end = (int)np; while (end < all.Length && all[end] != 0) end++;
            string name = System.Text.Encoding.UTF8.GetString(all, (int)np, end - (int)np);
            totalNamed++;
            classHist[cls] = classHist.GetValueOrDefault(cls) + 1;
            bool isTex = texSet.Contains(hash);
            if (isTex) namedTextures++;
            if (examples < 30 && name.Length > 0)
            { Console.WriteLine($"  {hash:X8} cls={cls:X8} tex={isTex} \"{name}\""); examples++; }
        }
    }
    Console.WriteLine($"\ntotal named tags={totalNamed}, named textures={namedTextures}");
    Console.WriteLine("classes: " + string.Join(", ", classHist.OrderByDescending(k => k.Value).Take(12).Select(k => $"{k.Key:X8}={k.Value}")));
}
else if (cmd == "h64")
{
    // Parse the hash64 table per v4nguard/tiger-pkg d2_beyondlight: size@0xB8, offset@0xBC;
    // seek offset+0x10 -> i64 rel; entries start at offset+0x20+rel; entry = {u64 hash64, u32 hash32, u32 ref}.
    string sub = args.Length > 1 ? args[1] : "sr_gear";
    string? file = files.FirstOrDefault(f => Path.GetFileName(f).Contains(sub, StringComparison.OrdinalIgnoreCase));
    if (file == null) { Console.WriteLine("no match"); return; }
    byte[] all = File.ReadAllBytes(file);
    uint size = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(0xB8));
    uint offset = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(0xBC));
    long rel = BinaryPrimitives.ReadInt64LittleEndian(all.AsSpan((int)offset + 0x10));
    long start = (long)offset + 0x20 + rel;
    Console.WriteLine($"{Path.GetFileName(file)}: h64 size={size} offset=0x{offset:X} rel=0x{rel:X} start=0x{start:X}");
    for (int i = 0; i < Math.Min(12, size); i++)
    {
        int o = (int)start + i * 16;
        if (o + 16 > all.Length) break;
        ulong h64 = BinaryPrimitives.ReadUInt64LittleEndian(all.AsSpan(o));
        uint h32 = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(o + 8));
        uint cls = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(o + 12));
        Console.WriteLine($"  [{i}] hash64={h64:X16}  hash32={h32:X8}  class={cls:X8}");
    }
}
else if (cmd == "header")
{
    // Dump the raw package header as u32s to locate the hash64 table (size/offset pair).
    string sub = args.Length > 1 ? args[1] : "sr_gear";
    string? file = files.FirstOrDefault(f => Path.GetFileName(f).Contains(sub, StringComparison.OrdinalIgnoreCase));
    if (file == null) { Console.WriteLine("no package matches " + sub); return; }
    long fsize = new FileInfo(file).Length;
    byte[] hdr = new byte[0x130];
    using (var fs = File.OpenRead(file)) { fs.Read(hdr, 0, hdr.Length); }
    Console.WriteLine($"{Path.GetFileName(file)}  fileSize={fsize:N0} (0x{fsize:X})");
    for (int o = 0; o < 0x130; o += 4)
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(o));
        // flag values that look like offsets into the file
        string note = (v > 0x130 && v < fsize) ? "  <- in-file offset?" : "";
        Console.WriteLine($"  0x{o:X3}: {v,12} (0x{v:X8}){note}");
    }
}
else if (cmd == "find")
{
    // Reverse-lookup: which tags reference a given texture hash? (scan larger tags too)
    uint target = Convert.ToUInt32(args[1], 16);
    var texSet = new HashSet<uint>();
    foreach (var pkg in best.Values)
        foreach (Entry e in pkg.Entries)
            if (e.FileType == 32 && e.FileSubType is 1 or 2 or 3) texSet.Add(pkg.TagHash(e.Index));

    int hits = 0;
    foreach (var pkg in best.Values)
        foreach (Entry e in pkg.Entries)
        {
            if (e.FileType == 32 && e.FileSubType is 1 or 2 or 3) continue;
            if (e.FileSize < 4 || e.FileSize > 2_000_000) continue;
            byte[] d;
            try { d = pkg.ReadEntry(e.Index); } catch { continue; }
            int refCount = 0, texRefs = 0;
            for (int o = 0; o + 4 <= d.Length; o += 4)
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
                if (v == target) refCount++;
                if (texSet.Contains(v)) texRefs++;
            }
            if (refCount > 0)
            {
                Console.WriteLine($"  referencer {pkg.TagHash(e.Index):X8} type={e.FileType}/{e.FileSubType} ref={e.Reference:X8} sz={e.FileSize} hits={refCount} totalTexRefs={texRefs}");
                if (++hits >= 12) return;
            }
        }
    Console.WriteLine($"done, {hits} referencers");
}
else if (cmd == "model")
{
    // Phase 1 model RE: confirm the SStaticMesh graph against real bytes.
    // Static model class = 0x80808635 (SStaticMesh); its StaticData child = 0x80808620 (SStaticMeshData);
    // vertex/index buffer headers are 12-byte SVertexHeader {u32 DataSize, s16 Stride, s16 Type, u32 0xDEADBEEF}.
    const uint SStaticMesh = 0x80808635, SStaticMeshData = 0x80808620;

    // Global resolver.
    var byHash = new Dictionary<uint, (TigerPackage pkg, int idx)>();
    foreach (var pkg in best.Values)
        for (int i = 0; i < pkg.Entries.Count; i++) byHash[pkg.TagHash(i)] = (pkg, i);
    Entry? E(uint h) => byHash.TryGetValue(h, out var l) ? l.pkg.Entries[l.idx] : null;
    byte[]? Read(uint h) => byHash.TryGetValue(h, out var l) ? l.pkg.ReadEntry(l.idx) : null;

    // Count + locate static meshes.
    var models = new List<(TigerPackage pkg, Entry e)>();
    foreach (var pkg in best.Values)
        foreach (Entry e in pkg.Entries)
            if (e.FileType == 8 && e.Reference == SStaticMesh) models.Add((pkg, e));
    var byPkg = models.GroupBy(m => m.pkg.Name).OrderByDescending(g => g.Count()).Take(8);
    Console.WriteLine($"SStaticMesh (0x{SStaticMesh:X8}) tags: {models.Count}");
    Console.WriteLine("  top packages: " + string.Join(", ", byPkg.Select(g => $"{g.Key}({g.Count()})")));

    if (models.Count == 0) return;

    // Pick a target: args[1] hash, else the first static mesh in sr_gear (clean single-mesh props).
    uint target;
    if (args.Length > 1) target = Convert.ToUInt32(args[1], 16);
    else { var pick = models.OrderBy(m => m.e.FileSize).First(m => m.e.FileSize is >= 0x40 and <= 0x200); target = pick.pkg.TagHash(pick.e.Index); }

    void RefScan(string label, uint hash)
    {
        byte[]? d = Read(hash);
        if (d == null) { Console.WriteLine($"{label}: cannot read {hash:X8}"); return; }
        Entry? me = E(hash);
        Console.WriteLine($"\n=== {label} {hash:X8}  type={me?.FileType}/{me?.FileSubType} ref={me?.Reference:X8} size={d.Length} ===");
        Console.WriteLine("  hex[0x00..0x80]: " + Convert.ToHexString(d.AsSpan(0, Math.Min(0x80, d.Length))));
        for (int o = 0; o + 4 <= d.Length && o < 0x120; o += 4)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
            if (byHash.TryGetValue(v, out var loc))
            {
                Entry ce = loc.pkg.Entries[loc.idx];
                Console.WriteLine($"    @0x{o:X3} -> {v:X8}  type={ce.FileType}/{ce.FileSubType} ref={ce.Reference:X8} size={ce.FileSize}" +
                                  (loc.pkg.Name != me?.ToString() ? $" [{loc.pkg.Name}]" : ""));
            }
        }
    }

    RefScan("SStaticMesh", target);

    // Resolve StaticData child (the 0x80808620 tag referenced inside).
    byte[] md = Read(target)!;
    uint staticData = 0;
    for (int o = 0; o + 4 <= md.Length && o < 0x120; o += 4)
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(md.AsSpan(o));
        if (E(v)?.Reference == SStaticMeshData) { staticData = v; break; }
    }
    if (staticData != 0)
    {
        RefScan("SStaticMeshData", staticData);
        // Dump any 12-byte SVertexHeader buffer tags referenced (32/4) and their data tags.
        byte[] sd = Read(staticData)!;
        var seen = new HashSet<uint>();
        for (int o = 0; o + 4 <= sd.Length; o += 4)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(sd.AsSpan(o));
            Entry? ce = E(v);
            if (ce == null || !seen.Add(v)) continue;
            if (ce.FileType == 32 && ce.FileSize is 12 or 16 or 24)
            {
                byte[] hb = Read(v)!;
                uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(hb.AsSpan(0));
                short stride = BinaryPrimitives.ReadInt16LittleEndian(hb.AsSpan(4));
                short type = BinaryPrimitives.ReadInt16LittleEndian(hb.AsSpan(6));
                uint magic = hb.Length >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(hb.AsSpan(8)) : 0;
                Entry? dataE = E(ce.Reference);
                Console.WriteLine($"  buffer {v:X8} (32/{ce.FileSubType}): DataSize={dataSize} Stride={stride} Type={type} magic={magic:X8}" +
                                  $"  -> data ref={ce.Reference:X8} type={dataE?.FileType}/{dataE?.FileSubType} size={dataE?.FileSize}" +
                                  (dataE != null && dataSize == dataE.FileSize ? "  [size match]" : ""));
            }
        }
    }
}
else if (cmd == "entity")
{
    // Phase 7 entity RE: from an SEntity (0x8080BAAD) tag, BFS references to find the SEntityModel
    // geometry tag (0x8080881C), confirm its layout, and list the mesh buffer refs.
    const uint SEntity = 0x8080BAAD, SEntityModel = 0x8080881C;
    var byHash = new Dictionary<uint, (TigerPackage pkg, int idx)>();
    foreach (var pkg in best.Values)
        for (int i = 0; i < pkg.Entries.Count; i++) byHash[pkg.TagHash(i)] = (pkg, i);
    Entry? E(uint h) => byHash.TryGetValue(h, out var l) ? l.pkg.Entries[l.idx] : null;
    byte[]? Read(uint h) => byHash.TryGetValue(h, out var l) ? l.pkg.ReadEntry(l.idx) : null;

    int entCount = best.Values.Sum(p => p.Entries.Count(e => e.FileType == 8 && e.Reference == SEntity));
    Console.WriteLine($"SEntity (0x{SEntity:X8}) tags: {entCount}");

    uint target = args.Length > 1 ? Convert.ToUInt32(args[1], 16)
        : best.Values.SelectMany(p => p.Entries.Where(e => e.FileType == 8 && e.Reference == SEntity).Select(e => p.TagHash(e.Index))).First();
    Console.WriteLine($"entity {target:X8} type={E(target)?.FileType}/{E(target)?.FileSubType} ref={E(target)?.Reference:X8}");

    // BFS to find SEntityModel geometry tags.
    var found = new List<uint>();
    var visited = new HashSet<uint> { target };
    var queue = new Queue<(uint hash, int depth)>();
    queue.Enqueue((target, 0));
    while (queue.Count > 0)
    {
        var (h, depth) = queue.Dequeue();
        if (depth > 5) continue;
        byte[]? d = Read(h);
        if (d == null) continue;
        for (int o = 0; o + 4 <= d.Length; o += 4)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
            if ((v & 0xFF000000) != 0x80000000) continue;
            var ce = E(v);
            if (ce == null) continue;
            if (ce.Reference == SEntityModel && !found.Contains(v)) found.Add(v);
            if (visited.Add(v) && depth < 5) queue.Enqueue((v, depth + 1));
        }
    }
    Console.WriteLine($"found {found.Count} SEntityModel tags: {string.Join(", ", found.Select(f => f.ToString("X8")))}");

    // Find the model resource (an 8080BADB tag containing the SEntityModel hash) and dump ExternalMaterials.
    if (found.Count > 0)
    {
        uint em0 = found[0];
        foreach (uint cand in visited)
        {
            var ce = E(cand);
            if (ce == null || ce.Reference != 0x8080BADB) continue;
            byte[] rd = Read(cand)!;
            int at = -1;
            for (int o = 0; o + 4 <= rd.Length; o += 4)
                if (BinaryPrimitives.ReadUInt32LittleEndian(rd.AsSpan(o)) == em0) { at = o; break; }
            if (at < 0) continue;
            int baseOff = at - 0x244;
            if (baseOff < 0) continue;
            Console.WriteLine($"\nmodel resource {cand:X8} (8080BADB) size={rd.Length}: SEntityModel@0x{at:X}");
            // Scan for the longest run of valid material tags (8/0) — that's the ExternalMaterials array.
            int bestStart = -1, bestLen = 0, curStart = -1, curLen = 0;
            for (int o = 0; o + 4 <= rd.Length; o += 4)
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(rd.AsSpan(o));
                var ve = E(v);
                bool isMat = ve != null && ve.FileType == 8 && (ve.Reference == 0x808031D8 || ve.Reference == 0x8080BAF8 || ve.Reference == 0x80805350 || ve.Reference == 0x80808567);
                if (isMat) { if (curStart < 0) { curStart = o; curLen = 0; } curLen++; if (curLen > bestLen) { bestLen = curLen; bestStart = curStart; } }
                else { curStart = -1; curLen = 0; }
            }
            Console.WriteLine($"  longest material run: {bestLen} mats @ 0x{bestStart:X}");
            for (int k = 0; k < Math.Min(bestLen, 10); k++)
            {
                uint mh = BinaryPrimitives.ReadUInt32LittleEndian(rd.AsSpan(bestStart + k * 4));
                byte[]? mb = Read(mh);
                int texCount = 0;
                if (mb != null)
                    for (int o = 0; o + 4 <= mb.Length; o += 4)
                    {
                        var te2 = E(BinaryPrimitives.ReadUInt32LittleEndian(mb.AsSpan(o)));
                        if (te2 != null && te2.FileType == 32 && te2.FileSubType is 1 or 2 or 3) texCount++;
                    }
                Console.WriteLine($"    [{k}] {mh:X8} ref={E(mh)?.Reference:X8} sameTexRefs={texCount}");
            }
            break;
        }
    }

    var mgr = new PackageManager(PKG_DIR, OODLE); mgr.Index((a, b) => { });
    foreach (uint em in found.Take(2))
    {
        byte[] d = Read(em)!;
        Console.WriteLine($"\n=== SEntityModel {em:X8} size={d.Length} ===");
        Console.WriteLine("  hex[0x00..0x40]: " + Convert.ToHexString(d.AsSpan(0, Math.Min(0x40, d.Length))));
        if (d.Length >= 0xD0)
        {
            long mCount = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(0x10));
            long mRel = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(0x18));
            int mOff = 0x10 + 0x18 + (int)mRel;
            float sx = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(0xA0));
            float sy = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(0xA4));
            float sz = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(0xA8));
            float tx = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(0xB0));
            Console.WriteLine($"  Meshes count={mCount} dataOff=0x{mOff:X}  ModelScale=({sx:F3},{sy:F3},{sz:F3}) ModelTrans.X={tx:F3}");
            float tcsx = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(0xC0));
            float tcsy = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(0xC4));
            float tctx = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(0xC8));
            float tcty = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(0xCC));
            Console.WriteLine($"  TexcoordScale=({tcsx:F5},{tcsy:F5}) TexcoordTrans=({tctx:F5},{tcty:F5})");
            // First mesh buffer refs + parts.
            if (mCount > 0 && mOff + 0x30 <= d.Length)
            {
                uint v1 = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(mOff + 0x00));
                uint v2 = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(mOff + 0x04));
                uint ib = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(mOff + 0x10));
                Console.WriteLine($"  mesh[0] Vertices1={v1:X8}({E(v1)?.FileType}/{E(v1)?.FileSubType}) Vertices2={v2:X8}({E(v2)?.FileType}/{E(v2)?.FileSubType}) Indices={ib:X8}({E(ib)?.FileType}/{E(ib)?.FileSubType})");
                // Diagnose UVs: load both vertex buffers, report strides + raw int16 UV ranges.
                foreach (var (label, vh) in new[] { ("V1", v1), ("V2", v2) })
                {
                    var vb = Tiger.Model.VertexBuffer.Load(mgr, vh);
                    if (vb == null) { Console.WriteLine($"  {label} {vh:X8}: load failed"); continue; }
                    Console.WriteLine($"  {label} {vh:X8} stride=0x{vb.Stride:X} type={vb.Type} verts={vb.VertexCount} providesPos={vb.ProvidesPosition} providesUV={vb.ProvidesTexcoord}");
                    if (vb.ProvidesTexcoord)
                    {
                        float minu = 9, maxu = -9, minv = 9, maxv = -9;
                        int n = Math.Min(vb.VertexCount, 6000);
                        for (int vi = 0; vi < n; vi++)
                            if (vb.Decode(vi, out _, out _, out var uvn) && uvn is System.Numerics.Vector2 uvv)
                            { minu = Math.Min(minu, uvv.X); maxu = Math.Max(maxu, uvv.X); minv = Math.Min(minv, uvv.Y); maxv = Math.Max(maxv, uvv.Y); }
                        Console.WriteLine($"     rawUV(snorm) U[{minu:F3},{maxu:F3}] V[{minv:F3},{maxv:F3}]");
                    }
                }
                long pCount = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(mOff + 0x20));
                long pRel = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(mOff + 0x28));
                int pOff = mOff + 0x20 + 0x18 + (int)pRel;
                Console.WriteLine($"  Parts count={pCount} dataOff=0x{pOff:X}");
                for (int k = 0; k < Math.Min(pCount, 8) && pOff + k * 0x28 + 0x28 <= d.Length; k++)
                {
                    int pe = pOff + k * 0x28;
                    uint mh = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pe + 0x00));
                    short vsi = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(pe + 0x04));
                    short prim = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(pe + 0x06));
                    uint ic = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pe + 0x0C));
                    byte lod = d[pe + 0x21];
                    var mhe = E(mh);
                    Console.WriteLine($"    part[{k}] Material={mh:X8}(t{mhe?.FileType}/{mhe?.FileSubType} ref={mhe?.Reference:X8}) VariantShaderIndex={vsi} prim={prim} idxCount={ic} lod={lod}");
                }
            }
        }
    }
}
else if (cmd == "pmat")
{
    // Dump a material's Pixel-shader texture table (the proper albedo = slot 0), MIDA-style.
    var mgr = new PackageManager(PKG_DIR, OODLE);
    mgr.Index((p, m) => { });
    uint mat = Convert.ToUInt32(args[1], 16);
    byte[]? d = mgr.ReadTag(mat);
    if (d == null) { Console.WriteLine("no material"); return; }
    foreach (var (label, baseOff) in new[] { ("Pixel", 0x278), ("Vertex", 0x58) })
    {
        int f = baseOff + 0x8; // Textures DynamicArray
        if (f + 16 > d.Length) continue;
        long tc = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(f));
        long trel = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(f + 8));
        int toff = f + 0x18 + (int)trel;
        Console.WriteLine($"{label}.Textures count={tc} dataOff=0x{toff:X}");
        for (int k = 0; k < Math.Min(tc, 12) && toff + k * 0x18 + 0x10 <= d.Length; k++)
        {
            int e = toff + k * 0x18;
            uint idx = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(e));
            ulong t64 = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(e + 8));
            ulong t64b = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(e + 0x10));
            uint th = mgr.TryResolveHash64(t64, out uint h) ? h : 0;
            uint thb = mgr.TryResolveHash64(t64b, out uint hb) ? hb : 0;
            string fmt = "?"; bool srgb = false;
            uint show = th != 0 ? th : thb;
            if (show != 0 && mgr.ByTag.TryGetValue(show, out var te)) { try { var hd = mgr.LoadHeader(te); fmt = $"{hd.Width}x{hd.Height} {hd.FormatName}"; srgb = Tiger.TigerTexture.IsSrgb(hd.DxgiFormat); } catch { } }
            Console.WriteLine($"  slot={idx} tex@0x8={th:X8} tex@0x10={thb:X8} {fmt} srgb={srgb}");
        }
    }
}
else if (cmd == "dtex")
{
    var mgr = new PackageManager(PKG_DIR, OODLE);
    mgr.Index((p, m) => { });
    uint th = Convert.ToUInt32(args[1], 16);
    if (!mgr.ByTag.TryGetValue(th, out var te)) { Console.WriteLine("not a texture"); return; }
    var dec = mgr.DecodeThumb(te, 400);
    if (dec is not { } d) { Console.WriteLine("decode failed"); return; }
    using var img = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(d.rgba, d.width, d.height);
    string path = Path.Combine(OUT, $"dtex_{th:X8}.png");
    img.SaveAsPng(path);
    Console.WriteLine($"{th:X8} {d.width}x{d.height} -> {path}");
}
else if (cmd == "ithumb")
{
    // Render a specific model's grid thumbnail (same CPU path as ThumbnailService) to verify UV/texture.
    var mgr = new PackageManager(PKG_DIR, OODLE);
    mgr.Index((p, m) => { });
    uint h = Convert.ToUInt32(args[1], 16);
    var parsed = Tiger.Model.ModelParse.Parse(mgr, mgr.ModelByTag[h]);
    if (parsed == null) { Console.WriteLine("no geometry"); return; }
    var g = parsed.WithVariant(parsed.DefaultVariant);
    var tex = new List<TexSample?>(); TexSample? primary = null;
    foreach (var part in g.Parts)
    {
        TexSample? s = null;
        if (part.MaterialHash != 0 && Tiger.Model.MaterialMap.Albedo(mgr, part.MaterialHash) is uint th
            && mgr.ByTag.TryGetValue(th, out var te) && mgr.DecodeThumb(te, 128) is { } d)
            s = new TexSample(d.rgba, d.width, d.height);
        primary ??= s; tex.Add(s);
    }
    if (primary != null) for (int i = 0; i < tex.Count; i++) tex[i] ??= primary;
    int sz = 256;
    byte[] rgba = Tiger.Model.IsoThumbnail.Render(g, sz, (28, 30, 36), (170, 174, 182),
        Tiger.Model.IsoThumbnail.DefaultAzimuth, Tiger.Model.IsoThumbnail.DefaultElevation, tex);
    using var img = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(rgba, sz, sz);
    string path = Path.Combine(OUT, $"ithumb_{h:X8}.png");
    img.SaveAsPng(path);
    Console.WriteLine($"{h:X8}: {g.Parts.Count} parts -> {path}");
}
else if (cmd == "decstep")
{
    var mgr = new PackageManager(PKG_DIR, OODLE);
    mgr.Index((p, m) => { });
    uint th = Convert.ToUInt32(args[1], 16);
    if (!mgr.ByTag.TryGetValue(th, out var te)) { Console.WriteLine("not a texture"); return; }
    foreach (int t in new[] { 2048, 1024, 512, 256, 128, 64 })
    {
        var d = mgr.DecodeThumb(te, t);
        if (d is not { } dd) { Console.WriteLine($"  target {t}: NULL"); continue; }
        int n = dd.rgba.Length / 4; long r = 0, g = 0, b = 0; int dark = 0;
        for (int i = 0; i < dd.rgba.Length; i += 4) { r += dd.rgba[i]; g += dd.rgba[i + 1]; b += dd.rgba[i + 2]; if (dd.rgba[i] + dd.rgba[i + 1] + dd.rgba[i + 2] < 24) dark++; }
        Console.WriteLine($"  target {t}: {dd.width}x{dd.height} avg=({r / n},{g / n},{b / n}) dark={100.0 * dark / n:F0}%");
    }
}
else if (cmd == "albedo")
{
    // Exercise the real MaterialMap selection path (same lib the app uses).
    var mgr = new PackageManager(PKG_DIR, OODLE);
    mgr.Index((p, m) => { });
    uint mat = Convert.ToUInt32(args[1], 16);
    var px = Tiger.Model.MaterialMap.PixelTextures(mgr, mat);
    Console.WriteLine($"PixelTextures: {string.Join(", ", px.Select(p => $"[{p.index}]{p.tex:X8}"))}");
    Console.WriteLine($"Albedo = {Tiger.Model.MaterialMap.Albedo(mgr, mat):X8}");
    Console.WriteLine($"Normal = {Tiger.Model.MaterialMap.Normal(mgr, mat):X8}");
    Console.WriteLine($"Gstack = {Tiger.Model.MaterialMap.Gstack(mgr, mat):X8}");
}
else if (cmd == "uvscan")
{
    // Characterize entity texcoord buffers: (type,stride) histogram + UV-span when decoded as int16 SNORM.
    // A narrow span (e.g. ~0.1) is the tell-tale of a float16 buffer being misread as int16.
    var mgr = new PackageManager(PKG_DIR, OODLE);
    mgr.Index((p, m) => { });
    int sample = args.Length > 1 ? int.Parse(args[1]) : 600;
    var ents = mgr.Models.Where(m => m.IsEntity).Take(sample).ToList();
    var hist = new Dictionary<string, int>();
    int narrow = 0, wide = 0, examined = 0;
    foreach (var e in ents)
    {
        var (model, resource) = Tiger.Model.EntityMesh.ResolveModel(mgr, e.TagHash);
        if (model is not uint mh) continue;
        byte[]? d = mgr.ReadTag(mh);
        if (d == null || d.Length < 0x130) continue;
        long mc = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(0x10));
        long mr = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(0x18));
        int mo = 0x10 + 0x18 + (int)mr;
        if (mc < 1 || mo + 0x10 > d.Length) continue;
        uint v2 = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(mo + 0x04));
        var vb = Tiger.Model.VertexBuffer.Load(mgr, v2);
        if (vb == null) continue;
        hist[$"type{vb.Type}/stride0x{vb.Stride:X}"] = hist.GetValueOrDefault($"type{vb.Type}/stride0x{vb.Stride:X}") + 1;
        if (!vb.ProvidesTexcoord) continue;
        float mn = 9, mx = -9; int n = Math.Min(vb.VertexCount, 4000);
        for (int vi = 0; vi < n; vi++)
            if (vb.Decode(vi, out _, out _, out var uvn) && uvn is System.Numerics.Vector2 uvv)
            { mn = Math.Min(mn, Math.Min(uvv.X, uvv.Y)); mx = Math.Max(mx, Math.Max(uvv.X, uvv.Y)); }
        examined++;
        if (mx - mn < 0.6) narrow++; else wide++;
    }
    Console.WriteLine("texcoord-buffer (type/stride) histogram:");
    foreach (var kv in hist.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Key} = {kv.Value}");
    Console.WriteLine($"UV span when read as int16: wide(>=0.6)={wide}  narrow(<0.6, likely float16 misread)={narrow}  of {examined} examined");
}
else if (cmd == "emisscan")
{
    // Find entity models whose gstack green channel has strong (>0.5) regions -> would glow.
    var mgr = new PackageManager(PKG_DIR, OODLE);
    mgr.Index((p, m) => { });
    int sample = args.Length > 1 ? int.Parse(args[1]) : 400;
    var ents = mgr.Models.Where(m => m.IsEntity).Take(sample).ToList();
    var hits = new List<(uint model, uint gstack, double frac)>();
    foreach (var e in ents)
    {
        try
        {
            var geom = Tiger.Model.ModelParse.Parse(mgr, e);
            if (geom == null) continue;
            foreach (var part in geom.Parts.Where(p => p.MaterialHash != 0).DistinctBy(p => p.MaterialHash))
            {
                if (Tiger.Model.MaterialMap.Gstack(mgr, part.MaterialHash) is not uint gh) continue;
                if (!mgr.ByTag.TryGetValue(gh, out var te)) continue;
                var d = mgr.Decode(te) ?? mgr.DecodeThumb(te, 256);
                if (d is not { } dd) continue;
                int n = dd.rgba.Length / 4, hi = 0;
                for (int i = 0; i < dd.rgba.Length; i += 4) if (dd.rgba[i + 1] > 140) hi++;
                double frac = (double)hi / n;
                if (frac is > 0.003 and < 0.30) { hits.Add((e.TagHash, gh, frac)); break; }
            }
        }
        catch { }
    }
    foreach (var h in hits.OrderByDescending(h => h.frac).Take(25))
        Console.WriteLine($"{h.model:X8}  gstack={h.gstack:X8}  greenHi={h.frac * 100:F1}%");
    Console.WriteLine($"scanned {ents.Count} entities, {hits.Count} in sparse-emissive range");
}
else if (cmd == "chan")
{
    // Split a texture into R/G/B grayscale PNGs + per-channel stats (identify packed gstack channels).
    var mgr = new PackageManager(PKG_DIR, OODLE);
    mgr.Index((p, m) => { });
    uint th = Convert.ToUInt32(args[1], 16);
    if (!mgr.ByTag.TryGetValue(th, out var te)) { Console.WriteLine("not a texture"); return; }
    var dec = mgr.DecodeThumb(te, 512);
    if (dec is not { } d) { Console.WriteLine("decode failed"); return; }
    int n = d.rgba.Length / 4;
    for (int c = 0; c < 3; c++)
    {
        var g = new byte[d.rgba.Length];
        long sum = 0; int hi = 0;
        for (int i = 0; i < n; i++)
        {
            byte v = d.rgba[i * 4 + c];
            g[i * 4] = g[i * 4 + 1] = g[i * 4 + 2] = v; g[i * 4 + 3] = 255;
            sum += v; if (v > 128) hi++;
        }
        using var img = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(g, d.width, d.height);
        string ch = "RGB"[c].ToString();
        string path = Path.Combine(OUT, $"chan_{th:X8}_{ch}.png");
        img.SaveAsPng(path);
        Console.WriteLine($"  {ch}: avg={sum / n,3}  >128={100.0 * hi / n,5:F1}%  -> {path}");
    }
}
else if (cmd == "mattex")
{
    // Decode a material's textures to PNG + report average colour (find the colourful albedo).
    var mgr = new PackageManager(PKG_DIR, OODLE);
    mgr.Index((p, m) => { });
    uint mat = Convert.ToUInt32(args[1], 16);
    foreach (var ch in Tiger.Model.MaterialMap.Channels(mgr, mat))
    {
        if (!mgr.ByTag.TryGetValue(ch.TexHash, out var te)) continue;
        var dec = mgr.Decode(te);
        if (dec is not { } d) { Console.WriteLine($"{ch.TagId} {ch.Width}x{ch.Height} {ch.Format} srgb={ch.Srgb} (decode failed)"); continue; }
        long r = 0, g = 0, b = 0; int n = d.rgba.Length / 4;
        for (int i = 0; i < d.rgba.Length; i += 4) { r += d.rgba[i]; g += d.rgba[i + 1]; b += d.rgba[i + 2]; }
        // colourfulness = max channel diff
        int ar = (int)(r / n), ag = (int)(g / n), ab = (int)(b / n);
        int chroma = Math.Max(ar, Math.Max(ag, ab)) - Math.Min(ar, Math.Min(ag, ab));
        using var img = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(d.rgba, d.width, d.height);
        string path = Path.Combine(OUT, $"mattex_{ch.TagId}.png");
        img.SaveAsPng(path);
        Console.WriteLine($"{ch.TagId} {ch.Width}x{ch.Height} {ch.Format} srgb={ch.Srgb} avg=({ar},{ag},{ab}) chroma={chroma} -> {path}");
    }
}
else if (cmd == "emesh")
{
    // Parse an entity model end-to-end → OBJ. Validates EntityMesh.
    var mgr = new PackageManager(PKG_DIR, OODLE);
    mgr.Index((p, m) => { });
    uint h = args.Length > 1 ? Convert.ToUInt32(args[1], 16) : 0x80A7BF74;
    var geom = Tiger.Model.EntityMesh.Parse(mgr, h);
    if (geom == null) { Console.WriteLine($"{h:X8}: no entity geometry"); return; }
    var (mn, mx) = geom.Bounds(); var sz = mx - mn;
    Console.WriteLine($"{h:X8}: parts={geom.Parts.Count} verts={geom.VertexCount} tris={geom.TriangleCount} size=({sz.X:F2},{sz.Y:F2},{sz.Z:F2})");
    string objPath = Path.Combine(OUT, $"entity_{h:X8}.obj");
    File.WriteAllText(objPath, Tiger.Model.ObjWriter.ToObj(geom, $"{h:X8}"));
    Console.WriteLine($"   wrote {objPath}");
}
else if (cmd == "mesh")
{
    // Parse a static mesh end-to-end and report stats + write OBJ. Validates the clean-room parser.
    var mgr = new PackageManager(PKG_DIR, OODLE);
    mgr.Index((p, m) => { });
    Console.WriteLine($"Indexed {mgr.Models.Count} static models.");

    var targets = new List<uint>();
    if (args.Length > 1) targets.Add(Convert.ToUInt32(args[1], 16));
    else
    {
        // A spread of sizes from environment packages: smallest, median-ish, largest.
        var ordered = mgr.Models.OrderBy(m => m.Size).ToList();
        if (ordered.Count > 0)
        {
            targets.Add(ordered[0].TagHash);
            targets.Add(ordered[ordered.Count / 2].TagHash);
            targets.Add(ordered[^1].TagHash);
            targets.Add(ordered.First(m => m.Size is >= 0x100 and <= 0x400).TagHash);
        }
    }

    foreach (uint h in targets)
    {
        var geom = Tiger.Model.StaticMesh.Parse(mgr, h);
        if (geom == null) { Console.WriteLine($"{h:X8}: parse failed"); continue; }
        var (min, max) = geom.Bounds();
        var size = max - min;
        Console.WriteLine($"\n{h:X8}: parts={geom.Parts.Count} verts={geom.VertexCount} tris={geom.TriangleCount}");
        Console.WriteLine($"   bounds min=({min.X:F3},{min.Y:F3},{min.Z:F3}) max=({max.X:F3},{max.Y:F3},{max.Z:F3}) size=({size.X:F3},{size.Y:F3},{size.Z:F3})");
        string objPath = Path.Combine(OUT, $"{h:X8}.obj");
        File.WriteAllText(objPath, Tiger.Model.ObjWriter.ToObj(geom, $"{h:X8}"));
        Console.WriteLine($"   wrote {objPath}");
    }
}
else if (cmd == "texmodel")
{
    // Export a model as OBJ + MTL + albedo PNGs (validates material->texture + UVs end-to-end).
    var mgr = new PackageManager(PKG_DIR, OODLE);
    mgr.Index((p, m) => { });
    uint h = args.Length > 1 ? Convert.ToUInt32(args[1], 16)
        : mgr.Models.OrderBy(m => m.Size).First(m => m.Size is >= 0x100 and <= 0x600).TagHash;
    var geom = Tiger.Model.StaticMesh.Parse(mgr, h);
    if (geom == null) { Console.WriteLine("parse failed"); return; }

    string dir = Path.Combine(OUT, $"tex_{h:X8}");
    Directory.CreateDirectory(dir);
    var mtl = new System.Text.StringBuilder();
    var obj = new System.Text.StringBuilder($"mtllib {h:X8}.mtl\n");
    var ci = System.Globalization.CultureInfo.InvariantCulture;
    int vbase = 1;
    var matCache = new Dictionary<uint, string>();
    for (int pi = 0; pi < geom.Parts.Count; pi++)
    {
        var part = geom.Parts[pi];
        string matName = $"mat_{pi}";
        string? tex = null;
        if (part.MaterialHash != 0 && MaterialMap.Albedo(mgr, part.MaterialHash) is uint th && mgr.ByTag.TryGetValue(th, out var te))
        {
            var dec = mgr.Decode(te);
            if (dec is { } d)
            {
                tex = $"{th:X8}.png";
                using var im = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(d.rgba, d.width, d.height);
                im.SaveAsPng(Path.Combine(dir, tex));
            }
        }
        mtl.Append($"newmtl {matName}\nKd 0.8 0.8 0.8\n");
        if (tex != null) mtl.Append($"map_Kd {tex}\n");
        mtl.Append('\n');

        obj.Append($"o part_{pi}\nusemtl {matName}\n");
        foreach (var v in part.Positions) obj.Append($"v {v.X.ToString(ci)} {v.Y.ToString(ci)} {v.Z.ToString(ci)}\n");
        foreach (var t in part.Texcoords) obj.Append($"vt {t.X.ToString(ci)} {t.Y.ToString(ci)}\n");
        for (int i = 0; i + 2 < part.Indices.Count; i += 3)
        {
            int a = vbase + part.Indices[i], b = vbase + part.Indices[i + 1], c = vbase + part.Indices[i + 2];
            obj.Append($"f {a}/{a} {b}/{b} {c}/{c}\n");
        }
        vbase += part.Positions.Count;
    }
    File.WriteAllText(Path.Combine(dir, $"{h:X8}.obj"), obj.ToString());
    File.WriteAllText(Path.Combine(dir, $"{h:X8}.mtl"), mtl.ToString());
    Console.WriteLine($"{h:X8}: {geom.Parts.Count} parts -> {dir}");
}
else if (cmd == "thumb")
{
    // Render iso thumbnails for a spread of models to PNG (visual check of IsoThumbnail).
    var mgr = new PackageManager(PKG_DIR, OODLE);
    mgr.Index((p, m) => { });
    var ordered = mgr.Models.OrderBy(m => m.Size).ToList();
    int n = ordered.Count;
    var picks = new[] { ordered[n / 50], ordered[n / 10], ordered[n / 3], ordered[n / 2], ordered[(int)(n * 0.8)], ordered[n - 2] };
    int sz = 256;
    foreach (var m in picks)
    {
        var geom = Tiger.Model.StaticMesh.Parse(mgr, m.TagHash);
        if (geom == null || geom.VertexCount == 0) { Console.WriteLine($"{m.TagId}: empty"); continue; }
        // Per-part albedo (small mip) for textured thumbnails.
        var tex = new List<TexSample?>();
        foreach (var part in geom.Parts)
        {
            TexSample? s = null;
            if (part.MaterialHash != 0 && MaterialMap.Albedo(mgr, part.MaterialHash) is uint th
                && mgr.ByTag.TryGetValue(th, out var te) && mgr.DecodeThumb(te, 128) is { } d)
                s = new TexSample(d.rgba, d.width, d.height);
            tex.Add(s);
        }
        byte[] rgba = IsoThumbnail.Render(geom, sz, (28, 30, 36), (170, 174, 182),
            IsoThumbnail.DefaultAzimuth, IsoThumbnail.DefaultElevation, tex);
        using var img = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(rgba, sz, sz);
        string path = Path.Combine(OUT, $"thumb_{m.TagId}.png");
        img.SaveAsPng(path);
        Console.WriteLine($"{m.TagId}: {geom.VertexCount} verts {geom.Parts.Count} parts -> {path}");
    }
}
else if (cmd == "stats")
{
    // Coverage: how many static models parse cleanly with non-empty geometry.
    var mgr = new PackageManager(PKG_DIR, OODLE);
    mgr.Index((p, m) => { });
    int total = mgr.Models.Count, ok = 0, empty = 0, fail = 0;
    long verts = 0, tris = 0;
    int sample = Math.Min(total, args.Length > 1 ? int.Parse(args[1]) : 3000);
    var rnd = mgr.Models.Where((_, i) => i % Math.Max(1, total / sample) == 0).ToList();
    foreach (var m in rnd)
    {
        ModelGeometry? g; try { g = Tiger.Model.StaticMesh.Parse(mgr, m.TagHash); } catch { fail++; continue; }
        if (g == null) { fail++; continue; }
        if (g.VertexCount == 0) { empty++; continue; }
        ok++; verts += g.VertexCount; tris += g.TriangleCount;
    }
    Console.WriteLine($"sampled {rnd.Count}/{total}: ok={ok} ({100.0 * ok / rnd.Count:F1}%) empty={empty} fail={fail}");
    if (ok > 0) Console.WriteLine($"avg verts={verts / ok} avg tris={tris / ok}");
}
else if (cmd == "assets")
{
    // Asset grouping: union-find over SAME-PACKAGE textures referenced together by a material.
    // (Same-package filter drops shared cross-package globals/details that would over-merge.)
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var texPkg = new Dictionary<uint, ushort>();
    foreach (var pkg in best.Values)
        foreach (Entry e in pkg.Entries)
            if (e.FileType == 32 && e.FileSubType is 1 or 2 or 3) texPkg[pkg.TagHash(e.Index)] = pkg.PkgId;

    var h64map = new Dictionary<ulong, uint>();
    foreach (string f in files)
        foreach (var (h64, h32) in ReadH64(f))
            if (texPkg.ContainsKey(h32)) h64map[h64] = h32;

    var parent = new Dictionary<uint, uint>();
    uint Find(uint x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
    void Union(uint a, uint b) { parent.TryAdd(a, a); parent.TryAdd(b, b); uint ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }
    foreach (var t in texPkg.Keys) parent[t] = t;

    var matClasses = new HashSet<uint> { 0x808031D8, 0x80805350, 0x80808567, 0x8080B9BA, 0x80808490, 0x8080AE01, 0x8080BEF0 };
    int materials = 0;
    // Pass 1: collect each material's same-package texture set + per-texture material-use count.
    var matSets = new List<List<uint>>();
    var useCount = new Dictionary<uint, int>();
    foreach (var pkg in best.Values)
        foreach (Entry e in pkg.Entries)
        {
            if (e.FileType != 8 || !matClasses.Contains(e.Reference)) continue;
            if (e.FileSize < 8 || e.FileSize > 65536) continue;
            byte[] d; try { d = pkg.ReadEntry(e.Index); } catch { continue; }
            var same = new List<uint>();
            for (int o = 0; o + 4 <= d.Length; o += 4)
            {
                uint v32 = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
                uint? tex = texPkg.ContainsKey(v32) ? v32 : null;
                if (tex == null && o + 8 <= d.Length)
                {
                    ulong v64 = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o));
                    if (h64map.TryGetValue(v64, out uint tt)) tex = tt;
                }
                if (tex is uint tv && texPkg[tv] == pkg.PkgId) same.Add(tv);
            }
            if (same.Count >= 2) { materials++; matSets.Add(same); foreach (var t in same.Distinct()) useCount[t] = useCount.GetValueOrDefault(t) + 1; }
        }
    // Pass 2: union, but don't link THROUGH a texture shared by many materials (a shared mask).
    const int ShareCap = 12;
    foreach (var same in matSets)
    {
        var linkable = same.Where(t => useCount[t] <= ShareCap).ToList();
        for (int i = 1; i < linkable.Count; i++) Union(linkable[0], linkable[i]);
    }

    // component sizes
    var comp = new Dictionary<uint, int>();
    foreach (var t in texPkg.Keys) comp[Find(t)] = comp.GetValueOrDefault(Find(t)) + 1;
    var sizeHist = new Dictionary<int, int>();
    foreach (var c in comp.Values) sizeHist[c] = sizeHist.GetValueOrDefault(c) + 1;
    int multi = comp.Count(c => c.Value >= 2), inGroups = comp.Where(c => c.Value >= 2).Sum(c => c.Value);
    Console.WriteLine($"materials used={materials} ({sw.ElapsedMilliseconds} ms)");
    Console.WriteLine($"asset groups (>=2 textures)={multi}, textures in groups={inGroups}/{texPkg.Count} ({100.0*inGroups/texPkg.Count:F1}%)");
    Console.WriteLine($"singles={comp.Count(c => c.Value == 1)}  total units={comp.Count}");
    Console.WriteLine("group-size histogram: " + string.Join(", ", sizeHist.OrderBy(k => k.Key).Take(20).Select(k => $"{k.Key}->{k.Value}")));
    Console.WriteLine($"largest group: {sizeHist.Keys.Max()} textures");
}
else if (cmd == "groups2")
{
    // Full grouping using BOTH hash32 (same-package) and hash64 (cross-package) references.
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var texSet = new HashSet<uint>();
    foreach (var pkg in best.Values)
        foreach (Entry e in pkg.Entries)
            if (e.FileType == 32 && e.FileSubType is 1 or 2 or 3) texSet.Add(pkg.TagHash(e.Index));

    // Global hash64 -> hash32 map (only entries that resolve to a texture).
    var h64map = new Dictionary<ulong, uint>();
    foreach (string f in files)
    {
        foreach (var (h64, h32) in ReadH64(f))
            if (texSet.Contains(h32)) h64map[h64] = h32;
    }
    Console.WriteLine($"hash64 map: {h64map.Count} entries point at textures ({sw.ElapsedMilliseconds} ms)");

    // texture hash -> (pkg, idx) for example printing
    var texInfoMap = new Dictionary<uint, (TigerPackage pkg, int idx)>();
    foreach (var pkg in best.Values)
        foreach (Entry e in pkg.Entries)
            if (e.FileType == 32 && e.FileSubType is 1 or 2 or 3) texInfoMap[pkg.TagHash(e.Index)] = (pkg, e.Index);

    var grouped = new HashSet<uint>();
    var texUseCount = new Dictionary<uint, int>();
    var sizeHist = new Dictionary<int, int>();
    var classHist = new Dictionary<uint, int>();
    int examplesShown = 0;
    var matClasses = new HashSet<uint> { 0x808031D8, 0x80805350, 0x80808567, 0x8080B9BA, 0x80808490, 0x8080AE01, 0x8080BEF0 };
    int materials = 0, reads = 0;
    foreach (var pkg in best.Values)
        foreach (Entry e in pkg.Entries)
        {
            if (e.FileType != 8 || !matClasses.Contains(e.Reference)) continue;
            if (e.FileSize < 8 || e.FileSize > 65536) continue;
            reads++;
            byte[] d;
            try { d = pkg.ReadEntry(e.Index); } catch { continue; }
            var refs = new HashSet<uint>();
            for (int o = 0; o + 4 <= d.Length; o += 4)
            {
                uint v32 = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
                if (texSet.Contains(v32)) refs.Add(v32);
                if (o + 8 <= d.Length)
                {
                    ulong v64 = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o));
                    if (h64map.TryGetValue(v64, out uint t)) refs.Add(t);
                }
            }
            if (refs.Count == 0) continue;
            materials++;
            classHist[e.Reference] = classHist.GetValueOrDefault(e.Reference) + 1;
            sizeHist[refs.Count] = sizeHist.GetValueOrDefault(refs.Count) + 1;
            foreach (var rr in refs) { grouped.Add(rr); texUseCount[rr] = texUseCount.GetValueOrDefault(rr) + 1; }
            if (examplesShown < 6 && e.Reference == 0x808031D8 && refs.Count is >= 3 and <= 8)
            {
                Console.WriteLine($"\nMaterial {pkg.TagHash(e.Index):X8} ({pkg.Name}) -> {refs.Count} textures:");
                foreach (var rr in refs)
                {
                    if (!texInfoMap.TryGetValue(rr, out var ti)) { Console.WriteLine($"    {rr:X8}"); continue; }
                    try { var hh = TigerTexture.ParseHeader(ti.pkg.ReadEntry(ti.idx)); Console.WriteLine($"    {rr:X8}  {hh.FormatName,-16} {hh.Width}x{hh.Height}  ({TextureEntry.DeriveCategory(ti.pkg.Name)})"); }
                    catch { Console.WriteLine($"    {rr:X8}"); }
                }
                examplesShown++;
            }
        }
    Console.WriteLine($"scanned {reads} type-8 tags, {materials} reference textures ({sw.ElapsedMilliseconds} ms)");
    Console.WriteLine($"textures grouped = {grouped.Count} / {texSet.Count} ({100.0 * grouped.Count / texSet.Count:F1}%)");
    Console.WriteLine($"80A68146 grouped? {grouped.Contains(0x80A68146)}");
    Console.WriteLine("group sizes: " + string.Join(", ", sizeHist.OrderBy(k => k.Key).Take(15).Select(k => $"{k.Key}->{k.Value}")));
    Console.WriteLine("ref-classes: " + string.Join(", ", classHist.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key:X8}={k.Value}")));
    // how shared are textures across materials?
    int sharedHeavily = texUseCount.Count(k => k.Value >= 10);
    int unique1 = texUseCount.Count(k => k.Value == 1);
    Console.WriteLine($"texture sharing: used-once={unique1}, used>=10 materials={sharedHeavily}, max={texUseCount.Values.DefaultIfEmpty(0).Max()}");
}
else if (cmd == "groups")
{
    // Full-catalogue grouping via the material class (entry.Reference == 0x80805350).
    const uint MaterialClass = 0x80805350;
    var texSet = new HashSet<uint>();
    var texFmt = new Dictionary<uint, (ushort fmt, int w, int h)>();
    foreach (var pkg in best.Values)
        foreach (Entry e in pkg.Entries)
            if (e.FileType == 32 && e.FileSubType is 1 or 2 or 3) texSet.Add(pkg.TagHash(e.Index));

    int matCount = 0, matWithTex = 0;
    var grouped = new HashSet<uint>();
    var sizeHist = new Dictionary<int, int>();
    int srgbFirst = 0, hasSrgb = 0, examples = 0;

    foreach (var pkg in best.Values)
        foreach (Entry e in pkg.Entries)
        {
            if (e.Reference != MaterialClass) continue;
            matCount++;
            byte[] d;
            try { d = pkg.ReadEntry(e.Index); } catch { continue; }
            var refs = new List<uint>();
            var seen = new HashSet<uint>();
            for (int o = 0; o + 4 <= d.Length; o += 4)
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
                if (texSet.Contains(v) && seen.Add(v)) refs.Add(v);
            }
            if (refs.Count == 0) continue;
            matWithTex++;
            sizeHist[refs.Count] = sizeHist.GetValueOrDefault(refs.Count) + 1;
            foreach (var rr in refs) grouped.Add(rr);
        }

    Console.WriteLine($"\nmaterials(class {MaterialClass:X8})={matCount}  withTextures={matWithTex}");
    Console.WriteLine($"textures grouped={grouped.Count} / {texSet.Count} ({100.0 * grouped.Count / texSet.Count:F1}%)");
    Console.WriteLine("group sizes: " + string.Join(", ", sizeHist.OrderBy(k => k.Key).Select(k => $"{k.Key}->{k.Value}")));
}
else if (cmd == "mat")
{
    // Find tags that reference multiple texture headers (candidate materials) and inspect groups.
    var texSet = new HashSet<uint>();
    var texLoc = new Dictionary<uint, (TigerPackage pkg, int idx)>();
    foreach (var pkg in best.Values)
        foreach (Entry e in pkg.Entries)
            if (e.FileType == 32 && e.FileSubType is 1 or 2 or 3)
            { uint hsh = pkg.TagHash(e.Index); texSet.Add(hsh); texLoc[hsh] = (pkg, e.Index); }

    string catFilter = args.Length > 1 ? args[1] : "sr_gear";
    bool global = catFilter == "global";
    var groupSizes = new Dictionary<int, int>();
    var matTypes = new Dictionary<string, int>();
    int examples = 0, materials = 0, reads = 0;
    const int readCap = 5_000_000;
    var groupedTex = new HashSet<uint>();

    foreach (var pkg in best.Values)
    {
        if (!global && !TextureEntry.DeriveCategory(pkg.Name).Equals(catFilter, StringComparison.OrdinalIgnoreCase)) continue;
        if (reads >= readCap) break;
        foreach (Entry e in pkg.Entries)
        {
            if (e.FileType == 32 && e.FileSubType is 1 or 2 or 3) continue;
            if (e.FileSize < 8 || e.FileSize > 65536) continue;
            if (reads++ >= readCap) break;
            byte[] d;
            try { d = pkg.ReadEntry(e.Index); } catch { continue; }
            var refs = new List<uint>();
            for (int o = 0; o + 4 <= d.Length; o += 4)
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
                if (texSet.Contains(v)) refs.Add(v);
            }
            var distinct = refs.Distinct().ToList();
            if (distinct.Count < 2) continue;
            materials++;
            foreach (var rr in distinct) groupedTex.Add(rr);
            groupSizes[distinct.Count] = groupSizes.GetValueOrDefault(distinct.Count) + 1;
            string mt = $"{e.FileType}/{e.FileSubType} ref={e.Reference:X8}";
            matTypes[mt] = matTypes.GetValueOrDefault(mt) + 1;
            if (examples < 8 && distinct.Count is >= 2 and <= 6)
            {
                Console.WriteLine($"\nMaterial {pkg.TagHash(e.Index):X8} type={mt} sz={e.FileSize} refs {distinct.Count} textures:");
                foreach (var rr in distinct)
                {
                    var (tp, ti) = texLoc[rr];
                    try { var hh = TigerTexture.ParseHeader(tp.ReadEntry(ti)); Console.WriteLine($"    {rr:X8}  {hh.FormatName,-16} {hh.Width}x{hh.Height}"); }
                    catch { Console.WriteLine($"    {rr:X8}  (parse err)"); }
                }
                examples++;
            }
        }
    }
    int totalTex = texSet.Count(hsh => texLoc[hsh].pkg.Name.Contains(catFilter, StringComparison.OrdinalIgnoreCase) || TextureEntry.DeriveCategory(texLoc[hsh].pkg.Name).Equals(catFilter, StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"\n[{catFilter}] materials={materials}  textures grouped={groupedTex.Count}");
    Console.WriteLine("group sizes: " + string.Join(", ", groupSizes.OrderBy(k => k.Key).Select(k => $"{k.Key}->{k.Value}")));
    Console.WriteLine("material entry types: " + string.Join(", ", matTypes.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}")));
}
else if (cmd == "dump")
{
    // Dump full header bytes for a few working vs failing textures to compare buffer refs.
    var byHash = new Dictionary<uint, (TigerPackage pkg, int idx)>();
    foreach (var pkg in best.Values)
        for (int i = 0; i < pkg.Entries.Count; i++) byHash[pkg.TagHash(i)] = (pkg, i);

    string catFilter = args.Length > 1 ? args[1] : "ui_pvp";
    int shown = 0;
    foreach (var pkg in best.Values)
    {
        if (!TextureEntry.DeriveCategory(pkg.Name).Equals(catFilter, StringComparison.OrdinalIgnoreCase)) continue;
        foreach (Entry e in pkg.Entries)
        {
            if (!(e.FileType == 32 && e.FileSubType is 1 or 2 or 3)) continue;
            byte[] d;
            try { d = pkg.ReadEntry(e.Index); } catch { continue; }
            if (d.Length < 0x40) continue;
            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x20));
            if (magic != 0xCAFE) continue;
            uint bufRef = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x3C));
            bool resolves = bufRef != 0xFFFFFFFF && byHash.ContainsKey(bufRef);
            // entry.Reference (first u32 of entry row) — Tiger's tag->tag link.
            uint entRef = e.Reference;
            string entInfo = "no";
            if (byHash.TryGetValue(entRef, out var loc2))
                entInfo = $"{loc2.pkg.Name}#{loc2.idx} type={loc2.pkg.Entries[loc2.idx].FileType}/{loc2.pkg.Entries[loc2.idx].FileSubType} sz={loc2.pkg.Entries[loc2.idx].FileSize}";
            Console.WriteLine($"\n{pkg.TagHash(e.Index):X8} {pkg.Name} sub={e.FileSubType} len={d.Length} dataSz={BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0)):X8} bufRef={bufRef:X8} resolves={resolves}");
            Console.WriteLine($"  entry.Reference={entRef:X8} -> {entInfo}");
            Console.WriteLine("  " + Convert.ToHexString(d.AsSpan(0, 64)));
            if (++shown >= 6) return;
        }
    }
}
else if (cmd == "stats")
{
    var mgr = new PackageManager(PKG_DIR, OODLE);
    mgr.Index((p, m) => { });
    int total = mgr.Textures.Count, ok = 0, unsupported = 0, noPixels = 0, decErr = 0;
    var failByCat = new Dictionary<string, int>();
    var okByFmt = new Dictionary<string, int>();
    foreach (var t in mgr.Textures)
    {
        TexHeader h;
        try { h = mgr.LoadHeader(t); } catch { decErr++; continue; }
        if (!TigerTexture.IsSupported(h.DxgiFormat)) { unsupported++; Bump(failByCat, t.Category); continue; }
        byte[]? px;
        try { px = mgr.ReadPixels(t, out _); } catch { px = null; }
        if (px == null) { noPixels++; Bump(failByCat, t.Category); continue; }
        var rgba = TigerTexture.DecodeRgba(h, px);
        if (rgba == null) { decErr++; Bump(failByCat, t.Category); continue; }
        ok++; Bump(okByFmt, h.FormatName);
    }
    Console.WriteLine($"\nTotal {total}: OK={ok} ({100.0*ok/total:F1}%)  unsupportedFmt={unsupported}  noPixels={noPixels}  decErr={decErr}");
    Console.WriteLine("OK by format: " + string.Join(", ", okByFmt.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}")));
    Console.WriteLine("Failures by category: " + string.Join(", ", failByCat.OrderByDescending(k => k.Value).Take(12).Select(k => $"{k.Key}={k.Value}")));

    static void Bump(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;
}
else if (cmd == "decode")
{
    var dxgi = KnownDxgi();
    // Cross-package tag-hash -> (pkg, index) map for resolving the large pixel buffer @0x3C.
    Console.Error.WriteLine("Building tag-hash map...");
    var byHash = new Dictionary<uint, (TigerPackage pkg, int idx)>();
    foreach (var pkg in best.Values)
        for (int i = 0; i < pkg.Entries.Count; i++)
            byHash[pkg.TagHash(i)] = (pkg, i);

    // Gather texture headers (32/1 main, 32/2 BC6H, 32/3 small).
    var headers = new List<(TigerPackage pkg, Entry e)>();
    foreach (var pkg in best.Values)
        foreach (Entry e in pkg.Entries)
            if (e.FileType == 32 && e.FileSubType is 1 or 2 or 3) headers.Add((pkg, e));
    Console.Error.WriteLine($"{headers.Count} texture headers found.");

    int want = args.Length > 1 ? int.Parse(args[1]) : 30;
    int dumped = 0, inline = 0, resolved = 0, failed = 0;
    var seenFmt = new HashSet<ushort>();
    // Spread the sample so we hit varied formats/packages.
    for (int n = 0; n < headers.Count && dumped < want; n++)
    {
        var (pkg, e) = headers[(int)((long)n * headers.Count / Math.Min(headers.Count, want * 40)) % headers.Count];
        byte[] hd;
        try { hd = pkg.ReadEntry(e.Index); } catch { continue; }
        if (hd.Length < 0x40) continue;
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(hd.AsSpan(0x20));
        if (magic != 0xCAFE) continue;
        ushort fmt = BinaryPrimitives.ReadUInt16LittleEndian(hd.AsSpan(0x04));
        ushort w = BinaryPrimitives.ReadUInt16LittleEndian(hd.AsSpan(0x22));
        ushort h = BinaryPrimitives.ReadUInt16LittleEndian(hd.AsSpan(0x24));
        uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(hd.AsSpan(0x00));
        uint bufRef = BinaryPrimitives.ReadUInt32LittleEndian(hd.AsSpan(0x3C));
        if (!dxgi.ContainsKey(fmt)) continue;

        // Prefer varied formats early.
        if (dumped > 8 && seenFmt.Contains(fmt) && (dumped % 3 != 0)) continue;

        byte[]? pixels = null;
        string src;
        if (bufRef != 0xFFFFFFFF && byHash.TryGetValue(bufRef, out var loc))
        {
            try { pixels = loc.pkg.ReadEntry(loc.idx); resolved++; src = $"buf {bufRef:X8} ({loc.pkg.Name}#{loc.idx})"; }
            catch { failed++; continue; }
        }
        else
        {
            // No external buffer: pixel data may follow the header inline.
            if (hd.Length > 0x40) { pixels = hd.AsSpan(0x40).ToArray(); inline++; src = "inline"; }
            else { failed++; continue; }
        }

        string tag = $"{pkg.TagHash(e.Index):X8}";
        string fmtName = dxgi[fmt];
        try
        {
            var rgba = TexDecode.DecodeTopMip(fmt, pixels!, w, h);
            if (rgba == null) { failed++; Console.WriteLine($"  SKIP {tag} {fmtName} {w}x{h} (no decoder)"); continue; }
            TexDecode.WritePng(Path.Combine(OUT, $"{tag}_{fmtName}_{w}x{h}.png"), rgba, w, h);
            // Also dump a .dds (header + full buffer) for external cross-check.
            File.WriteAllBytes(Path.Combine(OUT, $"{tag}_{fmtName}_{w}x{h}.dds"), TexDecode.BuildDds(fmt, w, h, pixels!));
            seenFmt.Add(fmt);
            dumped++;
            Console.WriteLine($"  OK   {tag} {fmtName,-16} {w,5}x{h,-5} dataSz={dataSize,-9} bufSz={pixels!.Length,-9} src={src}");
        }
        catch (Exception ex) { failed++; Console.WriteLine($"  ERR  {tag} {fmtName} {w}x{h}: {ex.Message}"); }
    }
    Console.WriteLine($"\nDumped {dumped} PNGs to {OUT}  (resolved={resolved} inline={inline} failed={failed})");
}

// Parse a package's hash64 table -> (hash64, hash32) pairs (v53 / d2_beyondlight layout).
static IEnumerable<(ulong h64, uint h32)> ReadH64(string file)
{
    byte[] hdr = new byte[0x130];
    using var fs = File.OpenRead(file);
    if (fs.Read(hdr, 0, hdr.Length) < hdr.Length) yield break;
    if (BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(0)) != 53) yield break;
    uint size = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(0xB8));
    uint offset = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(0xBC));
    if (size == 0 || offset == 0) yield break;
    byte[] tmp = new byte[16];
    fs.Seek(offset + 0x10, SeekOrigin.Begin);
    fs.Read(tmp, 0, 8);
    long rel = BinaryPrimitives.ReadInt64LittleEndian(tmp);
    long start = (long)offset + 0x20 + rel;
    fs.Seek(start, SeekOrigin.Begin);
    byte[] buf = new byte[(long)size * 16];
    int read = 0; while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n == 0) break; read += n; }
    for (int i = 0; i + 16 <= read; i += 16)
        yield return (BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(i)),
                      BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i + 8)));
}

static Dictionary<ushort, string> KnownDxgi() => new()
{
    [28] = "R8G8B8A8_UNORM", [29] = "R8G8B8A8_SRGB", [87] = "B8G8R8A8_UNORM", [88] = "B8G8R8A8_SRGB",
    [10] = "R16G16B16A16_FLOAT", [2] = "R32G32B32A32_FLOAT", [61] = "R8_UNORM", [49] = "R8G8_UNORM",
    [71] = "BC1_UNORM", [72] = "BC1_SRGB", [74] = "BC2_UNORM", [75] = "BC2_SRGB",
    [77] = "BC3_UNORM", [78] = "BC3_SRGB", [80] = "BC4_UNORM", [81] = "BC4_SNORM",
    [83] = "BC5_UNORM", [84] = "BC5_SNORM", [95] = "BC6H_UF16", [96] = "BC6H_SF16",
    [98] = "BC7_UNORM", [99] = "BC7_SRGB",
};
