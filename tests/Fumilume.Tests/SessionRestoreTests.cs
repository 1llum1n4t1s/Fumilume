using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

/// <summary>
/// 「保存していないタブがあってもそのまま閉じられ、次に開くと前回終了時の状態から続けられる」契約。
/// 保存側（<c>PersistSessionState</c>）と復元側（<c>InitializeAsync</c>）を実際に往復させて確かめる。
/// </summary>
[Collection(HeadlessAppCollection.Name)]
public sealed class SessionRestoreTests(HeadlessAppFixture fixture)
{
    [Fact]
    public async Task DuplicateSavedPathsRestoreOnlyOnce()
    {
        using var storage = new TemporaryStorage();
        var path = Path.Combine(storage.Path, "duplicate.txt");
        File.WriteAllText(path, "disk");
        Assert.True(SessionStateService.Save(new SessionState
        {
            Tabs = [new() { FilePath = path }, new() { FilePath = path.ToUpperInvariant() }],
            SelectedTabIndex = 1,
        }));
        var files = new FakeFileService { ReadText = "disk" };
        var viewModel = new MainWindowViewModel(files, new StubDialogService());

        await viewModel.InitializeAsync([]);

        Assert.Same(Assert.Single(viewModel.Documents), viewModel.SelectedTab);
        Assert.Equal(1, files.Reads);
    }

    [Fact]
    public void OpeningDuringRestoreUsesTheRestoredTab() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var path = Path.Combine(storage.Path, "concurrent.txt");
        File.WriteAllText(path, "disk");
        Assert.True(SessionStateService.Save(new SessionState { Tabs = [new() { FilePath = path }] }));
        var gate = new TaskCompletionSource();
        var files = new FakeFileService { ReadText = "disk", ReadGate = gate.Task };
        var viewModel = new MainWindowViewModel(files, new StubDialogService());
        var restore = viewModel.InitializeAsync([]);
        var open = viewModel.OpenPathsAsync([path]);
        gate.SetResult();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(restore.IsCompleted);
        Assert.True(open.IsCompleted);
        restore.GetAwaiter().GetResult();
        open.GetAwaiter().GetResult();

        Assert.Same(Assert.Single(viewModel.Documents), viewModel.SelectedTab);
        Assert.Equal(1, files.Reads);
    });

    [Fact]
    public async Task DuplicateUnsavedPathsKeepBothBuffersWithoutSharingASaveTarget()
    {
        using var storage = new TemporaryStorage();
        var path = Path.Combine(storage.Path, "duplicate.txt");
        Assert.True(SessionStateService.Save(new SessionState
        {
            Tabs =
            [
                new() { FilePath = path, IsModified = true, Text = "first" },
                new() { FilePath = path.ToUpperInvariant(), IsModified = true, Text = "second" },
            ],
            SelectedTabIndex = 1,
        }));
        var files = new FakeFileService();
        var viewModel = new MainWindowViewModel(files, new StubDialogService());
        await viewModel.InitializeAsync([]);

        Assert.Equal(2, viewModel.Documents.Count());
        Assert.Single(viewModel.Documents, document => document.FilePath is not null);
        Assert.Equal("second", viewModel.SelectedDocument!.Text);
        Assert.Null(viewModel.SelectedDocument.FilePath);
        Assert.All(viewModel.Documents, document => Assert.True(document.IsModified));
        Assert.Equal(0, files.Reads);

        Assert.True(await viewModel.PersistSessionStateAsync());
        var restoredAgain = CreateViewModel();
        await restoredAgain.InitializeAsync([]);
        Assert.Equal(new[] { "first", "second" }, restoredAgain.Documents.Select(document => document.Text));
        Assert.Single(restoredAgain.Documents, document => document.FilePath is not null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MetadataChangesSurviveUndoAndSessionRestore(bool encoding)
    {
        using var storage = new TemporaryStorage();
        var first = CreateViewModel("disk");
        await first.InitializeAsync([]);
        var document = first.SelectedDocument!;
        document.Load(Path.Combine(storage.Path, "metadata.txt"),
            new TextDocumentContent("disk", DocumentEncoding.Utf8, "\n"));
        if (encoding)
        {
            document.Encoding = DocumentEncoding.Utf8Bom;
        }
        else
        {
            document.NewLine = "\r\n";
        }
        document.EditorDocument.Insert(0, "x");
        document.EditorDocument.UndoStack.Undo();
        Assert.True(await first.PersistSessionStateAsync());

        var second = CreateViewModel();
        await second.InitializeAsync([]);
        var restored = Assert.Single(second.Documents);
        Assert.Equal("disk", restored.Text);
        Assert.Equal(document.Encoding, restored.Encoding);
        Assert.Equal(document.NewLine, restored.NewLine);
        Assert.True(restored.IsModified);
    }

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
    public void ExternallyDeletedDocumentSurvivesTheNextStartAsUnsavedText() => fixture.Run(() =>
        SingleThreadedAsync.Run(async () =>
    {
        using var storage = new TemporaryStorage();
        var path = Path.Combine(storage.Path, "deleted-externally.txt");
        await File.WriteAllTextAsync(
            path,
            "only remaining copy",
            TestContext.Current.CancellationToken);
        var first = new MainWindowViewModel(
            new DocumentFileService(),
            new StubDialogService(),
            new AppSettings());
        await first.InitializeAsync([path]);
        File.Delete(path);

        Assert.True(await first.ProcessExternalFileChangeAsync(path));
        Assert.True(first.SelectedDocument!.IsModified);
        Assert.True(await first.PersistSessionStateAsync());

        var second = new MainWindowViewModel(
            new DocumentFileService(),
            new StubDialogService(),
            new AppSettings());
        await second.InitializeAsync([]);

        var restored = Assert.Single(second.Documents);
        Assert.Equal("only remaining copy", restored.Text);
        Assert.Equal(path, restored.FilePath, ignoreCase: true);
        Assert.True(restored.IsModified);
    }));

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
        first.OpenSettingsCommand.Execute(null);
        await first.PersistSessionStateAsync();
        File.Delete(path);

        var second = CreateViewModel();
        await second.InitializeAsync([]);

        var remaining = Assert.Single(second.Documents);
        Assert.Null(remaining.FilePath);
        Assert.Equal(string.Empty, remaining.Text);
        Assert.NotNull(second.SettingsTab);
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
    public async Task PinnedTabsAndTheirOrderComeBackFromTheSession()
    {
        using var storage = new TemporaryStorage();

        var first = CreateViewModel();
        await first.InitializeAsync([]);
        first.SelectedDocument!.Text = "通常";
        first.NewDocumentCommand.Execute(null);
        var pinnedFirst = first.SelectedDocument!;
        pinnedFirst.Text = "固定 1";
        pinnedFirst.TogglePinCommand.Execute(null);
        first.NewDocumentCommand.Execute(null);
        var pinnedSecond = first.SelectedDocument!;
        pinnedSecond.Text = "固定 2";
        pinnedSecond.TogglePinCommand.Execute(null);
        first.SelectedTab = pinnedSecond;

        await first.PersistSessionStateAsync();

        var stored = SessionStateService.Load();
        Assert.Equal([true, true, false], stored.Tabs.Select(tab => tab.IsPinned));

        var restored = CreateViewModel();
        await restored.InitializeAsync([]);

        Assert.Equal(["固定 1", "固定 2", "通常"], restored.Documents.Select(tab => tab.Text));
        Assert.Equal([true, true, false], restored.Documents.Select(tab => tab.IsPinned));
        Assert.Equal("固定 2", restored.SelectedDocument!.Text);
    }

    [Fact]
    public void SessionsWrittenBeforePinningWasAddedLoadAsUnpinned()
    {
        using var storage = new TemporaryStorage();
        File.WriteAllText(
            Path.Combine(storage.Path, "session.json"),
            """{"Tabs":[{"Kind":"Document","UntitledName":"無題"}]}""");

        var tab = Assert.Single(SessionStateService.Load().Tabs);

        Assert.False(tab.IsPinned);
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

    [Theory]
    [InlineData("{ これは JSON ではない")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Tabs\":null}")]
    [InlineData("{\"Tabs\":[{\"IsModified\":true,\"BufferFile\":\"tab-\\u0000.txt\"}]}")]
    public async Task AnUnreadableSessionFileAndItsBuffersAreNotOverwrittenOnClose(string unreadableJson)
    {
        using var storage = new TemporaryStorage();
        Assert.True(SessionStateService.Save(SingleTabSession("一覧が壊れても残す内容")));
        var bufferPath = Assert.Single(Directory.GetFiles(Path.Combine(storage.Path, "session")));
        var sessionPath = Path.Combine(storage.Path, "session.json");
        File.WriteAllText(sessionPath, unreadableJson);

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync([]);

        Assert.Contains("既存の控えは保護しています", viewModel.StatusMessage);
        Assert.True(await viewModel.PersistSessionStateAsync());
        Assert.Equal(unreadableJson, File.ReadAllText(sessionPath));
        Assert.True(File.Exists(bufferPath));
    }

    [Fact]
    public async Task NewEditsUseARecoverySessionWhenThePrimarySessionIsUnreadable()
    {
        using var storage = new TemporaryStorage();
        Assert.True(SessionStateService.Save(SingleTabSession("主一覧から参照されていた内容")));
        var originalBuffer = Assert.Single(Directory.GetFiles(Path.Combine(storage.Path, "session")));
        var sessionPath = Path.Combine(storage.Path, "session.json");
        const string corruptJson = "{ これは JSON ではない";
        File.WriteAllText(sessionPath, corruptJson);

        var second = CreateViewModel();
        await second.InitializeAsync([]);
        second.SelectedDocument!.Text = "一覧破損後の新しい編集";

        Assert.True(second.PersistSessionStateForShutdown());
        Assert.Equal(corruptJson, File.ReadAllText(sessionPath));
        Assert.True(File.Exists(originalBuffer));

        var third = CreateViewModel();
        await third.InitializeAsync([]);

        var recovered = Assert.Single(third.Documents);
        Assert.Equal("一覧破損後の新しい編集", recovered.Text);
        Assert.True(recovered.IsModified);

        // 主一覧が取り除かれた次の起動では回復一覧を通常セッションへ昇格し、専用の控えを片付ける。
        File.Delete(sessionPath);
        var fourth = CreateViewModel();
        await fourth.InitializeAsync([]);
        Assert.Equal("一覧破損後の新しい編集", Assert.Single(fourth.Documents).Text);
        Assert.True(await fourth.PersistSessionStateAsync());
        Assert.True(File.Exists(sessionPath));
        Assert.False(Directory.Exists(Path.Combine(storage.Path, "session-recovery")));
    }

    [Fact]
    public async Task RecoveryIsMergedAfterATemporarilyUnreadablePrimaryBecomesReadable()
    {
        using var storage = new TemporaryStorage();
        Assert.True(SessionStateService.Save(SingleTabSession("主一覧に残っていた内容")));
        var sessionPath = Path.Combine(storage.Path, "session.json");

        await using (var locked = new FileStream(sessionPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var second = CreateViewModel();
            await second.InitializeAsync([]);
            second.SelectedDocument!.Text = "ロック中に書いた内容";
            Assert.True(second.PersistSessionStateForShutdown());
        }

        var third = CreateViewModel();
        await third.InitializeAsync([]);

        Assert.Contains(third.Documents, document => document.Text == "主一覧に残っていた内容");
        Assert.Contains(third.Documents, document => document.Text == "ロック中に書いた内容");
        Assert.True(await third.PersistSessionStateAsync());
        Assert.False(Directory.Exists(Path.Combine(storage.Path, "session-recovery")));
    }

    [Fact]
    public async Task AnUnreadableRecoverySnapshotIsNotOverwritten()
    {
        using var storage = new TemporaryStorage();
        Assert.True(SessionStateService.Save(SingleTabSession("主一覧に残っていた内容")));
        var sessionPath = Path.Combine(storage.Path, "session.json");
        File.WriteAllText(sessionPath, "{ 壊れた主一覧");

        var second = CreateViewModel();
        await second.InitializeAsync([]);
        second.SelectedDocument!.Text = "最初の回復内容";
        Assert.True(second.PersistSessionStateForShutdown());

        var recoveryRoot = Path.Combine(storage.Path, "session-recovery");
        var firstSnapshot = Assert.Single(Directory.GetDirectories(recoveryRoot, "snapshot-*"));
        var firstBuffer = Assert.Single(Directory.GetFiles(firstSnapshot, "tab-*.txt"));
        var recoveryManifest = Path.Combine(firstSnapshot, "session.json");
        const string corruptRecovery = "{ 壊れた回復一覧";
        File.WriteAllText(recoveryManifest, corruptRecovery);

        var third = CreateViewModel();
        await third.InitializeAsync([]);
        third.SelectedDocument!.Text = "回復一覧が壊れた後の編集";
        Assert.True(third.PersistSessionStateForShutdown());

        Assert.Equal(corruptRecovery, File.ReadAllText(recoveryManifest));
        Assert.True(File.Exists(firstBuffer));
        Assert.Equal(2, Directory.GetDirectories(recoveryRoot, "snapshot-*").Length);
    }

    [Fact]
    public async Task PromotionWaitsWhileARecoveryBufferIsTemporarilyUnreadable()
    {
        using var storage = new TemporaryStorage();
        var sessionPath = Path.Combine(storage.Path, "session.json");
        File.WriteAllText(sessionPath, "{ 壊れた主一覧");

        var first = CreateViewModel();
        await first.InitializeAsync([]);
        first.SelectedDocument!.Text = "一時的に読めない回復内容";
        Assert.True(first.PersistSessionStateForShutdown());
        File.Delete(sessionPath);

        var recoveryRoot = Path.Combine(storage.Path, "session-recovery");
        var snapshot = Assert.Single(Directory.GetDirectories(recoveryRoot, "snapshot-*"));
        var buffer = Assert.Single(Directory.GetFiles(snapshot, "tab-*.txt"));
        await using (var locked = new FileStream(buffer, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var second = CreateViewModel();
            await second.InitializeAsync([]);
            Assert.Contains("回復用セッションを一時的に読み込めませんでした", second.StatusMessage);
            Assert.True(await second.PersistSessionStateAsync());
            Assert.True(Directory.Exists(recoveryRoot));
            Assert.True(File.Exists(buffer));
        }

        var third = CreateViewModel();
        await third.InitializeAsync([]);
        Assert.Contains(third.Documents, document => document.Text == "一時的に読めない回復内容");
        Assert.True(await third.PersistSessionStateAsync());
        Assert.False(Directory.Exists(recoveryRoot));
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
    public void SynchronousShutdownDuringRestoreLeavesThePreviousSessionUntouched() => fixture.Run(() =>
        SingleThreadedAsync.Run(async () =>
    {
        using var storage = new TemporaryStorage();
        var firstPath = Path.Combine(storage.Path, "first.txt");
        var secondPath = Path.Combine(storage.Path, "second.txt");
        File.WriteAllText(firstPath, "first");
        File.WriteAllText(secondPath, "second");
        Assert.True(SessionStateService.Save(new SessionState
        {
            Tabs =
            [
                new SessionTabState { FilePath = firstPath },
                new SessionTabState { FilePath = secondPath },
            ],
            SelectedTabIndex = 1,
            SettingsTabOpen = true,
        }));
        var readGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = new FakeFileService { ReadText = "disk", ReadGate = readGate.Task };
        var viewModel = new MainWindowViewModel(files, new StubDialogService(), new AppSettings());
        var initialization = viewModel.InitializeAsync([]);
        SessionState preserved;

        try
        {
            Assert.Equal(1, files.Reads);
            Assert.True(viewModel.PersistSessionStateForShutdown());
            preserved = SessionStateService.Load();
        }
        finally
        {
            readGate.TrySetResult();
            await initialization;
        }

        Assert.Equal([firstPath, secondPath], preserved.Tabs.Select(tab => tab.FilePath));
        Assert.Equal(1, preserved.SelectedTabIndex);
        Assert.True(preserved.SettingsTabOpen);
    }));

    [Fact]
    public void SynchronousShutdownDuringRestoreKeepsNewEditsAndPendingTabs() => fixture.Run(() =>
        SingleThreadedAsync.Run(async () =>
    {
        using var storage = new TemporaryStorage();
        var firstPath = Path.Combine(storage.Path, "first.txt");
        var secondPath = Path.Combine(storage.Path, "second.txt");
        File.WriteAllText(firstPath, "first");
        File.WriteAllText(secondPath, "second");
        Assert.True(SessionStateService.Save(new SessionState
        {
            Tabs =
            [
                new SessionTabState { FilePath = firstPath },
                new SessionTabState { FilePath = secondPath },
            ],
            SelectedTabIndex = 1,
        }));
        var readGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = new FakeFileService { ReadText = "disk", ReadGate = readGate.Task };
        var viewModel = new MainWindowViewModel(files, new StubDialogService(), new AppSettings());
        var initialization = viewModel.InitializeAsync([]);
        SessionState stored;

        try
        {
            Assert.Equal(1, files.Reads);
            viewModel.SelectedDocument!.Text = "復元を待つ間の新しい編集";

            Assert.True(viewModel.PersistSessionStateForShutdown());
            stored = SessionStateService.Load();
        }
        finally
        {
            readGate.TrySetResult();
            await initialization;
        }

        Assert.Equal(3, stored.Tabs.Count);
        Assert.Contains(stored.Tabs, tab => tab.Text == "復元を待つ間の新しい編集");
        Assert.Equal([firstPath, secondPath], stored.Tabs.Where(tab => tab.FilePath is not null).Select(tab => tab.FilePath));
        Assert.Equal(0, stored.SelectedTabIndex);
    }));

    [Fact]
    public async Task PersistBeforeInitializationLeavesThePreviousSessionUntouched()
    {
        using var storage = new TemporaryStorage();
        Assert.True(SessionStateService.Save(SingleTabSession("前回の書きかけ")));
        var viewModel = CreateViewModel();

        Assert.True(await viewModel.PersistSessionStateAsync());

        var preserved = Assert.Single(SessionStateService.Load().Tabs);
        Assert.Equal("前回の書きかけ", preserved.Text);
    }

    [Fact]
    public async Task SystemShutdownSynchronouslyStoresTheCurrentUnsavedText()
    {
        using var storage = new TemporaryStorage();
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync([]);
        viewModel.SelectedDocument!.Text = "OS終了でも残す内容";

        Assert.True(viewModel.PersistSessionStateForShutdown());

        var stored = Assert.Single(SessionStateService.Load().Tabs);
        Assert.Equal("OS終了でも残す内容", stored.Text);
    }

    [Fact]
    public async Task AnUnsavedBufferSurvivesWhenRestoringItsTabThrows()
    {
        using var storage = new TemporaryStorage();
        var broken = new SessionState
        {
            Tabs =
            [
                new SessionTabState
                {
                    UntitledName = "無題",
                    IsModified = true,
                    Text = "復元に失敗しても残す内容",
                    Bookmarks = null!,
                },
            ],
            SelectedTabIndex = 0,
        };
        Assert.True(SessionStateService.Save(broken));

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync([]);
        Assert.Contains("復元できませんでした", viewModel.StatusMessage);

        Assert.True(await viewModel.PersistSessionStateAsync());
        var savedAgain = SessionStateService.Load();

        var preserved = Assert.Single(savedAgain.Tabs);
        Assert.True(preserved.IsModified);
        Assert.Equal("復元に失敗しても残す内容", preserved.Text);
    }

    [Fact]
    public async Task RestoringALargeSavedFileUsesTheSameConfirmationAsOpeningIt()
    {
        using var storage = new TemporaryStorage();
        var path = Path.Combine(storage.Path, "large.txt");
        using (var file = File.Create(path))
        {
            file.SetLength(2L * 1024 * 1024);
        }

        Assert.True(SessionStateService.Save(new SessionState
        {
            Tabs = [new SessionTabState { FilePath = path }],
            SelectedTabIndex = 0,
        }));
        var dialogs = new StubDialogService { ConfirmResult = false };
        var files = new FakeFileService();
        var viewModel = new MainWindowViewModel(
            files,
            dialogs,
            new AppSettings { LargeFileThresholdMegabytes = 1 });

        await viewModel.InitializeAsync([]);

        Assert.Equal(1, dialogs.Confirmations);
        Assert.Equal(0, files.Reads);
        Assert.Null(viewModel.SelectedDocument!.FilePath);
    }

    [Fact]
    public async Task ClosingIsRefusedWhenTheUnsavedContentCannotBeStored()
    {
        using var storage = new TemporaryStorage();
        // 主一覧と回復一覧の両方を書けない状態にして、未保存本文を預けられない場合を再現する。
        Directory.CreateDirectory(Path.Combine(storage.Path, "session.json"));
        File.WriteAllText(Path.Combine(storage.Path, "session-recovery"), "回復領域を作れないようにする");

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
        File.WriteAllText(Path.Combine(storage.Path, "session-recovery"), "回復領域を作れないようにする");

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
    public async Task ATemporarilyUnreadableUnsavedBufferIsRetriedOnTheNextStart()
    {
        using var storage = new TemporaryStorage();
        var path = Path.Combine(storage.Path, "note.txt");
        File.WriteAllText(path, "ディスクの内容");
        var state = new SessionState
        {
            Tabs =
            [
                new SessionTabState
                {
                    FilePath = path,
                    IsModified = true,
                    Text = "一時的に読めない書きかけ",
                },
            ],
            SelectedTabIndex = 0,
        };
        Assert.True(SessionStateService.Save(state));
        var bufferPath = Path.Combine(storage.Path, "session", Assert.IsType<string>(state.Tabs[0].BufferFile));

        await using (var locked = new FileStream(bufferPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var second = CreateViewModel("ディスクの内容");
            await second.InitializeAsync([]);
            Assert.Contains("復元できませんでした", second.StatusMessage);

            Assert.True(await second.PersistSessionStateAsync());
            Assert.True(File.Exists(bufferPath));
        }

        var third = CreateViewModel("ディスクの内容");
        await third.InitializeAsync([]);

        var restored = Assert.Single(third.Documents);
        Assert.Equal("一時的に読めない書きかけ", restored.Text);
        Assert.True(restored.IsModified);
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

        public int Reads { get; private set; }

        /// <summary>読み込みをここで止める（復元の途中で閉じられた状況を作る）。</summary>
        public Task? ReadGate { get; init; }

        public async Task<TextDocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            Reads++;
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

        public int Confirmations { get; private set; }

        public bool ConfirmResult { get; init; } = true;

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

        public Task<bool> ConfirmAsync(string title, string message)
        {
            Confirmations++;
            return Task.FromResult(ConfirmResult);
        }

        public Task<GrepQuery?> PickGrepQueryAsync(GrepQuery initial) => Task.FromResult<GrepQuery?>(null);

        public Task CheckForUpdatesAsync(bool manually) => Task.CompletedTask;
    }
}
