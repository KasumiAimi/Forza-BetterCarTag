using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace FH6GarageEditor;

internal sealed record ReleaseDownload(Uri Url, long Size, string Sha256);

internal sealed record PublishedRelease(Version Version, string Title, string Author, DateTimeOffset Published,
    ReleaseDownload? Download)
{
    public string VersionText => $"v{Version}";
    public string PublishedText => Published.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
}

internal sealed class ReleaseUpdates(HttpClient client)
{
    private const string Repository = "KasumiAimi/Forza-better-cartage";
    private const string AssetName = "BetterCarTage.exe";
    private const long MaxDownloadSize = 400L * 1024 * 1024;
    internal const string ReleasePage = "https://github.com/" + Repository + "/releases";
    internal const string FinishArgument = "--finish-editor-update";
    internal const string ReadyArgument = "--editor-update-ready";
    internal static readonly Version CurrentVersion = Version.Parse(typeof(ReleaseUpdates).Assembly.GetName().Version!.ToString(3));
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "The empty assembly location identifies the supported single-file distribution.")]
    internal static bool CanInstall => string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location) &&
        Environment.ProcessPath is not null;
    private static string HelperRoot => Path.Combine(GarageReader.Data, "updates");

    internal async Task<IReadOnlyList<PublishedRelease>> CheckAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=15");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode is 403 or 429)
            throw new HttpRequestException("GitHub 暂时限制了检查频率，请稍后重试。");
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var releases = new List<PublishedRelease>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
            string? tag = release.GetProperty("tag_name").GetString();
            if (tag is null || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var parsed) ||
                parsed.Build < 0 || parsed.Revision >= 0 || tag != $"v{parsed}") continue;
            var published = release.GetProperty("published_at");
            if (!published.TryGetDateTimeOffset(out var date)) continue;
            var name = release.GetProperty("name").GetString();
            var author = release.GetProperty("author").GetProperty("login").GetString() ?? "—";
            releases.Add(new PublishedRelease(parsed, string.IsNullOrWhiteSpace(name) ? tag : name, author,
                date, FindDownload(release, tag)));
        }
        return releases.OrderByDescending(release => release.Version).ToArray();
    }

    private static ReleaseDownload? FindDownload(JsonElement release, string tag)
    {
        string expected = $"{ReleasePage}/download/{tag}/{AssetName}";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName || asset.GetProperty("state").GetString() != "uploaded" ||
                asset.GetProperty("browser_download_url").GetString() != expected) continue;
            long size = asset.GetProperty("size").GetInt64();
            if (size is <= 0 or > MaxDownloadSize || !asset.TryGetProperty("digest", out var digest)) continue;
            var hash = digest.GetString();
            if (hash is { Length: 71 } && hash.StartsWith("sha256:", StringComparison.Ordinal) && hash[7..].All(char.IsAsciiHexDigit))
                return new ReleaseDownload(new Uri(expected), size, hash[7..]);
        }
        return null;
    }

    internal async Task DownloadAndRestartAsync(PublishedRelease release, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!CanInstall || release.Version <= CurrentVersion || release.Download is not { } download)
            throw new InvalidOperationException("此版本无法通过更新器安装。");
        var exe = Environment.ProcessPath!;
        var token = Guid.NewGuid().ToString("N");
        string staged = StagePath(exe, token);
        string helperFolder = Path.Combine(HelperRoot, token);
        bool handedOff = false;
        try
        {
            await DownloadAsync(download, staged, progress, cancellationToken);
            ValidateVersion(staged, release.Version);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(helperFolder);
            string helper = Path.Combine(helperFolder, Path.GetFileName(exe));
            File.Copy(exe, helper);
            using var process = Start(helper, FinishArgument, exe, token, Environment.ProcessId.ToString(),
                release.Version.ToString(), download.Sha256);
            handedOff = true;
        }
        finally
        {
            if (!handedOff)
            {
                TryDelete(staged);
                TryRemoveHelper(helperFolder);
            }
        }
    }

    internal async Task DownloadAsync(ReleaseDownload download, string file, IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != download.Size)
            throw new InvalidDataException("下载文件大小与发布记录不一致。");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long received = 0;
        int lastPercent = -1;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            received += read;
            if (received > download.Size) throw new InvalidDataException("下载文件超出了发布记录的大小。");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            int percent = (int)(100 * received / download.Size);
            if (percent != lastPercent)
            {
                lastPercent = percent;
                progress.Report(percent);
            }
        }
        if (received != download.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(download.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载文件的 SHA-256 校验未通过。");
    }

    internal static bool Finish(string target, string token, string parentId, string version, string sha256)
    {
        if (!Guid.TryParseExact(token, "N", out _) || !Path.IsPathFullyQualified(target) ||
            !int.TryParse(parentId, out int pid) || pid <= 0 || pid == Environment.ProcessId ||
            !Version.TryParse(version, out var expected) || expected <= CurrentVersion ||
            !string.Equals(Path.GetDirectoryName(Environment.ProcessPath), Path.Combine(HelperRoot, token), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(target), Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase))
            return false;

        string staged = StagePath(target, token);
        string backup = target + "." + token + ".bak";
        string ready = ReadyPath(token);
        bool replaced = false;
        bool ownsLock = false;
        using var mutex = new Mutex(false, @"Local\FH6GarageEditor.Update");
        try
        {
            try { ownsLock = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { ownsLock = true; }
            if (!ownsLock) throw new IOException("另一个窗口正在安装更新。");
            Process? parent;
            try { parent = Process.GetProcessById(pid); }
            catch (ArgumentException) { parent = null; }
            using (parent)
            {
                if (parent is not null && !parent.WaitForExit(30000))
                    throw new IOException("原程序尚未退出。");
            }
            using (var file = File.OpenRead(staged))
            {
                if (!Convert.ToHexString(SHA256.HashData(file)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装前校验未通过。");
            }
            ValidateVersion(staged, expected);
            ValidateVersion(target, CurrentVersion);
            File.Copy(target, backup);
            File.Move(staged, target, overwrite: true);
            replaced = true;
            using var installed = Start(target, ReadyArgument, token);
            var timer = Stopwatch.StartNew();
            while (!File.Exists(ready) && !installed.HasExited && timer.Elapsed < TimeSpan.FromSeconds(30))
                Thread.Sleep(150);
            if (!File.Exists(ready) || installed.HasExited)
            {
                if (!installed.HasExited)
                {
                    installed.Kill();
                    installed.WaitForExit(5000);
                }
                throw new IOException("新版未能启动。");
            }
            TryDelete(backup);
            TryDelete(ready);
            return true;
        }
        catch (Exception error)
        {
            try
            {
                if (replaced) File.Move(backup, target, overwrite: true);
                else TryDelete(backup);
                MessageBox.Show($"未能完成更新。将重新打开原版本。\n\n{error.Message}", "更新器", MessageBoxButton.OK, MessageBoxImage.Warning);
                using var restored = Start(target);
            }
            catch (Exception restoreError)
            {
                MessageBox.Show($"无法重新打开原版本。\n\n{restoreError.Message}", "更新器", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return false;
        }
        finally
        {
            TryDelete(staged);
            if (ownsLock) mutex.ReleaseMutex();
        }
    }

    internal static void SignalReady(string token)
    {
        if (!Guid.TryParseExact(token, "N", out _)) return;
        var folder = Path.Combine(HelperRoot, token);
        if (!Directory.Exists(folder)) return;
        var ready = ReadyPath(token);
        File.WriteAllText(ready, "");
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(500);
                if (File.Exists(ready)) continue;
                TryRemoveHelper(folder);
                if (!Directory.Exists(folder)) break;
            }
        });
    }

    internal static void CleanOldHelpers()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            if (Directory.Exists(HelperRoot))
            {
                foreach (string folder in Directory.EnumerateDirectories(HelperRoot))
                    if (Guid.TryParseExact(Path.GetFileName(folder), "N", out _) && Directory.GetLastWriteTimeUtc(folder) < cutoff)
                        TryRemoveHelper(folder);
            }
            if (CanInstall && Environment.ProcessPath is string exe)
            {
                var prefix = Path.GetFileName(exe) + ".";
                foreach (var stage in Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, prefix + "*.download"))
                {
                    var name = Path.GetFileNameWithoutExtension(stage);
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        Guid.TryParseExact(name[prefix.Length..], "N", out _) && File.GetLastWriteTimeUtc(stage) < cutoff)
                        TryDelete(stage);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string StagePath(string target, string token) => target + "." + token + ".download";
    private static string ReadyPath(string token) => Path.Combine(HelperRoot, token, "ready");

    private static void ValidateVersion(string file, Version expected)
    {
        var version = FileVersionInfo.GetVersionInfo(file);
        if (new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart) != expected ||
            version.FilePrivatePart != 0)
            throw new InvalidDataException("下载程序的版本号与发布标签不一致。");
    }

    private static Process Start(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { WorkingDirectory = Path.GetDirectoryName(file)!, UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("无法启动程序。");
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryRemoveHelper(string folder)
    {
        try
        {
            if (!Directory.Exists(folder) || File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint)) return;
            foreach (var file in Directory.EnumerateFiles(folder))
                if (Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file) == "ready")
                    TryDelete(file);
            Directory.Delete(folder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
