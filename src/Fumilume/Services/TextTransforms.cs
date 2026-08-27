using System.Text;

namespace Fumilume.Services;

/// <summary>
/// sakura エディタの「変換系」「編集系」コマンド相当のテキスト変換。
///
/// ここは純粋関数だけを置く。エディタの選択範囲・カーソル・Undo とのやり取りは
/// <see cref="ViewModels.DocumentViewModel"/> 側の責務にして、変換規則そのものを単体で試せるようにする。
/// </summary>
public static class TextTransforms
{
    /// <summary>半角と全角のずれ幅（ASCII の <c>!</c> と全角 <c>！</c> の差）。</summary>
    private const int WidthOffset = 0xFEE0;

    /// <summary>ひらがなとカタカナのずれ幅。</summary>
    private const int KanaOffset = 0x60;

    /// <summary>半角カタカナ（U+FF61〜U+FF9F）を並び順どおりに全角へ写した表。</summary>
    private const string HalfWidthKatakana =
        "｡｢｣､･ｦｧｨｩｪｫｬｭｮｯｰｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝﾞﾟ";

    private const string FullWidthKatakana =
        "。「」、・ヲァィゥェォャュョッーアイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワン゛゜";

    /// <summary>濁点が付く全角カタカナと、その素の字。</summary>
    private const string VoicedKatakana = "ガギグゲゴザジズゼゾダヂヅデドバビブベボヴヷヺ";
    private const string VoicedBase = "カキクケコサシスセソタチツテトハヒフヘホウワヲ";

    /// <summary>半濁点が付く全角カタカナと、その素の字。</summary>
    private const string SemiVoicedKatakana = "パピプペポ";
    private const string SemiVoicedBase = "ハヒフヘホ";

    // ===== 大文字・小文字 =====

    public static string ToLower(string text) => text.ToLowerInvariant();

    public static string ToUpper(string text) => text.ToUpperInvariant();

    // ===== 全角・半角 =====

    /// <summary>半角英数記号→全角英数記号。カタカナは触らない。</summary>
    public static string ToFullWidthAlphanumeric(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character switch
            {
                ' ' => '　',
                >= '!' and <= '~' => (char)(character + WidthOffset),
                _ => character,
            });
        }

        return builder.ToString();
    }

    /// <summary>全角英数記号→半角英数記号。カタカナは触らない。</summary>
    public static string ToHalfWidthAlphanumeric(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character switch
            {
                '　' => ' ',
                >= '！' and <= '～' => (char)(character - WidthOffset),
                _ => character,
            });
        }

        return builder.ToString();
    }

    /// <summary>全角→半角（英数記号とカタカナの両方）。</summary>
    public static string ToHalfWidth(string text)
        => ToHalfWidthKatakana(ToHalfWidthAlphanumeric(text));

    /// <summary>半角→全角（英数記号とカタカナの両方）。</summary>
    public static string ToFullWidth(string text)
        => ToFullWidthAlphanumeric(ToFullWidthKatakana(text));

    /// <summary>全角カタカナ→半角カタカナ。濁点・半濁点は 2 文字へ分解する。</summary>
    public static string ToHalfWidthKatakana(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            var voiced = VoicedKatakana.IndexOf(character);
            if (voiced >= 0)
            {
                AppendHalfWidthKatakana(builder, VoicedBase[voiced]);
                builder.Append('ﾞ');
                continue;
            }

            var semiVoiced = SemiVoicedKatakana.IndexOf(character);
            if (semiVoiced >= 0)
            {
                AppendHalfWidthKatakana(builder, SemiVoicedBase[semiVoiced]);
                builder.Append('ﾟ');
                continue;
            }

            AppendHalfWidthKatakana(builder, character);
        }

        return builder.ToString();
    }

    /// <summary>半角カタカナ→全角カタカナ。後続の濁点・半濁点は 1 文字へ合成する。</summary>
    public static string ToFullWidthKatakana(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var position = HalfWidthKatakana.IndexOf(text[index]);
            if (position < 0)
            {
                builder.Append(text[index]);
                continue;
            }

            var full = FullWidthKatakana[position];

            // 「ｶ」＋「ﾞ」を「ガ」へ畳む。合成できない字のときは濁点をそのまま次の周回へ残す。
            if (index + 1 < text.Length)
            {
                var combined = Combine(full, text[index + 1]);
                if (combined != full)
                {
                    builder.Append(combined);
                    index++;
                    continue;
                }
            }

            builder.Append(full);
        }

        return builder.ToString();
    }

    // ===== ひらがな・カタカナ =====

    /// <summary>ひらがな→カタカナ。</summary>
    public static string ToKatakana(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character is >= 'ぁ' and <= 'ゖ'
                ? (char)(character + KanaOffset)
                : character);
        }

        return builder.ToString();
    }

    /// <summary>カタカナ→ひらがな。ひらがなに対応しない「ヴ・ヷ・ヺ」はそのまま残す。</summary>
    public static string ToHiragana(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character is >= 'ァ' and <= 'ヶ'
                ? (char)(character - KanaOffset)
                : character);
        }

        return builder.ToString();
    }

    /// <summary>半角＋全ひら→全角・カタカナ（sakura の F_TOZENKAKUKATA 相当）。</summary>
    public static string ToFullWidthKatakanaAll(string text)
        => ToKatakana(ToFullWidth(text));

    /// <summary>半角＋全カタ→全角・ひらがな（sakura の F_TOZENKAKUHIRA 相当）。</summary>
    public static string ToFullWidthHiraganaAll(string text)
        => ToHiragana(ToFullWidth(text));

    /// <summary>半角カタカナ→全角ひらがな（sakura の F_HANKATATOZENHIRA 相当）。</summary>
    public static string HalfWidthKatakanaToHiragana(string text)
        => ToHiragana(ToFullWidthKatakana(text));

    // ===== タブと空白 =====

    /// <summary>TAB→空白。桁位置を保つため、次のタブ位置までを埋める。</summary>
    public static string TabToSpace(string text, int tabWidth)
    {
        tabWidth = Math.Max(1, tabWidth);
        var builder = new StringBuilder(text.Length);
        var column = 0;
        foreach (var character in text)
        {
            if (character == '\t')
            {
                var width = tabWidth - (column % tabWidth);
                builder.Append(' ', width);
                column += width;
            }
            else if (character is '\n' or '\r')
            {
                builder.Append(character);
                column = 0;
            }
            else
            {
                builder.Append(character);
                column++;
            }
        }

        return builder.ToString();
    }

    /// <summary>空白→TAB。タブ位置に達する連続空白だけを畳み、語間の 1 個は残す。</summary>
    public static string SpaceToTab(string text, int tabWidth)
    {
        tabWidth = Math.Max(1, tabWidth);
        var builder = new StringBuilder(text.Length);
        var pending = 0;
        var column = 0;

        foreach (var character in text)
        {
            if (character == ' ')
            {
                pending++;
                column++;
                if (column % tabWidth == 0)
                {
                    // タブ位置ちょうどに揃ったぶんだけを TAB へ畳む。1 個だけの空白は語間なので残す。
                    builder.Append(pending == 1 ? " " : "\t");
                    pending = 0;
                }

                continue;
            }

            builder.Append(' ', pending);
            pending = 0;

            if (character is '\n' or '\r')
            {
                builder.Append(character);
                column = 0;
                continue;
            }

            builder.Append(character);
            column = character == '\t' ? column + tabWidth - (column % tabWidth) : column + 1;
        }

        builder.Append(' ', pending);
        return builder.ToString();
    }

    // ===== 行単位の整形 =====

    /// <summary>各行の先頭の空白を削除（sakura の F_LTRIM 相当）。</summary>
    public static string TrimLineStarts(string text)
        => TransformLines(text, line => line.TrimStart(' ', '\t', '　'));

    /// <summary>各行の末尾の空白を削除（sakura の F_RTRIM 相当）。</summary>
    public static string TrimLineEnds(string text)
        => TransformLines(text, line => line.TrimEnd(' ', '\t', '　'));

    /// <summary>行を並べ替える。比較は序数で、大文字小文字を区別する。</summary>
    public static string SortLines(string text, bool descending)
    {
        var (lines, newLine, trailing) = SplitLines(text);
        var sorted = descending
            ? lines.OrderByDescending(line => line, StringComparer.Ordinal)
            : lines.OrderBy(line => line, StringComparer.Ordinal);
        return string.Join(newLine, sorted) + trailing;
    }

    /// <summary>連続する重複行を 1 行へまとめる（sakura の F_MERGE 相当）。</summary>
    public static string MergeLines(string text)
    {
        var (lines, newLine, trailing) = SplitLines(text);
        var merged = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            if (merged.Count == 0 || !string.Equals(merged[^1], line, StringComparison.Ordinal))
            {
                merged.Add(line);
            }
        }

        return string.Join(newLine, merged) + trailing;
    }

    // ===== Base64 / URL =====

    public static string Base64Encode(string text)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    /// <summary>Base64 として読めないときは <see langword="null"/> を返す（呼び出し側で知らせる）。</summary>
    public static string? Base64Decode(string text)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(text.Trim()));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string UrlEncode(string text) => Uri.EscapeDataString(text);

    /// <summary>URL エンコードとして読めないときは <see langword="null"/> を返す。</summary>
    public static string? UrlDecode(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    // ===== 内部 =====

    private static void AppendHalfWidthKatakana(StringBuilder builder, char character)
    {
        var position = FullWidthKatakana.IndexOf(character);
        builder.Append(position >= 0 ? HalfWidthKatakana[position] : character);
    }

    /// <summary>全角カタカナへ濁点・半濁点を合成する。合成できないときは元の字をそのまま返す。</summary>
    private static char Combine(char full, char mark)
    {
        if (mark == 'ﾞ')
        {
            var position = VoicedBase.IndexOf(full);
            return position >= 0 ? VoicedKatakana[position] : full;
        }

        if (mark == 'ﾟ')
        {
            var position = SemiVoicedBase.IndexOf(full);
            return position >= 0 ? SemiVoicedKatakana[position] : full;
        }

        return full;
    }

    private static string TransformLines(string text, Func<string, string> transform)
    {
        var (lines, newLine, trailing) = SplitLines(text);
        return string.Join(newLine, lines.Select(transform)) + trailing;
    }

    /// <summary>
    /// 改行で割る。末尾の改行は <c>trailing</c> として取り分けておき、
    /// 並べ替えや整形で「最後に空行が増える／消える」のを防ぐ。
    /// </summary>
    private static (List<string> Lines, string NewLine, string Trailing) SplitLines(string text)
    {
        var newLine = DetectNewLine(text);
        var trailing = string.Empty;
        if (text.EndsWith(newLine, StringComparison.Ordinal))
        {
            trailing = newLine;
            text = text[..^newLine.Length];
        }

        return ([.. text.Split(newLine)], newLine, trailing);
    }

    private static string DetectNewLine(string text)
    {
        var index = text.IndexOf('\n');
        if (index < 0)
        {
            return text.Contains('\r') ? "\r" : Environment.NewLine;
        }

        return index > 0 && text[index - 1] == '\r' ? "\r\n" : "\n";
    }
}
