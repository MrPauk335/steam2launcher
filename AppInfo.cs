using System.IO;
using System.Windows;

namespace Steam2Launcher;

public static class AppInfo
{
    public static string BaseDir { get; } = AppContext.BaseDirectory;
    public static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Steam2Launcher");
    public static string ConfigPath => Path.Combine(DataFolder, "config.json");
    public static string PostPath { get; set; } = "";
}
