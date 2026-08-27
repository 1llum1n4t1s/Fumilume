using Avalonia.Media;
using AvaloniaEdit.Highlighting;

namespace Fumilume.Services;

/// <summary>
/// 拡張子から強調表示（構文ハイライト）の定義を決め、テーマに合う色へ整える。
///
/// AvaloniaEdit に同梱されている定義はライトテーマ前提の配色で、Navy や MidnightBlue の
/// ように暗い色をそのまま使う。ダークの地に載せると本文より暗くなって読めないため、
/// 暗い前景色だけを持ち上げてから使う。
/// </summary>
public static class SyntaxHighlightingService
{
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
    /// 色を戻せるように、定義に最初から入っていた前景色を覚えておく。
    ///
    /// キーの比較を参照に固定しているのは、<see cref="HighlightingColor"/> が中身で等価判定と
    /// ハッシュを計算するため。前景色を書き換えた瞬間にハッシュが変わり、既定の比較では
    /// 控えを引けなくなって「明るくした色」を元の色として覚え直してしまう。
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
    /// <see cref="HighlightingManager.Instance"/> の定義はプロセスで共有されるため、元の色を控えてから
    /// 書き換える。毎回この控えを起点に計算するので、テーマを何度往復しても色が積み上がらない。
    /// 全定義をまとめて処理しないのは、同梱の定義に単体では読み込めないもの（他の定義から参照される
    /// 部品）が含まれており、触ろうとすると例外になるため。
    /// </summary>
    internal static void ApplyTheme(IHighlightingDefinition definition, bool isDark)
    {
        foreach (var color in definition.NamedHighlightingColors)
        {
            if (!OriginalForegrounds.TryGetValue(color, out var original))
            {
                original = TryReadColor(color.Foreground);
                OriginalForegrounds[color] = original;
            }

            if (original is not { } value)
            {
                continue;
            }

            color.Foreground = new SimpleHighlightingBrush(isDark ? Brighten(value) : value);
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

    /// <summary>暗い地でも本文と見分けられる明るさへ持ち上げる。色相は変えない。</summary>
    internal static Color Brighten(Color color)
    {
        var hsl = color.ToHsl();

        // 無彩色（黒や濃いグレー）は色で区別できないぶん、しっかり明るくする。
        var minimumLightness = hsl.S < 0.08 ? 0.82 : 0.64;
        if (hsl.L >= minimumLightness)
        {
            return color;
        }

        return new HslColor(hsl.A, hsl.H, Math.Min(1.0, hsl.S * 1.1), minimumLightness).ToRgb();
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
