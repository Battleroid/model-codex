using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tiger;

namespace ModelCodex.App.Services;

/// <summary>
/// Eager singleton holding config, the package manager, and shared caches.
/// Mirrors texture-codex's AppState (no DI; VMs reach <see cref="Instance"/> directly).
/// </summary>
public sealed class AppState
{
    public static AppState Instance { get; } = new();

    public AppConfig Config { get; private set; } = new();
    public PackageManager? Manager { get; private set; }

    private readonly string _dir;
    private readonly string _configPath;
    private readonly object _saveLock = new();

    private AppState()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModelCodex");
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "config.json");
        Load();
    }

    public string DataDir => _dir;

    // ---- Derived paths ----
    public string? PackagesDir => string.IsNullOrEmpty(Config.GameDir) ? null : Path.Combine(Config.GameDir!, "packages");
    public string? OodleDll => string.IsNullOrEmpty(Config.GameDir) ? null : Path.Combine(Config.GameDir!, "bin", "x64", "oo2core_9_win64.dll");

    public bool GameDirValid =>
        !string.IsNullOrEmpty(Config.GameDir) &&
        PackagesDir is { } p && Directory.Exists(p) &&
        OodleDll is { } o && File.Exists(o);

    public string EffectiveExportDir =>
        !string.IsNullOrWhiteSpace(Config.ExportDir)
            ? Config.ExportDir!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ModelCodex");

    // ---- Config persistence ----
    public void Load()
    {
        try
        {
            if (File.Exists(_configPath))
                Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configPath)) ?? new();
        }
        catch { Config = new(); }

        if (string.IsNullOrEmpty(Config.GameDir))
        {
            string guess = @"A:\Steam\steamapps\common\Marathon";
            if (Directory.Exists(guess)) Config.GameDir = guess;
        }
    }

    /// <summary>Merge-preserving save (keeps unknown keys), atomic, lock-guarded.</summary>
    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                JsonObject root;
                try
                {
                    root = File.Exists(_configPath)
                        ? JsonNode.Parse(File.ReadAllText(_configPath)) as JsonObject ?? new()
                        : new();
                }
                catch { root = new(); }
                root["GameDir"] = Config.GameDir;
                root["ExportDir"] = Config.ExportDir;
                root["ExportFormat"] = Config.ExportFormat;
                root["ExportTextures"] = Config.ExportTextures;
                root["Lookdev"] = Config.Lookdev;
                root["IsometricByDefault"] = Config.IsometricByDefault;
                root["PreviewBg"] = Config.PreviewBg;
                root["FlatShading"] = Config.FlatShading;
                File.WriteAllText(_configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    public void SetGameDir(string? v) { Config.GameDir = v; Save(); }
    public void SetExportDir(string? v) { Config.ExportDir = v; Save(); }
    public void SetExportFormat(string v) { Config.ExportFormat = v; Save(); }
    public void SetExportTextures(bool v) { Config.ExportTextures = v; Save(); }
    public void SetLookdev(string v) { Config.Lookdev = v; Save(); }
    public void SetIsometricByDefault(bool v) { Config.IsometricByDefault = v; Save(); }
    public void SetPreviewBg(string v) { Config.PreviewBg = v; Save(); }
    public void SetFlatShading(bool v) { Config.FlatShading = v; Save(); }

    /// <summary>Build the package/model index (call off the UI thread).</summary>
    public PackageManager BuildIndex(Action<double, string>? progress = null)
    {
        var mgr = new PackageManager(PackagesDir!, OodleDll!);
        mgr.Index(progress);
        Manager = mgr;
        return mgr;
    }
}
