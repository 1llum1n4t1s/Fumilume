namespace Fumilume.Services;

/// <summary>コード中の括弧を文字列・コメントと区別し、深さと対応相手を求める。</summary>
internal static class BracketPairService
{
    private static readonly HashSet<string> CSharpExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csx",
    };

    public static BracketLanguage LanguageFor(string? filePath)
    {
        var extension = Path.GetExtension(filePath) ?? string.Empty;
        if (CSharpExtensions.Contains(extension))
        {
            return BracketLanguage.CSharp;
        }

        if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            return BracketLanguage.Json;
        }

        if (string.Equals(extension, ".jsonc", StringComparison.OrdinalIgnoreCase))
        {
            return BracketLanguage.JsonWithComments;
        }

        return BracketLanguage.None;
    }

    public static BracketAnalysis Analyze(string text, BracketLanguage language)
    {
        if (language == BracketLanguage.None || text.Length == 0)
        {
            return BracketAnalysis.Empty;
        }

        var tokens = new List<BracketToken>();
        var openTokens = new Stack<int>();
        var mode = ScanMode.Code;
        var rawQuoteCount = 0;
        var allowComments = language is BracketLanguage.CSharp or BracketLanguage.JsonWithComments;

        for (var offset = 0; offset < text.Length; offset++)
        {
            var current = text[offset];
            var next = offset + 1 < text.Length ? text[offset + 1] : '\0';

            switch (mode)
            {
                case ScanMode.LineComment:
                    if (current is '\r' or '\n')
                    {
                        mode = ScanMode.Code;
                    }

                    continue;

                case ScanMode.BlockComment:
                    if (current == '*' && next == '/')
                    {
                        mode = ScanMode.Code;
                        offset++;
                    }

                    continue;

                case ScanMode.String:
                    if (current == '\\')
                    {
                        offset++;
                    }
                    else if (current == '"' || current is '\r' or '\n')
                    {
                        mode = ScanMode.Code;
                    }

                    continue;

                case ScanMode.VerbatimString:
                    if (current == '"' && next == '"')
                    {
                        offset++;
                    }
                    else if (current == '"')
                    {
                        mode = ScanMode.Code;
                    }

                    continue;

                case ScanMode.Character:
                    if (current == '\\')
                    {
                        offset++;
                    }
                    else if (current == '\'' || current is '\r' or '\n')
                    {
                        mode = ScanMode.Code;
                    }

                    continue;

                case ScanMode.RawString:
                    if (current == '"' && CountRun(text, offset, '"') >= rawQuoteCount)
                    {
                        offset += rawQuoteCount - 1;
                        mode = ScanMode.Code;
                    }

                    continue;
            }

            if (allowComments && current == '/' && next == '/')
            {
                mode = ScanMode.LineComment;
                offset++;
                continue;
            }

            if (allowComments && current == '/' && next == '*')
            {
                mode = ScanMode.BlockComment;
                offset++;
                continue;
            }

            if (language == BracketLanguage.CSharp
                && TrySkipInterpolatedString(text, offset, out var interpolatedEnd))
            {
                offset = interpolatedEnd;
                continue;
            }

            if (language == BracketLanguage.CSharp && current == '@' && next == '"')
            {
                mode = ScanMode.VerbatimString;
                offset++;
                continue;
            }

            if (current == '"')
            {
                rawQuoteCount = language == BracketLanguage.CSharp ? CountRun(text, offset, '"') : 0;
                if (rawQuoteCount >= 3)
                {
                    mode = ScanMode.RawString;
                    offset += rawQuoteCount - 1;
                }
                else
                {
                    mode = ScanMode.String;
                }

                continue;
            }

            if (language == BracketLanguage.CSharp && current == '\'')
            {
                mode = ScanMode.Character;
                continue;
            }

            if (IsOpening(current))
            {
                tokens.Add(new BracketToken(offset, current, openTokens.Count));
                openTokens.Push(tokens.Count - 1);
                continue;
            }

            if (!IsClosing(current))
            {
                continue;
            }

            if (openTokens.TryPeek(out var openingIndex)
                && IsPair(tokens[openingIndex].Character, current))
            {
                openTokens.Pop();
                var opening = tokens[openingIndex];
                tokens[openingIndex] = opening with { PairOffset = offset };
                tokens.Add(new BracketToken(offset, current, opening.Depth, opening.Offset));
            }
            else
            {
                tokens.Add(new BracketToken(offset, current, Math.Max(0, openTokens.Count - 1)));
            }
        }

        return new BracketAnalysis(tokens);
    }

    private static int CountRun(string text, int start, char value)
    {
        var count = 0;
        while (start + count < text.Length && text[start + count] == value)
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// 補間文字列を 1 個のリテラルとして読み飛ばす。補間式内の文字列も辿り、内側の引用符を
    /// 外側の終端と取り違えないようにする。
    /// </summary>
    private static bool TrySkipInterpolatedString(string text, int start, out int endOffset)
    {
        endOffset = start;
        var quoteOffset = -1;
        var verbatim = false;

        if (text[start] == '@'
            && start + 2 < text.Length
            && text[start + 1] == '$'
            && text[start + 2] == '"')
        {
            quoteOffset = start + 2;
            verbatim = true;
        }
        else if (text[start] == '$')
        {
            var dollarCount = CountRun(text, start, '$');
            var afterDollars = start + dollarCount;
            if (dollarCount == 1
                && afterDollars + 1 < text.Length
                && text[afterDollars] == '@'
                && text[afterDollars + 1] == '"')
            {
                quoteOffset = afterDollars + 1;
                verbatim = true;
            }
            else if (afterDollars < text.Length && text[afterDollars] == '"')
            {
                quoteOffset = afterDollars;
            }
        }

        if (quoteOffset < 0)
        {
            return false;
        }

        var quoteCount = CountRun(text, quoteOffset, '"');
        endOffset = !verbatim && quoteCount >= 3
            ? FindRawStringEnd(text, quoteOffset, quoteCount)
            : FindInterpolatedStringEnd(text, quoteOffset, verbatim);
        return true;
    }

    private static int FindInterpolatedStringEnd(string text, int openingQuoteOffset, bool verbatim)
    {
        for (var offset = openingQuoteOffset + 1; offset < text.Length; offset++)
        {
            var current = text[offset];
            var next = offset + 1 < text.Length ? text[offset + 1] : '\0';

            if (!verbatim && current == '\\')
            {
                offset++;
                continue;
            }

            if (verbatim && current == '"' && next == '"')
            {
                offset++;
                continue;
            }

            if (current == '"')
            {
                return offset;
            }

            if (current == '{')
            {
                if (next == '{')
                {
                    offset++;
                }
                else
                {
                    offset = FindInterpolationEnd(text, offset + 1);
                }
            }
            else if (current == '}' && next == '}')
            {
                offset++;
            }
        }

        return text.Length - 1;
    }

    private static int FindInterpolationEnd(string text, int start)
    {
        var braceDepth = 1;
        for (var offset = start; offset < text.Length; offset++)
        {
            var current = text[offset];
            var next = offset + 1 < text.Length ? text[offset + 1] : '\0';

            if (TrySkipInterpolatedString(text, offset, out var interpolatedEnd))
            {
                offset = interpolatedEnd;
                continue;
            }

            if (current == '@' && next == '"')
            {
                offset = FindVerbatimStringEnd(text, offset + 1);
                continue;
            }

            if (current == '"')
            {
                var quoteCount = CountRun(text, offset, '"');
                offset = quoteCount >= 3
                    ? FindRawStringEnd(text, offset, quoteCount)
                    : FindStringEnd(text, offset, '"');
                continue;
            }

            if (current == '\'')
            {
                offset = FindStringEnd(text, offset, '\'');
                continue;
            }

            if (current == '/' && next == '/')
            {
                offset = FindLineEnd(text, offset + 2);
                continue;
            }

            if (current == '/' && next == '*')
            {
                offset = FindBlockCommentEnd(text, offset + 2);
                continue;
            }

            if (current == '{')
            {
                braceDepth++;
            }
            else if (current == '}' && --braceDepth == 0)
            {
                return offset;
            }
        }

        return text.Length - 1;
    }

    private static int FindStringEnd(string text, int openingQuoteOffset, char quote)
    {
        for (var offset = openingQuoteOffset + 1; offset < text.Length; offset++)
        {
            if (text[offset] == '\\')
            {
                offset++;
            }
            else if (text[offset] == quote || text[offset] is '\r' or '\n')
            {
                return offset;
            }
        }

        return text.Length - 1;
    }

    private static int FindVerbatimStringEnd(string text, int openingQuoteOffset)
    {
        for (var offset = openingQuoteOffset + 1; offset < text.Length; offset++)
        {
            if (text[offset] != '"')
            {
                continue;
            }

            if (offset + 1 < text.Length && text[offset + 1] == '"')
            {
                offset++;
                continue;
            }

            return offset;
        }

        return text.Length - 1;
    }

    private static int FindRawStringEnd(string text, int openingQuoteOffset, int quoteCount)
    {
        for (var offset = openingQuoteOffset + quoteCount; offset < text.Length; offset++)
        {
            if (text[offset] == '"' && CountRun(text, offset, '"') >= quoteCount)
            {
                return offset + quoteCount - 1;
            }
        }

        return text.Length - 1;
    }

    private static int FindLineEnd(string text, int start)
    {
        var offset = start;
        while (offset < text.Length && text[offset] is not ('\r' or '\n'))
        {
            offset++;
        }

        return Math.Min(offset, text.Length - 1);
    }

    private static int FindBlockCommentEnd(string text, int start)
    {
        for (var offset = start; offset + 1 < text.Length; offset++)
        {
            if (text[offset] == '*' && text[offset + 1] == '/')
            {
                return offset + 1;
            }
        }

        return text.Length - 1;
    }

    private static bool IsOpening(char value) => value is '(' or '[' or '{';

    private static bool IsClosing(char value) => value is ')' or ']' or '}';

    private static bool IsPair(char opening, char closing)
        => (opening, closing) is ('(', ')') or ('[', ']') or ('{', '}');

    private enum ScanMode
    {
        Code,
        LineComment,
        BlockComment,
        String,
        VerbatimString,
        Character,
        RawString,
    }
}

internal enum BracketLanguage
{
    None,
    CSharp,
    Json,
    JsonWithComments,
}

internal readonly record struct BracketToken(
    int Offset,
    char Character,
    int Depth,
    int PairOffset = -1);

internal sealed class BracketAnalysis
{
    public static BracketAnalysis Empty { get; } = new([]);

    private readonly BracketToken[] _tokens;
    private readonly Dictionary<int, BracketToken> _tokensByOffset;

    public BracketAnalysis(IEnumerable<BracketToken> tokens)
    {
        _tokens = [.. tokens];
        _tokensByOffset = _tokens.ToDictionary(token => token.Offset);
    }

    public IReadOnlyList<BracketToken> Tokens => _tokens;

    public IEnumerable<BracketToken> InRange(int startOffset, int endOffset)
    {
        var low = 0;
        var high = _tokens.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_tokens[middle].Offset < startOffset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        for (var index = low; index < _tokens.Length && _tokens[index].Offset < endOffset; index++)
        {
            yield return _tokens[index];
        }
    }

    public bool TryGetPairBesideCaret(int caretOffset, out BracketToken token, out BracketToken pair)
    {
        if (caretOffset > 0 && TryGetPair(caretOffset - 1, out token, out pair))
        {
            return true;
        }

        return TryGetPair(caretOffset, out token, out pair);
    }

    private bool TryGetPair(int offset, out BracketToken token, out BracketToken pair)
    {
        if (_tokensByOffset.TryGetValue(offset, out token)
            && token.PairOffset >= 0
            && _tokensByOffset.TryGetValue(token.PairOffset, out pair))
        {
            return true;
        }

        token = default;
        pair = default;
        return false;
    }
}
