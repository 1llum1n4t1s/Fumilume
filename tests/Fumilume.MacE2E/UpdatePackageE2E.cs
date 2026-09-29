using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Fumilume.MacE2E;

internal static class UpdatePackageE2E
{
    public static async Task<int> RunAsync(string artifacts, string output)
    {
        artifacts = Path.GetFullPath(artifacts);
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidOperationException("Use an empty update verification directory.");
        Directory.CreateDirectory(output);
        var expectedVersion = typeof(Fumilume.App).Assembly.GetName().Version!.ToString(3);
        const string channel = "osx-arm64";
        const string installedVersion = "1.0.0";
        object result;
        var exitCode = 0;
        try
        {
            var feed = Path.Combine(artifacts, $"releases.{channel}.json");
            if (!File.Exists(feed)) throw new FileNotFoundException("Mac ARM64 update feed is missing", feed);
            var packages = Path.Combine(output, "packages");
            Directory.CreateDirectory(packages);
            // インストール状態だけを隔離した locator とし、feed/SDK/package は実配布物を使う。
            var locator = new TestVelopackLocator("Fumilume", installedVersion, packages);
            var manager = new UpdateManager(new SimpleFileSource(new DirectoryInfo(artifacts)),
                new UpdateOptions { ExplicitChannel = channel }, locator);
            var update = await manager.CheckForUpdatesAsync()
                ?? throw new InvalidOperationException("SDK did not find a Mac ARM64 update");
            var target = update.TargetFullRelease;
            if (target.PackageId != "Fumilume" || target.Version.ToString() != expectedVersion
                || !target.FileName.Contains(channel, StringComparison.Ordinal)
                || !target.FileName.EndsWith("-full.nupkg", StringComparison.Ordinal))
                throw new InvalidDataException($"Wrong SDK update selection: {target.PackageId}/{target.Version}/{target.FileName}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await manager.DownloadUpdatesAsync(update, null, timeout.Token);
            var downloaded = Path.Combine(packages, target.FileName);
            var original = Path.Combine(artifacts, target.FileName);
            var downloadedInfo = new FileInfo(downloaded);
            if (!downloadedInfo.Exists || downloadedInfo.Length != target.Size)
                throw new InvalidDataException("SDK downloaded package is missing or its size differs from the feed");
            var hash = await HashAsync(downloaded);
            if (string.IsNullOrWhiteSpace(target.SHA256)
                || !hash.Equals(target.SHA256, StringComparison.OrdinalIgnoreCase)
                || !hash.Equals(await HashAsync(original), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SDK downloaded package SHA256 differs from the feed/source package");
            result = new { schemaVersion = 1, passed = true, channel, installedVersion, expectedVersion,
                selectedVersion = target.Version.ToString(), package = target.FileName,
                size = downloadedInfo.Length, sha256 = hash, feed, downloaded,
                appReplacementVerified = false, utc = DateTimeOffset.UtcNow };
            Console.WriteLine($"PASS update-sdk-feed-download {target.FileName} SHA256={hash}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            result = new { schemaVersion = 1, passed = false, channel, installedVersion, expectedVersion,
                appReplacementVerified = false, error = exception.ToString(), utc = DateTimeOffset.UtcNow };
            Console.Error.WriteLine($"FAIL update-sdk-feed-download: {exception}");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "update-results.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return exitCode;
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
