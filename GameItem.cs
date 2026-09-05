using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Steam2Launcher;

public class GameItem : INotifyPropertyChanged
{
    private string _name;
    private string _description;
    private string _url;
    private string _installDir;
    private string _exePath;
    private string _statusText;
    private GameStatus _status;
    private bool _isBusy;
    private bool _isInstalled;

    public GameItem(string name, string url, string installDir, string exePath, bool installed) :
        this(name, "", url, installDir, exePath, installed) { }

    public GameItem(string name, string description, string url, string installDir, string exePath, bool installed)
    {
        _name = name;
        _description = description;
        _url = url;
        _installDir = installDir;
        _exePath = exePath;
        _isInstalled = installed;
        _status = installed ? GameStatus.Downloaded : GameStatus.NotDownloaded;
        _statusText = installed ? "Скачано ✓" : "Не скачано";
    }

    public string Name { get => _name; set { _name = value; OnProp(); } }
    public string Description { get => _description; set { _description = value; OnProp(); } }
    public string Url { get => _url; set { _url = value; OnProp(); } }

    public string InstallDir
    {
        get => _installDir;
        set { _installDir = value; OnProp(nameof(InstallDir)); }
    }

    public string ExePath
    {
        get => _exePath;
        set { _exePath = value; OnProp(nameof(ExeName)); OnProp(); }
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        set { _isInstalled = value; OnProp(); }
    }

    public GameStatus Status
    {
        get => _status;
        set { _status = value; OnProp(); OnProp(nameof(StatusText)); OnProp(nameof(StatusColour)); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnProp(nameof(IsDownloading)); }
    }

    public bool IsDownloading => _isBusy;

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnProp(); }
    }

    public string ExeName =>
        string.IsNullOrEmpty(_exePath) ? "" : Path.GetFileName(_exePath.Replace('\\', '/'));

    public string StatusColour =>
        _status == GameStatus.Downloaded ? "#FF57C78D"
        : _status == GameStatus.Error ? "#FFE05555" : "#FFFFC94D";

    public void AutoFindExe(string installRoot)
    {
        var full = FullInstallPath(installRoot);
        if (!Directory.Exists(full)) return;

        var preferred = new[] { "portal2", "portal", "hl2", "garrysmod", "left4dead2",
            "life.exe", "siege.exe", "run" };

        var found = new List<(string path, int depth, string name, string ext)>();
        foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext != ".exe" && ext != ".bat" && ext != ".cmd") continue;
            var rel = Path.GetRelativePath(full, f);
            var depth = rel.Count(c => c == '\\' || c == '/');
            if (depth > 5) continue;
            if (f.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)) continue;
            found.Add((f, depth, Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), ext));
        }
        if (found.Count == 0) return;

        var best = found
            .OrderBy(x => x.depth)
            .ThenBy(x => x.ext != ".exe" ? 1 : 0)
            .ThenBy(x => preferred.Contains(x.name) ? 0 : 1)
            .ThenByDescending(x => preferred.Contains(x.name))
            .First();
        var chosen = best.path;
        var chosenRel = Path.GetRelativePath(installRoot, chosen);
        ExePath = chosenRel.StartsWith("..") ? chosen : chosenRel;
    }

    public string FullInstallPath(string installRoot)
    {
        if (!string.IsNullOrWhiteSpace(_installDir))
        {
            if (Path.IsPathRooted(_installDir)) return _installDir;
            return Path.Combine(installRoot, _installDir);
        }
        return Path.Combine(installRoot, GameEntry.Sanitize(_name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnProp([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
