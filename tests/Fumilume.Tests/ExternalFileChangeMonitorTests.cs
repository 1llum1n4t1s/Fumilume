using Fumilume.Services;

namespace Fumilume.Tests;

public sealed class ExternalFileChangeMonitorTests
{
    [Fact]
    public async Task FileChangesAreReportedOnceAfterTheWriteSettles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Fumilume.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "watched.txt");
        var otherPath = Path.Combine(directory, "other.txt");
        await File.WriteAllTextAsync(path, "before", cancellationToken);
        await File.WriteAllTextAsync(otherPath, "other", cancellationToken);

        try
        {
            using var monitor = new ExternalFileChangeMonitor();
            var changed = new TaskCompletionSource<ExternalFileChangedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var notifications = 0;
            monitor.FileChanged += (_, args) =>
            {
                if (string.Equals(args.FullPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref notifications);
                    changed.TrySetResult(args);
                }
            };
            monitor.SetPaths([path]);

            await File.WriteAllTextAsync(path, "after one", cancellationToken);
            await File.WriteAllTextAsync(path, "after two", cancellationToken);
            // 対象更新で、既存ファイルに届く途中のデバウンス通知を取り消してはいけない。
            monitor.SetPaths([path, otherPath]);
            monitor.CheckForChanges();

            var args = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await Task.Delay(400, cancellationToken);

            Assert.Equal(Path.GetFullPath(path), args.FullPath, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(1, Volatile.Read(ref notifications));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnacknowledgedExternalChangeIsReportedAgainOnCheck()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Fumilume.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "retry.txt");
        await File.WriteAllTextAsync(path, "before", cancellationToken);

        try
        {
            using var monitor = new ExternalFileChangeMonitor();
            var notifications = new System.Collections.Concurrent.ConcurrentQueue<ExternalFileChangedEventArgs>();
            using var signal = new SemaphoreSlim(0);
            monitor.FileChanged += (_, args) =>
            {
                notifications.Enqueue(args);
                signal.Release();
            };
            monitor.SetPaths([path]);

            Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            Assert.True(notifications.TryDequeue(out var initial));
            monitor.Acknowledge(initial);

            await File.WriteAllTextAsync(path, "external", cancellationToken);
            Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            Assert.True(notifications.TryDequeue(out _));

            monitor.CheckForChanges();

            Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            Assert.True(notifications.TryDequeue(out var retried));
            monitor.Acknowledge(retried);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnacknowledgedInitialCheckIsRetriedEvenWhenMetadataDidNotChange()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Fumilume.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "initial-retry.txt");
        await File.WriteAllTextAsync(path, "unchanged", cancellationToken);

        try
        {
            using var monitor = new ExternalFileChangeMonitor();
            using var signal = new SemaphoreSlim(0);
            var notifications = 0;
            monitor.FileChanged += (_, _) =>
            {
                Interlocked.Increment(ref notifications);
                signal.Release();
            };
            monitor.SetPaths([path]);

            Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            monitor.CheckForChanges();
            Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));

            Assert.Equal(2, Volatile.Read(ref notifications));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
