using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

/// <summary>
/// 「保存していないタブがあってもそのまま閉じられ、次に開くと前回終了時の状態から続けられる」契約。
/// 保存側（<c>PersistSessionState</c>）と復元側（<c>InitializeAsync</c>）を実際に往復させて確かめる。
/// </summary>
public sealed class SessionRestoreTests
{
    [Fact]
    public async Task UnsavedTabsSurviveClosingAndComeBackOnTheNextStart()
    {
        using var storage = new TemporaryStorage();

        var first = CreateViewModel();
        await first.InitializeAsync([]);
        var document = first.SelectedDocument!;
        document.Text = "書きかけのメモ";
        document.CaretIndex = 3;
        await first.PersistSessionStateAsync();

        var second = CreateViewModel();
        await second.InitializeAsync([]);

        var restored = Assert.Single(second.Documents);
        Assert.Equal("書きかけのメモ", restored.Text);
        Assert.Equal(3, restored.CaretIndex);
        Assert.True(restored.IsModified);
        Assert.Null(restored.FilePath);
        Assert.Same(restored, second.SelectedTab);
    }

    [Fact]
    public async Task ClosingIsNotBlockedByUnsavedDocumentsWhileRestoreIsOn()
    {
        using var storage = new TemporaryStorage();

        var dialogs = new StubDialogService();
        var viewModel = new MainWindowViewModel(new FakeFileService(), dialogs, new AppSettings());
        viewModel.SelectedDocument!.Text = "未保存";

        Assert.True(await viewModel.CanCloseAsync());
        Assert.Equal(0, dialogs.UnsavedPrompts);
    }

    [Fact]
    public async Task ClosingStillAsksAboutUnsavedDocumentsWhileRestoreIsOff()
    {
        using var storage = new TemporaryStorage();

        var dialogs = new StubDialogService();
        var viewModel = new MainWindowViewModel(
            new FakeFileService(),
            dialogs,
            new AppSettings { RestoreSession = false });
        viewModel.SelectedDocument!.Text = "未保存";

        Assert.True(await viewModel.CanCloseAsync());
        Assert.Equal(1, dialogs.UnsavedPrompts);
    }

    [Fact]
    public async Task UnsavedChangesToASavedFileWinOverWhatIsOnDisk()
    {
        using var storage = new TemporaryStorage();
        var path = Path.Combine(storage.Path, "note.txt");
        File.WriteAllText(path, "ディスクの内容");

        var first = CreateViewModel("ディスクの内容");
        await first.InitializeAsync([path]);
        var document = first.SelectedDocument!;
        document.Text = "編集した内容";
        await first.PersistSessionStateAsync();

        var second = CreateViewModel("ディスクの内容");
        await second.InitializeAsync([]);

        var restored = Assert.Single(second.Documents);
        Assert.Equal("編集した内容", restored.Text);
        Assert.True(restored.IsModified);
        Assert.Equal(path, restored.FilePath);
    }

    [Fact]
    public async Task SavedTabsWithoutChangesAreReadBackFromDisk()
    {
        using var storage = new TemporaryStorage();
        var path = Path.Combine(storage.Path, "note.txt");
        File.WriteAllText(path, "保存済み");

        var first = CreateViewModel("保存済み");
        await first.InitializeAsync([path]);
        await first.PersistSessionStateAsync();

        var second = CreateViewModel("あとから変わった内容");
        await second.InitializeAsync([]);

        var restored = Assert.Single(second.Documents);
        Assert.Equal("あとから変わった内容", restored.Text);
        Assert.False(restored.IsModified);
    }

    [Fact]
    public async Task TabsWhoseFileDisappearedAreDroppedInsteadOfComingBackEmpty()
    {
        using var storage = new TemporaryStorage();
        var path = Path.Combine(storage.Path, "gone.txt");
        File.WriteAllText(path, "消える予定");

        var first = CreateViewModel("消える予定");
        await first.InitializeAsync([path]);
        await first.PersistSessionStateAsync();
        File.Delete(path);

        var second = CreateViewModel();
        await second.InitializeAsync([]);

        var remaining = Assert.Single(second.Documents);
        Assert.Null(remaining.FilePath);
        Assert.Equal(string.Empty, remaining.Text);
    }

    [Fact]
    public async Task TheSelectedTabAndTheSettingsTabComeBack()
    {
        using var storage = new TemporaryStorage();

        var first = CreateViewModel();
        await first.InitializeAsync([]);
        first.SelectedDocument!.Text = "1 枚目";
        first.NewDocumentCommand.Execute(null);
        first.SelectedDocument!.Text = "2 枚目";
        first.OpenSettingsCommand.Execute(null);
        first.SelectedTab = first.Documents.First();
        await first.PersistSessionStateAsync();

        var second = CreateViewModel();
        await second.InitializeAsync([]);

        Assert.Equal(["1 枚目", "2 枚目"], second.Documents.Select(document => document.Text));
        Assert.Equal("1 枚目", second.SelectedDocument!.Text);
        Assert.NotNull(second.SettingsTab);
        Assert.Contains(second.Tabs, tab => tab.IsSettingsTab);

        // 復元した「無題 2」と同じ名前を次の新規文書へ振らない。
        second.NewDocumentCommand.Execute(null);
        Assert.Equal("無題 3", second.SelectedDocument!.DisplayName);
    }

    [Fact]
    public async Task StartupFilesOpenOnTopOfTheRestoredTabs()
    {
        using var storage = new TemporaryStorage();
        var path = Path.Combine(storage.Path, "argument.txt");
        File.WriteAllText(path, "引数のファイル");

        var first = CreateViewModel();
        await first.InitializeAsync([]);
        first.SelectedDocument!.Text = "前回の書きかけ";
        await first.PersistSessionStateAsync();

        var second = CreateViewModel("引数のファイル");
        await second.InitializeAsync([path]);

        Assert.Equal(2, second.Documents.Count());
        Assert.Equal(path, second.SelectedDocument!.FilePath);
        Assert.Contains(second.Documents, document => document.Text == "前回の書きかけ");
    }

    [Fact]
    public async Task TurningRestoreOffThrowsTheStoredSessionAway()
    {
        using var storage = new TemporaryStorage();

        var settings = new AppSettings();
        var first = new MainWindowViewModel(new FakeFileService(), new StubDialogService(), settings);
        await first.InitializeAsync([]);
        first.SelectedDocument!.Text = "書きかけ";
        await first.PersistSessionStateAsync();
        Assert.True(File.Exists(Path.Combine(storage.Path, "session.json")));

        first.Options.RestoreSession = false;
        await first.PersistSessionStateAsync();

        Assert.False(File.Exists(Path.Combine(storage.Path, "session.json")));
        Assert.False(Directory.Exists(Path.Combine(storage.Path, "session")));
    }

    [Fact]
    public async Task ClosedTabsDoNotLeaveTheirUnsavedContentBehind()
    {
        using var storage = new TemporaryStorage();

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync([]);
        viewModel.SelectedDocument!.Text = "1 枚目";
        viewModel.NewDocumentCommand.Execute(null);
        viewModel.SelectedDocument!.Text = "2 枚目";
        await viewModel.PersistSessionStateAsync();
        Assert.Equal(2, Directory.GetFiles(Path.Combine(storage.Path, "session")).Length);

        await viewModel.CloseTabCommand.ExecuteAsync(viewModel.Documents.Last());
        await viewModel.PersistSessionStateAsync();

        Assert.Single(Directory.GetFiles(Path.Combine(storage.Path, "session")));
    }

    [Fact]
    public async Task ARestoredDocumentStaysModifiedAfterUndoingBackToItsRestoredText()
    {
        using var storage = new TemporaryStorage();

        var first = CreateViewModel();
        await first.InitializeAsync([]);
        first.SelectedDocument!.Text = "復元される内容";
        await first.PersistSessionStateAsync();

        var second = CreateViewModel();
        await second.InitializeAsync([]);
        var restored = second.SelectedDocument!;
        restored.EditorDocument.Insert(0, "追記");
        restored.EditorDocument.UndoStack.Undo();

        Assert.Equal("復元される内容", restored.Text);
        Assert.True(restored.IsModified);
    }

    [Fact]
    public void ACorruptSessionFileStartsWithAnEmptyWorkspace()
    {
        using var storage = new TemporaryStorage();
        File.WriteAllText(Path.Combine(storage.Path, "session.json"), "{ これは JSON ではない");

        var session = SessionStateService.Load();

        Assert.Empty(session.Tabs);
        Assert.Equal(-1, session.SelectedTabIndex);
    }

    [Fact]
    public async Task TheSessionIsNotRewrittenWhileTheRestoreIsStillRunning()
    {
        using var storage = new TemporaryStorage();
        var path = Path.Combine(storage.Path, "slow.txt");
        File.WriteAllText(path, "ディスクの内容");

        var first = CreateViewModel("ディスクの内容");
        await first.InitializeAsync([path]);
        first.NewDocumentCommand.Execute(null);
        first.SelectedDocument!.Text = "もう 1 枚の書きかけ";
        await first.PersistSessionStateAsync();

        // 1 枚目の読み込みで止めたまま閉じ始める（起動直後に閉じたときの状況）。
        var gate = new TaskCompletionSource();
        var second = new MainWindowViewModel(
            new FakeFileService { ReadText = "ディスクの内容", ReadGate = gate.Task },
            new StubDialogService(),
            new AppSettings());
        var initialization = second.InitializeAsync([]);
        var persist = second.PersistSessionStateAsync();

        Assert.False(persist.IsCompleted);
        gate.SetResult();
        Assert.True(await persist);
        await initialization;

        var third = CreateViewModel("ディスクの内容");
        await third.InitializeAsync([]);

        Assert.Equal(2, third.Documents.Count());
        Assert.Contains(third.Documents, document => document.Text == "もう 1 枚の書きかけ");
    }

    [Fact]
    public async Task ClosingIsRefusedWhenTheUnsavedContentCannotBeStored()
    {
        using var storage = new TemporaryStorage();
        // session.json と同じ名前のディレクトリを置いて、一覧の書き込みだけを失敗させる。
        Directory.CreateDirectory(Path.Combine(storage.Path, "session.json"));

        var dialogs = new StubDialogService();
        var viewModel = new MainWindowViewModel(new FakeFileService(), dialogs, new AppSettings());
        await viewModel.InitializeAsync([]);
        viewModel.SelectedDocument!.Text = "失いたくない内容";

        Assert.False(await viewModel.PersistSessionStateAsync());
        Assert.Equal(1, dialogs.Errors);
    }

    [Fact]
    public async Task AFailedSessionSaveDoesNotBlockClosingWhenNothingIsUnsaved()
    {
        using var storage = new TemporaryStorage();
        Directory.CreateDirectory(Path.Combine(storage.Path, "session.json"));

        var dialogs = new StubDialogService();
        var viewModel = new MainWindowViewModel(new FakeFileService(), dialogs, new AppSettings());
        await viewModel.InitializeAsync([]);

        Assert.True(await viewModel.PersistSessionStateAsync());
        Assert.Equal(0, dialogs.Errors);
    }

    [Fact]
    public void EachSaveWritesANewBufferInsteadOfOverwritingThePreviousOne()
    {
        using var storage = new TemporaryStorage();
        var sessionDirectory = Path.Combine(storage.Path, "session");

        Assert.True(SessionStateService.Save(SingleTabSession("1 回目")));
        var firstBuffer = Assert.Single(Directory.GetFiles(sessionDirectory));

        Assert.True(SessionStateService.Save(SingleTabSession("2 回目")));
        var secondBuffer = Assert.Single(Directory.GetFiles(sessionDirectory));

        Assert.NotEqual(firstBuffer, secondBuffer);
        Assert.Equal("2 回目", SessionStateService.Load().Tabs.Single().Text);
    }

    [Fact]
    public void AFailedSaveLeavesThePreviousSessionReadable()
    {
        using var storage = new TemporaryStorage();
        Assert.True(SessionStateService.Save(SingleTabSession("1 回目")));

        // 一覧の確定だけを失敗させる。控えを固定名で先に上書きしていると、ここで前回分が失われる。
        var sessionPath = Path.Combine(storage.Path, "session.json");
        File.SetAttributes(sessionPath, FileAttributes.ReadOnly);
        try
        {
            Assert.False(SessionStateService.Save(SingleTabSession("2 回目")));
        }
        finally
        {
            File.SetAttributes(sessionPath, FileAttributes.Normal);
        }

        Assert.Equal("1 回目", SessionStateService.Load().Tabs.Single().Text);
    }

    [Fact]
    public async Task ASavedFileWhoseUnsavedBufferIsGoneOpensFromDiskAndSaysSo()
    {
        using var storage = new TemporaryStorage();
        var path = Path.Combine(storage.Path, "note.txt");
        File.WriteAllText(path, "ディスクの内容");

        var first = CreateViewModel("ディスクの内容");
        await first.InitializeAsync([path]);
        first.SelectedDocument!.Text = "消える書きかけ";
        await first.PersistSessionStateAsync();
        DeleteSessionBuffers(storage);

        var second = CreateViewModel("ディスクの内容");
        await second.InitializeAsync([]);

        var restored = Assert.Single(second.Documents);
        Assert.Equal("ディスクの内容", restored.Text);
        Assert.False(restored.IsModified);
        Assert.Contains("復元できませんでした", second.StatusMessage);
    }

    [Fact]
    public async Task AnUntitledTabWhoseUnsavedBufferIsGoneIsNotRestoredAsAnEmptyTab()
    {
        using var storage = new TemporaryStorage();

        var first = CreateViewModel();
        await first.InitializeAsync([]);
        first.SelectedDocument!.Text = "控えごと消える";
        await first.PersistSessionStateAsync();
        DeleteSessionBuffers(storage);

        var second = CreateViewModel();
        await second.InitializeAsync([]);

        var remaining = Assert.Single(second.Documents);
        Assert.Equal(string.Empty, remaining.Text);
        Assert.False(remaining.IsModified);
        Assert.Contains("復元できませんでした", second.StatusMessage);
    }

    private static void DeleteSessionBuffers(TemporaryStorage storage)
    {
        foreach (var buffer in Directory.GetFiles(Path.Combine(storage.Path, "session")))
        {
            File.Delete(buffer);
        }
    }

    private static SessionState SingleTabSession(string text) => new()
    {
        Tabs = [new SessionTabState { UntitledName = "無題", IsModified = true, Text = text }],
        SelectedTabIndex = 0,
    };

    private static MainWindowViewModel CreateViewModel(string diskText = "")
        => new(new FakeFileService { ReadText = diskText }, new StubDialogService(), new AppSettings());

    private sealed class FakeFileService : IDocumentFileService
    {
        public string ReadText { get; init; } = string.Empty;

        /// <summary>読み込みをここで止める（復元の途中で閉じられた状況を作る）。</summary>
        public Task? ReadGate { get; init; }

        public async Task<TextDocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (ReadGate is { } gate)
            {
                await gate;
            }

            return new TextDocumentContent(ReadText, DocumentEncoding.Utf8, "\r\n");
        }

        public Task WriteAsync(
            string path,
            TextDocumentContent content,
            bool createBackup = false,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubDialogService : IEditorDialogService
    {
        /// <summary>未保存の確認を出した回数。復元が有効なら 0 のままになる。</summary>
        public int UnsavedPrompts { get; private set; }

        /// <summary>エラーを知らせた回数。</summary>
        public int Errors { get; private set; }

        public Task<IReadOnlyList<string>> PickOpenPathsAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);

        public Task<UnsavedDocumentDecision> ConfirmUnsavedAsync(string documentName)
        {
            UnsavedPrompts++;
            return Task.FromResult(UnsavedDocumentDecision.Discard);
        }

        public Task ShowErrorAsync(string title, string message)
        {
            Errors++;
            return Task.CompletedTask;
        }

        public Task<int?> PickLineNumberAsync(int currentLine, int maximumLine) => Task.FromResult<int?>(null);

        public Task<string?> PromptTextAsync(string title, string message, string initialText)
            => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<GrepQuery?> PickGrepQueryAsync(GrepQuery initial) => Task.FromResult<GrepQuery?>(null);

        public Task CheckForUpdatesAsync(bool manually) => Task.CompletedTask;
    }
}
