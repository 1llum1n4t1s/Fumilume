using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

/// <summary>
/// 左サイドのパネル。タブ一覧・アウトライン・ブックマークを 1 か所で切り替えるので、
/// 「開いていない面は引き直さない」「選んだ行へ飛ぶ」の 2 つが崩れないことを確かめる。
/// </summary>
public sealed class SidePanelTests
{
    private const string MarkdownPath = @"C:\sample\notes.md";

    [Fact]
    public void TheTabListIsShownFirst()
    {
        var viewModel = CreateViewModel();

        Assert.Equal(SidePanelKind.Tabs, viewModel.SidePanel);
        Assert.True(viewModel.IsTabsPanel);
        Assert.False(viewModel.IsOutlinePanel);
        Assert.False(viewModel.IsBookmarksPanel);
    }

    [Fact]
    public void TheChosenPanelIsRemembered()
    {
        using var storage = new TemporaryStorage();
        var settings = new AppSettings();
        var viewModel = CreateViewModel(settings);

        viewModel.SelectSidePanelCommand.Execute(SidePanelKind.Outline);

        Assert.Equal(nameof(SidePanelKind.Outline), settings.SidePanel);
        Assert.Equal(SidePanelKind.Outline, CreateViewModel(settings).SidePanel);
    }

    [Fact]
    public void AnUnreadableStoredPanelFallsBackToTheTabList()
    {
        var viewModel = CreateViewModel(new AppSettings { SidePanel = "存在しない面" });

        Assert.Equal(SidePanelKind.Tabs, viewModel.SidePanel);
    }

    [Fact]
    public void TheOutlineFillsWhileItsPanelIsOpen()
    {
        using var storage = new TemporaryStorage();
        var viewModel = CreateViewModel();
        LoadMarkdown(viewModel, "# 表題\n\n## 節\n");

        viewModel.SelectSidePanelCommand.Execute(SidePanelKind.Outline);

        Assert.Equal(["表題", "節"], viewModel.Outline.Select(item => item.Title));
        Assert.True(viewModel.HasOutline);
    }

    [Fact]
    public void TheOutlineIsNotParsedWhileItsPanelIsClosed()
    {
        var viewModel = CreateViewModel();

        LoadMarkdown(viewModel, "# 表題\n");

        Assert.Empty(viewModel.Outline);
    }

    [Fact]
    public void TheOutlineFollowsTheTextWhileItsPanelIsOpen()
    {
        using var storage = new TemporaryStorage();
        var viewModel = CreateViewModel();
        LoadMarkdown(viewModel, "# 表題\n");
        viewModel.SelectSidePanelCommand.Execute(SidePanelKind.Outline);

        viewModel.Documents.Single().Text = "# 表題\n\n## 足した節\n";

        Assert.Equal(["表題", "足した節"], viewModel.Outline.Select(item => item.Title));
    }

    [Fact]
    public void ChoosingAHeadingMovesTheCaretToItsLine()
    {
        using var storage = new TemporaryStorage();
        var viewModel = CreateViewModel();
        var document = LoadMarkdown(viewModel, "# 表題\n本文\n## 節\n");
        viewModel.SelectSidePanelCommand.Execute(SidePanelKind.Outline);

        viewModel.SelectedOutlineItem = viewModel.Outline.Single(item => item.Title == "節");

        Assert.Equal(3, document.CurrentLine);
        Assert.Equal(0, document.SelectionLength);
    }

    [Fact]
    public void TheOutlineSaysWhyItIsEmpty()
    {
        using var storage = new TemporaryStorage();
        var viewModel = CreateViewModel();
        viewModel.SelectSidePanelCommand.Execute(SidePanelKind.Outline);

        // 無題の文書は拡張子が無いので解析できない。
        Assert.Equal("この形式のアウトラインには対応していません", viewModel.OutlineEmptyMessage);

        LoadMarkdown(viewModel, "見出しの無い本文\n");
        Assert.Equal("見出しが見つかりません", viewModel.OutlineEmptyMessage);

        viewModel.OpenSettingsCommand.Execute(null);
        Assert.Equal("文書のタブを選ぶと見出しが出ます", viewModel.OutlineEmptyMessage);
    }

    [Fact]
    public void BookmarksMirrorTheMarkedLines()
    {
        using var storage = new TemporaryStorage();
        var viewModel = CreateViewModel();
        var document = LoadMarkdown(viewModel, "1 行目\n2 行目\n3 行目\n");
        viewModel.SelectSidePanelCommand.Execute(SidePanelKind.Bookmarks);

        Assert.False(viewModel.HasBookmarks);
        Assert.Equal("Ctrl+F2 で今の行に印を付けます", viewModel.BookmarkEmptyMessage);

        document.CaretIndex = document.EditorDocument.GetLineByNumber(2).Offset;
        document.ToggleBookmark();

        var entry = Assert.Single(viewModel.Bookmarks);
        Assert.Equal(2, entry.LineNumber);
        Assert.Equal("2 行目", entry.Display);

        document.ToggleBookmark();
        Assert.Empty(viewModel.Bookmarks);
    }

    [Fact]
    public void ChoosingABookmarkMovesTheCaretToItsLine()
    {
        using var storage = new TemporaryStorage();
        var viewModel = CreateViewModel();
        var document = LoadMarkdown(viewModel, "1 行目\n2 行目\n3 行目\n");
        document.CaretIndex = document.EditorDocument.GetLineByNumber(3).Offset;
        document.ToggleBookmark();
        viewModel.SelectSidePanelCommand.Execute(SidePanelKind.Bookmarks);
        document.CaretIndex = 0;

        viewModel.SelectedBookmark = viewModel.Bookmarks.Single();

        Assert.Equal(3, document.CurrentLine);
    }

    [Fact]
    public void EmptyMarkedLinesStillShowSomething()
    {
        using var storage = new TemporaryStorage();
        var viewModel = CreateViewModel();
        var document = LoadMarkdown(viewModel, "1 行目\n\n3 行目\n");
        viewModel.SelectSidePanelCommand.Execute(SidePanelKind.Bookmarks);

        document.CaretIndex = document.EditorDocument.GetLineByNumber(2).Offset;
        document.ToggleBookmark();

        Assert.Equal("（空行）", Assert.Single(viewModel.Bookmarks).Display);
    }

    [Fact]
    public void ThePanelsFollowTheSelectedTab()
    {
        using var storage = new TemporaryStorage();
        var viewModel = CreateViewModel();
        var first = LoadMarkdown(viewModel, "# 1 枚目\n");
        viewModel.SelectSidePanelCommand.Execute(SidePanelKind.Outline);
        Assert.Equal(["1 枚目"], viewModel.Outline.Select(item => item.Title));

        viewModel.NewDocumentCommand.Execute(null);
        var second = viewModel.Documents.Last();
        second.Load(@"C:\sample\other.md", new TextDocumentContent("# 2 枚目\n", DocumentEncoding.Utf8, "\r\n"));

        Assert.Equal(["2 枚目"], viewModel.Outline.Select(item => item.Title));

        viewModel.SelectedTab = first;
        Assert.Equal(["1 枚目"], viewModel.Outline.Select(item => item.Title));
    }

    /// <summary>印を付けた文書から離れても、戻れば同じ一覧が出る（購読先の張り替えが効く）。</summary>
    [Fact]
    public void MarksStayWithTheirOwnDocument()
    {
        using var storage = new TemporaryStorage();
        var viewModel = CreateViewModel();
        var first = LoadMarkdown(viewModel, "1 行目\n2 行目\n");
        viewModel.SelectSidePanelCommand.Execute(SidePanelKind.Bookmarks);
        first.CaretIndex = first.EditorDocument.GetLineByNumber(2).Offset;
        first.ToggleBookmark();

        viewModel.NewDocumentCommand.Execute(null);
        Assert.Empty(viewModel.Bookmarks);

        viewModel.SelectedTab = first;
        Assert.Equal(2, Assert.Single(viewModel.Bookmarks).LineNumber);
    }

    private static DocumentViewModel LoadMarkdown(MainWindowViewModel viewModel, string text)
    {
        var document = viewModel.Documents.Single();
        document.Load(MarkdownPath, new TextDocumentContent(text, DocumentEncoding.Utf8, "\r\n"));
        return document;
    }

    private static MainWindowViewModel CreateViewModel(AppSettings? settings = null)
        => new(new StubFileService(), new StubDialogService(), settings);

    private sealed class StubFileService : IDocumentFileService
    {
        public Task<TextDocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(new TextDocumentContent(string.Empty, DocumentEncoding.Utf8, Environment.NewLine));

        public Task WriteAsync(
            string path,
            TextDocumentContent content,
            bool createBackup = false,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubDialogService : IEditorDialogService
    {
        public Task<IReadOnlyList<string>> PickOpenPathsAsync()
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);

        public Task<UnsavedDocumentDecision> ConfirmUnsavedAsync(string documentName)
            => Task.FromResult(UnsavedDocumentDecision.Discard);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<int?> PickLineNumberAsync(int currentLine, int maximumLine) => Task.FromResult<int?>(null);

        public Task<string?> PromptTextAsync(string title, string message, string initialText)
            => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);

        public Task<GrepQuery?> PickGrepQueryAsync(GrepQuery initial) => Task.FromResult<GrepQuery?>(null);

        public Task CheckForUpdatesAsync(bool manually) => Task.CompletedTask;
    }
}
