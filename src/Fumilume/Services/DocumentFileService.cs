using System.Text;
using Fumilume.Models;

namespace Fumilume.Services;

public sealed class DocumentFileService : IDocumentFileService
{
    private static readonly UTF8Encoding Utf8Strict = new(false, true);

    public async Task<TextDocumentContent> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var (encoding, preambleLength, decoder) = DetectEncoding(bytes);

        string text;
        try
        {
            text = decoder.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                "対応している文字コード（UTF-8 / UTF-16）として読み込めませんでした。",
                ex);
        }

        return new TextDocumentContent(text, encoding, DetectNewLine(text));
    }

    public async Task WriteAsync(
        string path,
        TextDocumentContent content,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("保存先のフォルダーを特定できません。");
        Directory.CreateDirectory(directory);

        var normalizedText = NormalizeNewLines(content.Text, content.NewLine);
        var encoder = GetEncoding(content.Encoding);
        var preamble = encoder.GetPreamble();
        var body = encoder.GetBytes(normalizedText);
        var payload = new byte[preamble.Length + body.Length];
        preamble.CopyTo(payload, 0);
        body.CopyTo(payload, preamble.Length);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, payload, cancellationToken);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static string NormalizeNewLines(string text, string newLine)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", newLine, StringComparison.Ordinal);

    private static (DocumentEncoding Kind, int PreambleLength, Encoding Decoder) DetectEncoding(
        ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (DocumentEncoding.Utf8Bom, 3, new UTF8Encoding(false, true));
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (DocumentEncoding.Utf16LittleEndian, 2, new UnicodeEncoding(false, false, true));
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (DocumentEncoding.Utf16BigEndian, 2, new UnicodeEncoding(true, false, true));
        }

        return (DocumentEncoding.Utf8, 0, Utf8Strict);
    }

    private static Encoding GetEncoding(DocumentEncoding encoding)
        => encoding switch
        {
            DocumentEncoding.Utf8 => new UTF8Encoding(false, true),
            DocumentEncoding.Utf8Bom => new UTF8Encoding(true, true),
            DocumentEncoding.Utf16LittleEndian => new UnicodeEncoding(false, true, true),
            DocumentEncoding.Utf16BigEndian => new UnicodeEncoding(true, true, true),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };

    private static string DetectNewLine(string text)
    {
        var crlf = text.IndexOf("\r\n", StringComparison.Ordinal);
        var lf = text.IndexOf('\n');
        var cr = text.IndexOf('\r');

        if (crlf >= 0 && crlf <= (lf < 0 ? int.MaxValue : lf) && crlf <= (cr < 0 ? int.MaxValue : cr))
        {
            return "\r\n";
        }

        if (lf >= 0 && (cr < 0 || lf < cr))
        {
            return "\n";
        }

        return cr >= 0 ? "\r" : Environment.NewLine;
    }
}
