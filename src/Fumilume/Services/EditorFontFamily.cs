using System.Text;

namespace Fumilume.Services;

/// <summary>VS Code の <c>editor.fontFamily</c> と同じカンマ区切りの指定を解釈する。</summary>
public static class EditorFontFamily
{
    private static readonly string[] FallbackFamilies = ["Cascadia Mono", "Consolas", "monospace"];

    /// <summary>
    /// 単一引用符・二重引用符で囲んだ名前を受け取り、Avalonia のフォールバック一覧へ変換する。
    /// 入力途中の引用符や空欄があっても例外にせず、最後は等幅フォントへ落とす。
    /// </summary>
    public static string ToAvalonia(string? value)
    {
        var families = Parse(value);
        for (var index = 0; index < families.Count; index++)
        {
            families[index] = AppFontFamilies.ResolveEditorFont(families[index]);
        }

        // generic family まで明示されている場合は、VS Code 側で指定した順序をそのまま尊重する。
        if (families.Contains("monospace", StringComparer.OrdinalIgnoreCase))
        {
            return string.Join(", ", families);
        }

        foreach (var fallback in FallbackFamilies)
        {
            if (!families.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            {
                families.Add(fallback);
            }
        }

        return string.Join(", ", families);
    }

    internal static List<string> Parse(string? value)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var current = new StringBuilder();
        char? quote = null;
        foreach (var character in value)
        {
            if (quote is { } activeQuote)
            {
                if (character == activeQuote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == ',')
            {
                AddCurrent(result, current);
            }
            else
            {
                current.Append(character);
            }
        }

        AddCurrent(result, current);
        return result;
    }

    private static void AddCurrent(List<string> result, StringBuilder current)
    {
        var family = current.ToString().Trim();
        current.Clear();
        if (family.Length > 0 && !result.Contains(family, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(family);
        }
    }
}
