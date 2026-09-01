using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fumilume.Services;

namespace Fumilume.ViewModels;

/// <summary>
/// sakura エディタの <c>HandleCommand</c> に当たる分岐。
///
/// コマンド 1 つにつき <c>[RelayCommand]</c> を 1 つ足していくと、ツールバー・メニュー・
/// キー割り当て・コマンドパレットの 4 箇所へ同じ配線が増える。機能番号
/// （<see cref="EditorCommandId"/>）を引数に取る 1 コマンドへ寄せて、
/// UI 側は <see cref="EditorCommandCatalog"/> を読むだけで並べられるようにしている。
/// </summary>
public sealed partial class MainWindowViewModel
{
    private IReadOnlyList<EditorMenuNode>? _editorMenu;

    /// <summary>ツールバーのフライアウトが並べる 2 段メニュー（区分 → 機能）。</summary>
    public IReadOnlyList<EditorMenuNode> EditorMenu
        => _editorMenu ??= EditorMenuNode.BuildMenu(RunEditorCommandCommand);

    // ===== コマンドパレット =====

    /// <summary>
    /// コマンドパレットの開閉。sakura のメニューバーは Fumilume には置けない
    /// （タイトルバーを掴んで動かす作りと両立しない）ので、機能数が増えても入り口が
    /// 変わらない絞り込み式の一覧を用意している。
    /// </summary>
    [ObservableProperty]
    private bool _isCommandPaletteOpen;

    [ObservableProperty]
    private string _commandPaletteQuery = string.Empty;

    [ObservableProperty]
    private CommandPaletteEntry? _selectedPaletteCommand;

    /// <summary>絞り込み後の候補。</summary>
    public ObservableCollection<CommandPaletteEntry> CommandPaletteResults { get; } = [];

    /// <summary>ワークスペース操作の区分名。文書コマンドの区分と並べても意味が混ざらない粒度にする。</summary>
    private const string FileCategory = "ファイル";
    private const string ViewCategory = "表示";
    private const string SearchCategory = "検索";
    private const string MacroCategory = "マクロ";

    [RelayCommand]
    private void OpenCommandPalette()
    {
        CommandPaletteQuery = string.Empty;
        RefreshCommandPaletteResults();
        IsCommandPaletteOpen = true;
    }

    [RelayCommand]
    private void CloseCommandPalette() => IsCommandPaletteOpen = false;

    [RelayCommand]
    private Task RunSelectedPaletteCommandAsync()
    {
        if (SelectedPaletteCommand is not { } entry)
        {
            return Task.CompletedTask;
        }

        IsCommandPaletteOpen = false;
        return entry.RunAsync();
    }

    partial void OnCommandPaletteQueryChanged(string value) => RefreshCommandPaletteResults();

    private void RefreshCommandPaletteResults()
    {
        var query = CommandPaletteQuery.Trim();
        CommandPaletteResults.Clear();
        foreach (var entry in EnumeratePaletteEntries())
        {
            if (query.Length == 0 || Matches(entry, query))
            {
                CommandPaletteResults.Add(entry);
            }
        }

        SelectedPaletteCommand = CommandPaletteResults.FirstOrDefault();
    }

    /// <summary>
    /// パレットへ並べる候補。今の状態で実行できるものだけを出す
    /// （設定タブを見ているときに「大文字へ変換」を出しても押せないため）。
    /// </summary>
    private IEnumerable<CommandPaletteEntry> EnumeratePaletteEntries()
    {
        yield return Entry(FileCategory, "新しい文書", "Ctrl+N", NewDocument);
        yield return Entry(FileCategory, "ファイルを開く", "Ctrl+O", () => OpenAsync());

        if (IsDocumentSelected)
        {
            yield return Entry(FileCategory, "保存", "Ctrl+S", () => SaveAsync());
            yield return Entry(FileCategory, "名前を付けて保存", "Ctrl+Shift+S", () => SaveAsAsync());
        }

        if (Documents.Any(document => document.IsModified))
        {
            yield return Entry(FileCategory, "すべて保存", "Ctrl+Alt+S", () => SaveAllAsync());
        }

        if (CanReload())
        {
            yield return Entry(FileCategory, "ディスクから開き直す", "Ctrl+Shift+R", () => ReloadAsync());
        }

        if (SelectedTab is { } tab)
        {
            yield return Entry(FileCategory, "このタブを閉じる", null, () => CloseTabCoreAsync(tab));
        }

        yield return Entry(SearchCategory, "フォルダから探す", "Ctrl+Shift+F", () => GrepAsync());

        if (CanToggleMarkdownPreview())
        {
            yield return Entry(ViewCategory, "Markdown プレビューを切り替え", "Ctrl+Shift+M", ToggleMarkdownPreview);
        }

        yield return Entry(ViewCategory, "設定を開く", "Ctrl+,", () => EnsureSettingsTab(select: true));
        yield return Entry(ViewCategory, "更新を確認", null, () => _dialogs.CheckForUpdatesAsync(manually: true));

        yield return Entry(MacroCategory, MacroRecordingTitle, "Shift+F1", ToggleMacroRecording);
        if (HasRecordedMacro)
        {
            yield return Entry(MacroCategory, "マクロを実行", "Shift+F5", () => RunMacroAsync());
            yield return Entry(MacroCategory, "回数を指定してマクロを実行", null, () => RunMacroRepeatedlyAsync());
            yield return Entry(MacroCategory, "マクロに名前を付けて保存", null, () => SaveMacroAsAsync());
        }

        foreach (var macro in SavedMacros)
        {
            var saved = macro;
            yield return Entry(MacroCategory, $"{saved.Name}（{saved.Summary}）", null, () => RunSavedMacroAsync(saved));
        }

        if (!IsDocumentSelected)
        {
            yield break;
        }

        foreach (var definition in EditorCommandCatalog.All)
        {
            yield return new CommandPaletteEntry(
                definition.Category,
                definition.Title,
                definition.Gesture,
                () => RunEditorCommandAsync(definition.Id));
        }
    }

    private static CommandPaletteEntry Entry(string category, string title, string? gesture, Func<Task> run)
        => new(category, title, gesture, run);

    private static CommandPaletteEntry Entry(string category, string title, string? gesture, Action run)
        => new(category, title, gesture, () =>
        {
            run();
            return Task.CompletedTask;
        });

    private static bool Matches(CommandPaletteEntry entry, string query)
        => entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Category.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 非同期なのは「パターンに一致する行をマーク」だけが入力ダイアログを開くため。
    /// 他の分岐は最初の <c>await</c> まで同期で走り切るので、キー割り当てからの体感は変わらない。
    /// </summary>
    /// <summary>
    /// マクロに記録しないコマンド。どちらも入力ダイアログを開くので、記録できても再生時に止まる。
    /// 再生側で読み飛ばすのではなく記録の時点で弾くのは、一覧に「動かない手」を残さないため。
    /// </summary>
    private static readonly EditorCommandId[] NotRecordable =
        [EditorCommandId.GoToLine, EditorCommandId.BookmarkPattern];

    [RelayCommand(CanExecute = nameof(IsDocumentSelected))]
    private async Task RunEditorCommandAsync(EditorCommandId commandId)
    {
        if (IsCapturingMacro)
        {
            if (NotRecordable.Contains(commandId))
            {
                StatusMessage = "この操作はマクロに記録できません（入力を尋ねるため）";
            }
            else
            {
                RecordMacroStep(new MacroStep { Kind = MacroStepKind.Command, Command = commandId });
            }
        }

        if (SelectedDocument is not { } document)
        {
            return;
        }

        var title = EditorCommandCatalog.TitleOf(commandId);
        var tabWidth = Options.IndentationSize;

        switch (commandId)
        {
            // ===== 変換系（sakura と同じく選択範囲が要る） =====
            case EditorCommandId.ToUpper:
                Report(title, document.TransformSelection(TextTransforms.ToUpper));
                break;
            case EditorCommandId.ToLower:
                Report(title, document.TransformSelection(TextTransforms.ToLower));
                break;
            case EditorCommandId.ToHalfWidth:
                Report(title, document.TransformSelection(TextTransforms.ToHalfWidth));
                break;
            case EditorCommandId.ToFullWidth:
                Report(title, document.TransformSelection(TextTransforms.ToFullWidth));
                break;
            case EditorCommandId.ToHalfWidthAlphanumeric:
                Report(title, document.TransformSelection(TextTransforms.ToHalfWidthAlphanumeric));
                break;
            case EditorCommandId.ToFullWidthAlphanumeric:
                Report(title, document.TransformSelection(TextTransforms.ToFullWidthAlphanumeric));
                break;
            case EditorCommandId.ToHalfWidthKatakana:
                Report(title, document.TransformSelection(TextTransforms.ToHalfWidthKatakana));
                break;
            case EditorCommandId.ToFullWidthKatakana:
                Report(title, document.TransformSelection(TextTransforms.ToFullWidthKatakana));
                break;
            case EditorCommandId.HalfWidthKatakanaToHiragana:
                Report(title, document.TransformSelection(TextTransforms.HalfWidthKatakanaToHiragana));
                break;
            case EditorCommandId.ToFullWidthKatakanaAll:
                Report(title, document.TransformSelection(TextTransforms.ToFullWidthKatakanaAll));
                break;
            case EditorCommandId.ToFullWidthHiraganaAll:
                Report(title, document.TransformSelection(TextTransforms.ToFullWidthHiraganaAll));
                break;
            case EditorCommandId.TabToSpace:
                Report(title, document.TransformSelection(text => TextTransforms.TabToSpace(text, tabWidth)));
                break;
            case EditorCommandId.SpaceToTab:
                Report(title, document.TransformSelection(text => TextTransforms.SpaceToTab(text, tabWidth)));
                break;
            case EditorCommandId.Base64Encode:
                Report(title, document.TransformSelection(TextTransforms.Base64Encode));
                break;
            case EditorCommandId.Base64Decode:
                Report(title, document.TryTransformSelection(TextTransforms.Base64Decode),
                    "Base64 として読めない文字列です");
                break;
            case EditorCommandId.UrlEncode:
                Report(title, document.TransformSelection(TextTransforms.UrlEncode));
                break;
            case EditorCommandId.UrlDecode:
                Report(title, document.TryTransformSelection(TextTransforms.UrlDecode),
                    "URL エンコードとして読めない文字列です");
                break;

            // ===== 編集系（選択が無ければカーソル行が対象） =====
            case EditorCommandId.FormatDocument:
                FormatDocument(document);
                break;
            case EditorCommandId.TrimLineStarts:
                document.TransformSelectedLines(TextTransforms.TrimLineStarts);
                StatusMessage = $"{title}しました";
                break;
            case EditorCommandId.TrimLineEnds:
                document.TransformSelectedLines(TextTransforms.TrimLineEnds);
                StatusMessage = $"{title}しました";
                break;
            case EditorCommandId.SortLinesAscending:
                document.TransformSelectedLines(text => TextTransforms.SortLines(text, descending: false));
                StatusMessage = "選択行を昇順に並べ替えました";
                break;
            case EditorCommandId.SortLinesDescending:
                document.TransformSelectedLines(text => TextTransforms.SortLines(text, descending: true));
                StatusMessage = "選択行を降順に並べ替えました";
                break;
            case EditorCommandId.MergeLines:
                document.TransformSelectedLines(TextTransforms.MergeLines);
                StatusMessage = "重複行をまとめました";
                break;
            case EditorCommandId.DuplicateLine:
                document.DuplicateLines();
                StatusMessage = "行を二重化しました";
                break;
            case EditorCommandId.DeleteLine:
                document.DeleteLines();
                StatusMessage = "行を削除しました";
                break;
            case EditorCommandId.SelectLine:
                document.SelectCurrentLine();
                StatusMessage = "カーソル行を選択しました";
                break;
            case EditorCommandId.DeleteToLineStart:
                document.DeleteToLineStart();
                StatusMessage = "行頭まで削除しました";
                break;
            case EditorCommandId.DeleteToLineEnd:
                document.DeleteToLineEnd();
                StatusMessage = "行末まで削除しました";
                break;
            case EditorCommandId.IndentTab:
                document.IndentLines("\t");
                StatusMessage = "TAB で字下げしました";
                break;
            case EditorCommandId.UnindentTab:
                document.UnindentLines("\t");
                StatusMessage = "TAB の字下げを 1 段はがしました";
                break;
            case EditorCommandId.IndentSpace:
                document.IndentLines(new string(' ', tabWidth));
                StatusMessage = "空白で字下げしました";
                break;
            case EditorCommandId.UnindentSpace:
                document.UnindentLines(new string(' ', tabWidth));
                StatusMessage = "空白の字下げを 1 段はがしました";
                break;

            // ===== 挿入系 =====
            case EditorCommandId.InsertDate:
                document.InsertText(DateTime.Now.ToString("d"));
                StatusMessage = "日付を挿入しました";
                break;
            case EditorCommandId.InsertTime:
                document.InsertText(DateTime.Now.ToString("T"));
                StatusMessage = "時刻を挿入しました";
                break;
            case EditorCommandId.InsertFileName:
                InsertPathPart(document, fullPath: false);
                break;
            case EditorCommandId.InsertFilePath:
                InsertPathPart(document, fullPath: true);
                break;

            // ===== 検索・移動系 =====
            case EditorCommandId.GoToLine:
                await GoToLineAsync();
                break;
            case EditorCommandId.BookmarkToggle:
                StatusMessage = document.ToggleBookmark()
                    ? $"行 {document.CurrentLine:N0} にブックマークを付けました"
                    : $"行 {document.CurrentLine:N0} のブックマークを外しました";
                break;
            case EditorCommandId.BookmarkNext:
                StatusMessage = document.GoToNextBookmark()
                    ? $"行 {document.CurrentLine:N0} へ移動しました"
                    : "ブックマークがありません";
                break;
            case EditorCommandId.BookmarkPrevious:
                StatusMessage = document.GoToPreviousBookmark()
                    ? $"行 {document.CurrentLine:N0} へ移動しました"
                    : "ブックマークがありません";
                break;
            case EditorCommandId.BookmarkClear:
                document.Bookmarks.Clear();
                StatusMessage = "ブックマークをすべて外しました";
                break;
            case EditorCommandId.BookmarkPattern:
                await MarkBookmarksByPatternAsync(document);
                break;
            case EditorCommandId.GoToMatchingBracket:
                StatusMessage = document.GoToMatchingBracket()
                    ? "対応する括弧へ移動しました"
                    : "カーソル位置に対応する括弧がありません";
                break;
        }
    }

    /// <summary>sakura の「パターンに一致する行をマーク」。空欄なら何もしない。</summary>
    private async Task MarkBookmarksByPatternAsync(DocumentViewModel document)
    {
        var pattern = await _dialogs.PromptTextAsync(
            "パターンに一致する行をマーク",
            "行に含まれる文字列を入力してください。",
            string.Empty);
        if (string.IsNullOrEmpty(pattern))
        {
            return;
        }

        var marked = document.Bookmarks.MarkMatching(
            line => line.Contains(pattern, StringComparison.Ordinal));
        StatusMessage = marked > 0
            ? $"{marked:N0} 行にブックマークを付けました"
            : "一致する行がありませんでした";
    }

    private void InsertPathPart(DocumentViewModel document, bool fullPath)
    {
        if (document.FilePath is not { } path)
        {
            StatusMessage = "保存していない文書にはパスがありません";
            return;
        }

        document.InsertText(fullPath ? path : Path.GetFileName(path));
        StatusMessage = fullPath ? "フルパスを挿入しました" : "ファイル名を挿入しました";
    }

    private void FormatDocument(DocumentViewModel document)
    {
        var result = DocumentFormattingService.Format(
            document.FilePath,
            document.Text,
            document.NewLine,
            document.Encoding,
            Options.IndentationSize,
            Options.ConvertTabsToSpaces);

        switch (result.Outcome)
        {
            case DocumentFormatOutcome.Success:
                StatusMessage = document.ReplaceWholeDocument(result.Text!)
                    ? "文書全体を書式整形しました"
                    : "文書は既に整形されています";
                break;
            case DocumentFormatOutcome.Invalid:
                StatusMessage = result.Message ?? "構文を解釈できないため書式整形できませんでした";
                break;
            default:
                StatusMessage = result.Message ?? "このファイル形式の書式整形には対応していません";
                break;
        }
    }

    /// <summary>変換系の結果を伝える。選択が無い／変換できないときは黙って失敗させない。</summary>
    private void Report(string title, bool applied, string failureMessage = "変換する範囲を選択してください")
        => StatusMessage = applied ? $"{title} を適用しました" : failureMessage;
}
