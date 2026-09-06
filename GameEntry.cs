using System.IO;

namespace Steam2Launcher;

public enum GameStatus
{
    NotDownloaded,
    Downloading,
    Downloaded,
    Error
}

public class GameEntry
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
    public string DirectUrl { get; set; } = "";
    public string Exe { get; set; } = "";
    public string LaunchArgs { get; set; } = "";
    public string InstallDir { get; set; } = "";
    public string ExePath { get; set; } = "";
    public GameStatus Status { get; set; } = GameStatus.NotDownloaded;
    public string Error { get; set; } = "";

    public string InstallFullPath =>
        string.IsNullOrWhiteSpace(InstallDir)
            ? Path.Combine(AppInfo.BaseDir, "Games", Sanitize(Name))
            : Path.IsPathRooted(InstallDir) ? InstallDir : Path.Combine(AppInfo.BaseDir, InstallDir);

    public static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
