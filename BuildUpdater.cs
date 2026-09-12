using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Steam2Launcher;

/// <summary>GitHub release manifest for incremental game updates (delta patches).</summary>
public class BuildManifest
{
    public string Kind { get; set; } = "delta";
    public string Name { get; set; } = "";
    public int Base { get; set; }
    public int Version { get; set; }
    public string Delta { get; set; } = "";
    public List<string> Removed { get; set; } = new();

    /// <summary>Part file names for base releases (kind = "base"), in order.</summary>
    public List<string> Parts { get; set; } = new();

    public string Tag { get; set; } = "";
}

/// <summary>
/// Fetches and applies incremental updates published as GitHub releases on a per-game repo.
/// Release layout (see publish.ps1):
///   build-N tag with 2 assets: manifest.json + fstop_delta_N.zip (cumulative vs base).
/// Installed builds carry buildinfo.json {name, base, version} at the install root.
/// </summary>
public static class BuildUpdater
{
    private const string BuildInfoName = "buildinfo.json";

    /// <summary>Reads (base, version) from buildinfo.json inside the install dir; default (1,1).</summary>
    public static (int Base, int Version) ReadBuildInfo(string installDir)
    {
        if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
        {
            var path = Path.Combine(installDir, BuildInfoName);
            if (File.Exists(path))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var b = doc.RootElement.TryGetProperty("base", out var be) ? be.GetInt32() : 1;
                    var v = doc.RootElement.TryGetProperty("version", out var ve) ? ve.GetInt32() : 1;
                    return (b, v);
                }
                catch { }
            }
        }
        return (1, 1);
    }

    /// <summary>Writes buildinfo.json marking the installed build.</summary>
    public static void WriteBuildInfo(string installDir, int baseVersion, int version)
    {
        try
        {
            var info = new Dictionary<string, int> { ["base"] = baseVersion, ["version"] = version };
            File.WriteAllText(Path.Combine(installDir, BuildInfoName),
                JsonSerializer.Serialize(info));
        }
        catch { }
    }

    /// <summary>True when the remote release is newer than the installed build.</summary>
    public static bool IsNewer(BuildManifest remote, int installedBase, int installedVersion)
    {
        if (remote == null) return false;
        if (remote.Base != installedBase) return remote.Base > installedBase;
        return remote.Version > installedVersion;
    }

    /// <summary>
    /// Fetches the latest BASE release parts (kind="base") for owner/repo: the freshly
    /// installed full build. Returns the ordered part download URLs + resolved manifest.
    /// </summary>
    public static async Task<(BuildManifest? Manifest, List<string> PartUrls, string? Error)>
        FetchBasePartsAsync(string repo, CancellationToken ct)
    {
        try
        {
            using var http = MakeHttp();
            var url = $"https://api.github.com/repos/{repo}/releases?per_page=30";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return (null, new(), $"GitHub ответил HTTP {(int)resp.StatusCode}");
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            List<(string Tag, string ManifestUrl)> candidates = new();
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                var tag = rel.TryGetProperty("tag_name", out var tg) ? tg.GetString() : null;
                string? mUrl = null;
                if (rel.TryGetProperty("assets", out var assets))
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? n.GetString() : "";
                        var bUrl = a.TryGetProperty("browser_download_url", out var bu) ? bu.GetString() : null;
                        if (name == "manifest.json" && bUrl != null) mUrl = bUrl;
                    }
                }
                if (mUrl != null) candidates.Add((tag ?? "", mUrl));
            }
            if (candidates.Count == 0)
                return (null, new(), "Нет релизов с manifest.json");

            // newest-first, take the first base-kind release
            foreach (var (tag, manifestUrl) in candidates)
            {
                var manifestText = await http.GetStringAsync(manifestUrl, ct);
                BuildManifest? m;
                try { m = JsonSerializer.Deserialize<BuildManifest>(manifestText); }
                catch { m = null; }
                if (m == null || m.Kind != "base") continue;

                m.Tag = tag;
                var partUrls = new List<string>();
                using var mdoc = JsonDocument.Parse(manifestText);
                if (m.Parts.Count > 0)
                {
                    // map part names -> browser urls from the same release's assets
                    var assets = new List<(string Name, string Url)>();
                    var relUrl = $"https://api.github.com/repos/{repo}/releases/tags/{tag}";
                    using var relResp = await http.GetAsync(relUrl, ct);
                    if (relResp.IsSuccessStatusCode)
                    {
                        var relJson = await relResp.Content.ReadAsStringAsync(ct);
                        using var rdoc = JsonDocument.Parse(relJson);
                        if (rdoc.RootElement.TryGetProperty("assets", out var ra))
                        {
                            foreach (var a in ra.EnumerateArray())
                            {
                                var nm = a.TryGetProperty("name", out var nn) ? nn.GetString() : "";
                                var bu = a.TryGetProperty("browser_download_url", out var bb) ? bb.GetString() : null;
                                if (nm != null && bu != null) assets.Add((nm, bu));
                            }
                        }
                    }
                    foreach (var p in m.Parts)
                    {
                        var found = assets.FirstOrDefault(x => x.Name == p);
                        if (found.Name != null) partUrls.Add(found.Url);
                    }
                }
                if (partUrls.Count != m.Parts.Count)
                    return (null, new(), $"Не найдены все части базы ({partUrls.Count}/{m.Parts.Count})");
                return (m, partUrls, null);
            }
            return (null, new(), "В релизах нет базы (manifest kind=base)");
        }
        catch (Exception ex)
        {
            return (null, new(), ex.Message);
        }
    }

    /// <summary>
    /// Fetches the latest release manifest for owner/repo and resolves the delta asset URL.
    /// Returns null if the repo has no release with a manifest.json asset.
    /// </summary>
    public static async Task<(BuildManifest? Manifest, string? DeltaUrl, string? Tag, string? Error)>
        FetchLatestAsync(string repo, CancellationToken ct)
    {
        try
        {
            using var http = MakeHttp();
            var url = $"https://api.github.com/repos/{repo}/releases/latest";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return (null, null, null, resp.StatusCode == HttpStatusCode.NotFound
                    ? "Нет релизов в репозитории обновлений"
                    : $"GitHub ответил HTTP {(int)resp.StatusCode}");
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.TryGetProperty("tag_name", out var tg) ? tg.GetString() : null;

            string? manifestUrl = null;
            string? deltaUrl = null;
            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : "";
                    var bUrl = a.TryGetProperty("browser_download_url", out var bu) ? bu.GetString() : null;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(bUrl)) continue;
                    if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                    {
                        manifestUrl = bUrl;
                    }
                    else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        deltaUrl ??= bUrl;
                }
            }
            if (string.IsNullOrEmpty(manifestUrl) || manifestUrl == null)
                return (null, null, tag, "В релизе нет manifest.json");
            if (deltaUrl == null)
                return (null, null, tag, "В релизе нет файла обновления (*.zip)");

            var manifestText = await http.GetStringAsync(manifestUrl, ct);
            var manifest = JsonSerializer.Deserialize<BuildManifest>(manifestText);
            if (manifest == null) return (null, null, tag, "Ошибка чтения manifest.json");
            manifest.Removed ??= new();
            return (manifest, deltaUrl, tag, null);
        }
        catch (Exception ex)
        {
            return (null, null, null, ex.Message);
        }
    }

    /// <summary>Deletes files listed as removed from the install dir, safely contained.</summary>
    public static void ApplyRemoved(string installDir, IEnumerable<string> removed)
    {
        var root = Path.GetFullPath(installDir).TrimEnd('\\', '/');
        foreach (var rel in removed)
        {
            var safe = rel.Replace('/', '\\').TrimStart('\\');
            if (safe.Length == 0 || safe.Contains("..")) continue;
            var full = Path.GetFullPath(Path.Combine(installDir, safe));
            if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                && !full.Equals(root, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (File.Exists(full)) File.Delete(full);
                else if (Directory.Exists(full)) Directory.Delete(full, true);
            }
            catch { }
        }
    }

    private static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Steam2Launcher/1.3.0");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }
}