namespace Fumilume.Services;

public sealed record FolderTreeEntry(string FullPath, string Name, bool IsDirectory, bool IsReparsePoint);
public sealed record FolderTreeListing(IReadOnlyList<FolderTreeEntry> Entries, string? ErrorMessage);

/// <summary>フォルダー一覧のディスクアクセスを UI スレッドから分離する。</summary>
public sealed class FolderTreeService
{
    public Task<string> ValidateFolderAsync(string path, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException("フォルダーが見つからないか、アクセスできません。");
            return fullPath;
        }, cancellationToken);

    public Task<FolderTreeListing> ListAsync(string path, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            var entries = new List<FolderTreeEntry>();
            string? error = null;
            try
            {
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = entry.Attributes;
                        entries.Add(new(entry.FullName, entry.Name,
                            (attributes & FileAttributes.Directory) != 0,
                            (attributes & FileAttributes.ReparsePoint) != 0));
                    }
                    catch (Exception ex) when (IsFileSystemError(ex))
                    {
                        error = "一部の項目にアクセスできません。再読込してください。";
                    }
                }
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                error = $"フォルダーを読み込めません: {ex.Message}";
            }
            return new FolderTreeListing(entries.OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal).ToArray(), error);
        }, cancellationToken);

    public IDisposable Watch(string path, Action changed) => new FolderTreeWatch(path, changed);

    internal static bool IsFileSystemError(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException;

    private sealed class FolderTreeWatch : IDisposable
    {
        private readonly object _gate = new();
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _timer;
        private bool _disposed;

        public FolderTreeWatch(string path, Action changed)
        {
            _timer = new Timer(_ =>
            {
                lock (_gate)
                {
                    if (!_disposed) changed();
                }
            }, null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnChanged;
            _watcher.Error += OnError;
            try { _watcher.EnableRaisingEvents = true; }
            catch { Dispose(); throw; }
        }

        private void OnChanged(object sender, FileSystemEventArgs args) => Schedule();
        private void OnError(object sender, ErrorEventArgs args) => Schedule();
        private void Schedule()
        {
            lock (_gate)
            {
                if (!_disposed) _timer.Change(250, Timeout.Infinite);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _watcher.Dispose();
                _timer.Dispose();
            }
        }
    }
}
