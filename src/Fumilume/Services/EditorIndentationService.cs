using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Indentation;
using AvaloniaEdit.Indentation.CSharp;

namespace Fumilume.Services;

/// <summary>ファイル形式に合う Enter 後のインデント規則を選ぶ。</summary>
internal static class EditorIndentationService
{
    public static IIndentationStrategy Resolve(string? filePath, TextEditorOptions options)
    {
        var language = BracketPairService.LanguageFor(filePath);
        return language switch
        {
            BracketLanguage.CSharp => new CSharpIndentationStrategy(options),
            BracketLanguage.Json or BracketLanguage.JsonWithComments
                => new JsonIndentationStrategy(options.IndentationString, language),
            _ => new DefaultIndentationStrategy(),
        };
    }

    /// <summary>指定位置の行を字下げし、字下げ直後のカーソル位置を返す。</summary>
    public static int IndentLine(
        TextDocument document,
        int offset,
        string? filePath,
        TextEditorOptions options)
    {
        var line = document.GetLineByOffset(Math.Clamp(offset, 0, document.TextLength));
        Resolve(filePath, options).IndentLine(document, line);

        line = document.GetLineByNumber(line.LineNumber);
        var text = document.GetText(line);
        var indentationLength = 0;
        while (indentationLength < text.Length && text[indentationLength] is ' ' or '\t')
        {
            indentationLength++;
        }

        return line.Offset + indentationLength;
    }
}

/// <summary>JSON の括弧の深さから、現在行の字下げを決める。</summary>
internal sealed class JsonIndentationStrategy(
    string indentationString,
    BracketLanguage language = BracketLanguage.Json) : IIndentationStrategy
{
    public void IndentLine(TextDocument document, DocumentLine line)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(line);

        var analysis = BracketPairService.Analyze(document.Text, language);
        var depth = 0;
        foreach (var token in analysis.Tokens)
        {
            if (token.Offset >= line.Offset)
            {
                break;
            }

            depth += token.Character is '(' or '[' or '{' ? 1 : -1;
            depth = Math.Max(0, depth);
        }

        var text = document.GetText(line);
        var existingIndentLength = 0;
        while (existingIndentLength < text.Length && text[existingIndentLength] is ' ' or '\t')
        {
            existingIndentLength++;
        }

        if (existingIndentLength < text.Length && text[existingIndentLength] is ']' or '}')
        {
            depth = Math.Max(0, depth - 1);
        }

        document.Replace(
            line.Offset,
            existingIndentLength,
            string.Concat(Enumerable.Repeat(indentationString, depth)),
            OffsetChangeMappingType.RemoveAndInsert);
    }

    public void IndentLines(TextDocument document, int beginLine, int endLine)
    {
        ArgumentNullException.ThrowIfNull(document);
        for (var lineNumber = beginLine; lineNumber <= endLine; lineNumber++)
        {
            IndentLine(document, document.GetLineByNumber(lineNumber));
        }
    }
}
