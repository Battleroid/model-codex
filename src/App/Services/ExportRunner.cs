using System.IO;
using Tiger.Model;

namespace ModelCodex.App.Services;

/// <summary>Bridges the UI to <see cref="ModelExporter"/> using the user's configured format/dir/textures.</summary>
public static class ExportRunner
{
    /// <summary>Export one model; returns the written path (throws on failure).</summary>
    public static string ExportOne(ModelEntry entry)
    {
        var st = AppState.Instance;
        var mgr = st.Manager ?? throw new InvalidOperationException("not indexed");
        var geom = ModelParse.Parse(mgr, entry) ?? throw new Exception("parse failed");
        string dir = Path.Combine(st.EffectiveExportDir, Sanitize(entry.Category));
        return ModelExporter.Export(mgr, geom, entry.TagId, dir, st.Config.ExportFormat, st.Config.ExportTextures);
    }

    /// <summary>Bulk export, namespaced by category. Returns the count written.</summary>
    public static async Task<int> ExportBulk(IReadOnlyList<ModelEntry> entries,
        Action<double, string> progress, CancellationToken ct)
    {
        var st = AppState.Instance;
        var mgr = st.Manager ?? throw new InvalidOperationException("not indexed");
        string fmt = st.Config.ExportFormat;
        bool tex = st.Config.ExportTextures;
        string root = st.EffectiveExportDir;
        int done = 0, ok = 0;

        await Task.Run(() =>
        {
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var geom = ModelParse.Parse(mgr, e);
                    if (geom is { Parts.Count: > 0 })
                    {
                        ModelExporter.Export(mgr, geom, e.TagId, Path.Combine(root, Sanitize(e.Category)), fmt, tex);
                        ok++;
                    }
                }
                catch { /* skip a bad model */ }
                done++;
                if (done % 8 == 0 || done == entries.Count)
                    progress(done / (double)entries.Count, $"Exporting {done}/{entries.Count}");
            }
        }, ct);
        return ok;
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length == 0 ? "models" : name;
    }
}
