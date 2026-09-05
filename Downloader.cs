using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Steam2Launcher;

public class DownloadProgress
{
    public long DownloadedBytes { get; set; }
    public long? TotalBytes { get; set; }
    public string Message { get; set; } = "";
    public double Percent => TotalBytes.HasValue && TotalBytes > 0
        ? (double)DownloadedBytes / TotalBytes.Value * 100.0
        : 0;
}

public class DownloadResult
{
    public bool Success { get; set; }
    public string LocalPath { get; set; } = "";
    public string Error { get; set; } = "";
    public string ContentType { get; set; } = "";
}

/// <summary>Thrown when auto-download is not possible and the URL should be opened in a browser.</summary>
public class ManualDownloadNeededException : Exception
{
    public string PageUrl { get; }
    public ManualDownloadNeededException(string message, string pageUrl) : base(message) => PageUrl = pageUrl;
}

/// <summary>Thrown when the server requires HTTP Basic auth that the user did not provide.</summary>
public class AuthRequiredException : Exception
{
    public Uri Uri { get; }
    public AuthRequiredException(string message, Uri uri) : base(message) => Uri = uri;
}

/// <summary>Prompt callback: returns "user:pass", or null if the user cancelled.
/// <paramref name="tryIndex"/> = number of failed attempts with credentials already made.</summary>
public delegate Task<string?> CredentialPrompt(Downloader downloader, Uri uri, int tryIndex);

public class Downloader
{
    private const string GofileWtFallback = "4fd6sg89d7s6";

    private readonly HttpClient _http;

    public Downloader(bool keepCookies = false)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            UseCookies = keepCookies
        };
        _http = new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
    }

    /// <summary>
    /// Resolves a share-page URL to a direct download URL + suggested file name.
    /// </summary>
    public async Task<(string directUrl, string fileName)> ResolveAsync(string url,
        CancellationToken ct)
    {
        url = url.Trim();

        if (url.Contains("gofile.io", StringComparison.OrdinalIgnoreCase))
            return await ResolveGofile(url, ct);

        if (url.Contains("mediafire.com", StringComparison.OrdinalIgnoreCase))
            return await ResolveMediafire(url, ct);

        if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            return await ResolveGithub(url, ct);

        if (url.Contains("moddb.com", StringComparison.OrdinalIgnoreCase))
            return await ResolveModdb(url, ct);

        // Direct link
        return (url, FileNameFromUrl(url));
    }

    // ---------------- Gofile ----------------

    private async Task<(string, string)> ResolveGofile(string pageUrl, CancellationToken ct)
    {
        var m = Regex.Match(pageUrl, @"gofile\.io/d/([A-Za-z0-9]+)");
        if (!m.Success) throw new Exception("Не удалось разобрать ссылку gofile: " + pageUrl);
        var id = m.Groups[1].Value;

        try
        {
            var wt = await FetchGofileWebsiteToken(ct);

            // 1) Create a guest account for a usable token
            var token = await CreateGofileTokenAsync(ct);

            return await FetchGofileContent(id, token, wt, pageUrl, ct);
        }
        catch (ManualDownloadNeededException)
        {
            throw;
        }
        catch (Exception ex) when (ex.Message.Contains("rate", StringComparison.OrdinalIgnoreCase)
                                   || ex.Message.Contains("429"))
        {
            throw new ManualDownloadNeededException(
                "gofile: превышен лимит запросов (429). Попробуйте позже или скачайте вручную:",
                pageUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
        {
            throw new ManualDownloadNeededException(
                "gofile.io недоступен из вашей сети — TCP-подключение блокируется на уровне провайдера/маршрута.\n" +
                "Включите VPN или прокси и нажмите «Скачать» ещё раз. Либо скачайте вручную:\n\n" + ex.Message,
                pageUrl);
        }
    }

    private async Task<string> FetchGofileWebsiteToken(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://gofile.io/dist/js/config.js");
            req.Headers.Referrer = new Uri("https://gofile.io/");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var mm = Regex.Match(body, @"wt\s*[:=]\s*[""']([A-Za-z0-9]{6,})[""']");
                if (mm.Success) return mm.Groups[1].Value;
            }
        }
        catch { }
        return GofileWtFallback;
    }

    private async Task<string> CreateGofileTokenAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.gofile.io/accounts")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Origin", "https://gofile.io");
        req.Headers.Add("Referer", "https://gofile.io/");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var d)
            && d.TryGetProperty("token", out var t))
            return t.GetString() ?? "";
        if (body.Contains("rateLimit")) throw new Exception("gofile: rate limit while creating account");
        throw new Exception("gofile: не удалось создать гостевой аккаунт");
    }

    private async Task<(string, string)> FetchGofileContent(string id, string token, string wt,
        string pageUrl, CancellationToken ct)
    {
        var apiUrl = $"https://api.gofile.io/contents/{id}";
        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Website-Token", wt);
        req.Headers.Referrer = new Uri("https://gofile.io/");
        req.Headers.Add("Origin", "https://gofile.io");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if ((int)resp.StatusCode == 429 || body.Contains("rateLimit"))
            throw new Exception("gofile: rate limit (429), попробуйте позже");

        if ((int)resp.StatusCode == 401 || body.Contains("notPremium") || body.Contains("wrongToken"))
            throw new ManualDownloadNeededException(
                "gofile: доступ к файлам ограничен (требуется premium/авторизация). Скачайте вручную:",
                pageUrl);

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("children", out var children))
            throw new ManualDownloadNeededException(
                "gofile: не удалось получить содержимое. Скачайте вручную:", pageUrl);

        foreach (var child in children.EnumerateObject())
        {
            var c = child.Value;
            string? link = null;
            if (c.TryGetProperty("directLink", out var dl)) link = dl.GetString();
            if (string.IsNullOrWhiteSpace(link) && c.TryGetProperty("link", out var l)) link = l.GetString();
            if (string.IsNullOrWhiteSpace(link)) continue;

            var name = c.TryGetProperty("name", out var n) ? n.GetString() : null;
            return (link, string.IsNullOrWhiteSpace(name) ? FileNameFromUrl(link) : name);
        }
        throw new ManualDownloadNeededException("gofile: в папке нет файлов или нужен пароль. Скачайте вручную:", pageUrl);
    }

    // ---------------- Mediafire ----------------

    private async Task<(string, string)> ResolveMediafire(string pageUrl, CancellationToken ct)
    {
        var html = await _http.GetStringAsync(pageUrl, ct);
        var m = Regex.Match(html,
            @"https?://download[^""' ]+\.mediafire\.com/[^""' ]+");
        if (!m.Success)
            m = Regex.Match(html, @"href=""(https?://download[^""]+)""");
        if (!m.Success) throw new Exception("mediafire: не удалось найти прямую ссылку");
        var direct = m.Groups[1].Success ? m.Groups[1].Value : m.Value;
        direct = Regex.Replace(direct, @"[\\/]+(?=[a-z]{3}\.\w+\.mediafire)", "/");
        return (direct, FileNameFromUrl(direct));
    }

    // ---------------- GitHub ----------------

    private async Task<(string, string)> ResolveGithub(string pageUrl, CancellationToken ct)
    {
        // Forms: https://github.com/{owner}/{repo}/releases/tag/{tag}
        //        https://github.com/{owner}/{repo}/releases/latest
        var m = Regex.Match(pageUrl, @"github\.com/([^/]+)/([^/?#]+)/releases/(?:tag|download)/(.+?)(?:[?#]|$)");
        if (!m.Success)
        {
            m = Regex.Match(pageUrl, @"github\.com/([^/]+)/([^/?#]+)/releases/latest[?#]?");
            if (m.Success)
            {
                var lastUrl = $"https://api.github.com/repos/{m.Groups[1].Value}/{m.Groups[2].Value}/releases/latest";
                using var lreq = new HttpRequestMessage(HttpMethod.Get, lastUrl);
                lreq.Headers.UserAgent.ParseAdd("Steam2Launcher/1.0");
                using var lres = await _http.SendAsync(lreq, HttpCompletionOption.ResponseHeadersRead, ct);
                var lbody = await lres.Content.ReadAsStringAsync(ct);
                using var ldoc = JsonDocument.Parse(lbody);
                return PickGithubAsset(ldoc.RootElement.Clone());
            }
            throw new Exception("Не удалось разобрать ссылку GitHub release");
        }

        var owner = m.Groups[1].Value;
        var repo = m.Groups[2].Value;
        var tag = Uri.UnescapeDataString(m.Groups[3].Value);
        var releases = await GetGithubReleases($"{owner}/{repo}", ct);
        foreach (var rel in releases.EnumerateArray())
        {
            if (rel.TryGetProperty("tag_name", out var tg) && tg.GetString() == tag)
                return PickGithubAsset(rel);
        }
        throw new Exception($"Найдена версия GitHub {tag}");
    }

    private static (string, string) PickGithubAsset(JsonElement releaseOrArray)
    {
        IEnumerable<JsonElement> releases = releaseOrArray.ValueKind == JsonValueKind.Array
            ? releaseOrArray.EnumerateArray()
            : new[] { releaseOrArray };

        foreach (var rel in releases)
        {
            if (!rel.TryGetProperty("assets", out var assets)) continue;
            foreach (var a in assets.EnumerateArray())
            {
                if (!a.TryGetProperty("browser_download_url", out var urlEl)) continue;
                var u = urlEl.GetString();
                if (string.IsNullOrWhiteSpace(u)) continue;
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                return (u, string.IsNullOrWhiteSpace(name) ? FileNameFromUrl(u) : name);
            }
        }
        throw new Exception("В релизе GitHub не найдено файлов для скачивания");
    }

    private async Task<JsonElement> GetGithubReleases(string ownerRepo, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{ownerRepo}/releases?per_page=50";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Steam2Launcher/1.0");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    // ---------------- ModDB ----------------

    private async Task<(string, string)> ResolveModdb(string pageUrl, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            req.Headers.Referrer = new Uri("https://www.moddb.com/");
            req.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                throw new ManualDownloadNeededException("moddb: страница закрыта от ботов (403). Скачайте вручную:", pageUrl);
            var html = await resp.Content.ReadAsStringAsync(ct);

            // Step 1: find /downloads/start/{id}
            var start = Regex.Match(html, @"downloads/start/(\d+)");
            if (!start.Success)
                throw new ManualDownloadNeededException("moddb: не найден пермалинк скачивания. Скачайте вручную:", pageUrl);
            var startId = start.Groups[1].Value;

            // Step 2: mirror list page
            var allUrl = $"https://www.moddb.com/downloads/start/{startId}/all";
            using var req2 = new HttpRequestMessage(HttpMethod.Get, allUrl);
            req2.Headers.Referrer = new Uri(pageUrl);
            req2.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            using var resp2 = await _http.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, ct);
            var html2 = await resp2.Content.ReadAsStringAsync(ct);

            var mirror = Regex.Match(html2, @"(https?://www\.moddb\.com/downloads/mirror/\d+/[^""' ]+)(?=[^""' ]*[""'])");
            var fallback = Regex.Match(html2, @"https?://[^""' ]+\.(?:zip|rar|7z)(?=[^""' ]*[""'])");
            if (!mirror.Success && !fallback.Success)
                throw new ManualDownloadNeededException("moddb: не удалось получить зеркало. Скачайте вручную:", pageUrl);

            var direct = mirror.Success ? mirror.Groups[1].Value : fallback.Value;
            return (direct, FileNameFromUrl(direct));
        }
        catch (ManualDownloadNeededException) { throw; }
        catch (Exception ex)
        {
            throw new ManualDownloadNeededException("moddb: ошибка загрузки: " + ex.Message + " Скачайте вручную:", pageUrl);
        }
    }

    // ---------------- Raw download ----------------

    /// <summary>
    /// Downloads a (resolved) direct URL to destPath, reporting progress.
    /// If the server replies 401, the <paramref name="credentials"/> callback is invoked
    /// and the request is retried once with HTTP Basic auth.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(string directUrl, string destPath,
        IProgress<DownloadProgress> progress, CancellationToken ct,
        CredentialPrompt? credentials = null)
    {
        var uri = new Uri(directUrl);
        var credentialTries = 0;

        while (true)
        {
            string? creds = null;
            if (credentialTries > 0)
            {
                if (credentials == null)
                    throw new AuthRequiredException(
                        $"Сервер {uri.Host} требует авторизацию (HTTP 401). Укажите логин и пароль.", uri);
                creds = await credentials(this, uri, credentialTries);
                if (string.IsNullOrEmpty(creds))
                    throw new AuthRequiredException(
                        $"Сервер {uri.Host} требует авторизацию (HTTP 401), но вы отменили вход.", uri);
                if (credentialTries > 3)
                    throw new AuthRequiredException(
                        $"Сервер {uri.Host} не принял логин/пароль (HTTP 401). Скачайте файл вручную.", uri);
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, directUrl);
            if (!string.IsNullOrEmpty(creds))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(creds)));

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode == 401)
            {
                credentialTries++;
                continue; // retry with credentials (or throw)
            }

            if (!resp.IsSuccessStatusCode)
                return new DownloadResult { Success = false, Error = $"HTTP {(int)resp.StatusCode}" };

            var total = resp.Content.Headers.ContentLength;
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 81920, useAsync: true);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                progress?.Report(new DownloadProgress
                {
                    DownloadedBytes = read,
                    TotalBytes = total,
                    Message = $"{read / 1048576.0:0.0} МБ / {(total ?? 0) / 1048576.0:0.0} МБ"
                });
            }

            return new DownloadResult
            {
                Success = true,
                LocalPath = destPath,
                ContentType = contentType
            };
        }
    }

    public static string FileNameFromUrl(string url)
    {
        try
        {
            var u = new Uri(url);
            var name = Path.GetFileName(Uri.UnescapeDataString(u.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(name) && name != "/")
                return name;
        }
        catch { }
        return "download_" + Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>Fetches a text payload (e.g. remote games.json) with the same client settings.</summary>
    public async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode}: {url}");
        return await resp.Content.ReadAsStringAsync(ct);
    }
}