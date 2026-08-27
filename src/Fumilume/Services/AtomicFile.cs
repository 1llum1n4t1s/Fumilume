using System.Text;

namespace Fumilume.Services;

/// <summary>
/// 「書いている途中で落ちても元ファイルが壊れない」書き込み。
///
/// <see cref="File.WriteAllText(string, string)"/> は先に対象を 0 バイトへ切り詰めてから書くため、
/// 途中でプロセスが終了すると中身の消えたファイルが残る。設定 JSON がそうなると次回起動で全設定が
/// 既定値へ戻ってしまうので、同じディレクトリへ一時ファイルを書き切ってから置き換える。
/// 置き換えは「成功して新しい内容」か「失敗して元のまま」のどちらかにしかならない。
///
/// 一時ファイルを同じディレクトリに作るのは、別ボリューム間だと置き換えができずコピーになるため。
/// </summary>
internal static class AtomicFile
{
    /// <summary>文字列を UTF-8（BOM なし）で原子的に書き込む。失敗時は例外を投げ、元ファイルは触らない。</summary>
    /// <param name="backupPath">置き換え前の内容を残す先。null なら残さない。</param>
    public static void WriteAllText(string path, string text, string? backupPath = null)
        => WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text), backupPath);

    private static void WriteAllBytes(string path, byte[] bytes, string? backupPath)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)
            ?? throw new IOException($"書き込み先のディレクトリを特定できません: {path}");
        System.IO.Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $".fumilume-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                // 置き換え前にディスクまで送る（電源断で「置き換えは済んだが中身は空」を避ける）
                stream.Flush(flushToDisk: true);
            }

            Replace(temporary, full, backupPath);
        }
        finally
        {
            // 置き換えに成功していれば既に無い。失敗経路でゴミを残さないための後始末。
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 一時ファイルの消し漏れは本処理の成否に影響しない。
            }
        }
    }

    private static void Replace(string temporary, string destination, string? backupPath)
    {
        if (!File.Exists(destination))
        {
            File.Move(temporary, destination);
            return;
        }

        try
        {
            File.Replace(temporary, destination, backupPath, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // ReplaceFile を実装しないファイルシステム（クラウド同期ドライブなど）向けの退避路。
            // MoveFileEx 相当なので、こちらも中途半端な状態にはならない。
            // File.Replace と違って旧内容を退避してくれないため、明示的にコピーしておく。
            if (backupPath is not null)
            {
                try
                {
                    File.Copy(destination, backupPath, overwrite: true);
                }
                catch (Exception backupEx) when (backupEx is IOException or UnauthorizedAccessException)
                {
                    // 退避の失敗は本体の書き込み失敗より軽いので続行する。
                }
            }

            File.Move(temporary, destination, overwrite: true);
        }
    }
}
