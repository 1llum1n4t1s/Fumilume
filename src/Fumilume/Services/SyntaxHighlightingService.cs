using Avalonia.Media;
using AvaloniaEdit.Highlighting;

namespace Fumilume.Services;

/// <summary>
/// 拡張子から強調表示（構文ハイライト）の定義を決め、One Dark 系の色へ整える。
///
/// AvaloniaEdit に同梱されている定義は言語ごとに色の割り当てが異なるため、コメント・文字列・
/// キーワードなどの意味を共通の役割へ分類する。ダークは Atom One Dark、ライトは同じ色相を持つ
/// One Light を基準にし、どちらの背景でも読めるコントラストを保つ。
/// </summary>
public static class SyntaxHighlightingService
{
    private enum SyntaxRole
    {
        Text,
        Comment,
        String,
        Number,
        Keyword,
        Function,
        Type,
        Variable,
        Property,
        Tag,
        Entity,
        Operator,
        Added,
        Removed,
    }

    private readonly record struct SyntaxPalette(
        Color Text,
        Color Comment,
        Color String,
        Color Number,
        Color Keyword,
        Color Function,
        Color Type,
        Color Variable,
        Color Property,
        Color Tag,
        Color Entity,
        Color Operator,
        Color Added,
        Color Removed)
    {
        public Color For(SyntaxRole role) => role switch
        {
            SyntaxRole.Comment => Comment,
            SyntaxRole.String => String,
            SyntaxRole.Number => Number,
            SyntaxRole.Keyword => Keyword,
            SyntaxRole.Function => Function,
            SyntaxRole.Type => Type,
            SyntaxRole.Variable => Variable,
            SyntaxRole.Property => Property,
            SyntaxRole.Tag => Tag,
            SyntaxRole.Entity => Entity,
            SyntaxRole.Operator => Operator,
            SyntaxRole.Added => Added,
            SyntaxRole.Removed => Removed,
            _ => Text,
        };
    }

    // Atom One Dark の正規パレット。演算子・区切りは専用色ではなく本文色を使う。
    private static readonly SyntaxPalette DarkPalette = new(
        Text: Color.Parse("#ABB2BF"),
        Comment: Color.Parse("#5C6370"),
        String: Color.Parse("#98C379"),
        Number: Color.Parse("#D19A66"),
        Keyword: Color.Parse("#C678DD"),
        Function: Color.Parse("#61AFEF"),
        Type: Color.Parse("#E5C07B"),
        Variable: Color.Parse("#E06C75"),
        Property: Color.Parse("#D19A66"),
        Tag: Color.Parse("#E06C75"),
        Entity: Color.Parse("#56B6C2"),
        Operator: Color.Parse("#ABB2BF"),
        Added: Color.Parse("#98C379"),
        Removed: Color.Parse("#E06C75"));

    // Atom One Light の対応パレット。色相の役割を Dark と揃える。
    private static readonly SyntaxPalette LightPalette = new(
        Text: Color.Parse("#383A42"),
        Comment: Color.Parse("#A0A1A7"),
        String: Color.Parse("#50A14F"),
        Number: Color.Parse("#986801"),
        Keyword: Color.Parse("#A626A4"),
        Function: Color.Parse("#4078F2"),
        Type: Color.Parse("#C18401"),
        Variable: Color.Parse("#E45649"),
        Property: Color.Parse("#986801"),
        Tag: Color.Parse("#E45649"),
        Entity: Color.Parse("#0184BC"),
        Operator: Color.Parse("#383A42"),
        Added: Color.Parse("#50A14F"),
        Removed: Color.Parse("#E45649"));

    /// <summary>
    /// 拡張子から定義を引くときの上書き。AvaloniaEdit の既定解決では合わないものだけを持つ。
    /// </summary>
    private static readonly Dictionary<string, string> DefinitionOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        // 既定では見出しの文字サイズまで変える MarkDownWithFontSize が選ばれる。
        // Fumilume は別途プレビューを持つので、編集画面は色だけ変える MarkDown を使う。
        [".md"] = "MarkDown",
        [".markdown"] = "MarkDown",

        // AvaloniaEdit に TypeScript と XAML の定義は無い。文法が近いもので代用する。
        [".ts"] = "JavaScript",
        [".mjs"] = "JavaScript",
        [".cjs"] = "JavaScript",
        [".axaml"] = "XML",
        [".xaml"] = "XML",
        [".config"] = "XML",
        [".csproj"] = "XML",
        [".props"] = "XML",
        [".targets"] = "XML",
        [".slnx"] = "XML",
        [".svg"] = "XML",
    };

    /// <summary>
    /// 名前から役割を判定できない色も One 系へ寄せられるよう、定義に最初から入っていた色相を覚えておく。
    ///
    /// キーの比較を参照に固定しているのは、<see cref="HighlightingColor"/> が中身で等価判定と
    /// ハッシュを計算するため。前景色を書き換えた瞬間にハッシュが変わるので、既定の比較では
    /// 控えを引けなくなってOne系へ変換した色を元の色として覚え直してしまう。
    /// </summary>
    private static readonly Dictionary<HighlightingColor, Color?> OriginalForegrounds =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// 拡張子に対応する定義を、今のテーマに合う配色で返す。対応が無ければ
    /// <see langword="null"/>（強調しない）。
    /// </summary>
    public static IHighlightingDefinition? Resolve(string? filePath, bool isDark)
    {
        var definition = ResolveDefinition(filePath);
        if (definition is null)
        {
            return null;
        }

        ApplyTheme(definition, isDark);
        return definition;
    }

    /// <summary>
    /// 1 つの定義をテーマへ合わせる。
    ///
    /// <see cref="HighlightingManager.Instance"/> の定義はプロセスで共有されるため、元の色相を控えてから
    /// 書き換える。毎回、固定パレットかこの控えを起点にするので、テーマを何度往復しても色が積み上がらない。
    /// 全定義をまとめて処理しないのは、同梱の定義に単体では読み込めないもの（他の定義から参照される
    /// 部品）が含まれており、触ろうとすると例外になるため。
    /// </summary>
    internal static void ApplyTheme(IHighlightingDefinition definition, bool isDark)
    {
        var colors = new HashSet<HighlightingColor>(ReferenceEqualityComparer.Instance);
        foreach (var color in definition.NamedHighlightingColors)
        {
            colors.Add(color);
        }

        // Markdown の字下げコードが C# を参照するように、定義の中には別定義の RuleSet を
        // 入れ子で使うものがある。NamedHighlightingColors は直下の色しか返さないため、
        // 実際の描画ルールから参照先までたどらないと Blue / Green などの既定色が残る。
        CollectRuleSetColors(
            definition.MainRuleSet,
            new HashSet<HighlightingRuleSet>(ReferenceEqualityComparer.Instance),
            colors);

        foreach (var color in colors)
        {
            if (!OriginalForegrounds.TryGetValue(color, out var original))
            {
                original = TryReadColor(color.Foreground);
                OriginalForegrounds[color] = original;
            }

            var palette = isDark ? DarkPalette : LightPalette;
            var role = ClassifyRole(color.Name);
            if (role is { } knownRole)
            {
                color.Foreground = new SimpleHighlightingBrush(palette.For(knownRole));
            }
            else if (original is { } value)
            {
                color.Foreground = new SimpleHighlightingBrush(MapOriginalColor(value, palette));
            }
        }
    }

    private static void CollectRuleSetColors(
        HighlightingRuleSet? ruleSet,
        HashSet<HighlightingRuleSet> visitedRuleSets,
        HashSet<HighlightingColor> colors)
    {
        if (ruleSet is null || !visitedRuleSets.Add(ruleSet))
        {
            return;
        }

        foreach (var rule in ruleSet.Rules)
        {
            AddColor(rule.Color, colors);
        }

        foreach (var span in ruleSet.Spans)
        {
            AddColor(span.StartColor, colors);
            AddColor(span.SpanColor, colors);
            AddColor(span.EndColor, colors);
            CollectRuleSetColors(span.RuleSet, visitedRuleSets, colors);
        }
    }

    private static void AddColor(HighlightingColor? color, HashSet<HighlightingColor> colors)
    {
        if (color is not null)
        {
            colors.Add(color);
        }
    }

    private static IHighlightingDefinition? ResolveDefinition(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(extension))
        {
            return null;
        }

        try
        {
            return DefinitionOverrides.TryGetValue(extension, out var name)
                ? HighlightingManager.Instance.GetDefinition(name)
                : HighlightingManager.Instance.GetDefinitionByExtension(extension);
        }
        catch (HighlightingDefinitionInvalidException ex)
        {
            // 定義が読めないのは色が付かないだけのこと。編集はそのまま続けられる。
            AppLogger.For("Fumilume.SyntaxHighlightingService").Warn(
                $"強調表示の定義を読み込めませんでした: {extension}",
                ex);
            return null;
        }
    }

    private static SyntaxRole? ClassifyRole(string? colorName)
    {
        if (string.IsNullOrWhiteSpace(colorName))
        {
            return null;
        }

        if (HasAny(colorName, "Added"))
        {
            return SyntaxRole.Added;
        }

        if (HasAny(colorName, "Removed", "Broken"))
        {
            return SyntaxRole.Removed;
        }

        if (HasAny(colorName, "Comment", "BlockQuote", "JavaDoc"))
        {
            return SyntaxRole.Comment;
        }

        if (HasAny(colorName, "String", "Character", "Char", "Regex", "CData", "AttributeValue", "Code", "FileName"))
        {
            return SyntaxRole.String;
        }

        if (HasAny(colorName, "Type", "Class"))
        {
            return SyntaxRole.Type;
        }

        if (HasAny(colorName, "Number", "Digit", "Constant", "Literal", "TrueFalse", "Bool", "Null", "Date", "Position", "Value"))
        {
            return SyntaxRole.Number;
        }

        if (HasAny(colorName, "Tag", "Heading", "Selector", "Section", "Header"))
        {
            return SyntaxRole.Tag;
        }

        if (HasAny(colorName, "Variable"))
        {
            return SyntaxRole.Variable;
        }

        if (HasAny(colorName, "Attribute", "Property", "Field"))
        {
            return SyntaxRole.Property;
        }

        if (HasAny(colorName, "Keyword", "Statement", "Modifier", "Control", "Access", "Visibility", "Namespace", "Package", "Preprocessor", "This", "Friend", "GetSet"))
        {
            return SyntaxRole.Keyword;
        }

        if (HasAny(colorName, "Method", "Function", "Command"))
        {
            return SyntaxRole.Function;
        }

        if (HasAny(colorName, "Link", "Image"))
        {
            return SyntaxRole.Function;
        }

        if (HasAny(colorName, "Entity"))
        {
            return SyntaxRole.Entity;
        }

        if (HasAny(colorName, "Operator", "Punctuation", "Brace", "Colon", "Slash", "Assignment"))
        {
            return SyntaxRole.Operator;
        }

        return SyntaxRole.Text;
    }

    private static bool HasAny(string value, params string[] candidates)
        => candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    /// <summary>未知の役割も元の色相に近い One 系の色へ寄せ、言語固有の原色へ戻さない。</summary>
    private static Color MapOriginalColor(Color color, SyntaxPalette palette)
    {
        var hsl = color.ToHsl();

        if (hsl.S < 0.15)
        {
            return palette.Text;
        }

        return hsl.H switch
        {
            < 15 or >= 345 => palette.Variable,
            < 55 => palette.Number,
            < 85 => palette.Type,
            < 165 => palette.String,
            < 200 => palette.Entity,
            < 260 => palette.Function,
            < 330 => palette.Keyword,
            _ => palette.Variable,
        };
    }

    /// <summary>
    /// 定義に入っている前景色を読む。
    /// 描画時の状況（<c>ITextRunConstructionContext</c>）から色を決める種類のブラシは
    /// ここでは読めないので、その色は触らずに元のまま使う。
    /// </summary>
    private static Color? TryReadColor(HighlightingBrush? brush)
    {
        if (brush is null)
        {
            return null;
        }

        try
        {
            return brush.GetColor(null!);
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
        {
            return null;
        }
    }
}
