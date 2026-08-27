namespace Fumilume.Services;

/// <summary>
/// エディタ機能の識別子。sakura エディタの <c>Funccode</c>（F_TOUPPER などの機能番号）に当たる。
///
/// コマンドを 1 つ増やすたびに <c>[RelayCommand]</c> を書き足すと、
/// ツールバー・メニュー・キー割り当て・コマンドパレットの 4 箇所へ同じ配線が増えていく。
/// sakura と同じく「機能番号 1 つ＋分岐 1 箇所」に寄せて、UI 側は
/// <see cref="EditorCommandCatalog"/> を読むだけで済むようにしている。
/// </summary>
public enum EditorCommandId
{
    // ===== 変換系 =====
    ToLower,
    ToUpper,
    ToHalfWidth,
    ToFullWidth,
    ToHalfWidthKatakana,
    ToFullWidthKatakana,
    ToFullWidthKatakanaAll,
    ToFullWidthHiraganaAll,
    HalfWidthKatakanaToHiragana,
    ToFullWidthAlphanumeric,
    ToHalfWidthAlphanumeric,
    TabToSpace,
    SpaceToTab,
    Base64Encode,
    Base64Decode,
    UrlEncode,
    UrlDecode,

    // ===== 編集系（行） =====
    TrimLineStarts,
    TrimLineEnds,
    SortLinesAscending,
    SortLinesDescending,
    MergeLines,
    DuplicateLine,
    DeleteLine,
    IndentTab,
    UnindentTab,
    IndentSpace,
    UnindentSpace,
    DeleteToLineStart,
    DeleteToLineEnd,

    // ===== 挿入系 =====
    InsertDate,
    InsertTime,
    InsertFileName,
    InsertFilePath,

    // ===== 検索・移動系 =====
    GoToLine,
    GoToMatchingBracket,
    BookmarkToggle,
    BookmarkNext,
    BookmarkPrevious,
    BookmarkClear,
    BookmarkPattern,
}

/// <summary>コマンド 1 件の定義。メニューとコマンドパレットはこれを読んで並べる。</summary>
/// <param name="Id">機能番号。</param>
/// <param name="Category">メニューの区分。コマンドパレットの絞り込みにも使う。</param>
/// <param name="Title">画面に出す名前。</param>
/// <param name="Gesture">割り当てたキー。無いときは <see langword="null"/>。</param>
public sealed record EditorCommandDefinition(
    EditorCommandId Id,
    string Category,
    string Title,
    string? Gesture = null);

/// <summary>コマンドの一覧。並び順がそのままメニューの並びになる。</summary>
public static class EditorCommandCatalog
{
    public const string EditCategory = "編集";
    public const string ConvertCategory = "変換";
    public const string InsertCategory = "挿入";

    /// <summary>行や括弧、ブックマークへ飛ぶもの。sakura では検索系にまとまっている。</summary>
    public const string JumpCategory = "検索・移動";

    /// <summary>
    /// ツールバーの区分ごとのボタン名（<c>x:Name</c>）とアイコン。
    /// アイコンは Segoe Fluent Icons のコードポイントで、フォントに実在することを確認済み。
    /// </summary>
    public static IReadOnlyList<EditorCommandCategoryIcon> CategoryIcons { get; } =
    [
        new(EditCategory, "EditMenuButton", ""),
        new(ConvertCategory, "ConvertMenuButton", ""),
        new(InsertCategory, "InsertMenuButton", ""),
        new(JumpCategory, "JumpMenuButton", ""),
    ];

    public static IReadOnlyList<EditorCommandDefinition> All { get; } =
    [
        new(EditorCommandId.ToUpper, ConvertCategory, "大文字", "Ctrl+F7"),
        new(EditorCommandId.ToLower, ConvertCategory, "小文字", "Ctrl+F6"),
        new(EditorCommandId.ToHalfWidth, ConvertCategory, "全角→半角"),
        new(EditorCommandId.ToFullWidth, ConvertCategory, "半角→全角"),
        new(EditorCommandId.ToHalfWidthAlphanumeric, ConvertCategory, "全角英数→半角英数"),
        new(EditorCommandId.ToFullWidthAlphanumeric, ConvertCategory, "半角英数→全角英数"),
        new(EditorCommandId.ToHalfWidthKatakana, ConvertCategory, "全角カタカナ→半角カタカナ"),
        new(EditorCommandId.ToFullWidthKatakana, ConvertCategory, "半角カタカナ→全角カタカナ"),
        new(EditorCommandId.HalfWidthKatakanaToHiragana, ConvertCategory, "半角カタカナ→全角ひらがな"),
        new(EditorCommandId.ToFullWidthKatakanaAll, ConvertCategory, "半角＋全ひら→全角カタカナ"),
        new(EditorCommandId.ToFullWidthHiraganaAll, ConvertCategory, "半角＋全カタ→全角ひらがな"),
        new(EditorCommandId.TabToSpace, ConvertCategory, "TAB→空白", "Ctrl+Alt+F5"),
        new(EditorCommandId.SpaceToTab, ConvertCategory, "空白→TAB", "Ctrl+Shift+Alt+F5"),
        new(EditorCommandId.Base64Encode, ConvertCategory, "Base64 エンコード"),
        new(EditorCommandId.Base64Decode, ConvertCategory, "Base64 デコード", "Alt+F6"),
        new(EditorCommandId.UrlEncode, ConvertCategory, "URL エンコード"),
        new(EditorCommandId.UrlDecode, ConvertCategory, "URL デコード"),

        new(EditorCommandId.DuplicateLine, EditCategory, "行の二重化", "Ctrl+I"),
        new(EditorCommandId.DeleteLine, EditCategory, "行削除", "Ctrl+Shift+E"),
        new(EditorCommandId.DeleteToLineStart, EditCategory, "行頭まで削除"),
        new(EditorCommandId.DeleteToLineEnd, EditCategory, "行末まで削除"),
        // Tab / Shift+Tab の字下げは AvaloniaEdit が選択範囲へ既定で効かせている。
        new(EditorCommandId.IndentTab, EditCategory, "TAB インデント", "Tab"),
        new(EditorCommandId.UnindentTab, EditCategory, "逆 TAB インデント", "Shift+Tab"),
        new(EditorCommandId.IndentSpace, EditCategory, "SPACE インデント"),
        new(EditorCommandId.UnindentSpace, EditCategory, "逆 SPACE インデント"),
        new(EditorCommandId.TrimLineStarts, EditCategory, "行頭の空白を削除", "Alt+L"),
        new(EditorCommandId.TrimLineEnds, EditCategory, "行末の空白を削除"),
        new(EditorCommandId.SortLinesAscending, EditCategory, "選択行を昇順ソート"),
        new(EditorCommandId.SortLinesDescending, EditCategory, "選択行を降順ソート"),
        new(EditorCommandId.MergeLines, EditCategory, "重複行をマージ"),

        new(EditorCommandId.InsertDate, InsertCategory, "日付を挿入"),
        new(EditorCommandId.InsertTime, InsertCategory, "時刻を挿入"),
        new(EditorCommandId.InsertFileName, InsertCategory, "ファイル名を挿入"),
        new(EditorCommandId.InsertFilePath, InsertCategory, "フルパスを挿入"),

        new(EditorCommandId.GoToLine, JumpCategory, "指定行へ移動", "Ctrl+G"),
        new(EditorCommandId.GoToMatchingBracket, JumpCategory, "対括弧の検索", "Ctrl+OemCloseBrackets"),
        new(EditorCommandId.BookmarkToggle, JumpCategory, "ブックマークの設定・解除", "Ctrl+F2"),
        new(EditorCommandId.BookmarkNext, JumpCategory, "次のブックマークへ", "F2"),
        new(EditorCommandId.BookmarkPrevious, JumpCategory, "前のブックマークへ", "Shift+F2"),
        new(EditorCommandId.BookmarkClear, JumpCategory, "ブックマークの全解除", "Ctrl+Shift+F2"),
        new(EditorCommandId.BookmarkPattern, JumpCategory, "パターンに一致する行をマーク"),
    ];

    /// <summary>メニュー用に区分ごとへまとめる。並び順は <see cref="All"/> の登場順を保つ。</summary>
    public static IReadOnlyList<EditorCommandGroup> Groups { get; } =
    [
        .. All.GroupBy(command => command.Category)
            .Select(group => new EditorCommandGroup(group.Key, [.. group])),
    ];

    private static readonly Dictionary<EditorCommandId, EditorCommandDefinition> ById =
        All.ToDictionary(command => command.Id);

    /// <summary>状態メッセージへ出す名前を引く。</summary>
    public static string TitleOf(EditorCommandId id)
        => ById.TryGetValue(id, out var command) ? command.Title : id.ToString();
}

/// <summary>メニュー 1 段ぶん。</summary>
public sealed record EditorCommandGroup(string Category, IReadOnlyList<EditorCommandDefinition> Commands);

/// <summary>ツールバーに並べる区分アイコン 1 個ぶん。</summary>
/// <param name="Category">対応する区分名。</param>
/// <param name="ButtonName">XAML 側のボタンの <c>x:Name</c>。</param>
/// <param name="Glyph">Segoe Fluent Icons のグリフ。</param>
public sealed record EditorCommandCategoryIcon(string Category, string ButtonName, string Glyph);
