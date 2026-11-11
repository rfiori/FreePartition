using FreePartition.Core.Updates;

namespace FreePartition.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("v0.2.0", "0.1.0", true)]
    [InlineData("v0.1.1", "0.1.0", true)]
    [InlineData("v1.0.0", "0.9.9", true)]
    [InlineData("v0.1.0", "0.1.0", false)]
    [InlineData("v0.1.0", "0.1.0+a1b2c3d", false)]
    [InlineData("v0.0.9", "0.1.0", false)]
    [InlineData("v0.10.0", "0.9.0", true)]          // numeric, not text, comparison
    [InlineData("v0.2.0", "0.2.0-beta.1", true)]    // final release beats its pre-release
    [InlineData("v0.2.0-beta.1", "0.2.0", false)]
    [InlineData("v0.2.0-beta.2", "0.2.0-beta.1", true)]
    [InlineData("latest", "0.1.0", false)]
    [InlineData("", "0.1.0", false)]
    [InlineData("v0.2.0", "garbage", false)]
    public void IsNewer_compares_versions(string candidate, string current, bool expected) =>
        Assert.Equal(expected, AppVersion.IsNewer(candidate, current));

    [Fact]
    public void TryParse_reads_tag_parts()
    {
        Assert.True(AppVersion.TryParse("v1.2.3-beta.1+abc", out var v, out var pre));
        Assert.Equal(new Version(1, 2, 3), v);
        Assert.Equal("beta.1", pre);
    }

    private const string Hash1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Hash2 = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void FindChecksum_matches_the_exact_file_name()
    {
        string sums = $"{Hash1}  FreePartition-0.2.0-win-x64.zip\r\n{Hash2}  FreePartition-0.2.0-win-x64-self-contained.zip\r\n";

        Assert.Equal(Hash1, UpdateService.FindChecksum(sums, "FreePartition-0.2.0-win-x64.zip"));
        Assert.Equal(Hash2.ToLowerInvariant(), UpdateService.FindChecksum(sums, "FreePartition-0.2.0-win-x64-self-contained.zip"));
        Assert.Null(UpdateService.FindChecksum(sums, "other.zip"));
        Assert.Null(UpdateService.FindChecksum("", "x.zip"));
    }

    private static string ReleaseJson(string tag, bool withChecksums = true)
    {
        const string baseUrl = "https://github.com/rfiori/FreePartition/releases/download/";
        string checksums = withChecksums
            ? $",{{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"{baseUrl}{tag}/SHA256SUMS.txt\"}}"
            : "";
        return $$"""
        {"tag_name":"{{tag}}","html_url":"https://github.com/rfiori/FreePartition/releases/tag/{{tag}}","body":"notes",
         "assets":[
          {"name":"FreePartition-0.2.0-win-x64.zip","browser_download_url":"{{baseUrl}}{{tag}}/FreePartition-0.2.0-win-x64.zip"},
          {"name":"FreePartition-0.2.0-win-x64-self-contained.zip","browser_download_url":"{{baseUrl}}{{tag}}/FreePartition-0.2.0-win-x64-self-contained.zip"},
          {"name":"FreePartition-WinPE.iso","browser_download_url":"{{baseUrl}}{{tag}}/FreePartition-WinPE.iso"}
          {{checksums}}]}
        """;
    }

    [Fact]
    public void ParseRelease_picks_the_asset_of_the_same_flavor()
    {
        var selfContained = UpdateService.ParseRelease(ReleaseJson("v0.2.0"), "0.1.0", frameworkDependent: false);
        var dependent = UpdateService.ParseRelease(ReleaseJson("v0.2.0"), "0.1.0", frameworkDependent: true);

        Assert.Equal("FreePartition-0.2.0-win-x64-self-contained.zip", selfContained!.AssetName);
        Assert.Equal("FreePartition-0.2.0-win-x64.zip", dependent!.AssetName);
        Assert.True(selfContained.CanInstall);
        Assert.Equal("notes", selfContained.Notes);
    }

    [Fact]
    public void ParseRelease_returns_null_when_not_newer() =>
        Assert.Null(UpdateService.ParseRelease(ReleaseJson("v0.1.0"), "0.1.0", false));

    [Fact]
    public void ParseRelease_without_checksums_cannot_be_installed()
    {
        var release = UpdateService.ParseRelease(ReleaseJson("v0.2.0", withChecksums: false), "0.1.0", false);

        Assert.NotNull(release);
        Assert.False(release.CanInstall);
    }

    [Fact]
    public void ParseRelease_ignores_assets_from_other_hosts()
    {
        const string json = """
        {"tag_name":"v0.2.0","html_url":"x","assets":[
          {"name":"FreePartition-0.2.0-win-x64-self-contained.zip","browser_download_url":"https://evil.example/FreePartition-0.2.0-win-x64-self-contained.zip"}]}
        """;

        var release = UpdateService.ParseRelease(json, "0.1.0", false);

        Assert.Null(release!.AssetUrl);
        Assert.False(release.CanInstall);
    }

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.0\", true)]
    [InlineData(@"C:\Tools\FreePartition\", false)]
    public void IsFrameworkDependent_detects_the_shared_runtime(string runtimeDir, bool expected) =>
        Assert.Equal(expected, UpdateService.IsFrameworkDependent(runtimeDir));

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fp-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void InstallFiles_replaces_files_and_keeps_the_old_ones_aside()
    {
        string source = NewTempDir(), target = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(target, "FreePartition.exe"), "old");
            File.WriteAllText(Path.Combine(target, "settings.json"), "mine");
            File.WriteAllText(Path.Combine(source, "FreePartition.exe"), "new");

            UpdateService.InstallFiles(source, target);

            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "FreePartition.exe")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(target, "FreePartition.exe.old")));
            Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "settings.json")));   // untouched

            UpdateService.CleanUpOldFiles(target);
            Assert.False(File.Exists(Path.Combine(target, "FreePartition.exe.old")));
        }
        finally { UpdateService.TryDeleteDirectory(source); UpdateService.TryDeleteDirectory(target); }
    }

    [Fact]
    public void InstallFiles_restores_the_old_files_when_a_copy_fails()
    {
        string source = NewTempDir(), target = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(target, "a.dll"), "old-a");
            File.WriteAllText(Path.Combine(source, "a.dll"), "new-a");
            File.WriteAllText(Path.Combine(source, "b.dll"), "new-b");
            // A directory where b.dll must go makes the second copy fail.
            Directory.CreateDirectory(Path.Combine(target, "b.dll"));

            Assert.ThrowsAny<Exception>(() => UpdateService.InstallFiles(source, target));

            Assert.Equal("old-a", File.ReadAllText(Path.Combine(target, "a.dll")));
            Assert.False(File.Exists(Path.Combine(target, "a.dll.old")));
        }
        finally { UpdateService.TryDeleteDirectory(source); UpdateService.TryDeleteDirectory(target); }
    }

    [Fact]
    public void Settings_round_trip_and_survive_a_damaged_file()
    {
        string dir = NewTempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            Assert.True(UpdateSettings.Load(path).CheckOnStartup);   // missing file: defaults

            new UpdateSettings { CheckOnStartup = false, SkippedVersion = "v0.2.0" }.Save(path);
            var loaded = UpdateSettings.Load(path);
            Assert.False(loaded.CheckOnStartup);
            Assert.Equal("v0.2.0", loaded.SkippedVersion);

            File.WriteAllText(path, "{ not json");
            Assert.True(UpdateSettings.Load(path).CheckOnStartup);
        }
        finally { UpdateService.TryDeleteDirectory(dir); }
    }
}
