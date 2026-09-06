using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;

namespace Steam2Launcher;

public partial class MainWindow : Window
{
    private readonly Downloader _downloader = new();
    private readonly ObservableCollection<GameItem> _games = new();
    private CancellationTokenSource? _cts;
    private string _installRoot = "";
    private string _theme = "Dark";

    public MainWindow()
    {
        InitializeComponent();
        GamesGrid.ItemsSource = _games;
        LoadSettings();
        RescanPost();
    }

    private void LoadSettings()
    {
        var settings = Storage.LoadSettings();
        SeedKnownCredentials(settings);

        // Data file candidates: games.json (preferred) then post.txt, at source folder / exe folder / cwd
        var root = Path.GetFullPath(Path.Combine(AppInfo.BaseDir, "..", "..", "..", ".."));
        var sourceJson = Path.Combine(root, "games.json");
        var sourceTxt = Path.Combine(root, "post.txt");
        var candidates = new[]
        {
            settings.PostPath,
            sourceJson,
            sourceTxt,
            Path.Combine(AppInfo.BaseDir, "games.json"),
            Path.Combine(AppInfo.BaseDir, "post.txt"),
            Path.Combine(Environment.CurrentDirectory, "games.json"),
            Path.Combine(Environment.CurrentDirectory, "post.txt")
        };
        AppInfo.PostPath = candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c)) ?? "";

        if (string.IsNullOrEmpty(AppInfo.PostPath))
        {
            var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Укажите файл со играми (games.json или post.txt)",
                Filter = "Список игр (*.json;*.txt)|*.json;*.txt|Все файлы|*.*"
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                AppInfo.PostPath = dlg.FileName;
        }

        _installRoot = !string.IsNullOrWhiteSpace(settings.InstallRoot) && Directory.Exists(settings.InstallRoot)
            ? settings.InstallRoot
            : Path.Combine(AppInfo.BaseDir, "Games");
        TxtInstallRoot.Text = _installRoot;
        TxtStatus.Text = $"Файл: {Path.GetFileName(AppInfo.PostPath)}";
        ApplyTheme(string.IsNullOrWhiteSpace(settings.Theme) ? "Dark" : settings.Theme);
    }

    private static void SeedKnownCredentials(AppSettings settings)
    {
        // Known leak-hosting creds so hosted builds download without prompting.
        // Always overwrites so stale/wrong values never get stuck.
        var known = new (string Host, string Creds)[]
        {
            ("files.kotle.uk", "gaben:gaben"),
            ("kotle.uk", "gaben:gaben")
        };
        var changed = false;
        foreach (var (host, creds) in known)
        {
            if (!settings.HostCredentials.TryGetValue(host, out var cur) || cur != creds)
            {
                settings.HostCredentials[host] = creds;
                changed = true;
            }
        }
        if (changed) Storage.SaveSettings(settings);
    }

    private void ApplyTheme(string name)
    {
        _theme = name == "Light" ? "Light" : "Dark";
        var appDicts = System.Windows.Application.Current.Resources.MergedDictionaries;
        for (var i = appDicts.Count - 1; i >= 0; i--)
        {
            if (appDicts[i].Contains("ThemeName"))
                appDicts.RemoveAt(i);
        }
        var dict = new ResourceDictionary
        {
            Source = new Uri(_theme == "Light" ? "ThemeLight.xaml" : "ThemeDark.xaml", UriKind.Relative)
        };
        appDicts.Add(dict);
        BtnTheme.Content = _theme == "Light" ? "Тема: Светлая" : "Тема: Тёмная";
    }

    private void BtnTheme_Click(object sender, RoutedEventArgs e)
    {
        ApplyTheme(_theme == "Light" ? "Dark" : "Light");
        var settings = Storage.LoadSettings();
        settings.Theme = _theme;
        Storage.SaveSettings(settings);
    }

    private void RescanPost()
    {
        if (!File.Exists(AppInfo.PostPath))
        {
            TxtStatus.Text = "Файл игр не найден: " + AppInfo.PostPath;
            return;
        }
        var entries = PostParser.LoadGames(AppInfo.PostPath);

        var saved = Storage.LoadSaved();
        _games.Clear();
        foreach (var e in entries)
        {
            var s = saved.FirstOrDefault(x => x.Url == e.Url);
            var installDir = s?.InstallDir ?? "";
            var installed = GameInstalledLocally(e.Name, installDir);
            var g = new GameItem(e.Name, e.Description, e.Url, installDir, s?.ExePath ?? "", installed)
            {
                DirectUrl = string.IsNullOrWhiteSpace(s?.DirectUrl) ? e.DirectUrl : s.DirectUrl,
                LaunchArgs = string.IsNullOrWhiteSpace(s?.LaunchArgs) ? e.LaunchArgs : s.LaunchArgs
            };
            if (installed && string.IsNullOrEmpty(g.ExePath))
            {
                // Server-recommended exe (relative to install dir) if it exists, else auto-detect
                var suggested = e.Exe;
                if (!string.IsNullOrWhiteSpace(suggested))
                {
                    var suggestedFull = Path.IsPathRooted(suggested) ? suggested : Path.Combine(_installRoot, suggested);
                    if (File.Exists(suggestedFull)) g.ExePath = suggested;
                    else if (File.Exists(Path.Combine(FullInstallPath(g), suggested))) g.ExePath = suggested;
                }
                if (string.IsNullOrEmpty(g.ExePath)) g.AutoFindExe(_installRoot);
                if (string.IsNullOrEmpty(g.LaunchArgs)) g.AutoDetectLaunchArgs(_installRoot);
            }
            _games.Add(g);
        }

        TxtStatus.Text = $"Игр: {_games.Count}  ·  файл: {Path.GetFileName(AppInfo.PostPath)}";
        TxtInstallRoot.Text = _installRoot;
        SaveGames();
    }

    private bool GameInstalledLocally(string name, string manualInstallDir)
    {
        string full;
        if (!string.IsNullOrWhiteSpace(manualInstallDir))
        {
            if (Path.IsPathRooted(manualInstallDir)) full = manualInstallDir;
            else full = Path.Combine(_installRoot, manualInstallDir);
        }
        else
        {
            full = Path.Combine(_installRoot, GameEntry.Sanitize(name));
        }
        return Directory.Exists(full) &&
               (Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Any() ||
                Directory.EnumerateDirectories(full).Any());
    }

    private string FullInstallPath(GameItem g) => g.FullInstallPath(_installRoot);

    private string FullExePath(GameItem g)
    {
        if (string.IsNullOrEmpty(g.ExePath)) return "";
        if (Path.IsPathRooted(g.ExePath)) return g.ExePath;
        return Path.Combine(_installRoot, g.ExePath);
    }

    private void SaveSettings()
    {
        Storage.SaveSettings(new AppSettings
        {
            InstallRoot = _installRoot,
            PostPath = AppInfo.PostPath,
            Theme = _theme
        });
    }

    private void SaveGames()
    {
        var saved = _games.Select(g => new SavedEntry
        {
            Name = g.Name,
            Url = g.Url,
            DirectUrl = g.DirectUrl,
            LaunchArgs = g.LaunchArgs,
            InstallDir = g.InstallDir,
            ExePath = g.ExePath
        }).ToList();
        Storage.Save(saved);
        SaveSettings();
    }

    private void BtnRescan_Click(object sender, RoutedEventArgs e) => RescanPost();

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new EditDialog("", "");
        if (dlg.ShowDialog() == true)
        {
            _games.Add(new GameItem(dlg.GameName, dlg.GameUrl, GameEntry.Sanitize(dlg.GameName), "", false));
            SaveGames();
            TxtStatus.Text = $"Добавлено: {dlg.GameName}";
        }
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Выберите папку для скачивания игр",
            SelectedPath = Directory.Exists(_installRoot) ? _installRoot : AppInfo.BaseDir
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _installRoot = dlg.SelectedPath;
            TxtInstallRoot.Text = _installRoot;
            SaveGames();
        }
    }

    private GameItem? Selected => GamesGrid.SelectedItem as GameItem;

    // ---------- Авто-установка SmartSteamEmu ----------

    private static bool IsEmulator(GameItem g) =>
        g.Name.Contains("SmartSteamEmu", StringComparison.OrdinalIgnoreCase)
        || g.Url.Contains("SmartSteamEmu", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// После установки любой игры доматывает SmartSteamEmu (если лаунчер его скачал)
    /// в папки всех установленных игр, чтобы эмулятор работал без ручных шагов.
    /// Папка эмулятора ищется как "SmartSteamEmu/SmartSteamEmu" внутри его установки.
    /// </summary>
    private void AfterInstall(GameItem justInstalled)
    {
        var emu = _games.FirstOrDefault(IsEmulator);
        if (emu == null) return;
        var emuDir = FullInstallPath(emu);
        if (!Directory.Exists(emuDir)) return;

        var payload = Path.Combine(emuDir, "SmartSteamEmu", "SmartSteamEmu");
        if (!Directory.Exists(payload)) payload = emuDir;

        foreach (var g in _games)
        {
            if (g == emu) continue;
            var gDir = FullInstallPath(g);
            if (!Directory.Exists(gDir)) continue;

            var target = gDir;
            if (!string.IsNullOrEmpty(g.ExePath))
            {
                var exeDir = Path.GetDirectoryName(FullExePath(g));
                if (exeDir != null && Directory.Exists(exeDir)) target = exeDir;
            }
            CopyPayload(payload, target);
        }

        // Сам эмулятор удобнее запускать через SSELauncher.exe (графический лаунчер)
        if (ReferenceEquals(justInstalled, emu))
        {
            var sse = Path.Combine(emuDir, "SSELauncher.exe");
            if (File.Exists(sse)) emu.ExePath = sse;
        }
    }

    private static void CopyPayload(string payload, string targetDir)
    {
        foreach (var f in Directory.EnumerateFiles(payload))
            File.Copy(f, Path.Combine(targetDir, Path.GetFileName(f)), true);
        foreach (var d in Directory.EnumerateDirectories(payload))
            CopyDirectory(d, Path.Combine(targetDir, Path.GetFileName(d)));
    }

    private static void CopyDirectory(string srcDir, string dstDir)
    {
        Directory.CreateDirectory(dstDir);
        foreach (var f in Directory.EnumerateFiles(srcDir))
            File.Copy(f, Path.Combine(dstDir, Path.GetFileName(f)), true);
        foreach (var d in Directory.EnumerateDirectories(srcDir))
            CopyDirectory(d, Path.Combine(dstDir, Path.GetFileName(d)));
    }

    /// <summary>Handles HTTP 401: returns saved or user-entered "user:pass", or null if cancelled.</summary>
    private Task<string?> PromptCredentials(Downloader downloader, Uri uri, int tryIndex)
    {
        var settings = Storage.LoadSettings();
        var host = uri.Host;
        var saved = settings.HostCredentials.TryGetValue(host, out var v) ? v : "";
        var savedUser = "";
        if (saved.Contains(':', StringComparison.Ordinal))
        {
            savedUser = saved[..saved.IndexOf(':')];
            var savedPass = saved[(saved.IndexOf(':') + 1)..];
            if (tryIndex == 1 && !string.IsNullOrEmpty(savedUser) && !string.IsNullOrEmpty(savedPass))
                return Task.FromResult<string?>(saved); // first retry: silently reuse stored credentials
        }

        var dlg = new AuthDialog(host);
        if (!string.IsNullOrEmpty(savedUser)) dlg.SetUser(savedUser);
        if (dlg.ShowDialog() != true) return Task.FromResult<string?>(null);

        var joined = dlg.User + ":" + dlg.Pass;
        settings.HostCredentials[host] = joined;
        Storage.SaveSettings(settings);
        return Task.FromResult<string?>(joined);
    }

    private void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        var g = Selected;
        if (g == null)
        {
            MessageBox.Show("Выберите игру из списка.", "Steam2 Лаунчер",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (g.IsDownloading)
        {
            _cts?.Cancel();
            return;
        }
        _ = DownloadAsync(g);
    }

    private async Task DownloadAsync(GameItem g)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        g.IsBusy = true;
        g.StatusText = "Определение ссылки…";
        Progress.IsIndeterminate = true;
        TxtProgress.Text = "Подключение…";

        try
        {
            // Direct link already resolved on the server -> skip network resolution
            var (directUrl, fileName) = !string.IsNullOrWhiteSpace(g.DirectUrl)
                ? (g.DirectUrl, Downloader.FileNameFromUrl(g.DirectUrl))
                : await _downloader.ResolveAsync(g.Url, ct);
            if (string.IsNullOrEmpty(fileName)) fileName = GameEntry.Sanitize(g.Name) + ".zip";
            var downloadPath = Path.Combine(Path.GetTempPath(), "steam2dl_" + Guid.NewGuid().ToString("N")[..8]
                + "_" + GameEntry.Sanitize(fileName));

            var progress = new Progress<DownloadProgress>(p =>
            {
                g.StatusText = "Скачивание…";
                Progress.IsIndeterminate = false;
                Progress.Value = p.Percent;
                TxtProgress.Text = p.Message;
            });

            var result = await _downloader.DownloadAsync(directUrl, downloadPath, progress, ct, PromptCredentials);
            if (!result.Success)
                throw new Exception("Ошибка загрузки: " + result.Error);

            // Extract
            var dest = FullInstallPath(g);
            Directory.CreateDirectory(dest);

            if (ArchiveExtractor.IsArchive(downloadPath, result.ContentType))
            {
                var exProgress = new Progress<string>(msg =>
                {
                    g.StatusText = msg;
                    Progress.IsIndeterminate = true;
                    TxtProgress.Text = msg;
                });
                await ArchiveExtractor.ExtractAsync(downloadPath, dest, exProgress, ct);
            }
            else
            {
                // Not an archive - just move the downloaded file into install dir
                var targetFile = Path.Combine(dest, Path.GetFileName(downloadPath));
                File.Move(downloadPath, targetFile, true);
            }

            g.Status = GameStatus.Downloaded;
            g.StatusText = "Скачано ✓";
            g.InstallDir = Path.GetRelativePath(_installRoot, dest);
            if (g.InstallDir.StartsWith("..")) g.InstallDir = dest;
            g.IsInstalled = true;
            g.AutoFindExe(_installRoot);
            if (string.IsNullOrEmpty(g.LaunchArgs)) g.AutoDetectLaunchArgs(_installRoot);
            AfterInstall(g);
            SaveGames();
            TxtStatus.Text = $"Игра «{g.Name}» установлена в {dest}";
            Progress.Value = 100;
            TxtProgress.Text = "Готово";
        }
        catch (ManualDownloadNeededException mdn)
        {
            g.Status = GameStatus.Error;
            g.StatusText = "Скачать вручную";
            var answer = MessageBox.Show(
                "Автоматическое скачивание недоступно для этой ссылки:\n\n" + mdn.Message + "\n\n" +
                mdn.PageUrl + "\n\nОткрыть страницу в браузере?",
                "Steam2 Лаунчер", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo { FileName = mdn.PageUrl, UseShellExecute = true });
        }
        catch (OperationCanceledException)
        {
            g.StatusText = "Отменено";
            TxtProgress.Text = "Отменено";
        }
        catch (Exception ex)
        {
            g.Status = GameStatus.Error;
            g.StatusText = "Ошибка: " + ex.Message;
            MessageBox.Show($"Не удалось скачать «{g.Name}»:\n{ex.Message}",
                "Steam2 Лаунчер", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            g.IsBusy = false;
            Progress.IsIndeterminate = false;
        }
    }

    private void BtnLaunch_Click(object sender, RoutedEventArgs e)
    {
        var g = Selected;
        if (g == null)
        {
            MessageBox.Show("Выберите игру.", "Steam2 Лаунчер",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dest = FullInstallPath(g);
        if (!Directory.Exists(dest))
        {
            MessageBox.Show("Игра ещё не установлена. Сначала скачайте и распакуйте её.",
                "Steam2 Лаунчер", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrEmpty(g.ExePath) || !File.Exists(FullExePath(g)))
        {
            g.AutoFindExe(_installRoot);
            if (string.IsNullOrEmpty(g.ExePath))
            {
                ChooseExe(g);
                if (string.IsNullOrEmpty(g.ExePath)) return;
            }
        }
        try
        {
            var fullExe = FullExePath(g);
            var psi = new ProcessStartInfo
            {
                FileName = fullExe,
                WorkingDirectory = Path.GetDirectoryName(fullExe),
                UseShellExecute = true
            };
            if (!string.IsNullOrWhiteSpace(g.LaunchArgs))
                psi.Arguments = g.LaunchArgs;
            Process.Start(psi);
            TxtStatus.Text = $"Запущено: {g.Name}" + (string.IsNullOrWhiteSpace(g.LaunchArgs) ? "" : " (" + g.LaunchArgs + ")");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось запустить: " + ex.Message, "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ChooseExe(GameItem g)
    {
        var dest = FullInstallPath(g);
        var dlg = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Выберите исполняемый файл игры (.exe или .bat)",
            InitialDirectory = Directory.Exists(dest) ? dest : AppInfo.BaseDir,
            Filter = "Исполняемые файлы (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|Все файлы (*.*)|*.*"
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            g.ExePath = Path.GetRelativePath(_installRoot, dlg.FileName);
            if (g.ExePath.StartsWith("..")) g.ExePath = dlg.FileName;
            SaveGames();
        }
    }

    private void GamesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected != null) BtnEdit_Click(sender, e);
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        var g = Selected;
        if (g == null) return;
        var dlg = new EditDialog(g.Name, g.Url, g.InstallDir, g.ExePath);
        if (dlg.ShowDialog() == true)
        {
            g.Name = dlg.GameName;
            g.Url = dlg.GameUrl;
            g.InstallDir = dlg.GameInstallDir;
            if (dlg.ExeChosen)
                g.ExePath = dlg.GameExePath;
            SaveGames();
            GamesGrid.Items.Refresh();
        }
    }
}
