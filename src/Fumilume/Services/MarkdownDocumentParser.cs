using System.Text;

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
}

public sealed record MarkdownBlock(MarkdownBlockKind Kind, string Text, int Level = 0);

/// <summary>プレビュー用の軽量 Markdown ブロック解析。外部通信や HTML 実行は行わない。</summary>
public static class MarkdownDocumentParser
{
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        var paragraph = new StringBuilder();
        var code = new StringBuilder();
        var inCode = false;

        foreach (var rawLine in (markdown ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                if (inCode)
                {
                    blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, code.ToString().TrimEnd('\r', '\n')));
                    code.Clear();
                }

                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                code.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            var trimmed = line.TrimStart();
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
            else if (TryGetNumberedItem(trimmed, out var numberedText))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Numbered, StripInlineMarkup(numberedText)));
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
        if (inCode && code.Length > 0)
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

    internal static string StripInlineMarkup(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '[' && TryReadLink(text, index, out var label, out var consumed))
            {
                result.Append(label);
                index += consumed - 1;
                continue;
            }

            if (text[index] == '!' && index + 1 < text.Length && text[index + 1] == '['
                && TryReadLink(text, index + 1, out var alt, out consumed))
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

    private static bool TryReadLink(string text, int start, out string label, out int consumed)
    {
        var closeLabel = text.IndexOf(']', start + 1);
        if (closeLabel < 0 || closeLabel + 1 >= text.Length || text[closeLabel + 1] != '(')
        {
            label = string.Empty;
            consumed = 0;
            return false;
        }

        var closeUrl = text.IndexOf(')', closeLabel + 2);
        if (closeUrl < 0)
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

    private static bool TryGetNumberedItem(string text, out string body)
    {
        var index = 0;
        while (index < text.Length && char.IsDigit(text[index]))
        {
            index++;
        }

        if (index > 0 && index + 1 < text.Length && text[index] == '.' && text[index + 1] == ' ')
        {
            body = text[(index + 2)..];
            return true;
        }

        body = string.Empty;
        return false;
    }
}
