using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Steam2Launcher;

public class GameItem : INotifyPropertyChanged
{
    private string _name;
    private string _description;
    private string _url;
    private string _directUrl;
    private string _launchArgs;
    private string _repo;
    private int _baseVersion;
    private int _installedVersion;
    private string _installDir;
    private string _exePath;
    private string _statusText;
    private GameStatus _status;
    private bool _isBusy;
    private bool _isInstalled;
    private bool _upToDate = true;
    private string _suggestedExe = "";
    public string SuggestedExe { get => _suggestedExe; set { _suggestedExe = value; OnProp(); } }

    public GameItem(string name, string url, string installDir, string exePath, bool installed) :
        this(name, "", url, installDir, exePath, installed) { }

    public GameItem(string name, string description, string url, string installDir, string exePath, bool installed)
    {
        _name = name;
        _description = description;
        _url = url;
        _directUrl = "";
        _launchArgs = "";
        _repo = "";
        _installDir = installDir;
        _exePath = exePath;
        _isInstalled = installed;
        _status = installed ? GameStatus.Downloaded : GameStatus.NotDownloaded;
        _statusText = installed ? "Скачано ✓" : "Не скачано";
    }

    public string Name { get => _name; set { _name = value; OnProp(); } }
    public string Description { get => _description; set { _description = value; OnProp(); } }
    public string Url { get => _url; set { _url = value; OnProp(); } }
    public string DirectUrl { get => _directUrl; set { _directUrl = value; OnProp(); } }
    public string LaunchArgs { get => _launchArgs; set { _launchArgs = value; OnProp(); } }
    public string Repo { get => _repo; set { _repo = value; OnProp(); } }
    public int BaseVersion { get => _baseVersion; set { _baseVersion = value; OnProp(); } }
    public int InstalledVersion { get => _installedVersion; set { _installedVersion = value; OnProp(); } }
    /// <summary>True when the game has a build repo configured (base + delta updates).</summary>
    public bool HasUpdates => !string.IsNullOrWhiteSpace(_repo);

    /// <summary>True when the game has a build repo configured (base + delta updates).</summary>
    public bool HasPendingUpdate => HasUpdates && !_upToDate && _isInstalled;

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

    /// <summary>
    /// True when the installed build matches the latest published build (no update
    /// pending). The "Играть" action button relies on this instead of the raw repo flag.
    /// </summary>
    public bool UpToDate
    {
        get => _upToDate;
        set { _upToDate = value; OnProp(); OnProp(nameof(HasUpdates)); }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnProp(); }
    }

    public string ExeName =>
        string.IsNullOrEmpty(_exePath) ? "" : Path.GetFileName(_exePath.Replace('\\', '/'));

    public string CardLetter =>
        string.IsNullOrWhiteSpace(_name) ? "?" : _name.Trim()[..1].ToUpperInvariant();

    public System.Windows.Media.Brush StatusBrush
    {
        get
        {
            try
            {
                var c = new System.Windows.Media.BrushConverter();
                return c.ConvertFromString(StatusColour) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
            }
            catch { }
            return System.Windows.Media.Brushes.Gray;
        }
    }

    public string StatusColour =>
        _status == GameStatus.Downloaded ? "#FF57C78D"
        : _status == GameStatus.Error ? "#FFE05555" : "#FFFFC94D";

    public void AutoFindExe(string installRoot)
    {
        var full = FullInstallPath(installRoot);
        if (!Directory.Exists(full)) return;

        if (!string.IsNullOrWhiteSpace(SuggestedExe))
        {
            var direct = Path.Combine(full, SuggestedExe);
            if (File.Exists(direct))
            {
                var relDirect = Path.GetRelativePath(installRoot, direct);
                ExePath = relDirect.StartsWith("..") ? direct : relDirect;
                return;
            }
            foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(f), SuggestedExe, StringComparison.OrdinalIgnoreCase))
                {
                    var relF = Path.GetRelativePath(installRoot, f);
                    ExePath = relF.StartsWith("..") ? f : relF;
                    return;
                }
            }
        }

        var preferred = new[] { "portal2", "portal", "hl2", "garrysmod", "left4dead2",
            "life.exe", "siege.exe", "run" };

        var found = new List<(string path, int depth, string name, string ext)>();
        foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext != ".exe" && ext != ".bat" && ext != ".cmd" && ext != ".reg") continue;
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

        AutoDetectLaunchArgs(installRoot);
    }

    /// <summary>
    /// Source-engine style builds: an engine exe (hl2.exe/portal2.exe/...) needs the game
    /// module dir passed as -game &lt;mod&gt;. Finds a folder containing gameinfo.txt inside
    /// the install and, if the exe is an engine, fills LaunchArgs.
    /// </summary>
    public void AutoDetectLaunchArgs(string installRoot)
    {
        var full = FullInstallPath(installRoot);
        if (!Directory.Exists(full)) return;
        var exeName = Path.GetFileName(ExePath.Replace('\\', '/')).ToLowerInvariant();
        if (exeName != "hl2.exe" && exeName != "hl.exe" && exeName != "portal.exe"
            && exeName != "portal2.exe" && exeName != "svencoop.exe" && exeName != "steamapps.exe"
            && exeName != "clientserver.exe" && exeName != "left4dead2.exe" && exeName != "left4dead.exe")
            return;

        var mod = FindSourceMod(full);
        if (mod == null) return;
        LaunchArgs = "-game " + mod;
    }

    /// <summary>Finds the gameinfo.txt closest to the install root and returns its parent dir name, or null.</summary>
    private static string? FindSourceMod(string installRoot)
    {
        string? best = null;
        var bestDepth = int.MaxValue;
        try
        {
            foreach (var f in Directory.EnumerateFiles(installRoot, "gameinfo.txt", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(installRoot, f);
                var depth = rel.Count(c => c == '\\' || c == '/');
                if (depth >= bestDepth || depth > 4) continue;
                var dir = Path.GetDirectoryName(f);
                if (dir == null || string.Equals(dir.TrimEnd('\\', '/'), installRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) continue;
                var folder = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(folder) || folder.Length > 32) continue;
                best = folder;
                bestDepth = depth;
            }
        }
        catch { }
        return best;
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
