using Fumilume.Models;

namespace Fumilume.Services;

public sealed class DocumentFileService : IDocumentFileService
{
    private const int DecodeRetryDelayMilliseconds = 40;
    private const int ReadRetryCount = 2;

    public async Task<TextDocumentContent> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var bytes = await ReadSharedBytesWithRetryAsync(path, cancellationToken);
        try
        {
            var content = DocumentEncodingService.Decode(bytes);
            if (content.Encoding is DocumentEncoding.ShiftJis or DocumentEncoding.EucJp
                && DocumentEncodingService.HasIncompleteUtf8Tail(bytes))
            {
                // 末尾が「未完のUTF-8」と「成立する旧来形式」の両方に見える場合は、短時間だけ
                // 書込みの続きを待つ。変化がなければ最初の判定を採用する。
                await Task.Delay(DecodeRetryDelayMilliseconds, cancellationToken);
                var retriedBytes = await ReadSharedBytesWithRetryAsync(path, cancellationToken);
                return bytes.AsSpan().SequenceEqual(retriedBytes)
                    ? content
                    : DocumentEncodingService.Decode(retriedBytes);
            }

            return content;
        }
        catch (InvalidDataException)
        {
            // 追記中のログはマルチバイト文字の途中まで見える瞬間がある。短時間後に内容が
            // 変わった場合だけ再判定し、安定している不正データを無限に読み直さない。
            await Task.Delay(DecodeRetryDelayMilliseconds, cancellationToken);
            var retriedBytes = await ReadSharedBytesWithRetryAsync(path, cancellationToken);
            if (bytes.AsSpan().SequenceEqual(retriedBytes))
            {
                throw;
            }

            return DocumentEncodingService.Decode(retriedBytes);
        }
    }

    private static async Task<byte[]> ReadSharedBytesWithRetryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ReadSharedBytesAsync(path, cancellationToken);
            }
            catch (IOException ex) when (attempt < ReadRetryCount && IsSharingViolation(ex))
            {
                await Task.Delay(20 * (attempt + 1), cancellationToken);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception)
        => (exception.HResult & 0xFFFF) is 32 or 33;

    private static async Task<byte[]> ReadSharedBytesAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 書き込み中のログを読み、ログローテーションによる削除・置換も妨げない。
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (length > Array.MaxLength)
        {
            throw new IOException("ファイルが大きすぎるため読み込めませんでした。");
        }

        // 読み込み開始時の長さまでに限定し、追記が続いても読み終えられるようにする。
        var bytes = new byte[(int)length];
        var count = await stream.ReadAtLeastAsync(bytes, bytes.Length,
            throwOnEndOfStream: false, cancellationToken);
        if (count < bytes.Length)
        {
            // 読み込み中に切り詰められた場合は、実際に読めた部分だけをデコードする。
            Array.Resize(ref bytes, count);
        }

        return bytes;
    }

    public async Task WriteAsync(
        string path,
        TextDocumentContent content,
        bool createBackup = false,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("保存先のフォルダーを特定できません。");
        Directory.CreateDirectory(directory);

        var normalizedText = NormalizeNewLines(content.Text, content.NewLine);
        var encoder = DocumentEncodingService.GetEncoding(content.Encoding);
        var preamble = encoder.GetPreamble();
        var body = encoder.GetBytes(normalizedText);
        var payload = new byte[preamble.Length + body.Length];
        preamble.CopyTo(payload, 0);
        body.CopyTo(payload, preamble.Length);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(payload, cancellationToken);

                // 置き換え前にディスクまで送る（電源断で「置き換えは済んだが中身は空」を避ける）。
                // 設定ファイルと同じ扱いにする。失われて困る度合いは本文のほうが大きい。
                stream.Flush(flushToDisk: true);
            }

            // sakura の「保存時にバックアップを作成する」相当。上書き直前の内容を .bak へ退避する。
            // バックアップに失敗しても保存そのものは通す（保存できないほうが困る）。
            if (createBackup && File.Exists(fullPath))
            {
                try
                {
                    File.Copy(fullPath, fullPath + ".bak", overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLogger.For<DocumentFileService>().Warn(
                        $"バックアップを作成できませんでした: {fullPath}.bak",
                        ex);
                }
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    /// <summary>保存の後始末を試みる。後始末の失敗で、本来の保存例外を上書きしない。</summary>
    internal static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.For<DocumentFileService>().Warn($"一時ファイルを削除できませんでした: {temporaryPath}", ex);
        }
    }

    public static string NormalizeNewLines(string text, string newLine)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", newLine, StringComparison.Ordinal);
}
