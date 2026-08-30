namespace Fumilume.Models;

public enum DocumentEncoding
{
    Utf8,
    Utf8Bom,
    Utf16LittleEndian,
    Utf16BigEndian,
}

/// <summary>保存時に選べる改行コード。文字列の正本を XAML のコマンド引数でも共有する。</summary>
public static class DocumentNewLines
{
    public const string CrLf = "\r\n";
    public const string Lf = "\n";
    public const string Cr = "\r";

    public static bool IsSupported(string value) => value is CrLf or Lf or Cr;

    /// <summary>CRLF・LF・CRを同じ行区切りとして分割する。末尾の空行も保持する。</summary>
    public static string[] SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n'))
            {
                continue;
            }

            lines.Add(text[start..index]);
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            start = index + 1;
        }

        lines.Add(text[start..]);
        return [.. lines];
    }

    /// <summary>行区切りを元の形のまま保ち、各行の本文だけを変換する。</summary>
    public static string TransformLines(string text, Func<string, string> transform)
    {
        var result = new System.Text.StringBuilder(text.Length);
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n'))
            {
                continue;
            }

            result.Append(transform(text[start..index]));
            result.Append(text[index]);
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                result.Append(text[++index]);
            }

            start = index + 1;
        }

        result.Append(transform(text[start..]));
        return result.ToString();
    }
}

public sealed record TextDocumentContent(
    string Text,
    DocumentEncoding Encoding,
    string NewLine);
