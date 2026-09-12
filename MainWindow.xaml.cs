using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using Cursors = System.Windows.Input.Cursors;
using MessageBox = System.Windows.MessageBox;

namespace Steam2Launcher;

public partial class MainWindow : Window
{
    public const string CurrentVersion = "v1.4.1";
    public const string UpdateRepo = "MrPauk335/steam2launcher";

    private readonly Downloader _downloader = new();
    private readonly ObservableCollection<GameItem> _games = new();
    private CancellationTokenSource? _cts;
    private PauseTokenSource? _pause;
    private string _installRoot = "";
    private string _theme = "Dark";
    public string? LatestVersion { get; private set; }
    public string? LatestUrl { get; private set; }
    public string? LatestAssetUrl { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        GamesGrid.ItemsSource = _games;
        LoadSettings();
        RescanPost();
        _ = CheckForUpdatesAsync();
        UpdateButtonStates();
    }

    // ====================== Button state management ======================

    private bool IsBusy => Selected?.IsDownloading == true;

    private void UpdateButtonStates()
    {
        var g = Selected;
        var busy = g != null && g.IsDownloading;

        BtnDownload.IsEnabled = g != null && !busy;
        BtnUpdate.IsEnabled = g != null && !busy && g.HasUpdates && g.IsInstalled;
        BtnPause.IsEnabled = busy;
        BtnCancel.IsEnabled = busy;

        if (busy)
        {
            BtnDownload.Content = "Скачать";
            BtnDownload.IsEnabled = false;
            BtnPause.Content = _pause?.IsPaused == true ? "▶ Продолжить" : "⏸ Пауза";
        }
        else
        {
            BtnPause.Content = "⏸ Пауза";
        }
    }

    private void RefreshUpdateButton() => UpdateButtonStates();

    private void GamesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateButtonStates();
    }

    // ====================== Pause / Cancel buttons ======================

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        if (_pause == null) return;
        if (_pause.IsPaused)
        {
            _pause.Resume();
            BtnPause.Content = "⏸ Пауза";
            TxtProgress.Text = TxtProgress.Text.Replace(" [ПАУЗА]", "") + " [ПАУЗА]";
        }
        else
        {
            _pause.Pause();
            BtnPause.Content = "▶ Продолжить";
            TxtProgress.Text += " [ПАУЗА]";
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _pause?.Resume(); // unblock the read loop so cancellation propagates
    }

    // ====================== Auto-update check ======================

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var url = $"https://api.github.com/repos/{UpdateRepo}/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Steam2Launcher/" + CurrentVersion);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return;
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var htmlUrl = doc.RootElement.GetProperty("html_url").GetString() ?? "";
            string? assetUrl = null;
            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        assetUrl = a.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }
            if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(htmlUrl)) return;
            if (IsNewer(tag, CurrentVersion))
            {
                LatestVersion = tag;
                LatestUrl = htmlUrl;
                LatestAssetUrl = assetUrl;
                Dispatcher.Invoke(() => ShowUpdateBanner(tag, htmlUrl));
            }
        }
        catch { /* ignore: no internet / rate limit / etc. */ }
    }

    private static bool IsNewer(string remote, string local)
    {
        int[] Parse(string s) => s.TrimStart('v', 'V').Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var a = Parse(remote); var b = Parse(local);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var av = i < a.Length ? a[i] : 0;
            var bv = i < b.Length ? b[i] : 0;
            if (av > bv) return true;
            if (av < bv) return false;
        }
        return false;
    }

    private void ShowUpdateBanner(string tag, string url)
    {
        TxtUpdate.Text = $"Обновление {tag} доступно (у тебя {CurrentVersion}) — нажмите, чтобы обновить";
        TxtUpdate.Tag = url;
        TxtUpdate.Visibility = Visibility.Visible;
        TxtUpdate.Cursor = System.Windows.Input.Cursors.Hand;
        TxtUpdate.MouseLeftButtonUp -= TxtUpdate_Click;
        TxtUpdate.MouseLeftButtonUp += TxtUpdate_Click;
    }

    private void TxtUpdate_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _ = SelfUpdateAsync();
    }

    /// <summary>
    /// Self-update: download new launcher zip, extract, write swap script, restart.
    /// </summary>
    private async Task SelfUpdateAsync()
    {
        if (string.IsNullOrEmpty(LatestVersion) || string.IsNullOrEmpty(LatestAssetUrl))
        {
            if (!string.IsNullOrEmpty(LatestUrl))
                try { Process.Start(new ProcessStartInfo { FileName = LatestUrl, UseShellExecute = true }); } catch { }
            return;
        }

        var tag = LatestVersion;
        var assetUrl = LatestAssetUrl;

        TxtUpdate.Text = $"Скачивание обновления {tag}…";
        TxtUpdate.Cursor = Cursors.Arrow;

        _cts = new CancellationTokenSource();
        _pause = new PauseTokenSource();
        var ct = _cts.Token;

        try
        {
            var stagingDir = Path.Combine(Path.GetTempPath(), "steam2launcher_upd_" + GameEntry.Sanitize(tag));
            Directory.CreateDirectory(stagingDir);
            var zipPath = Path.Combine(stagingDir, "update.zip");

            var progress = new Progress<DownloadProgress>(p =>
            {
                TxtProgress.Text = $"Обновление лаунчера: {p.Message}";
                Progress.IsIndeterminate = false;
                Progress.Value = p.Percent;
            });

            var result = await _downloader.DownloadAsync(assetUrl, zipPath, progress, ct);
            if (!result.Success)
                throw new Exception("Ошибка скачивания обновления: " + result.Error);

            TxtUpdate.Text = $"Распаковка обновления {tag}…";
            Progress.IsIndeterminate = true;

            // Extract zip: find the Steam2Launcher.exe inside
            var newExePath = "";
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    var name = Path.GetFileName(entry.FullName);
                    if (name.Equals("Steam2Launcher.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        newExePath = Path.Combine(stagingDir, name);
                        entry.ExtractToFile(newExePath, true);
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(newExePath) || !File.Exists(newExePath))
                throw new Exception("В обновлении нет Steam2Launcher.exe");

            // Write swap script
            var currentExe = Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppInfo.BaseDir, "Steam2Launcher.exe");
            var exeDir = Path.GetDirectoryName(currentExe) ?? AppInfo.BaseDir;

            var scriptPath = Path.Combine(stagingDir, "swap.cmd");
            var script = $@"@echo off
chcp 65001 >nul
:wait
tasklist /FI ""IMAGENAME eq Steam2Launcher.exe"" 2>nul | find /I ""Steam2Launcher.exe"" >nul
if %errorlevel%==0 ( timeout /t 1 /nobreak >nul & goto wait )
copy /Y ""{newExePath}"" ""{currentExe}"" >nul
del /q ""{zipPath}"" 2>nul
del /q ""{newExePath}"" 2>nul
rmdir ""{stagingDir}"" 2>nul
start """" ""{currentExe}""
del ""%~f0""
";
            File.WriteAllText(scriptPath, script);

            TxtUpdate.Text = $"Обновление готово! Перезапуск…";
            Progress.Value = 100;

            // Launch swap script and exit
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true
            });

            Environment.Exit(0);
        }
        catch (OperationCanceledException)
        {
            TxtUpdate.Text = "Обновление отменено";
            TxtUpdate.Cursor = Cursors.Hand;
            TxtUpdate.Tag = LatestUrl;
        }
        catch (Exception ex)
        {
            TxtUpdate.Text = $"Ошибка обновления: {ex.Message} — нажмите для перехода на страницу";
            TxtUpdate.Cursor = Cursors.Hand;
            TxtUpdate.Tag = LatestUrl;
        }
        finally
        {
            _pause = null;
            UpdateButtonStates();
            Progress.IsIndeterminate = false;
        }
    }

    // ====================== Settings ======================

    private void LoadSettings()
    {
        var settings = Storage.LoadSettings();
        SeedKnownCredentials(settings);

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

    // ====================== Game list ======================

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
                LaunchArgs = string.IsNullOrWhiteSpace(s?.LaunchArgs) ? e.LaunchArgs : s.LaunchArgs,
                Repo = string.IsNullOrWhiteSpace(s?.Repo) ? e.Repo : s.Repo
            };
            var full = FullInstallPath(g);
            if (installed)
            {
                var bi = BuildUpdater.ReadBuildInfo(full);
                g.BaseVersion = bi.Base;
                g.InstalledVersion = bi.Version;
                if (g.HasUpdates)
                    g.StatusText = $"v{bi.Version} ✓";
            }
            if (installed && string.IsNullOrEmpty(g.ExePath))
            {
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
            ExePath = g.ExePath,
            Repo = g.Repo,
            BaseVersion = g.BaseVersion,
            InstalledVersion = g.InstalledVersion
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

    // ====================== Install from base parts (download ALL, then extract ALL) ======================

    private async Task InstallBaseFromRepoAsync(GameItem g)
    {
        _cts = new CancellationTokenSource();
        _pause = new PauseTokenSource();
        var ct = _cts.Token;
        g.IsBusy = true;
        g.StatusText = "Поиск базы…";
        Progress.IsIndeterminate = true;
        TxtProgress.Text = "Запрос частей базы…";
        UpdateButtonStates();

        var dest = FullInstallPath(g);
        Directory.CreateDirectory(dest);
        var workDir = Path.Combine(Path.GetTempPath(), "steam2base_" + GameEntry.Sanitize(g.Name) + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        try
        {
            // ---- Fetch manifest ----
            var (manifest, partUrls, error) = await BuildUpdater.FetchBasePartsAsync(g.Repo, ct);
            if (manifest == null || partUrls.Count == 0)
            {
                if (!string.IsNullOrEmpty(error))
                    throw new Exception("Не удалось получить базу:\n" + error);
                throw new Exception("База не найдена в репозитории обновлений.");
            }

            // ---- Phase 1: probe sizes via HEAD ----
            TxtProgress.Text = "Определение размеров…";
            var sizes = new long[partUrls.Count];
            long totalBytes = 0;
            for (int i = 0; i < partUrls.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                _pause.Wait(ct);
                var sz = await _downloader.GetFileSizeAsync(partUrls[i], ct);
                sizes[i] = sz ?? 0;
                totalBytes += sizes[i];
            }
            var hasTotal = totalBytes > 0;
            long downloadedSoFar = 0;

            // ---- Phase 2: download ALL parts ----
            var localParts = new List<string>();
            for (int i = 0; i < partUrls.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                _pause.Wait(ct);

                var partUrl = partUrls[i];
                var fileName = Downloader.FileNameFromUrl(partUrl);
                if (string.IsNullOrEmpty(fileName)) fileName = $"part_{i + 1}.zip";
                var localPath = Path.Combine(workDir, fileName);
                localParts.Add(localPath);

                g.StatusText = $"Скачивание: часть {i + 1}/{partUrls.Count}";
                var capturedIdx = i;
                var capturedFar = downloadedSoFar;

                var partProgress = new Progress<DownloadProgress>(p =>
                {
                    var overallBytes = capturedFar + p.DownloadedBytes;
                    var pct = hasTotal ? (double)overallBytes / totalBytes * 100.0 : 0;
                    var speed = p.SpeedBytesPerSec;
                    var remaining = hasTotal ? totalBytes - overallBytes : 0;
                    var eta = speed > 0 && remaining > 0 ? TimeSpan.FromSeconds(remaining / speed) : (TimeSpan?)null;
                    var etaStr = eta != null
                        ? eta.Value.TotalHours >= 1 ? $"  осталось {eta.Value.Hours}ч {eta.Value.Minutes}мин"
                                                  : $"  осталось {eta.Value.Minutes}мин {eta.Value.Seconds}сек"
                        : "";

                    g.StatusText = $"Скачивание: {capturedIdx + 1}/{partUrls.Count}";
                    Progress.IsIndeterminate = false;
                    Progress.Value = Math.Min(pct, 100);
                    TxtProgress.Text = $"Часть {capturedIdx + 1}/{partUrls.Count}" +
                        $"  {FormatBytes(overallBytes)}{(hasTotal ? " / " + FormatBytes(totalBytes) : "")}" +
                        $"  {FormatPercent(pct)}" +
                        (speed > 0 ? $"  {speed / 1048576.0:0.0} МБ/с" : "") +
                        etaStr +
                        (_pause?.IsPaused == true ? "  [ПАУЗА]" : "");
                });

                var result = await _downloader.DownloadAsync(partUrl, localPath, partProgress, ct, PromptCredentials, _pause);
                if (!result.Success)
                    throw new Exception($"Ошибка загрузки части {i + 1}: {result.Error}");

                downloadedSoFar += sizes[i] > 0 ? sizes[i] : new FileInfo(localPath).Length;
            }

            // ---- Phase 3: extract ALL parts ----
            g.StatusText = "Распаковка…";
            Progress.IsIndeterminate = true;

            for (int i = 0; i < localParts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                _pause.Wait(ct);

                var partPath = localParts[i];
                TxtProgress.Text = $"Распаковка: часть {i + 1}/{localParts.Count} ({Path.GetFileName(partPath)})";

                var exProgress = new Progress<string>(msg =>
                {
                    g.StatusText = msg;
                    TxtProgress.Text = msg;
                });
                await ArchiveExtractor.ExtractAsync(partPath, dest, exProgress, ct, _pause);

                try { File.Delete(partPath); } catch { }
            }

            // ---- Phase 4: finalize ----
            BuildUpdater.WriteBuildInfo(dest, manifest.Base, manifest.Version);
            g.BaseVersion = manifest.Base;
            g.InstalledVersion = manifest.Version;
            g.InstallDir = Path.GetRelativePath(_installRoot, dest);
            if (g.InstallDir.StartsWith("..")) g.InstallDir = dest;
            g.Status = GameStatus.Downloaded;
            g.StatusText = $"v{manifest.Version} ✓";
            g.IsInstalled = true;
            g.AutoFindExe(_installRoot);
            if (string.IsNullOrEmpty(g.LaunchArgs)) g.AutoDetectLaunchArgs(_installRoot);
            AfterInstall(g);
            SaveGames();
            Progress.Value = 100;
            TxtProgress.Text = "База v" + manifest.Version + " установлена" + (string.IsNullOrEmpty(manifest.Tag) ? "" : " (" + manifest.Tag + ")");
            TxtStatus.Text = $"Игра «{g.Name}» установлена в {dest}";
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
            MessageBox.Show($"Не удалось установить «{g.Name}»:\n{ex.Message}",
                "Steam2 Лаунчер", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            g.IsBusy = false;
            _pause = null;
            Progress.IsIndeterminate = false;
            RefreshUpdateButton();
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    // ====================== Delta update ======================

    private async Task UpdateAsync(GameItem g)
    {
        _cts = new CancellationTokenSource();
        _pause = new PauseTokenSource();
        var ct = _cts.Token;
        g.IsBusy = true;
        g.StatusText = "Проверка обновлений…";
        Progress.IsIndeterminate = true;
        TxtProgress.Text = "Запрос к GitHub…";
        UpdateButtonStates();

        try
        {
            var installDir = FullInstallPath(g);
            if (!Directory.Exists(installDir))
            {
                MessageBox.Show("Игра не установлена. Сначала скачайте её.",
                    "Steam2 Лаунчер", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var (manifest, deltaUrl, tag, error) = await BuildUpdater.FetchLatestAsync(g.Repo, ct);
            if (manifest == null)
            {
                if (!string.IsNullOrEmpty(error))
                    MessageBox.Show("Не удалось проверить обновления:\n" + error,
                        "Steam2 Лаунчер", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!BuildUpdater.IsNewer(manifest, g.BaseVersion, g.InstalledVersion))
            {
                g.StatusText = g.HasUpdates ? $"v{g.InstalledVersion} ✓ актуально" : "Скачано ✓";
                TxtProgress.Text = "Установлена последняя версия";
                return;
            }

            if (manifest.Base != g.BaseVersion)
            {
                var answer = MessageBox.Show(
                    $"Вышла новая БАЗА обновления (v{manifest.Base}) — текущая база v{g.BaseVersion}.\n\n" +
                    "Лаунчер перекачает новую базу и затем применит обновления.",
                    "Steam2 Лаунчер", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (answer == MessageBoxResult.Yes)
                {
                    await InstallBaseFromRepoAsync(g);
                    if (g.InstalledVersion > 0)
                        await UpdateAsync(g);
                }
                return;
            }

            // Download delta zip
            var deltaName = Downloader.FileNameFromUrl(deltaUrl ?? "");
            if (string.IsNullOrEmpty(deltaName)) deltaName = "update.zip";
            var downloadPath = Path.Combine(Path.GetTempPath(), "steam2upd_" + Guid.NewGuid().ToString("N")[..8]
                + "_" + GameEntry.Sanitize(deltaName));
            g.StatusText = $"Скачивание обновления v{manifest.Version}…";

            var progress = new Progress<DownloadProgress>(p =>
            {
                Progress.IsIndeterminate = false;
                Progress.Value = p.Percent;
                TxtProgress.Text = $"Обновление: {p.Message}";
            });
            var result = await _downloader.DownloadAsync(deltaUrl ?? "", downloadPath, progress, ct, PromptCredentials, _pause);
            if (!result.Success)
                throw new Exception("Ошибка загрузки обновления: " + result.Error);

            // Extract over the install dir
            if (ArchiveExtractor.IsArchive(downloadPath, result.ContentType))
            {
                g.StatusText = "Распаковка обновления…";
                var exProgress = new Progress<string>(msg =>
                {
                    g.StatusText = msg;
                    Progress.IsIndeterminate = true;
                    TxtProgress.Text = msg;
                });
                await ArchiveExtractor.ExtractAsync(downloadPath, installDir, exProgress, ct, _pause);
            }
            else
            {
                File.Move(downloadPath, Path.Combine(installDir, Path.GetFileName(downloadPath)), true);
            }

            BuildUpdater.ApplyRemoved(installDir, manifest.Removed);
            BuildUpdater.WriteBuildInfo(installDir, manifest.Base, manifest.Version);
            g.BaseVersion = manifest.Base;
            g.InstalledVersion = manifest.Version;
            g.StatusText = $"v{manifest.Version} ✓";
            SaveGames();
            Progress.Value = 100;
            TxtProgress.Text = $"Обновлено до v{manifest.Version}" + (string.IsNullOrEmpty(tag) ? "" : $" ({tag})");
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
            MessageBox.Show($"Не удалось обновить «{g.Name}»:\n{ex.Message}",
                "Steam2 Лаунчер", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            g.IsBusy = false;
            _pause = null;
            Progress.IsIndeterminate = false;
            RefreshUpdateButton();
        }
    }

    private void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        var g = Selected;
        if (g == null) return;
        _ = UpdateAsync(g);
    }

    // ====================== SmartSteamEmu ======================

    private static bool IsEmulator(GameItem g) =>
        g.Name.Contains("SmartSteamEmu", StringComparison.OrdinalIgnoreCase)
        || g.Url.Contains("SmartSteamEmu", StringComparison.OrdinalIgnoreCase);

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

    // ====================== Auth ======================

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
                return Task.FromResult<string?>(saved);
        }

        var dlg = new AuthDialog(host);
        if (!string.IsNullOrEmpty(savedUser)) dlg.SetUser(savedUser);
        if (dlg.ShowDialog() != true) return Task.FromResult<string?>(null);

        var joined = dlg.User + ":" + dlg.Pass;
        settings.HostCredentials[host] = joined;
        Storage.SaveSettings(settings);
        return Task.FromResult<string?>(joined);
    }

    // ====================== Download / Launch ======================

    private void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        var g = Selected;
        if (g == null)
        {
            MessageBox.Show("Выберите игру из списка.", "Steam2 Лаунчер",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var repo = EffectiveRepo(g);
        if (repo != null)
        {
            g.Repo = repo;
            if (Directory.Exists(FullInstallPath(g)))
                _ = UpdateAsync(g);
            else
                _ = InstallBaseFromRepoAsync(g);
            return;
        }
        _ = DownloadAsync(g);
    }

    private static string? EffectiveRepo(GameItem g)
    {
        if (!string.IsNullOrWhiteSpace(g.Repo)) return g.Repo;
        var inferred = Downloader.GitRepoFromUrl(g.Url);
        if (!string.IsNullOrWhiteSpace(inferred)) return inferred;
        return null;
    }

    private async Task DownloadAsync(GameItem g)
    {
        _cts = new CancellationTokenSource();
        _pause = new PauseTokenSource();
        var ct = _cts.Token;
        g.IsBusy = true;
        g.StatusText = "Определение ссылки…";
        Progress.IsIndeterminate = true;
        TxtProgress.Text = "Подключение…";
        UpdateButtonStates();

        try
        {
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

            var result = await _downloader.DownloadAsync(directUrl, downloadPath, progress, ct, PromptCredentials, _pause);
            if (!result.Success)
                throw new Exception("Ошибка загрузки: " + result.Error);

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
                await ArchiveExtractor.ExtractAsync(downloadPath, dest, exProgress, ct, _pause);
            }
            else
            {
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
            if (g.HasUpdates)
            {
                BuildUpdater.WriteBuildInfo(dest, 1, 1);
                g.BaseVersion = 1;
                g.InstalledVersion = 1;
                g.StatusText = "v1 ✓";
            }
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
            _pause = null;
            Progress.IsIndeterminate = false;
            RefreshUpdateButton();
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

    // ====================== Helpers ======================

    private static string FormatBytes(long b)
    {
        if (b >= 1073741824) return $"{b / 1073741824.0:0.00} ГБ";
        if (b >= 1048576) return $"{b / 1048576.0:0.0} МБ";
        if (b >= 1024) return $"{b / 1024.0:0.0} КБ";
        return $"{b} Б";
    }

    private static string FormatPercent(double pct) => $"{pct:0.0}%";
}
