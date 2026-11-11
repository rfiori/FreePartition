using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace FreePartition.Core.Updates;

/// <summary>A GitHub release newer than the running app. The asset fields are null when the release has no suitable zip.</summary>
public sealed record ReleaseInfo(
    string Tag,
    string Notes,
    string PageUrl,
    string? AssetName,
    string? AssetUrl,
    string? ChecksumsUrl)
{
    /// <summary>True when the app can download and verify this release by itself.</summary>
    public bool CanInstall => AssetName is not null && AssetUrl is not null && ChecksumsUrl is not null;
}

/// <summary>
/// Looks for the latest GitHub release, downloads it, checks its SHA-256 and swaps the files on disk.
/// The only network call is to api.github.com (and the release download it points to).
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "rfiori/FreePartition";
    public const string LatestReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const string ChecksumsFileName = "SHA256SUMS.txt";

    // Only files published on this repository's releases are ever downloaded and run.
    private const string DownloadPrefix = "https://github.com/" + Repository + "/releases/download/";

    private const long MaxChecksumsBytes = 64 * 1024;
    private const long MaxDownloadBytes = 500L * 1024 * 1024;

    private readonly HttpClient _http;

    public UpdateService(HttpClient http) => _http = http;

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub rejects requests without a User-Agent. Nothing about the user or machine is sent.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FreePartition-Updater", "1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>
    /// True when this copy needs the framework-dependent build (it runs on the shared .NET runtime), false for the
    /// self-contained one. The update keeps the same flavor the user installed.
    /// </summary>
    public static bool IsFrameworkDependent(string runtimeDirectory)
    {
        string normalized = runtimeDirectory.Replace('/', '\\');
        return normalized.Contains(@"\shared\Microsoft.NETCore.App\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Asks GitHub for the latest release. Returns null when the running version is already the newest.</summary>
    public async Task<ReleaseInfo?> CheckAsync(string currentVersion, bool frameworkDependent, CancellationToken ct = default)
    {
        string json = await _http.GetStringAsync(LatestReleaseUrl, ct).ConfigureAwait(false);
        return ParseRelease(json, currentVersion, frameworkDependent);
    }

    /// <summary>Reads the JSON of <c>releases/latest</c>. Null when the release is not newer than <paramref name="currentVersion"/>.</summary>
    public static ReleaseInfo? ParseRelease(string json, string currentVersion, bool frameworkDependent)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!AppVersion.IsNewer(tag, currentVersion)) return null;

        string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        string page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";

        // Both zips end in "win-x64.zip" / "win-x64-self-contained.zip", so the two suffixes never overlap.
        string suffix = frameworkDependent ? "-win-x64.zip" : "-win-x64-self-contained.zip";
        string? assetName = null, assetUrl = null, checksumsUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (!IsTrustedDownloadUrl(url)) continue;

                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) (assetName, assetUrl) = (name, url);
                else if (name.Equals(ChecksumsFileName, StringComparison.OrdinalIgnoreCase)) checksumsUrl = url;
            }
        }

        return new ReleaseInfo(tag, notes, page, assetName, assetUrl, checksumsUrl);
    }

    /// <summary>Only https links to this repository's release files are accepted.</summary>
    public static bool IsTrustedDownloadUrl(string url) =>
        url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase) && !url.Contains("..");

    /// <summary>Finds the hash of <paramref name="fileName"/> in a <c>sha256sum</c> style file ("hash  name").</summary>
    public static string? FindChecksum(string checksums, string fileName)
    {
        foreach (string raw in checksums.Split('\n'))
        {
            string line = raw.Trim();
            int space = line.IndexOf(' ');
            if (space != 64) continue;

            string name = line[(space + 1)..].TrimStart(' ', '*');
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return line[..64].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// Downloads the zip, verifies it against <c>SHA256SUMS.txt</c> and unpacks it. Returns the folder with the new
    /// files. A hash that does not match deletes the download and throws; nothing is installed.
    /// </summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!release.CanInstall) throw new InvalidOperationException("This release has no installable package.");
        if (!IsTrustedDownloadUrl(release.AssetUrl!) || !IsTrustedDownloadUrl(release.ChecksumsUrl!))
            throw new InvalidOperationException("The download link is not a FreePartition release file.");

        string work = Path.Combine(Path.GetTempPath(), "FreePartition-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string sums = await ReadTextAsync(release.ChecksumsUrl!, ct).ConfigureAwait(false);
            string expected = FindChecksum(sums, release.AssetName!)
                              ?? throw new InvalidDataException($"{ChecksumsFileName} has no hash for {release.AssetName}.");

            string zip = Path.Combine(work, release.AssetName!);
            string actual = await DownloadFileAsync(release.AssetUrl!, zip, progress, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded file does not match its SHA-256. The update was discarded.");

            string files = Path.Combine(work, "files");
            ZipFile.ExtractToDirectory(zip, files);   // refuses entries that would land outside the folder
            File.Delete(zip);
            return files;
        }
        catch
        {
            TryDeleteDirectory(work);
            throw;
        }
    }

    private async Task<string> ReadTextAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxChecksumsBytes)
            throw new InvalidDataException($"{ChecksumsFileName} is too large.");
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Streams a file to disk and returns its SHA-256 (lowercase hex).</summary>
    private async Task<string> DownloadFileAsync(string url, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        if (total > MaxDownloadBytes) throw new InvalidDataException("The update file is unexpectedly large.");

        using var hash = SHA256.Create();
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hash.TransformBlock(buffer, 0, read, null, 0);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (done > MaxDownloadBytes) throw new InvalidDataException("The update file is unexpectedly large.");
            if (total > 0) progress?.Report((double)done / total.Value);
        }
        hash.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(hash.Hash!).ToLowerInvariant();
    }

    /// <summary>
    /// Copies the new files over the installed ones. A running .exe cannot be overwritten but it can be renamed, so
    /// each existing file is moved to <c>name.old</c> first. If anything fails, the old files are put back.
    /// </summary>
    public static void InstallFiles(string sourceDir, string targetDir)
    {
        var moved = new List<(string Old, string Original)>();
        var created = new List<string>();
        try
        {
            foreach (string source in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(targetDir, Path.GetRelativePath(sourceDir, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                if (File.Exists(destination))
                {
                    string old = destination + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(destination, old);
                    moved.Add((old, destination));
                }
                File.Copy(source, destination);
                created.Add(destination);
            }
        }
        catch
        {
            foreach (string file in created) TryDeleteFile(file);
            foreach (var (old, original) in moved)
            {
                try { File.Move(old, original, overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    /// <summary>Deletes the <c>.old</c> files left by a previous update. Call it at startup.</summary>
    public static void CleanUpOldFiles(string appDir)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(appDir, "*.old", SearchOption.TopDirectoryOnly))
                TryDeleteFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
