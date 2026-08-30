using System.Text;
using System.Text.RegularExpressions;
using Fumilume.Models;

namespace Fumilume.Services;

/// <summary>アウトラインの 1 行。</summary>
/// <param name="Level">入れ子の深さ（1 が最上位）。表示の字下げに使う。</param>
/// <param name="Title">見出しとして出す文言。</param>
/// <param name="LineNumber">本文での行番号（1 始まり）。</param>
public sealed record OutlineItem(int Level, string Title, int LineNumber)
{
    /// <summary>字下げの幅（px）。XAML から Thickness を組めないので、間隔をあける桟の幅として渡す。</summary>
    public double Indent => (Level - 1) * 10.0;
}

/// <summary>
/// 秀丸エディタの「アウトライン解析」相当。本文から見出しを拾って左パネルへ並べる。
///
/// 対応するのは次の 2 つだけで、判定できない形式では空を返す。中途半端に拾った一覧は
/// 「ここに無いなら本文にも無い」と誤読させるため、出さないほうが害が小さい。
///
/// - Markdown: ATX（<c>#</c>）と Setext（<c>===</c> / <c>---</c>）の見出し
/// - 波括弧で入れ子を作る言語: 型とメンバーの宣言
///
/// 後者は構文解析ではなく行単位の照合なので、複数行にまたがる宣言や逐語的文字列
/// （<c>@"..."</c>）の中身は正しく数えられない。取りこぼしても本文の編集には影響しない。
/// </summary>
public static partial class OutlineService
{
    /// <summary>並べる上限。これを超える見出しは一覧として使えないので打ち切る。</summary>
    internal const int MaximumItems = 2000;

    private const int MaximumLevel = 6;

    private static readonly HashSet<string> MarkdownExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdown", ".mkd" };

    private static readonly HashSet<string> BraceExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".csx", ".c", ".h", ".cpp", ".hpp", ".cc", ".hh", ".cxx",
            ".java", ".kt", ".kts", ".scala", ".swift", ".go", ".rs",
            ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx", ".php",
        };

    /// <summary>宣言に見えても実体は制御構文・呼び出しの語。これらが混ざる行は見出しにしない。</summary>
    private static readonly HashSet<string> NotDeclarations =
        new(StringComparer.Ordinal)
        {
            "if", "else", "while", "for", "foreach", "do", "switch", "case", "when",
            "try", "catch", "finally", "using", "lock", "fixed", "unchecked", "checked",
            "return", "throw", "yield", "await", "new", "break", "continue", "goto",
            "typeof", "nameof", "sizeof", "default", "is", "as", "in", "out",
        };

    /// <summary>本文から見出しを拾う。対応しない形式では空を返す。</summary>
    public static IReadOnlyList<OutlineItem> Parse(string? filePath, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var extension = filePath is null ? string.Empty : Path.GetExtension(filePath);
        var lines = DocumentNewLines.SplitLines(text);
        List<OutlineItem> items = MarkdownExtensions.Contains(extension) ? ParseMarkdown(lines)
            : BraceExtensions.Contains(extension) ? ParseBraces(lines)
            : [];

        return items.Count > MaximumItems ? items.GetRange(0, MaximumItems) : items;
    }

    /// <summary>この拡張子のアウトラインを出せるか。パネルの案内文の出し分けに使う。</summary>
    public static bool IsSupported(string? filePath)
    {
        var extension = filePath is null ? string.Empty : Path.GetExtension(filePath);
        return MarkdownExtensions.Contains(extension) || BraceExtensions.Contains(extension);
    }

    // ===== Markdown =====

    private static List<OutlineItem> ParseMarkdown(string[] lines)
    {
        var items = new List<OutlineItem>();
        string? fence = null;

        // YAML の前書きは本文ではないので、閉じるまで丸ごと飛ばす（--- を見出し扱いしない）。
        var start = lines.Length > 0 && lines[0].Trim() is "---" ? ClosingFrontMatter(lines) : 0;

        for (var index = start; index < lines.Length; index++)
        {
            var trimmed = lines[index].Trim();

            if (FencePattern().Match(trimmed) is { Success: true } fenceMatch)
            {
                // 開いている記号と同じ種類でだけ閉じる（``` の中の ~~~ は本文）。
                var kind = fenceMatch.Value[..1];
                fence = fence is null ? kind : fence == kind ? null : fence;
                continue;
            }

            if (fence is not null || trimmed.Length == 0)
            {
                continue;
            }

            if (AtxPattern().Match(trimmed) is { Success: true } atx)
            {
                items.Add(new OutlineItem(
                    atx.Groups["hashes"].Value.Length,
                    atx.Groups["title"].Value.Trim(),
                    index + 1));
                continue;
            }

            // Setext は次の行の下線で決まる。箇条書きや引用の記号で始まる行は本文なので除く。
            if (trimmed[0] is '#' or '-' or '*' or '+' or '>' or '|' or '=' || index + 1 >= lines.Length)
            {
                continue;
            }

            var underline = lines[index + 1].Trim();
            if (underline.Length >= 2 && (underline.All(c => c == '=') || underline.All(c => c == '-')))
            {
                items.Add(new OutlineItem(underline[0] == '=' ? 1 : 2, trimmed, index + 1));
                index++;
            }
        }

        return items;
    }

    private static int ClosingFrontMatter(string[] lines)
    {
        for (var index = 1; index < lines.Length; index++)
        {
            if (lines[index].Trim() is "---" or "...")
            {
                return index + 1;
            }
        }

        return 0;
    }

    // ===== 波括弧で入れ子を作る言語 =====

    private static List<OutlineItem> ParseBraces(string[] lines)
    {
        var items = new List<OutlineItem>();
        var depth = 0;
        var inBlockComment = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var code = StripCommentsAndStrings(lines[index], ref inBlockComment);
            if (Declaration(code.Trim()) is { } title)
            {
                items.Add(new OutlineItem(Math.Clamp(depth + 1, 1, MaximumLevel), title, index + 1));
            }

            depth = Math.Max(0, depth + code.Count(c => c == '{') - code.Count(c => c == '}'));
        }

        return items;
    }

    /// <summary>行 1 本を宣言として読めるなら見出しの文言を返す。読めなければ <see langword="null"/>。</summary>
    private static string? Declaration(string trimmed)
    {
        // 属性・プリプロセッサ・メソッド連鎖の続きは宣言ではない。
        if (trimmed.Length == 0 || trimmed[0] is '[' or '#' or '.' or '}' or ')' or ':' or ',')
        {
            return null;
        }

        if (TypePattern().Match(trimmed) is { Success: true } type)
        {
            return type.Groups["name"].Value.Trim();
        }

        var member = MemberPattern().Match(trimmed);
        if (!member.Success)
        {
            member = PropertyPattern().Match(trimmed);
        }

        if (!member.Success || NotDeclarations.Contains(member.Groups["name"].Value))
        {
            return null;
        }

        // 「throw new Foo(...)」のように、制御構文の続きが宣言に見えることがある。
        var signature = member.Groups["signature"].Value;
        return WordPattern().Matches(signature).Any(word => NotDeclarations.Contains(word.Value))
            ? null
            : member.Groups["name"].Value.Trim();
    }

    /// <summary>
    /// 括弧の数え間違いを防ぐため、注釈と文字列の中身を落とす。逐語的文字列が行をまたぐ場合は
    /// 追随できないが、その行以降の入れ子が 1 段ずれるだけで一覧そのものは出る。
    /// </summary>
    private static string StripCommentsAndStrings(string line, ref bool inBlockComment)
    {
        var builder = new StringBuilder(line.Length);
        var quote = '\0';

        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];

            if (inBlockComment)
            {
                if (current == '*' && index + 1 < line.Length && line[index + 1] == '/')
                {
                    inBlockComment = false;
                    index++;
                }

                continue;
            }

            if (quote != '\0')
            {
                if (current == '\\' && index + 1 < line.Length)
                {
                    index++;
                }
                else if (current == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
                builder.Append(' ');
                continue;
            }

            if (current == '/' && index + 1 < line.Length)
            {
                if (line[index + 1] == '/')
                {
                    break;
                }

                if (line[index + 1] == '*')
                {
                    inBlockComment = true;
                    index++;
                    continue;
                }
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    [GeneratedRegex("^(?:`{3,}|~{3,})")]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"^(?<hashes>\#{1,6})\s+(?<title>.+?)\s*\#*$")]
    private static partial Regex AtxPattern();

    [GeneratedRegex(
        @"^(?:(?:public|private|protected|internal|file|static|sealed|abstract|partial|export|declare|final|readonly|ref|unsafe|new)\s+)*"
        + @"(?:namespace|class|struct|interface|enum|module|trait|impl|record(?:\s+(?:class|struct))?)\s+"
        + @"(?<name>[@\w][\w\d_]*(?:\.[\w\d_]+)*(?:\s*<[^<>]*>)?)")]
    private static partial Regex TypePattern();

    [GeneratedRegex(@"^(?<signature>[^;{}=()]*\S\s+)(?<name>[@\w][\w\d_]*(?:\s*<[^<>()]*>)?)\s*\(")]
    private static partial Regex MemberPattern();

    [GeneratedRegex(@"^(?<signature>[^;{}=()]*\S\s+)(?<name>[@\w][\w\d_]*)\s*(?:\{\s*(?:get|set|init)\b|=>)")]
    private static partial Regex PropertyPattern();

    [GeneratedRegex(@"[A-Za-z_]\w*")]
    private static partial Regex WordPattern();
}
