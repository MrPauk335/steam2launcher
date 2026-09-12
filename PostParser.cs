using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Steam2Launcher;

/// <summary>
/// Loads games from either games.json (Name/Description/Url array) or post.txt.
/// </summary>
public static class PostParser
{
    /// <summary>Loads entries from a file: .json uses strict schema, else chat-format post.txt.</summary>
    public static List<GameEntry> LoadGames(string path)
    {
        var text = File.ReadAllText(path);
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return ParseJson(text);
        return Parse(text);
    }

    public static List<GameEntry> ParseJson(string json)
    {
        var result = new List<GameEntry>();
        List<GameEntry>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<GameEntry>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException)
        {
            return result;
        }
        if (items == null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in items)
        {
            if (string.IsNullOrWhiteSpace(e.Url) || !seen.Add(e.Url.Trim())) continue;
            result.Add(new GameEntry
            {
                Name = string.IsNullOrWhiteSpace(e.Name) ? Downloader.FileNameFromUrl(e.Url) : e.Name.Trim(),
                Description = (e.Description ?? "").Trim(),
                DirectUrl = (e.DirectUrl ?? "").Trim(),
                Exe = (e.Exe ?? "").Trim(),
                LaunchArgs = (e.LaunchArgs ?? "").Trim(),
                Repo = (e.Repo ?? "").Trim(),
                Url = e.Url.Trim(),
                Status = GameStatus.NotDownloaded
            });
        }
        return result;
    }

    private static readonly Regex UrlRegex = new(
        @"(https?://[^\s\)'\""<>]+)", RegexOptions.Compiled);

    public static List<GameEntry> Parse(string text)
    {
        var result = new List<GameEntry>();
        var seen = new HashSet<string>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#") || line.StartsWith("//")) continue;

            var urls = UrlRegex.Matches(line).Cast<Match>()
                .Select(m => StripTrailingPunct(m.Value))
                .Where(u => seen.Add(u))
                .ToList();
            if (urls.Count == 0) continue;

            foreach (var url in urls)
            {
                var name = ExtractName(line, url);
                result.Add(new GameEntry { Name = name, Url = url, Status = GameStatus.NotDownloaded });
            }
        }
        return result;
    }

    private static string StripTrailingPunct(string url)
    {
        while (url.Length > 0 && ".,;:)]}".Contains(url[^1]))
            url = url[..^1];
        return url;
    }

    /// <summary>Names can differ per line; url is used to locate surrounding text.</summary>
    private static string ExtractName(string line, string url)
    {
        // Explicit "Name | url"
        var bar = line.IndexOf('|');
        if (bar >= 0)
        {
            var left = line[..bar].Trim();
            if (left.Length > 0 && !left.StartsWith("http"))
                return Clean(left);
        }

        var idx = line.IndexOf(url, StringComparison.OrdinalIgnoreCase);
        string? beforeName = null;
        string? afterName = null;

        if (idx > 0)
        {
            var before = StripChatMetric(line[..idx]);
            beforeName = ExtractTitle(before);
        }

        if (idx >= 0 && idx + url.Length < line.Length)
        {
            var after = line[(idx + url.Length)..];
            // stop at another URL or explanatory parentheses after the name
            var sec = UrlRegex.Match(after);
            if (sec.Success) after = after[..sec.Index];
            after = after.Trim().Trim('(', ')', '.', ';', ',').Trim();
            var paren = after.IndexOf('(');
            if (paren > 0) after = after[..paren].Trim();
            after = Regex.Replace(after, @"\s+", " ").Trim().TrimEnd(':', ',', '.');
            if (after.Length > 1 && after.Length < 60 && !after.Contains("http"))
                afterName = after;
        }

        // Prefer "before" except when it looks like a bare nick (single short token) or a URL leftover
        if (!string.IsNullOrEmpty(beforeName) && !beforeName.Contains("http"))
        {
            var tokens = beforeName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var singleNick = tokens.Length == 1 && beforeName.Length <= 16;
            if (!singleNick) return beforeName;
        }

        if (!string.IsNullOrEmpty(afterName)) return afterName;

        if (!string.IsNullOrEmpty(beforeName) && !beforeName.Contains("http")) return beforeName;

        var uri = TryGetFileName(url);
        return uri ?? "Игра " + GameEntry.Sanitize(url);
    }

    /// <summary>Strips "[date] nick:" and plain "nick:" when the rest looks like content, not a game title.</summary>
    private static string StripChatMetric(string s)
    {
        // Timestamp form: "[...] Nick:"
        var m = Regex.Match(s, @"^\s*\[[^\]]*\]\s*[^:\[\]]{1,40}:?\s*");
        if (m.Success) return s[m.Length..];
        return s;
    }

    private static string ExtractTitle(string s)
    {
        s = s.Trim().Trim(' ', '(', ')', ':', ',', '.');
        // "Title: скачать" / "Title: ссылка" etc.
        var m = Regex.Match(s, @"^(.*?)\s*:\s*(?:скачать|Скачать|download|Download|ссылка|тут)\s*[()]*$");
        if (m.Success && m.Groups[1].Value.Trim().Length > 0)
            return m.Groups[1].Value.Trim();
        // "Title: anything"
        m = Regex.Match(s, @"^(.*?)\s*:\s*.*$");
        if (m.Success && m.Groups[1].Value.Trim().Length > 0)
            return m.Groups[1].Value.Trim();
        return s;
    }

    private static string? TryGetFileName(string url)
    {
        try
        {
            var u = new Uri(url);
            var last = u.Segments[^1];
            last = Uri.UnescapeDataString(last).Trim('/');
            if (string.IsNullOrEmpty(last) || last == "/") return null;
            if (last.IndexOf('.') > 0 && last.Length < 90) return last;
            if (last.Length >= 3 && last.Length <= 50) return last;
        }
        catch { }
        return null;
    }

    private static string Clean(string s)
    {
        s = Regex.Replace(s, @"^\s*[\[\]()]*\s*", "");
        s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd(':');
        return s;
    }
}