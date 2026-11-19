namespace Fumilume.ViewModels;

/// <summary>
/// コマンドパレットの 1 行。
///
/// パレットを「すべての機能へ行ける唯一の入口」にするため、文書へ効くコマンド
/// （<see cref="Services.EditorCommandCatalog"/> の 50 件）と、ファイルを開く・保存するといった
/// ワークスペース操作を同じ並びへ載せる。実行の中身は生成時に閉じ込め、パレット側は
/// 「区分・名前・キー」だけを見て並べる。
/// </summary>
public sealed class CommandPaletteEntry(string category, string title, string? gesture, Func<Task> run)
{
    public string Category { get; } = category;

    public string Title { get; } = title;

    /// <summary>割り当てられているキーの表示。無いときは <see langword="null"/>。</summary>
    public string? Gesture { get; } = ToDisplayGesture(gesture);

    private static readonly (string Name, string Symbol)[] OemKeyNames =
    [
        ("OemOpenBrackets", "["),
        ("OemCloseBrackets", "]"),
        ("OemComma", ","),
        ("OemPeriod", "."),
        ("OemMinus", "-"),
        ("OemSemicolon", ";"),
        ("OemQuestion", "/"),
        ("OemTilde", "`"),
        ("OemPipe", "\\"),
        ("OemQuotes", "'"),
    ];

    /// <summary>
    /// キー定義の内部名（<c>OemCloseBrackets</c> など）を、画面に出す記号へ直す。
    /// 表示だけを整え、割り当て自体（KeyBinding の Gesture）は変えない。
    /// </summary>
    internal static string? ToDisplayGesture(string? gesture)
    {
        if (string.IsNullOrEmpty(gesture))
        {
            return gesture;
        }

        var display = gesture;
        foreach (var (name, symbol) in OemKeyNames)
        {
            display = display.Replace(name, symbol, StringComparison.Ordinal);
        }

        return display;
    }

    public Task RunAsync() => run();
}
