using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Fumilume.Models;

namespace Fumilume.Services;

public enum DocumentFormatOutcome
{
    Success,
    Unsupported,
    Invalid,
}

public sealed record DocumentFormatResult(
    DocumentFormatOutcome Outcome,
    string? Text = null,
    string? Message = null);

/// <summary>
/// 文書全体の空白と字下げを形式ごとに整える。外部プロセスへ依存せず、
/// 構文を安全に解釈できない形式では元の文書を返さず失敗として扱う。
/// </summary>
public static class DocumentFormattingService
{
    private static readonly HashSet<string> JsonExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json",
    };

    private static readonly HashSet<string> XmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".axaml", ".xaml", ".config", ".csproj", ".props", ".targets", ".slnx", ".svg",
    };

    private static readonly HashSet<string> BraceLanguageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".js", ".mjs", ".cjs", ".ts", ".css",
    };

    public static DocumentFormatResult Format(
        string? filePath,
        string text,
        string newLine,
        DocumentEncoding encoding,
        int indentationSize,
        bool convertTabsToSpaces)
    {
        if (filePath is null)
        {
            return new(
                DocumentFormatOutcome.Unsupported,
                Message: "保存してファイル形式を確定してから書式整形してください");
        }

        var extension = Path.GetExtension(filePath);
        newLine = DocumentNewLines.IsSupported(newLine) ? newLine : Environment.NewLine;
        indentationSize = Math.Clamp(indentationSize, 1, 16);
        var indent = convertTabsToSpaces ? new string(' ', indentationSize) : "\t";

        if (JsonExtensions.Contains(extension))
        {
            return FormatJson(text, newLine, indentationSize, convertTabsToSpaces);
        }

        if (XmlExtensions.Contains(extension))
        {
            return FormatXml(text, newLine, indent, encoding);
        }

        if (BraceLanguageExtensions.Contains(extension))
        {
            return TryFormatBraceLanguage(text, newLine, indent, out var formatted)
                ? new(DocumentFormatOutcome.Success, formatted)
                : new(
                    DocumentFormatOutcome.Invalid,
                    Message: "括弧、文字列、またはコメントが閉じられていないため書式整形できませんでした");
        }

        return new(
            DocumentFormatOutcome.Unsupported,
            Message: $"{extension} ファイルの書式整形には対応していません");
    }

    private static DocumentFormatResult FormatJson(
        string text,
        string newLine,
        int indentationSize,
        bool convertTabsToSpaces)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = true,
                IndentCharacter = convertTabsToSpaces ? ' ' : '\t',
                IndentSize = convertTabsToSpaces ? indentationSize : 1,
            }))
            {
                document.RootElement.WriteTo(writer);
            }

            var formatted = Encoding.UTF8.GetString(stream.ToArray());
            return new(DocumentFormatOutcome.Success, PreserveFinalNewLine(formatted, text, newLine));
        }
        catch (JsonException)
        {
            return new(
                DocumentFormatOutcome.Invalid,
                Message: "JSONの構文を解釈できないため書式整形できませんでした");
        }
    }

    private static DocumentFormatResult FormatXml(
        string text,
        string newLine,
        string indent,
        DocumentEncoding encoding)
    {
        try
        {
            var readerSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreWhitespace = true,
                XmlResolver = null,
            };
            using var textReader = new StringReader(text);
            using var reader = XmlReader.Create(textReader, readerSettings);
            var document = XDocument.Load(reader, LoadOptions.None);

            var builder = new StringBuilder(text.Length + 64);
            using var stringWriter = new EncodingStringWriter(builder, ResolveEncoding(encoding));
            using (var writer = XmlWriter.Create(stringWriter, new XmlWriterSettings
            {
                CheckCharacters = true,
                Indent = true,
                IndentChars = indent,
                NewLineChars = newLine,
                NewLineHandling = NewLineHandling.None,
                OmitXmlDeclaration = document.Declaration is null,
            }))
            {
                document.Save(writer);
            }

            return new(
                DocumentFormatOutcome.Success,
                PreserveFinalNewLine(builder.ToString(), text, newLine));
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            return new(
                DocumentFormatOutcome.Invalid,
                Message: "XMLの構文を解釈できないため書式整形できませんでした");
        }
    }

    private static bool TryFormatBraceLanguage(
        string text,
        string newLine,
        string indent,
        out string formatted)
    {
        var lines = DocumentNewLines.SplitLines(text);
        var state = new CodeScanState();
        var builder = new StringBuilder(text.Length + lines.Length * indent.Length);

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var preserveLeadingWhitespace = state.Mode is CodeLexMode.VerbatimString
                or CodeLexMode.RawString
                or CodeLexMode.TemplateString;
            var content = preserveLeadingWhitespace ? line : line.TrimStart(' ', '\t');

            if (!preserveLeadingWhitespace && content.Length > 0)
            {
                var leading = CountLeadingClosings(content);
                var braceDepth = Math.Max(0, state.BraceDepth - leading.Braces);
                var hasContinuation = state.ParenthesisDepth - leading.Parentheses > 0
                    || state.BracketDepth - leading.Brackets > 0;
                var indentationLevel = content[0] == '#'
                    ? 0
                    : braceDepth + (hasContinuation ? 1 : 0);
                for (var level = 0; level < indentationLevel; level++)
                {
                    builder.Append(indent);
                }
            }

            builder.Append(content);
            if (lineIndex < lines.Length - 1)
            {
                builder.Append(newLine);
            }

            if (!ScanCodeLine(content, state))
            {
                formatted = text;
                return false;
            }
        }

        if (state.Mode != CodeLexMode.Code
            || state.BraceDepth != 0
            || state.ParenthesisDepth != 0
            || state.BracketDepth != 0)
        {
            formatted = text;
            return false;
        }

        formatted = builder.ToString();
        return true;
    }

    private static ClosingCounts CountLeadingClosings(string content)
    {
        var braces = 0;
        var parentheses = 0;
        var brackets = 0;
        foreach (var character in content)
        {
            switch (character)
            {
                case '}':
                    braces++;
                    break;
                case ')':
                    parentheses++;
                    break;
                case ']':
                    brackets++;
                    break;
                default:
                    return new(braces, parentheses, brackets);
            }
        }

        return new(braces, parentheses, brackets);
    }

    private static bool ScanCodeLine(string line, CodeScanState state)
    {
        for (var index = 0; index < line.Length; index++)
        {
            if (state.Mode == CodeLexMode.BlockComment)
            {
                var end = line.IndexOf("*/", index, StringComparison.Ordinal);
                if (end < 0)
                {
                    return true;
                }

                state.Mode = CodeLexMode.Code;
                index = end + 1;
                continue;
            }

            if (state.Mode == CodeLexMode.VerbatimString)
            {
                if (line[index] != '"')
                {
                    continue;
                }

                if (index + 1 < line.Length && line[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                state.Mode = CodeLexMode.Code;
                continue;
            }

            if (state.Mode == CodeLexMode.RawString)
            {
                if (line[index] != '"')
                {
                    continue;
                }

                var quoteCount = CountRun(line, index, '"');
                if (quoteCount >= state.RawStringQuoteCount)
                {
                    state.Mode = CodeLexMode.Code;
                    index += state.RawStringQuoteCount - 1;
                }

                continue;
            }

            if (state.Mode == CodeLexMode.TemplateString)
            {
                if (line[index] == '\\')
                {
                    index++;
                }
                else if (line[index] == '`')
                {
                    state.Mode = CodeLexMode.Code;
                }

                continue;
            }

            var character = line[index];
            if (character == '/' && index + 1 < line.Length)
            {
                if (line[index + 1] == '/')
                {
                    return true;
                }

                if (line[index + 1] == '*')
                {
                    state.Mode = CodeLexMode.BlockComment;
                    index++;
                    continue;
                }
            }

            if (TryStartRawString(line, ref index, state)
                || TryStartVerbatimString(line, ref index, state))
            {
                continue;
            }

            if (character == '`')
            {
                state.Mode = CodeLexMode.TemplateString;
                continue;
            }

            if (character == '"' || character == '\'')
            {
                if (!SkipQuotedString(line, ref index, character))
                {
                    return false;
                }

                continue;
            }

            switch (character)
            {
                case '{':
                    state.BraceDepth++;
                    break;
                case '}':
                    if (--state.BraceDepth < 0)
                    {
                        return false;
                    }

                    break;
                case '(':
                    state.ParenthesisDepth++;
                    break;
                case ')':
                    if (--state.ParenthesisDepth < 0)
                    {
                        return false;
                    }

                    break;
                case '[':
                    state.BracketDepth++;
                    break;
                case ']':
                    if (--state.BracketDepth < 0)
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    private static bool TryStartRawString(string line, ref int index, CodeScanState state)
    {
        var quoteStart = index;
        if (line[index] == '$')
        {
            while (quoteStart < line.Length && line[quoteStart] == '$')
            {
                quoteStart++;
            }
        }

        if (quoteStart >= line.Length || line[quoteStart] != '"')
        {
            return false;
        }

        var quoteCount = CountRun(line, quoteStart, '"');
        if (quoteCount < 3)
        {
            return false;
        }

        state.Mode = CodeLexMode.RawString;
        state.RawStringQuoteCount = quoteCount;
        index = quoteStart + quoteCount - 1;
        return true;
    }

    private static bool TryStartVerbatimString(string line, ref int index, CodeScanState state)
    {
        var quoteIndex = index;
        if (line[index] == '@')
        {
            quoteIndex++;
            if (quoteIndex < line.Length && line[quoteIndex] == '$')
            {
                quoteIndex++;
            }
        }
        else if (line[index] == '$'
            && index + 1 < line.Length
            && line[index + 1] == '@')
        {
            quoteIndex += 2;
        }
        else
        {
            return false;
        }

        if (quoteIndex >= line.Length || line[quoteIndex] != '"')
        {
            return false;
        }

        state.Mode = CodeLexMode.VerbatimString;
        index = quoteIndex;
        return true;
    }

    private static bool SkipQuotedString(string line, ref int index, char quote)
    {
        for (index++; index < line.Length; index++)
        {
            if (line[index] == '\\')
            {
                index++;
            }
            else if (line[index] == quote)
            {
                return true;
            }
        }

        return false;
    }

    private static int CountRun(string text, int start, char character)
    {
        var index = start;
        while (index < text.Length && text[index] == character)
        {
            index++;
        }

        return index - start;
    }

    private static string PreserveFinalNewLine(string formatted, string original, string newLine)
    {
        var normalized = NormalizeLineEndings(formatted, newLine).TrimEnd('\r', '\n');
        return EndsWithNewLine(original) ? normalized + newLine : normalized;
    }

    private static string NormalizeLineEndings(string text, string newLine)
        => string.Join(newLine, DocumentNewLines.SplitLines(text));

    private static bool EndsWithNewLine(string text)
        => text.EndsWith('\r') || text.EndsWith('\n');

    private static Encoding ResolveEncoding(DocumentEncoding encoding)
        => encoding switch
        {
            DocumentEncoding.Utf16LittleEndian => Encoding.Unicode,
            DocumentEncoding.Utf16BigEndian => Encoding.BigEndianUnicode,
            _ => Encoding.UTF8,
        };

    private sealed class EncodingStringWriter(StringBuilder builder, Encoding encoding)
        : StringWriter(builder, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding { get; } = encoding;
    }

    private sealed class CodeScanState
    {
        public CodeLexMode Mode { get; set; }

        public int RawStringQuoteCount { get; set; }

        public int BraceDepth { get; set; }

        public int ParenthesisDepth { get; set; }

        public int BracketDepth { get; set; }
    }

    private enum CodeLexMode
    {
        Code,
        BlockComment,
        VerbatimString,
        RawString,
        TemplateString,
    }

    private readonly record struct ClosingCounts(int Braces, int Parentheses, int Brackets);
}
