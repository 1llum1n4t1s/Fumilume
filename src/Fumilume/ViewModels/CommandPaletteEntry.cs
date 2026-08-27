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

    /// <summary>割り当てられているキー。無いときは <see langword="null"/>。</summary>
    public string? Gesture { get; } = gesture;

    public Task RunAsync() => run();
}
