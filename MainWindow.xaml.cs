using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Cursors = System.Windows.Input.Cursors;
using MessageBox = System.Windows.MessageBox;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using BrushConverter = System.Windows.Media.BrushConverter;

namespace Steam2Launcher;

public partial class MainWindow : Window
{
    public static string CurrentVersion
    {
        get
        {
            try
            {
                var att = System.Attribute.GetCustomAttribute(
                System.Reflection.Assembly.GetExecutingAssembly(),
                typeof(System.Reflection.AssemblyInformationalVersionAttribute))
                as System.Reflection.AssemblyInformationalVersionAttribute;
                var ver = att?.InformationalVersion?.Split('+')[0];
                if (string.IsNullOrWhiteSpace(ver)) ver = "1.0.0";
                return "v" + ver;
            }
            catch { return "v1.0.0"; }
        }
    }
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

    private readonly ContextMenu _ctxMenu = new();
    private readonly MenuItem _ctxMainItem = new() { Header = "Скачать" };
    private readonly MenuItem _ctxPlayItem = new() { Header = "Играть" };
    private readonly MenuItem _ctxVerifyItem = new() { Header = "Проверить целостность" };
    private readonly MenuItem _ctxUninstallItem = new() { Header = "Удалить игру" };
    private readonly MenuItem _ctxEditItem = new() { Header = "Свойства" };
    private readonly MenuItem _ctxFolderItem = new() { Header = "Открыть папку" };

    public MainWindow()
    {
        InitializeComponent();
        BuildContextMenu();
        GamesList.ItemsSource = _games;
        LoadSettings();
        RescanPost();
        _ = CheckForUpdatesAsync();
        UpdateButtonStates();
    }

    private void BuildContextMenu()
    {
        _ctxMainItem.Click += (s, e) => BtnMain_Click(s, e);
        _ctxPlayItem.Click += (s, e) => { var g = Selected; if (g?.IsInstalled == true) LaunchGame(g); };
        _ctxVerifyItem.Click += (s, e) => BtnVerify_Click(s, e);
        _ctxUninstallItem.Click += (s, e) => BtnUninstall_Click(s, e);
        _ctxEditItem.Click += (s, e) => BtnEdit_Click(s, e);
        _ctxFolderItem.Click += (s, e) => BtnOpenFolder_Click(s, e);
        _ctxMenu.Items.Add(_ctxMainItem);
        _ctxMenu.Items.Add(_ctxPlayItem);
        _ctxMenu.Items.Add(new Separator());
        _ctxMenu.Items.Add(_ctxVerifyItem);
        _ctxMenu.Items.Add(_ctxUninstallItem);
        _ctxMenu.Items.Add(new Separator());
        _ctxMenu.Items.Add(_ctxEditItem);
        _ctxMenu.Items.Add(_ctxFolderItem);
    }

    // ═══════════════════════════ Selection / Details ═══════════════════════════

    private GameItem? Selected => GamesList.SelectedItem as GameItem;

    private void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshDetails();
        UpdateButtonStates();
    }

    private void RefreshDetails()
    {
        var g = Selected;
        if (g == null)
        {
            try { TileHero.Background = new BrushConverter().ConvertFromString("#3B82F6") as Brush ?? Brushes.Blue; }
            catch { }
            HeroDot.Fill = Brushes.Transparent;
            LitName.Text = "Выберите игру";
            LitStatus.Text = "";
            LitDesc.Text = "";
            LitMeta.Text = "";
            _ctxMainItem.Visibility = Visibility.Collapsed;
            _ctxPlayItem.Visibility = Visibility.Collapsed;
            _ctxVerifyItem.Visibility = Visibility.Collapsed;
            _ctxUninstallItem.Visibility = Visibility.Collapsed;
            _ctxEditItem.Visibility = Visibility.Collapsed;
            _ctxFolderItem.Visibility = Visibility.Collapsed;
            return;
        }

        try { TileHero.Background = new BrushConverter().ConvertFromString(g.StatusColour) as Brush ?? Brushes.SlateGray; }
        catch { }
        HeroDot.Fill = g.StatusBrush;
        LitName.Text = g.Name;
        LitStatus.Text = g.StatusText;
        LitDesc.Text = string.IsNullOrWhiteSpace(g.Description) ? "" : g.Description;

        var lines = new List<string>();
        if (g.IsInstalled)
        {
            var ver = g.InstalledVersion > 0 ? $"v{g.InstalledVersion}" : "—";
            var bver = g.HasUpdates && g.BaseVersion > 0 ? $"  (база v{g.BaseVersion})" : "";
            lines.Add($"Версия: {ver}{bver}");
            lines.Add($"Папка: {FullInstallPath(g)}");
            if (!string.IsNullOrEmpty(g.ExePath)) lines.Add($"Файл: {g.ExeName}");
            if (g.HasUpdates) lines.Add("Обновления: GitHub");
        }
        else
            lines.Add("Не установлена");
        LitMeta.Text = string.Join(Environment.NewLine, lines);

        var repo = EffectiveRepo(g);
        if (!g.IsInstalled)
        {
            _ctxMainItem.Visibility = repo != null ? Visibility.Visible : Visibility.Collapsed;
            _ctxMainItem.Header = "Скачать";
            _ctxPlayItem.Visibility = Visibility.Collapsed;
        }
        else
        {
            _ctxMainItem.Visibility = g.HasUpdates ? Visibility.Visible : Visibility.Collapsed;
            _ctxMainItem.Header = "Обновить";
            _ctxPlayItem.Visibility = Visibility.Visible;
        }
        _ctxVerifyItem.Visibility = g.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        _ctxUninstallItem.Visibility = g.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        _ctxEditItem.Visibility = Visibility.Visible;
        _ctxFolderItem.Visibility = g.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    }

    // ═══════════════════════════ Button state ═══════════════════════════

    private void UpdateButtonStates()
    {
        var g = Selected;
        var busy = g != null && g.IsDownloading;

        if (busy && g != null)
        {
            ShowMainBtnProgress(0, true, g.StatusText);
        }
        else
        {
            HideMainBtnProgress();
            if (g == null) BtnMain.Content = "Выберите игру";
else if (!g.IsInstalled) BtnMain.Content = "Скачать";
        else if (g.IsInstalled && (g.UpToDate || !g.HasUpdates)) BtnMain.Content = "Играть";
        else if (g.HasPendingUpdate) BtnMain.Content = "Обновить";
        else BtnMain.Content = "Играть";
        }

        BtnMain.IsEnabled = g != null && !busy;
        BtnVerify.IsEnabled = g != null && !busy && g.IsInstalled;
        BtnUninstall.IsEnabled = g != null && !busy && g.IsInstalled;
        BtnOpenFolder.IsEnabled = g != null && !busy && g.IsInstalled;
        BtnEdit.IsEnabled = g != null && !busy;
        BtnPause.IsEnabled = busy;
        BtnCancel.IsEnabled = busy;

        if (busy)
            BtnPause.Content = _pause?.IsPaused == true ? "▶ Продолжить" : "⏸ Пауза";
        else
            BtnPause.Content = "⏸ Пауза";
    }

    // Show the install progress bar in place of the big action button (Steam-style).
    private void ShowMainBtnProgress(double pct, bool indeterminate, string text)
    {
        BtnMain.Visibility = Visibility.Collapsed;
        BtnMainProgressWrap.Visibility = Visibility.Visible;
        BtnMainProgress.IsIndeterminate = indeterminate;
        if (!indeterminate) BtnMainProgress.Value = Math.Max(0, Math.Min(100, pct));
        BtnMainProgressText.Text = text;
    }

    private void HideMainBtnProgress()
    {
        BtnMainProgressWrap.Visibility = Visibility.Collapsed;
        BtnMain.Visibility = Visibility.Visible;
    }

    private static string FormatEtaShort(TimeSpan? eta)
    {
        if (eta == null) return "";
        if (eta.Value.TotalHours >= 1) return $" · осталось {eta.Value.Hours}ч {eta.Value.Minutes}мин";
        if (eta.Value.TotalMinutes >= 1) return $" · осталось {eta.Value.Minutes}мин";
        return $" · осталось {eta.Value.Seconds}сек";
    }

    // ═══════════════════════════ Tile right-click selection ═══════════════════════════

    private void GamesList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(GamesList);
        var hit = VisualTreeHelper.HitTest(GamesList, pos);
        if (hit == null) return;
        var item = FindParent<ListBoxItem>(hit.VisualHit);
        if (item != null) GamesList.SelectedItem = item.DataContext;
        if (Selected == null) return;
        RefreshDetails();
        _ctxMenu.PlacementTarget = GamesList;
        _ctxMenu.Placement = PlacementMode.MousePoint;
        _ctxMenu.IsOpen = true;
        e.Handled = true;
    }

    private static T? FindParent<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T typed) return typed;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // ═══════════════════════════ Pause / Cancel ═══════════════════════════

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        if (_pause == null) return;
        if (_pause.IsPaused)
        {
            _pause.Resume();
            BtnPause.Content = "⏸ Пауза";
            TxtProgress.Text = TxtProgress.Text.Replace(" [ПАУЗА]", "");
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
        _pause?.Resume();
    }

    // ═══════════════════════════ Launch ═══════════════════════════

    private void GamesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var g = Selected;
        if (g != null && g.IsInstalled) LaunchGame(g);
    }

    private void LaunchGame(GameItem g)
    {
        if (g == null) return;
        var dest = FullInstallPath(g);
        if (!Directory.Exists(dest))
        {
            MessageBox.Show("Игра ещё не установлена. Сначала скачайте её.",
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
            if (!string.IsNullOrWhiteSpace(g.LaunchArgs)) psi.Arguments = g.LaunchArgs;
            Process.Start(psi);
            TxtStatus.Text = $"Запущено: {g.Name}";
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
            RefreshDetails();
        }
    }

    // ═══════════════════════════ Main action button ═══════════════════════════

    private void BtnMain_Click(object sender, RoutedEventArgs e)
    {
        var g = Selected;
        if (g == null) return;
        if (!g.IsInstalled)
            BtnDownload_Click(sender, e);
        else if (g.IsInstalled && g.UpToDate)
            LaunchGame(g);
        else if (g.HasUpdates)
            BtnUpdate_Click(sender, e);
        else
            LaunchGame(g);
    }

    // ═══════════════════════════ Verify integrity ═══════════════════════════

    private void BtnVerify_Click(object sender, RoutedEventArgs e)
    {
        var g = Selected;
        if (g == null || !g.IsInstalled) return;
        var dir = FullInstallPath(g);
        g.StatusText = "Проверка…";
        TxtProgress.Text = $"Проверка целостности «{g.Name}»…";
        Progress.IsIndeterminate = true;
        Progress.Visibility = Visibility.Visible;
        Task.Run(() => BuildUpdater.VerifyFiles(g.Name, dir)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                Progress.IsIndeterminate = false;
                Progress.Visibility = Visibility.Collapsed;
                TxtProgress.Text = "";
                g.StatusText = t.IsFaulted ? "Ошибка проверки" : "Проверено ✓";
                RefreshDetails();
                var msg = t.IsFaulted ? "Ошибка: " + t.Exception?.InnerExceptions?.First()?.Message : t.Result;
                var icon = t.IsFaulted ? MessageBoxImage.Error : MessageBoxImage.Information;
                MessageBox.Show(msg, "Проверка целостности", MessageBoxButton.OK, icon);
            });
        });
    }

    // ═══════════════════════════ Uninstall ═══════════════════════════

    private void BtnUninstall_Click(object sender, RoutedEventArgs e)
    {
        var g = Selected;
        if (g == null || !g.IsInstalled) return;
        var dir = FullInstallPath(g);

        string sizeStr = "—";
        try
        {
            var size = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            sizeStr = FormatBytes(size);
        }
        catch { }

        var answer = MessageBox.Show(
            $"Удалить «{g.Name}»?\n\nРазмер: {sizeStr}\nПапка: {dir}\n\nВсе файлы будут удалены безвозвратно.",
            "Удаление игры", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось удалить: " + ex.Message, "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        g.IsInstalled = false;
        g.Status = GameStatus.NotDownloaded;
        g.StatusText = "Не скачано";
        g.InstallDir = "";
        g.ExePath = "";
        g.BaseVersion = 0;
        g.InstalledVersion = 0;
        SaveGames();
        RefreshDetails();
        UpdateButtonStates();
        TxtStatus.Text = $"«{g.Name}» удалена.";
    }

    // ═══════════════════════════ Open folder ═══════════════════════════

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var g = Selected;
        if (g == null || !g.IsInstalled) return;
        var dir = FullInstallPath(g);
        if (Directory.Exists(dir))
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        else
            MessageBox.Show("Папка не найдена.", "Steam2 Лаунчер", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ═══════════════════════════ Auto-update ═══════════════════════════

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
        catch { /* no internet / rate limit */ }
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
        UpdateBanner.Visibility = Visibility.Visible;
        TxtUpdate.Cursor = Cursors.Hand;
        TxtUpdate.MouseLeftButtonUp -= TxtUpdate_Click;
        TxtUpdate.MouseLeftButtonUp += TxtUpdate_Click;
    }

    private void TxtUpdate_Click(object sender, MouseButtonEventArgs e) => _ = SelfUpdateAsync();

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
                Progress.Visibility = Visibility.Visible;
            });

            var result = await _downloader.DownloadAsync(assetUrl, zipPath, progress, ct);
            if (!result.Success)
                throw new Exception("Ошибка скачивания обновления: " + result.Error);

            TxtUpdate.Text = $"Распаковка обновления {tag}…";
            Progress.IsIndeterminate = true;

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

    // ═══════════════════════════ Settings / Theme ═══════════════════════════

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
        BtnTheme.Content = _theme == "Light" ? "Светлая" : "Тёмная";
    }

    private void BtnTheme_Click(object sender, RoutedEventArgs e)
    {
        ApplyTheme(_theme == "Light" ? "Dark" : "Light");
        var settings = Storage.LoadSettings();
        settings.Theme = _theme;
        Storage.SaveSettings(settings);
    }

    // ═══════════════════════════ Game list ═══════════════════════════

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
                g.BaseVersion = s?.BaseVersion ?? 0;
                g.InstalledVersion = s?.InstalledVersion ?? 0;
                g.UpToDate = s?.UpToDate ?? false;
                var biPath = Path.Combine(full, "buildinfo.json");
                if (File.Exists(biPath))
                {
                    var bi = BuildUpdater.ReadBuildInfo(full);
                    g.BaseVersion = bi.Base;
                    g.InstalledVersion = bi.Version;
                }
                if (g.HasUpdates) g.StatusText = $"v{g.InstalledVersion} ✓";
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

        // Check installed repo games against their latest release, so the button can
        // become "Играть" when everything is already up to date.
        foreach (var g in _games.Where(x => x.IsInstalled && x.HasUpdates))
            _ = RefreshUpdateStatusAsync(g);
    }

    private async Task RefreshUpdateStatusAsync(GameItem g)
    {
        try
        {
            var (manifest, deltaUrl, tag, error) =
                await BuildUpdater.FetchLatestAsync(g.Repo, CancellationToken.None);
            bool upToDate;
            if (manifest == null)
            {
                // Repo released nothing usable (no delta / base yet): nothing to download.
                upToDate = true;
            }
            else if (manifest.Kind != "base"
                && BuildUpdater.IsNewer(manifest, g.BaseVersion, g.InstalledVersion))
            {
                upToDate = false;
            }
            else if (manifest.Kind == "base" && manifest.Base <= g.BaseVersion)
            {
                upToDate = true;
            }
            else
            {
                upToDate = false;
            }

            Dispatcher.Invoke(() =>
            {
                g.UpToDate = upToDate;
                if (upToDate && g.IsInstalled && g.HasUpdates)
                    g.StatusText = $"v{g.InstalledVersion} ✓";
                SaveGames();
                RefreshUpdateButton();
            });
        }
        catch { }
    }

    private bool GameInstalledLocally(string name, string manualInstallDir)
    {
        string full;
        if (!string.IsNullOrWhiteSpace(manualInstallDir))
        {
            full = Path.IsPathRooted(manualInstallDir) ? manualInstallDir : Path.Combine(_installRoot, manualInstallDir);
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
            InstalledVersion = g.InstalledVersion,
            UpToDate = g.UpToDate
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
            if (dlg.ExeChosen) g.ExePath = dlg.GameExePath;
            SaveGames();
            GamesList.Items.Refresh();
            RefreshDetails();
        }
    }

    // ═══════════════════════════ Install from base parts ═══════════════════════════

    private async Task InstallBaseFromRepoAsync(GameItem g)
    {
        _cts = new CancellationTokenSource();
        _pause = new PauseTokenSource();
        var ct = _cts.Token;
        g.IsBusy = true;
        g.StatusText = "Поиск базы…";
        Progress.IsIndeterminate = true;
        Progress.Visibility = Visibility.Visible;
        TxtProgress.Text = "Запрос частей базы…";
        UpdateButtonStates();

        var dest = FullInstallPath(g);
        Directory.CreateDirectory(dest);
        var workDir = Path.Combine(Path.GetTempPath(), "steam2base_" + GameEntry.Sanitize(g.Name) + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        try
        {
            var (manifest, partUrls, error) = await BuildUpdater.FetchBasePartsAsync(g.Repo, ct);
            if (manifest == null || partUrls.Count == 0)
            {
                if (!string.IsNullOrEmpty(error))
                    throw new Exception("Не удалось получить базу:\n" + error);
                throw new Exception("База не найдена в репозитории обновлений.");
            }

            TxtProgress.Text = "Определение размеров…";
            var sizes = new long[partUrls.Count];
            long totalBytes = 0;
            for (int i = 0; i < partUrls.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                _pause.Wait(ct);
                sizes[i] = (await _downloader.GetFileSizeAsync(partUrls[i], ct)) ?? 0;
                totalBytes += sizes[i];
            }
            var hasTotal = totalBytes > 0;
            long downloadedSoFar = 0;

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
                    Progress.Visibility = Visibility.Visible;
                    Progress.Value = Math.Min(pct, 100);
                    var mainTxt = $"{FormatPercent(pct)} · {speed / 1048576.0:0.0} МБ/с" + FormatEtaShort(eta);
                    ShowMainBtnProgress(Math.Min(pct, 100), false, mainTxt);
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

            g.StatusText = "Распаковка…";
            Progress.IsIndeterminate = true;
            ShowMainBtnProgress(0, true, "Распаковка…");

            for (int i = 0; i < localParts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                _pause.Wait(ct);

                var partPath = localParts[i];
                TxtProgress.Text = $"Распаковка: часть {i + 1}/{localParts.Count} ({Path.GetFileName(partPath)})";
                ShowMainBtnProgress(0, true, $"Распаковка: {i + 1}/{localParts.Count}");

                var exProgress = new Progress<string>(msg =>
                {
                    g.StatusText = msg;
                    TxtProgress.Text = msg;
                });
                await ArchiveExtractor.ExtractAsync(partPath, dest, exProgress, ct, _pause);
                try { File.Delete(partPath); } catch { }
            }

            BuildUpdater.WriteBuildInfo(dest, manifest.Base, manifest.Version);
            try { BuildUpdater.WriteFileManifest(dest); } catch { }
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
            Progress.Visibility = Visibility.Collapsed;
            RefreshUpdateButton();
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    // ═══════════════════════════ Delta update ═══════════════════════════

    private async Task UpdateAsync(GameItem g)
    {
        _cts = new CancellationTokenSource();
        _pause = new PauseTokenSource();
        var ct = _cts.Token;
        g.IsBusy = true;
        g.StatusText = "Проверка обновлений…";
        Progress.IsIndeterminate = true;
        Progress.Visibility = Visibility.Visible;
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
                g.UpToDate = true;
                SaveGames();
                RefreshUpdateButton();
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
                    if (g.InstalledVersion > 0) await UpdateAsync(g);
                }
                return;
            }

            var deltaName = Downloader.FileNameFromUrl(deltaUrl ?? "");
            if (string.IsNullOrEmpty(deltaName)) deltaName = "update.zip";
            var downloadPath = Path.Combine(Path.GetTempPath(), "steam2upd_" + Guid.NewGuid().ToString("N")[..8]
                + "_" + GameEntry.Sanitize(deltaName));
            g.StatusText = $"Скачивание обновления v{manifest.Version}…";

            var progress = new Progress<DownloadProgress>(p =>
            {
                Progress.IsIndeterminate = false;
                Progress.Visibility = Visibility.Visible;
                Progress.Value = p.Percent;
                ShowMainBtnProgress(p.Percent, false,
                    $"{FormatPercent(p.Percent)} · {p.SpeedBytesPerSec / 1048576.0:0.0} МБ/с" + FormatEtaShort(p.Eta));
                TxtProgress.Text = $"Обновление: {p.Message}";
            });
            var result = await _downloader.DownloadAsync(deltaUrl ?? "", downloadPath, progress, ct, PromptCredentials, _pause);
            if (!result.Success)
                throw new Exception("Ошибка загрузки обновления: " + result.Error);

            if (ArchiveExtractor.IsArchive(downloadPath, result.ContentType))
            {
                g.StatusText = "Распаковка обновления…";
                ShowMainBtnProgress(0, true, "Распаковка…");
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
            try { BuildUpdater.WriteFileManifest(installDir); } catch { }
            g.BaseVersion = manifest.Base;
            g.InstalledVersion = manifest.Version;
            g.UpToDate = true;
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
            Progress.Visibility = Visibility.Collapsed;
            RefreshUpdateButton();
        }
    }

    private void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        var g = Selected;
        if (g == null) return;
        _ = UpdateAsync(g);
    }

    // ═══════════════════════════ SmartSteamEmu ═══════════════════════════

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

    // ═══════════════════════════ Auth ═══════════════════════════

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

    // ═══════════════════════════ Download ═══════════════════════════

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
        Progress.Visibility = Visibility.Visible;
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
                Progress.Visibility = Visibility.Visible;
                Progress.Value = p.Percent;
                ShowMainBtnProgress(p.Percent, false,
                    $"{FormatPercent(p.Percent)} · {p.SpeedBytesPerSec / 1048576.0:0.0} МБ/с" + FormatEtaShort(p.Eta));
                TxtProgress.Text = p.Message;
            });

            var result = await _downloader.DownloadAsync(directUrl, downloadPath, progress, ct, PromptCredentials, _pause);
            if (!result.Success)
                throw new Exception("Ошибка загрузки: " + result.Error);

            var dest = FullInstallPath(g);
            Directory.CreateDirectory(dest);

            if (ArchiveExtractor.IsArchive(downloadPath, result.ContentType))
            {
                ShowMainBtnProgress(0, true, "Распаковка…");
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
            try { BuildUpdater.WriteFileManifest(dest); } catch { }
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
            Progress.Visibility = Visibility.Collapsed;
            RefreshUpdateButton();
        }
    }

    // ═══════════════════════════ Helpers ═══════════════════════════

    private void RefreshUpdateButton() => UpdateButtonStates();

    private static string FormatBytes(long b)
    {
        if (b >= 1073741824) return $"{b / 1073741824.0:0.00} ГБ";
        if (b >= 1048576) return $"{b / 1048576.0:0.0} МБ";
        if (b >= 1024) return $"{b / 1024.0:0.0} КБ";
        return $"{b} Б";
    }

    private static string FormatPercent(double pct) => $"{pct:0.0}%";
}
