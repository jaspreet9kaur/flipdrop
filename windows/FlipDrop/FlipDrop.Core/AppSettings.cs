using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlipDrop.Core;

public enum EntryEdge { Right, Left, Top, Bottom }

/// <summary>User-visible preferences, stored as plain JSON (no secrets here).</summary>
public sealed class AppSettings
{
    public string SaveFolder { get; set; } = AppPaths.DefaultReceiveDir;
    public string Effect { get; set; } = "Rainbow"; // Rainbow | Portal | Comet | Ripple

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EntryEdge Edge { get; set; } = EntryEdge.Right;

    public bool PlaySound { get; set; } = true;

    /// <summary>Landing animation intensity. "Full" | "Subtle" | "Off". Missing/unknown → Full.</summary>
    public string Animation { get; set; } = "Full";

    /// <summary>UI theme. "System" | "Light" | "Dark". Missing/unknown → System.</summary>
    public string Theme { get; set; } = "System";

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile));
                if (s is not null) return s;
            }
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureDataDir();
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Opts));
        }
        catch { /* non-fatal */ }
    }
}
