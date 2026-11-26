using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Fumilume.Services;

/// <summary>
/// Avalonia の <see cref="IClipboard"/>（<see cref="DataFormat.File"/>）でファイルを出し入れする。
/// macOS のクリップボードには「切り取り」の区別が無いので、切り取りの集合と世代（Token）はプロセス内で持ち、
/// クリップボードのファイルがその集合と一致するときだけ切り取りとして扱う。UI スレッドから呼ぶ。
/// </summary>
internal sealed class MacFileClipboard(Func<IClipboard?> getClipboard, Func<IStorageProvider?> getStorageProvider)
{
    private sealed record Marker(IReadOnlyList<string> Paths, bool IsCut, long Token);

    private Marker? _marker;
    private long _lastToken;

    public async Task<bool> SetAsync(IReadOnlyList<string> paths, bool isCut)
    {
        var clipboard = getClipboard();
        var provider = getStorageProvider();
        if (clipboard is null || provider is null || paths.Count == 0)
        {
            return false;
        }

        var items = new List<IStorageItem>(paths.Count);
        try
        {
            foreach (var path in paths)
            {
                IStorageItem? item = Directory.Exists(path)
                    ? await provider.TryGetFolderFromPathAsync(path)
                    : await provider.TryGetFileFromPathAsync(path);
                if (item is null)
                {
                    return false;
                }

                items.Add(item);
            }

            await clipboard.SetFilesAsync(items);
            _marker = new Marker(paths.ToArray(), isCut, ++_lastToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.For<MacFileClipboard>().Warn("ファイルをクリップボードへ入れられませんでした。", ex);
            return false;
        }
        finally
        {
            foreach (var item in items)
            {
                item.Dispose();
            }
        }
    }

    public async Task<FileClipboardContent?> GetAsync()
    {
        var paths = await ReadPathsAsync();
        if (paths is not { Count: > 0 })
        {
            return null;
        }

        return _marker is { } marker && SamePaths(marker.Paths, paths)
            ? new FileClipboardContent(paths, marker.IsCut, marker.Token)
            : new FileClipboardContent(paths, false, 0);
    }

    public async Task<bool> HasAsync()
    {
        var clipboard = getClipboard();
        if (clipboard is null)
        {
            return false;
        }

        try
        {
            return (await clipboard.GetDataFormatsAsync()).Contains(DataFormat.File);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<bool> ClearIfUnchangedAsync(long token)
    {
        var clipboard = getClipboard();
        if (clipboard is null || _marker is not { } marker || token == 0 || marker.Token != token)
        {
            return false;
        }

        var paths = await ReadPathsAsync();
        if (paths is null || !SamePaths(marker.Paths, paths))
        {
            return false;
        }

        try
        {
            await clipboard.ClearAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        _marker = null;
        return true;
    }

    private async Task<IReadOnlyList<string>?> ReadPathsAsync()
    {
        var clipboard = getClipboard();
        if (clipboard is null)
        {
            return null;
        }

        IReadOnlyList<IStorageItem>? items;
        try
        {
            items = await clipboard.TryGetFilesAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        if (items is null)
        {
            return null;
        }

        var paths = new List<string>(items.Count);
        foreach (var item in items)
        {
            if (item.TryGetLocalPath() is { Length: > 0 } local)
            {
                paths.Add(local);
            }

            item.Dispose();
        }

        return paths;
    }

    private static bool SamePaths(IReadOnlyList<string> left, IReadOnlyList<string> right)
        => left.Count == right.Count
            && left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);
}
