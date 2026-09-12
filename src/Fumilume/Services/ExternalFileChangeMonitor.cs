namespace Fumilume.Services;

public sealed class ExternalFileChangedEventArgs : EventArgs
{
    internal ExternalFileChangedEventArgs(string fullPath, long changeId)
    {
        FullPath = fullPath;
        ChangeId = changeId;
    }

    public string FullPath { get; }

    internal long ChangeId { get; }
}

public interface IExternalFileChangeMonitor : IDisposable
{
    event EventHandler<ExternalFileChangedEventArgs>? FileChanged;

    void SetPaths(IEnumerable<string> paths);

    /// <summary>通知に対応する読み込み・保留処理が完了したことを記録する。</summary>
    void Acknowledge(ExternalFileChangedEventArgs change);

    /// <summary>監視バッファーの取りこぼしに備え、記録したファイル状態を現在値と照合する。</summary>
    void CheckForChanges();
}

/// <summary>
/// 開いている文書のフォルダーを監視し、同じファイルへ続けて届く通知を 1 回へまとめる。
/// </summary>
internal sealed class ExternalFileChangeMonitor : IExternalFileChangeMonitor
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(200);

    private readonly object _gate = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileStamp> _stamps =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _pending =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, DeliveredChange> _delivered = [];
    private HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private long _nextChangeId;
    private bool _disposed;

    public event EventHandler<ExternalFileChangedEventArgs>? FileChanged;

    public void SetPaths(IEnumerable<string> paths)
    {
        var normalized = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] added;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_paths.SetEquals(normalized))
            {
                return;
            }

            added = normalized.Except(_paths, StringComparer.OrdinalIgnoreCase).ToArray();
            var removed = _paths.Except(normalized, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var path in removed)
            {
                if (_pending.Remove(path, out var pending))
                {
                    pending.Cancel();
                    pending.Dispose();
                }

                _stamps.Remove(path);
                foreach (var changeId in _delivered
                             .Where(item => string.Equals(
                                 item.Value.Path,
                                 path,
                                 StringComparison.OrdinalIgnoreCase))
                             .Select(item => item.Key)
                             .ToArray())
                {
                    _delivered.Remove(changeId);
                }
            }

            DisposeWatchers();
            _paths = normalized;
            foreach (var path in added)
            {
                _stamps[path] = FileStamp.Read(path);
            }

            foreach (var directory in _paths
                         .Select(Path.GetDirectoryName)
                         .OfType<string>()
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var watcher = new FileSystemWatcher(directory)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName
                            | NotifyFilters.CreationTime
                            | NotifyFilters.LastWrite
                            | NotifyFilters.Size,
                    };
                    watcher.Changed += OnFileChanged;
                    watcher.Created += OnFileChanged;
                    watcher.Deleted += OnFileChanged;
                    watcher.Renamed += OnFileRenamed;
                    watcher.Error += OnWatcherError;
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(directory, watcher);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // 監視を張れなくても文書を開く処理は成功させる。ウィンドウ復帰時の照合が補助経路になる。
                    AppLogger.For<ExternalFileChangeMonitor>().Warn(
                        $"フォルダーを監視できませんでした: {directory}",
                        ex);
                }
            }
        }

        // 読み込み完了から監視開始までの短い間に更新されても見落とさないよう、新規対象は内容を一度照合する。
        foreach (var path in added)
        {
            QueueNotification(path);
        }
    }

    public void Acknowledge(ExternalFileChangedEventArgs change)
    {
        lock (_gate)
        {
            if (_disposed
                || !_delivered.Remove(change.ChangeId, out var delivered)
                || !_paths.Contains(delivered.Path))
            {
                return;
            }

            _stamps[delivered.Path] = delivered.Stamp;
        }
    }

    public void CheckForChanges()
    {
        string[] changed;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var unacknowledged = _delivered.Values
                .Select(change => change.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            changed = _paths
                .Where(path => unacknowledged.Contains(path)
                    || !_stamps.TryGetValue(path, out var previous)
                    || previous != FileStamp.Read(path))
                .ToArray();
        }

        foreach (var path in changed)
        {
            QueueNotification(path);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var pending in _pending.Values)
            {
                pending.Cancel();
                pending.Dispose();
            }

            _pending.Clear();
            DisposeWatchers();
            _paths.Clear();
            _stamps.Clear();
            _delivered.Clear();
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args)
        => QueueNotification(args.FullPath);

    private void OnFileRenamed(object sender, RenamedEventArgs args)
    {
        QueueNotification(args.OldFullPath);
        QueueNotification(args.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        AppLogger.For<ExternalFileChangeMonitor>().Warn(
            "ファイル監視の通知を取りこぼしたため、開いている文書を再確認します。",
            args.GetException());
        string[] paths;
        lock (_gate)
        {
            paths = _disposed ? [] : [.. _paths];
        }

        foreach (var path in paths)
        {
            QueueNotification(path);
        }
    }

    private void QueueNotification(string path)
    {
        path = Path.GetFullPath(path);
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposed || !_paths.Contains(path))
            {
                return;
            }

            if (_pending.Remove(path, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            cancellation = new CancellationTokenSource();
            _pending[path] = cancellation;
        }

        _ = NotifyAfterQuietPeriodAsync(path, cancellation);
    }

    private async Task NotifyAfterQuietPeriodAsync(string path, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(QuietPeriod, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        EventHandler<ExternalFileChangedEventArgs>? handler;
        ExternalFileChangedEventArgs change;
        lock (_gate)
        {
            if (_disposed
                || !_pending.TryGetValue(path, out var current)
                || !ReferenceEquals(current, cancellation))
            {
                return;
            }

            _pending.Remove(path);
            cancellation.Dispose();
            var changeId = ++_nextChangeId;
            var stamp = FileStamp.Read(path);
            foreach (var previousId in _delivered
                         .Where(item => string.Equals(
                             item.Value.Path,
                             path,
                             StringComparison.OrdinalIgnoreCase))
                         .Select(item => item.Key)
                         .ToArray())
            {
                _delivered.Remove(previousId);
            }

            _delivered[changeId] = new DeliveredChange(path, stamp);
            handler = FileChanged;
            change = new ExternalFileChangedEventArgs(path, changeId);
        }

        handler?.Invoke(this, change);
    }

    private void DisposeWatchers()
    {
        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnFileChanged;
            watcher.Created -= OnFileChanged;
            watcher.Deleted -= OnFileChanged;
            watcher.Renamed -= OnFileRenamed;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    private readonly record struct FileStamp(bool Exists, long Length, DateTime LastWriteUtc)
    {
        public static FileStamp Read(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists
                    ? new FileStamp(true, info.Length, info.LastWriteTimeUtc)
                    : default;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return default;
            }
        }
    }

    private readonly record struct DeliveredChange(string Path, FileStamp Stamp);
}
