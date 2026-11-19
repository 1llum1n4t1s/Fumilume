using System.Text;
using Fumilume.Models;

namespace Fumilume.Services;

public enum MarkdownBlockKind
{
    Heading,
    Paragraph,
    Bullet,
    Numbered,
    Quote,
    Code,
    Rule,
    Table,
}

public enum MarkdownColumnAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>表ブロック。先頭行が見出し行で、各行の列数は Alignments と同じ。</summary>
public sealed record MarkdownTable(
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<MarkdownColumnAlignment> Alignments);

public sealed record MarkdownBlock(
    MarkdownBlockKind Kind,
    string Text,
    int Level = 0,
    int Number = 0,
    MarkdownTable? Table = null);

/// <summary>プレビュー用の軽量 Markdown ブロック解析。外部通信や HTML 実行は行わない。</summary>
public static class MarkdownDocumentParser
{
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        var paragraph = new StringBuilder();
        var code = new StringBuilder();
        var codeFenceLength = 0;

        var lines = DocumentNewLines.EnumerateLines(markdown ?? string.Empty).ToList();
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var trimmed = line.TrimStart();
            var markerLength = CountPrefix(trimmed, '`');
            if (markerLength >= 3 && (codeFenceLength == 0
                || (markerLength >= codeFenceLength
                    && trimmed[markerLength..].All(character => character is ' ' or '\t'))))
            {
                FlushParagraph();
                if (codeFenceLength > 0)
                {
                    blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, code.ToString().TrimEnd('\r', '\n')));
                    code.Clear();
                    codeFenceLength = 0;
                }
                else
                {
                    // 短いフェンスや異種の記号・後続文字ではコードブロックを閉じない。
                    codeFenceLength = markerLength;
                }
                continue;
            }

            if (codeFenceLength > 0)
            {
                code.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            if (TryReadTable(lines, ref lineIndex, out var table))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Table, string.Empty, Table: table));
                continue;
            }

            var headingLevel = CountPrefix(trimmed, '#');
            if (headingLevel is >= 1 and <= 6 && trimmed.Length > headingLevel && trimmed[headingLevel] == ' ')
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(
                    MarkdownBlockKind.Heading,
                    StripInlineMarkup(trimmed[(headingLevel + 1)..]),
                    headingLevel));
            }
            else if (IsRule(trimmed))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Rule, string.Empty));
            }
            else if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Quote, StripInlineMarkup(trimmed[2..])));
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal)
                     || trimmed.StartsWith("* ", StringComparison.Ordinal)
                     || trimmed.StartsWith("+ ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Bullet, StripInlineMarkup(trimmed[2..])));
            }
            else if (TryGetNumberedItem(trimmed, out var number, out var numberedText))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(
                    MarkdownBlockKind.Numbered,
                    StripInlineMarkup(numberedText),
                    Number: number));
            }
            else
            {
                if (paragraph.Length > 0)
                {
                    paragraph.Append(' ');
                }

                paragraph.Append(line.Trim());
            }
        }

        FlushParagraph();
        if (codeFenceLength > 0 && code.Length > 0)
        {
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, code.ToString().TrimEnd('\r', '\n')));
        }

        return blocks;

        void FlushParagraph()
        {
            if (paragraph.Length == 0)
            {
                return;
            }

            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, StripInlineMarkup(paragraph.ToString())));
            paragraph.Clear();
        }
    }

    private const int MaximumTableRows = 5000;

    /// <summary>見出し行と区切り行（---|:--:）が揃った GFM のパイプ表だけを表として扱う。</summary>
    private static bool TryReadTable(List<string> lines, ref int index, out MarkdownTable? table)
    {
        table = null;
        if (index + 1 >= lines.Count || !lines[index].Contains('|'))
        {
            return false;
        }

        var header = SplitTableRow(lines[index]);
        var delimiters = SplitTableRow(lines[index + 1]);
        if (header.Count == 0 || header.Count != delimiters.Count
            || !delimiters.All(IsDelimiterCell))
        {
            return false;
        }

        var alignments = delimiters.Select(cell =>
        {
            var left = cell.StartsWith(':');
            var right = cell.EndsWith(':');
            return left && right ? MarkdownColumnAlignment.Center
                : right ? MarkdownColumnAlignment.Right
                : MarkdownColumnAlignment.Left;
        }).ToArray();

        var rows = new List<IReadOnlyList<string>> { NormalizeRow(header, alignments.Length) };
        var next = index + 2;
        while (next < lines.Count && rows.Count < MaximumTableRows
            && !string.IsNullOrWhiteSpace(lines[next]) && lines[next].Contains('|'))
        {
            rows.Add(NormalizeRow(SplitTableRow(lines[next]), alignments.Length));
            next++;
        }

        index = next - 1;
        table = new MarkdownTable(rows, alignments);
        return true;
    }

    private static IReadOnlyList<string> NormalizeRow(List<string> cells, int count)
    {
        var row = new string[count];
        for (var column = 0; column < count; column++)
        {
            row[column] = column < cells.Count ? StripInlineMarkup(cells[column]) : string.Empty;
        }

        return row;
    }

    private static bool IsDelimiterCell(string cell)
    {
        var body = cell.Trim(':');
        return body.Length >= 1 && body.All(character => character == '-');
    }

    private static List<string> SplitTableRow(string line)
    {
        var text = line.Trim();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inCode = false;
        for (var position = 0; position < text.Length; position++)
        {
            var character = text[position];
            if (character == '\\' && position + 1 < text.Length && text[position + 1] == '|')
            {
                cell.Append('|');
                position++;
            }
            else if (character == '`')
            {
                inCode = !inCode;
                cell.Append(character);
            }
            else if (character == '|' && !inCode)
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(character);
            }
        }

        cells.Add(cell.ToString().Trim());
        // 行頭・行末の区切りが作る空セルを除く。
        if (text.StartsWith('|') && cells.Count > 0)
        {
            cells.RemoveAt(0);
        }

        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal) && cells.Count > 0)
        {
            cells.RemoveAt(cells.Count - 1);
        }

        return cells;
    }

    internal static string StripInlineMarkup(string text)
    {
        var result = new StringBuilder(text.Length);
        var closeLabel = -1;
        var closeUrl = -1;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '[' && TryReadLink(text, index, ref closeLabel, ref closeUrl, out var label, out var consumed))
            {
                result.Append(label);
                index += consumed - 1;
                continue;
            }

            if (text[index] == '!' && index + 1 < text.Length && text[index + 1] == '['
                && TryReadLink(text, index + 1, ref closeLabel, ref closeUrl, out var alt, out consumed))
            {
                result.Append("画像: ").Append(alt);
                index += consumed;
                continue;
            }

            if (text[index] is '*' or '`')
            {
                continue;
            }

            if (text[index] == '_' &&
                (index == 0 || index == text.Length - 1 || char.IsWhiteSpace(text[Math.Max(0, index - 1)])))
            {
                continue;
            }

            result.Append(text[index]);
        }

        return result.ToString();
    }

    private static bool TryReadLink(string text, int start, ref int closeLabel, ref int closeUrl,
        out string label, out int consumed)
    {
        // 失敗したリンクでも同じ末尾を繰り返し探索しない。見つからない位置も末尾として記憶する。
        if (closeLabel <= start)
        {
            closeLabel = text.IndexOf(']', start + 1);
            if (closeLabel < 0) closeLabel = text.Length;
        }
        if (closeLabel + 1 >= text.Length || text[closeLabel + 1] != '(')
        {
            label = string.Empty;
            consumed = 0;
            return false;
        }

        if (closeUrl <= closeLabel + 1)
        {
            closeUrl = text.IndexOf(')', closeLabel + 2);
            if (closeUrl < 0) closeUrl = text.Length;
        }
        if (closeUrl >= text.Length)
        {
            label = string.Empty;
            consumed = 0;
            return false;
        }

        label = text[(start + 1)..closeLabel];
        consumed = closeUrl - start + 1;
        return true;
    }

    private static int CountPrefix(string text, char marker)
    {
        var count = 0;
        while (count < text.Length && text[count] == marker)
        {
            count++;
        }

        return count;
    }

    private static bool IsRule(string text)
    {
        var compact = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact.Length >= 3 && compact.All(character => character == '-')
            || compact.Length >= 3 && compact.All(character => character == '*');
    }

    private static bool TryGetNumberedItem(string text, out int number, out string body)
    {
        var index = 0;
        while (index < text.Length && char.IsDigit(text[index]))
        {
            index++;
        }

        if (index > 0 && index + 1 < text.Length && text[index] == '.' && text[index + 1] == ' ')
        {
            _ = int.TryParse(text.AsSpan(0, index), out number);
            body = text[(index + 2)..];
            return true;
        }

        number = 0;
        body = string.Empty;
        return false;
    }
}
