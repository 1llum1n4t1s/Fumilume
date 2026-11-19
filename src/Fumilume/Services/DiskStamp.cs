namespace Fumilume.Services;

/// <summary>
/// ディスク上のファイルの状態（最終更新時刻UTCとサイズ）。内容は読まないので、大きなファイルでも安い。
/// セッションの未保存バッファを控えた時点のディスク側状態を覚え、閉じている間の外部更新を見分けるために使う。
/// </summary>
internal readonly record struct DiskStamp(long LastWriteUtcTicks, long Length)
{
    public static DiskStamp? TryRead(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new DiskStamp(info.LastWriteTimeUtc.Ticks, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>セッション上の2項目から復元する。どちらかが欠けた旧形式や不正値は null（比較しない）。</summary>
    public static DiskStamp? FromSession(long? ticks, long? length)
        => ticks is { } t && t >= 0 && t <= DateTime.MaxValue.Ticks && length is { } l && l >= 0
            ? new DiskStamp(t, l)
            : null;
}
