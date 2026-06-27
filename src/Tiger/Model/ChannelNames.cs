using System.IO;

namespace Tiger.Model;

/// <summary>
/// Resolves Tiger "channel" hashes to names. The engine hashes a string with 32-bit FNV-1
/// (seed 0x811c9dc5, prime 0x01000193) — verified against known channels (fp_iron_sight = 0x43A361CE,
/// is_local_player = 0x8A4DE2D7, sun_glow_color = 0x056007C7, fog_decay_color = 0x9EC7A5E8).
///
/// The full name list isn't in the game tags — Deimos/MIDA ship an external wordlist. We seed a small
/// built-in set of verified names and additionally load a user-supplied <c>wordlist.txt</c> (one name per
/// line) if present next to the exe or in %AppData%\ModelCodex, so coverage can grow without code changes.
/// </summary>
public static class ChannelNames
{
    private static readonly Dictionary<uint, string> Map = new();
    private static bool _loaded;

    public static uint Fnv(string s)
    {
        uint v = 0x811c9dc5;
        foreach (char c in s) { v *= 0x01000193; v ^= c; }
        return v;
    }

    /// <summary>The channel's name, or "unk_XXXXXXXX" when not in the wordlist.</summary>
    public static string Resolve(uint hash)
    {
        EnsureLoaded();
        return Map.TryGetValue(hash, out var n) ? n : $"unk_{hash:X8}";
    }

    public static bool IsKnown(uint hash) { EnsureLoaded(); return Map.ContainsKey(hash); }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        // Verified built-ins.
        foreach (var s in new[]
        {
            "fp_iron_sight", "parent.fp_iron_sight", "is_local_player",
            "sun_glow_color", "fog_decay_color",
        })
            Map[Fnv(s)] = s;

        // Optional external wordlists (community-sourced); first found wins per hash.
        foreach (var path in WordlistPaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path))
                {
                    var name = line.Trim();
                    if (name.Length == 0) continue;
                    Map.TryAdd(Fnv(name), name);
                }
            }
            catch { }
        }
    }

    private static IEnumerable<string> WordlistPaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "wordlist.txt");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModelCodex", "wordlist.txt");
    }
}
