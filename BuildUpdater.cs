using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Steam2Launcher;

/// <summary>GitHub release manifest for incremental game updates (delta patches).</summary>
public class BuildManifest
{
    public string Name { get; set; } = "";
    public int Base { get; set; }
    public int Version { get; set; }
    public string Delta { get; set; } = "";
    public List<string> Removed { get; set; } = new();
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
                        manifestUrl = bUrl;
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