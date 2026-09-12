using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using SharpCompress.Archives;

namespace Steam2Launcher;

public static class ArchiveExtractor
{
    /// <summary>True if the file looks like a compressed archive we can handle.</summary>
    public static bool IsArchive(string path, string contentType)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".zip": return true;
            case ".7z": return true;
            case ".rar": return true;
        }
        if (contentType.Contains("zip", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static async Task ExtractAsync(string archivePath, string destDir,
        IProgress<string> progress, CancellationToken ct, PauseTokenSource? pause = null)
    {
        Directory.CreateDirectory(destDir);
        var ext = Path.GetExtension(archivePath).ToLowerInvariant();

        if (ext == ".zip")
        {
            await ExtractZipManagedAsync(archivePath, destDir, progress, ct, pause);
            return;
        }

        // 7z / rar => managed SharpCompress first, fall back to external 7-Zip
        try
        {
            await ExtractManagedAsync(archivePath, destDir, progress, ct, pause);
            return;
        }
        catch (Exception ex) when (ex is SharpCompress.Common.ArchiveException
                                   || ex is SharpCompress.Common.InvalidFormatException)
        {
            // fall through to 7-Zip
        }

        var sevenZip = FindSevenZip();
        if (sevenZip == null)
            throw new Exception("Не удалось распаковать архив. Для .7z/.rar можно положить 7z.exe в папку лаунчера или установить 7-Zip.");

        var psi = new ProcessStartInfo
        {
            FileName = sevenZip,
            Arguments = $"x \"{archivePath}\" -o\"{destDir}\" -y",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        using var proc = Process.Start(psi);
        if (proc == null)
            throw new Exception("Не удалось запустить 7-Zip.");
        var errTask = proc.StandardError.ReadToEndAsync();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync(ct);
        var msg = await outTask + await errTask;
        if (proc.ExitCode != 0)
            throw new Exception("7-Zip ошибка: " + msg);
    }

    private static async Task ExtractZipManagedAsync(string archivePath, string destDir,
        IProgress<string> progress, CancellationToken ct, PauseTokenSource? pause)
    {
        var destFull = Path.GetFullPath(destDir);
        await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var total = zip.Entries.Count;
            var i = 0;
            foreach (var e in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                pause?.Wait(ct);
                i++;
                progress?.Report($"Распаковка: {i}/{total} {e.FullName}");
                var target = Path.GetFullPath(Path.Combine(destDir, e.FullName));
                if (!target.StartsWith(destFull, StringComparison.OrdinalIgnoreCase)) continue;
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (string.IsNullOrEmpty(e.Name)) continue;
                using var src = e.Open();
                using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                src.CopyTo(dst);
            }
        }, ct);
    }

    private static async Task ExtractManagedAsync(string archivePath, string destDir,
        IProgress<string> progress, CancellationToken ct, PauseTokenSource? pause)
    {
        var destFull = Path.GetFullPath(destDir);
        await Task.Run(() =>
        {
            using var archive = ArchiveFactory.Open(archivePath);
            var files = archive.Entries.Where(e => !e.IsDirectory).ToList();
            var i = 0;
            foreach (var e in files)
            {
                ct.ThrowIfCancellationRequested();
                pause?.Wait(ct);
                i++;
                progress?.Report($"Распаковка: {i}/{files.Count} {e.Key}");
                var target = Path.GetFullPath(Path.Combine(destDir, e.Key ?? Path.GetFileName(archivePath)));
                if (!target.StartsWith(destFull, StringComparison.OrdinalIgnoreCase)) continue;
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using var src = e.OpenEntryStream();
                using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                src.CopyTo(dst);
            }
        }, ct);
    }

    private static string? FindSevenZip()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "7z.exe");
        if (File.Exists(local)) return local;
        var x86 = @"C:\Program Files\7-Zip\7z.exe";
        var x86_2 = @"C:\Program Files (x86)\7-Zip\7z.exe";
        if (File.Exists(x86)) return x86;
        if (File.Exists(x86_2)) return x86_2;
        return null;
    }
}