using System.IO;
using System.Text.Json;

namespace Steam2Launcher;

public static class Storage
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public static List<SavedEntry> LoadSaved()
    {
        if (!File.Exists(AppInfo.ConfigPath)) return new List<SavedEntry>();
        try
        {
            var json = File.ReadAllText(AppInfo.ConfigPath);
            return JsonSerializer.Deserialize<List<SavedEntry>>(json, JsonOpts) ?? new();
        }
        catch { return new List<SavedEntry>(); }
    }

    public static void Save(List<SavedEntry> entries)
    {
        Directory.CreateDirectory(AppInfo.DataFolder);
        File.WriteAllText(AppInfo.ConfigPath, JsonSerializer.Serialize(entries, JsonOpts));
    }

    private static string SettingsPath => Path.Combine(AppInfo.DataFolder, "settings.json");

    public static AppSettings LoadSettings()
    {
        if (File.Exists(SettingsPath))
        {
            try
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
            catch { }
        }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings s)
    {
        Directory.CreateDirectory(AppInfo.DataFolder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, JsonOpts));
    }
}

public class AppSettings
{
    public string InstallRoot { get; set; } = "";
    public string PostPath { get; set; } = "";
    public string Theme { get; set; } = "Dark";

    /// <summary>HTTP Basic credentials per host: host -> "user:pass".</summary>
    public Dictionary<string, string> HostCredentials { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class SavedEntry
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string DirectUrl { get; set; } = "";
    public string LaunchArgs { get; set; } = "";
    public string InstallDir { get; set; } = "";
    public string ExePath { get; set; } = "";

    /// <summary>Per-build update repo (owner/repo) for delta releases, e.g. "MrPauk335/fstop-builds".</summary>
    public string Repo { get; set; } = "";

    /// <summary>Base archive version the installed build was derived from.</summary>
    public int BaseVersion { get; set; }

    /// <summary>Version of the installed build (from the embedded buildinfo.json / last applied delta).</summary>
    public int InstalledVersion { get; set; }
}
