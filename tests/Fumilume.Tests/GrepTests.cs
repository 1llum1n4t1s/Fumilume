using System.Text;
using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

/// <summary>
/// フォルダ横断検索（秀丸の grep 相当）と、結果からのタグジャンプ。
/// 実際のファイルを置いて検索させ、開いた文書のカーソル位置まで確かめる。
/// </summary>
public sealed class GrepTests
{
    [Fact]
    public async Task MatchesAreReportedWithTheirFileLineAndColumn()
    {
        using var storage = new TemporaryStorage();
        Write(storage, "one.txt", "一行目\n目的の言葉がある\n三行目");
        Write(storage, "two.txt", "無関係");

        var result = await Search(storage, "目的の言葉");

        var match = Assert.Single(result.Matches);
        Assert.Equal(Path.Combine(storage.Path, "one.txt"), match.FilePath);
        Assert.Equal(2, match.LineNumber);
        Assert.Equal(1, match.Column);
        Assert.Equal("目的の言葉がある", match.LineText);
        Assert.Equal(2, result.SearchedFiles);
    }

    [Fact]
    public async Task CrOnlyFilesReportTheSameLineNumberAsTheEditor()
    {
        using var storage = new TemporaryStorage();
        Write(storage, "classic-mac.txt", "一行目\r目的の言葉\r三行目");

        var match = Assert.Single((await Search(storage, "目的の言葉")).Matches);

        Assert.Equal(2, match.LineNumber);
        Assert.Equal("目的の言葉", match.LineText);
    }

    [Fact]
    public async Task SubfoldersAreSearchedOnlyWhenAsked()
    {
        using var storage = new TemporaryStorage();
        Directory.CreateDirectory(Path.Combine(storage.Path, "nested"));
        Write(storage, "top.txt", "みつかる");
        Write(storage, Path.Combine("nested", "deep.txt"), "みつかる");

        Assert.Equal(2, (await Search(storage, "みつかる")).Matches.Count);
        Assert.Single((await Search(storage, "みつかる", recursive: false)).Matches);
    }

    [Fact]
    public async Task TheFileMaskLimitsWhichFilesAreRead()
    {
        using var storage = new TemporaryStorage();
        Write(storage, "note.txt", "対象");
        Write(storage, "note.md", "対象");
        Write(storage, "note.cs", "対象");

        var result = await Search(storage, "対象", mask: "*.txt;*.md");

        Assert.Equal(2, result.Matches.Count);
        Assert.DoesNotContain(result.Matches, match => match.FilePath.EndsWith(".cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CaseSensitivityFollowsTheQuery()
    {
        using var storage = new TemporaryStorage();
        Write(storage, "case.txt", "Fumilume\nfumilume");

        Assert.Equal(2, (await Search(storage, "fumilume")).Matches.Count);
        Assert.Single((await Search(storage, "fumilume", matchCase: true)).Matches);
    }

    [Fact]
    public async Task RegularExpressionsFindTheirFirstPositionOnTheLine()
    {
        using var storage = new TemporaryStorage();
        Write(storage, "log.txt", "2026-08-27 起動\nメッセージ");

        var result = await Search(storage, @"\d{4}-\d{2}-\d{2}", useRegex: true);

        var match = Assert.Single(result.Matches);
        Assert.Equal(1, match.LineNumber);
        Assert.Equal(1, match.Column);
    }

    /// <summary>実行ファイルや画像まで開くと結果が汚れるので、中身を見て弾く。</summary>
    [Fact]
    public async Task BinaryFilesAreSkipped()
    {
        using var storage = new TemporaryStorage();
        File.WriteAllBytes(Path.Combine(storage.Path, "image.dat"), [0x50, 0x4B, 0x00, 0x01, 0x41]);
        Write(storage, "text.txt", "A");

        var result = await Search(storage, "A");

        Assert.Single(result.Matches);
        Assert.Equal(1, result.SearchedFiles);
        Assert.Equal(1, result.SkippedFiles);
    }

    /// <summary>UTF-16 は本文に NUL を含む。バイナリ扱いで飛ばさずに検索できること。</summary>
    [Fact]
    public async Task Utf16FilesAreStillSearched()
    {
        using var storage = new TemporaryStorage();
        File.WriteAllText(Path.Combine(storage.Path, "utf16.txt"), "検索できる", new UnicodeEncoding(false, true));

        var result = await Search(storage, "検索できる");

        Assert.Single(result.Matches);
    }

    [Fact]
    public async Task CancellingStopsTheSearch()
    {
        using var storage = new TemporaryStorage();
        Write(storage, "note.txt", "内容");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new GrepService(new DocumentFileService())
                .SearchAsync(Query(storage, "内容"), cancellation.Token));
    }

    [Fact]
    public async Task AMissingFolderYieldsNothingInsteadOfThrowing()
    {
        using var storage = new TemporaryStorage();

        var result = await new GrepService(new DocumentFileService()).SearchAsync(
            Query(storage, "何か") with { Folder = Path.Combine(storage.Path, "存在しない") },
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Matches);
    }

    [Theory]
    [InlineData("*.txt;*.md", 2)]
    [InlineData("*.txt, *.md", 2)]
    [InlineData("", 1)]
    public void MasksAreSplitOnCommonSeparators(string mask, int expected)
        => Assert.Equal(expected, GrepService.SplitMasks(mask).Count);

    // ===== ワークスペースとの接続 =====

    [Fact]
    public void SearchingOpensAResultTabAndJumpingOpensTheFileAtThatLine() => SingleThreadedAsync.Run(async () =>
    {
        using var storage = new TemporaryStorage();
        var path = Path.Combine(storage.Path, "target.txt");
        File.WriteAllText(path, "一行目\n二行目\n探している行\n四行目");

        var viewModel = new MainWindowViewModel(
            new DocumentFileService(),
            new StubDialogService { GrepQuery = Query(storage, "探している行") },
            new AppSettings());
        await viewModel.GrepCommand.ExecuteAsync(null);

        var tab = Assert.IsType<GrepResultTabViewModel>(viewModel.SelectedTab);
        var item = Assert.Single(tab.Matches);
        Assert.Equal("探している行", item.Preview);
        Assert.Contains("target.txt", item.Location);

        await tab.OpenSelectedCommand.ExecuteAsync(null);

        var document = viewModel.SelectedDocument!;
        Assert.Equal(path, document.FilePath);
        Assert.Equal(3, document.CurrentLine);
        // 飛んだ先の行はまるごと選択して、どこへ来たのか分かるようにする。
        Assert.Equal("探している行", document.SelectedText);
    });

    [Fact]
    public async Task CancellingTheDialogLeavesTheWorkspaceUntouched()
    {
        using var storage = new TemporaryStorage();
        var viewModel = new MainWindowViewModel(
            new DocumentFileService(),
            new StubDialogService(),
            new AppSettings());

        await viewModel.GrepCommand.ExecuteAsync(null);

        Assert.Single(viewModel.Tabs);
        Assert.False(viewModel.IsGrepSelected);
    }

    [Fact]
    public async Task TheSearchConditionsAreRememberedForTheNextSearch()
    {
        using var storage = new TemporaryStorage();
        Write(storage, "note.txt", "覚える");
        var dialogs = new StubDialogService { GrepQuery = Query(storage, "覚える", mask: "*.txt") };
        var settings = new AppSettings();
        var viewModel = new MainWindowViewModel(new DocumentFileService(), dialogs, settings);

        await viewModel.GrepCommand.ExecuteAsync(null);

        Assert.Equal("覚える", settings.GrepPattern);
        Assert.Equal(storage.Path, settings.GrepFolder);
        Assert.Equal("*.txt", settings.GrepFileMask);
        Assert.Equal("覚える", SettingsService.Load().GrepPattern);
    }

    [Fact]
    public void ClosingTheLastResultTabLeavesAnEmptyDocument() => SingleThreadedAsync.Run(async () =>
    {
        using var storage = new TemporaryStorage();
        Write(storage, "note.txt", "内容");
        var viewModel = new MainWindowViewModel(
            new DocumentFileService(),
            new StubDialogService { GrepQuery = Query(storage, "内容") },
            new AppSettings());
        await viewModel.GrepCommand.ExecuteAsync(null);
        var tab = Assert.IsType<GrepResultTabViewModel>(viewModel.SelectedTab);

        // 検索を始めると、まっさらな無題は押し出されずに残っている。
        Assert.Contains(viewModel.Documents, document => document.Text.Length == 0);

        await viewModel.CloseTabCommand.ExecuteAsync(tab);

        Assert.DoesNotContain(viewModel.Tabs, item => item.IsGrepTab);
        Assert.NotEmpty(viewModel.Documents);
    });

    private static void Write(TemporaryStorage storage, string relativePath, string text)
        => File.WriteAllText(Path.Combine(storage.Path, relativePath), text);

    private static GrepQuery Query(
        TemporaryStorage storage,
        string pattern,
        string mask = "*.*",
        bool recursive = true,
        bool matchCase = false,
        bool useRegex = false)
        => new(pattern, storage.Path, mask, recursive, matchCase, useRegex);

    private static Task<GrepResult> Search(
        TemporaryStorage storage,
        string pattern,
        string mask = "*.*",
        bool recursive = true,
        bool matchCase = false,
        bool useRegex = false)
        => new GrepService(new DocumentFileService()).SearchAsync(
            Query(storage, pattern, mask, recursive, matchCase, useRegex),
            TestContext.Current.CancellationToken);

    private sealed class StubDialogService : IEditorDialogService
    {
        /// <summary>検索ダイアログが返す条件。null なら取り消し扱い。</summary>
        public GrepQuery? GrepQuery { get; init; }

        public Task<GrepQuery?> PickGrepQueryAsync(GrepQuery initial) => Task.FromResult(GrepQuery);

        public Task<IReadOnlyList<string>> PickOpenPathsAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);

        public Task<UnsavedDocumentDecision> ConfirmUnsavedAsync(string documentName)
            => Task.FromResult(UnsavedDocumentDecision.Discard);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<int?> PickLineNumberAsync(int currentLine, int maximumLine) => Task.FromResult<int?>(null);

        public Task<string?> PromptTextAsync(string title, string message, string initialText)
            => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task CheckForUpdatesAsync(bool manually) => Task.CompletedTask;
    }
}
