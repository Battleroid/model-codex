namespace ModelCodex.App.Services;

/// <summary>Persisted user settings (see <see cref="AppState"/> for load/save).</summary>
public sealed class AppConfig
{
    public string? GameDir { get; set; }
    public string? ExportDir { get; set; }

    /// <summary>Default model export format: "glb" | "obj" | "fbx" | "stl".</summary>
    public string ExportFormat { get; set; } = "glb";
    /// <summary>Export the model's mapped textures alongside it.</summary>
    public bool ExportTextures { get; set; } = true;

    /// <summary>Default shading mode in previews/tabs: "lookdev" | "shaded" | "albedo" | "normal" | "wire".</summary>
    public string Lookdev { get; set; } = "lookdev";
    /// <summary>Open model tabs in isometric projection by default (else perspective).</summary>
    public bool IsometricByDefault { get; set; } = false;
    /// <summary>Preview backdrop colour as "#RRGGBB", persisted across sessions.</summary>
    public string PreviewBg { get; set; } = "#101014";
    /// <summary>Smooth (averaged) vs flat (faceted) shading in previews.</summary>
    public bool FlatShading { get; set; } = false;
    /// <summary>Slowly rotate each grid tile's thumbnail as a live spinning preview (off by default).</summary>
    public bool SpinPreviews { get; set; } = false;
}
