namespace Fumilume.ViewModels;

/// <summary>
/// 右クリックメニューの 1 行（View 非依存）。ViewModel が組み立て、View が MenuFlyout へ変換する。
/// テストは <see cref="Header"/>・<see cref="IsEnabled"/>・並びを検査する。
/// </summary>
/// <param name="Header">表示名。</param>
/// <param name="Run">選んだときの処理。サブメニューの親や無効な行は null でよい。</param>
/// <param name="Gesture">Windows 表記のショートカット（例 "Ctrl+W"）。Mac の ⌘ 表記への変換は View が行う。無いときは null。</param>
/// <param name="IsEnabled">選べるか。</param>
/// <param name="Children">サブメニューの項目。無いときは null。</param>
/// <param name="ToolTip">ツールチップ（フルパスなど）。無いときは null。</param>
public sealed record ContextMenuEntry(
    string Header,
    Func<Task>? Run = null,
    string? Gesture = null,
    bool IsEnabled = true,
    IReadOnlyList<ContextMenuEntry>? Children = null,
    string? ToolTip = null)
{
    /// <summary>区切り線か（そのとき <see cref="Header"/> は空文字列で、他の値は使わない）。</summary>
    public bool IsSeparator { get; init; }

    /// <summary>区切り線。</summary>
    public static ContextMenuEntry Separator { get; } = new(string.Empty, IsEnabled: false) { IsSeparator = true };

    /// <summary>サブメニューを持つか。</summary>
    public bool HasChildren => Children is { Count: > 0 };
}
