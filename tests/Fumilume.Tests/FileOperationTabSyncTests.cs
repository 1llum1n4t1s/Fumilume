using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

/// <summary>改名・移動・削除・貼り付けと、開いているタブ・カーソル位置・閉じた履歴の整合。</summary>
[Collection(HeadlessAppCollection.Name)]
public sealed class FileOperationTabSyncTests(HeadlessAppFixture fixture)
{
    [Fact]
    public void RenamingAPristineDocumentRelocatesItWithoutChangingState() => Run(async ctx =>
    {
        var path = ctx.Write("docs", "a.txt", "alpha");
        await ctx.Vm.OpenPathsAsync([path]);
        var document = ctx.Doc(path);
        ctx.Dialogs.Reply = "b.txt";

        await ctx.Vm.RenameTabFileAsync(document);

        var renamed = Path.Combine(ctx.Folder("docs"), "b.txt");
        Assert.Equal(renamed, document.FilePath);
        Assert.False(document.IsModified);
        Assert.Equal("b.txt", document.DisplayName);
        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(path));
        Assert.Same(document, ctx.Vm.Tabs.Single(tab => ReferenceEquals(tab, document)));
        Assert.Single(ctx.Vm.Tabs, tab => MainWindowViewModel.GetTabFilePath(tab) == renamed);
    });

    [Fact]
    public void RenamingAModifiedDocumentKeepsTextUndoAndSavesOnlyToTheNewPath() => Run(async ctx =>
    {
        var path = ctx.Write("docs", "a.txt", "alpha");
        await ctx.Vm.OpenPathsAsync([path]);
        var document = ctx.Doc(path);
        document.Text = "alpha edited";
        ctx.Dialogs.Reply = "b.txt";

        await ctx.Vm.RenameTabFileAsync(document);

        var renamed = Path.Combine(ctx.Folder("docs"), "b.txt");
        Assert.Equal("alpha edited", document.Text);
        Assert.True(document.IsModified);
        Assert.True(document.CanUndo);

        ctx.Vm.SelectedTab = document;
        await ctx.Vm.SaveCommand.ExecuteAsync(null);

        Assert.False(document.IsModified);
        Assert.Equal("alpha edited", File.ReadAllText(renamed));
        Assert.False(File.Exists(path));
    });

    [Fact]
    public void StaleNotificationForTheOldPathDoesNotMarkTheRenamedDocumentDeleted() => Run(async ctx =>
    {
        var path = ctx.Write("docs", "a.txt", "alpha");
        await ctx.Vm.OpenPathsAsync([path]);
        var document = ctx.Doc(path);
        ctx.Dialogs.Reply = "b.txt";
        await ctx.Vm.RenameTabFileAsync(document);

        await ctx.Vm.ProcessExternalFileChangeAsync(path);
        await ctx.Vm.ProcessExternalFileChangeAsync(document.FilePath!);

        Assert.False(document.IsModified);
        Assert.DoesNotContain("削除", ctx.Vm.StatusMessage);
    });

    [Fact]
    public void RenamingAFolderRelocatesEveryTabUnderItAndKeepsPreviewTabsPreview() => Run(async ctx =>
    {
        var first = ctx.Write("docs/sub", "one.txt", "1");
        var second = ctx.Write("docs/sub", "two.txt", "2");
        var outside = ctx.Write("docs", "three.txt", "3");
        await ctx.Vm.OpenPathsAsync([first, outside]);
        await ctx.Vm.OpenFolderFileAsync(second, permanent: false);
        ctx.Doc(first).Text = "1 edited";
        ctx.Dialogs.Reply = "renamed";

        await ctx.Vm.RenamePathAsync(Path.Combine(ctx.Folder("docs"), "sub"));

        var folder = Path.Combine(ctx.Folder("docs"), "renamed");
        var one = ctx.Doc(Path.Combine(folder, "one.txt"));
        var two = ctx.Doc(Path.Combine(folder, "two.txt"));
        Assert.True(one.IsModified);
        Assert.Equal("1 edited", one.Text);
        Assert.True(two.IsPreview);
        Assert.Equal(outside, ctx.Doc(outside).FilePath);
        Assert.Equal(ctx.Vm.Tabs.Count, ctx.Vm.Tabs.Select(tab => MainWindowViewModel.GetTabFilePath(tab) ?? tab.TabTitle)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
    });

    [Fact]
    public void DeletingClosesPristineDocumentsAndKeepsModifiedOnesAsMissingOnDisk() => Run(async ctx =>
    {
        var pristine = ctx.Write("docs", "a.txt", "a");
        var edited = ctx.Write("docs", "b.txt", "b");
        await ctx.Vm.OpenPathsAsync([pristine, edited]);
        ctx.Doc(edited).Text = "b edited";

        await ctx.Vm.DeleteTabFileAsync(ctx.Doc(pristine));
        await ctx.Vm.DeleteTabFileAsync(ctx.Doc(edited));

        Assert.DoesNotContain(ctx.Vm.Documents, document => document.FilePath == pristine);
        var kept = ctx.Doc(edited);
        Assert.True(kept.IsModified);
        Assert.Equal("b edited", kept.Text);
        Assert.Contains("ディスク上から削除", ctx.Vm.StatusMessage);
        Assert.Equal([pristine, edited], ctx.Shell.TrashedPaths);
        Assert.Empty(ctx.Vm.RecentClosedTabPaths);
    });

    [Fact]
    public void RenamingMovesCaretPositionsAndSessionPathsAndClosedHistory() => Run(async ctx =>
    {
        ctx.Vm.Options.RestoreCaretPosition = true;
        var open = ctx.Write("docs/sub", "open.txt", "0123456789");
        var closed = ctx.Write("docs/sub", "closed.txt", "x");
        await ctx.Vm.OpenPathsAsync([open, closed]);
        ctx.Doc(open).CaretIndex = 4;
        await ctx.Vm.Tabs.OfType<DocumentViewModel>().Single(d => d.FilePath == closed).CloseTabCommand.ExecuteAsync(null);
        ctx.Vm.Options.Settings.CaretPositions[closed] = 1;
        ctx.Dialogs.Reply = "moved";

        await ctx.Vm.RenamePathAsync(Path.Combine(ctx.Folder("docs"), "sub"));

        var folder = Path.Combine(ctx.Folder("docs"), "moved");
        var positions = ctx.Vm.Options.Settings.CaretPositions;
        Assert.Equal(4, positions[Path.Combine(folder, "open.txt")]);
        Assert.Equal(1, positions[Path.Combine(folder, "closed.txt")]);
        Assert.DoesNotContain(positions.Keys, key => key.Contains("sub"));
        Assert.Contains(ctx.Vm.CaptureSession().Tabs, tab => tab.FilePath == Path.Combine(folder, "open.txt"));
        Assert.Equal([Path.Combine(folder, "closed.txt")], ctx.Vm.RecentClosedTabPaths);
    });

    [Fact]
    public void PastingAFolderIntoItselfOrOntoAnOpenTabNameIsRefusedBeforeTheShellRuns() => Run(async ctx =>
    {
        var inside = ctx.Write("docs/sub", "in.txt", "i");
        var existing = ctx.Write("target", "same.txt", "old");
        var source = ctx.Write("docs", "same.txt", "new");
        await ctx.Vm.OpenPathsAsync([existing]);

        await ctx.Shell.SetFileClipboardAsync([Path.Combine(ctx.Folder("docs"), "sub")], isCut: true);
        await ctx.Vm.PasteIntoFolderAsync(Path.Combine(ctx.Folder("docs"), "sub"));
        var child = Path.GetDirectoryName(ctx.Write("docs/sub/child", "c.txt", "c"))!;
        await ctx.Shell.SetFileClipboardAsync([Path.Combine(ctx.Folder("docs"), "sub")], isCut: true);
        await ctx.Vm.PasteIntoFolderAsync(child);
        await ctx.Shell.SetFileClipboardAsync([Path.Combine(ctx.Folder("docs"), "sub")], isCut: false);
        await ctx.Vm.PasteIntoFolderAsync(child);
        await ctx.Shell.SetFileClipboardAsync([source], isCut: true);
        await ctx.Vm.PasteIntoFolderAsync(ctx.Folder("target"));

        Assert.Empty(ctx.Shell.CallsOf(FakeShellFileService.TransferOperation));
        Assert.Equal(4, ctx.Dialogs.Errors.Count);
        Assert.True(File.Exists(source));
    });

    [Fact]
    public void CopyPasteLeavesOpenTabsCaretPositionsAndClosedHistoryUntouched() => Run(async ctx =>
    {
        ctx.Vm.Options.RestoreCaretPosition = true;
        var path = ctx.Write("docs", "a.txt", "0123456789");
        var closed = ctx.Write("docs", "closed.txt", "x");
        Directory.CreateDirectory(Path.Combine(ctx.Root, "other"));
        await ctx.Vm.OpenPathsAsync([path, closed]);
        var document = ctx.Doc(path);
        document.Text = "edited";
        document.CaretIndex = 3;
        await ctx.Doc(closed).CloseTabCommand.ExecuteAsync(null);
        ctx.Vm.Options.Settings.CaretPositions[path] = 3;
        var tabCount = ctx.Vm.Tabs.Count;
        var history = ctx.Vm.RecentClosedTabPaths.ToArray();
        var positions = ctx.Vm.Options.Settings.CaretPositions.ToArray();

        await ctx.Shell.SetFileClipboardAsync([path], isCut: false);
        await ctx.Vm.PasteIntoFolderAsync(ctx.Folder("docs"));
        await ctx.Vm.PasteIntoFolderAsync(ctx.Folder("other"));

        Assert.Equal(path, document.FilePath);
        Assert.True(document.IsModified);
        Assert.Equal(tabCount, ctx.Vm.Tabs.Count);
        Assert.Equal(history, ctx.Vm.RecentClosedTabPaths);
        Assert.Equal(positions, ctx.Vm.Options.Settings.CaretPositions.ToArray());
        Assert.Single(ctx.Vm.Documents, d => d.FilePath == path);
    });

    [Fact]
    public void SavingWhileAShellOperationRelocatesTheDocumentWritesOnlyToTheNewPath() => Run(async ctx =>
    {
        var path = ctx.Write("docs", "a.txt", "alpha");
        var renamed = Path.Combine(ctx.Folder("docs"), "b.txt");
        await ctx.Vm.OpenPathsAsync([path]);
        var document = ctx.Doc(path);
        document.Text = "alpha edited";
        ctx.Vm.SelectedTab = document;
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();

        var operation = ctx.Vm.RunShellOperationAsync([path], async () =>
        {
            File.Move(path, renamed);
            started.SetResult();
            await release.Task;
            return ShellOperationResult.Success([new ShellTransfer(path, renamed)]);
        }, detachPdfs: true);
        await started.Task;
        var save = ctx.Vm.SaveCommand.ExecuteAsync(null);
        release.SetResult();
        await operation;
        await save;

        Assert.Equal(renamed, document.FilePath);
        Assert.False(File.Exists(path));
        Assert.Equal("alpha edited", File.ReadAllText(renamed));
        Assert.False(document.IsModified);
    });

    [Fact]
    public void PartialFailureStillRelocatesWhatWasMovedAndLeavesTheClipboard() => Run(async ctx =>
    {
        var moved = ctx.Write("docs", "a.txt", "a");
        var blocked = ctx.Write("docs", "b.txt", "b");
        ctx.Write("target", "b.txt", "already there");
        await ctx.Vm.OpenPathsAsync([moved, blocked]);
        await ctx.Shell.SetFileClipboardAsync([moved, blocked], isCut: true);

        await ctx.Vm.PasteIntoFolderAsync(ctx.Folder("target"));

        Assert.Equal(Path.Combine(ctx.Folder("target"), "a.txt"), ctx.Doc(Path.Combine(ctx.Folder("target"), "a.txt")).FilePath);
        Assert.Equal(blocked, ctx.Doc(blocked).FilePath);
        Assert.Single(ctx.Dialogs.Errors);
        Assert.NotNull(ctx.Shell.Clipboard);
        Assert.Single(ctx.Vm.Documents, document => document.FilePath is not null && Path.GetFileName(document.FilePath) == "a.txt");
    });

    [Fact]
    public void SuccessfulCutPasteMovesTabsAndClearsTheClipboard() => Run(async ctx =>
    {
        var path = ctx.Write("docs", "a.txt", "a");
        Directory.CreateDirectory(Path.Combine(ctx.Root, "target"));
        await ctx.Vm.OpenPathsAsync([path]);
        await ctx.Shell.SetFileClipboardAsync([path], isCut: true);

        await ctx.Vm.PasteIntoFolderAsync(ctx.Folder("target"));

        Assert.Equal(Path.Combine(ctx.Folder("target"), "a.txt"), ctx.Vm.Documents.Single(d => d.FilePath is not null).FilePath);
        Assert.Null(ctx.Shell.Clipboard);
    });

    [Fact]
    public void RenameRejectsInvalidAndExistingNamesWithoutTouchingTheShell() => Run(async ctx =>
    {
        var path = ctx.Write("docs", "a.txt", "a");
        ctx.Write("docs", "taken.txt", "t");
        await ctx.Vm.OpenPathsAsync([path]);

        ctx.Dialogs.Reply = "bad/name";
        await ctx.Vm.RenamePathAsync(path);
        ctx.Dialogs.Reply = "taken.txt";
        await ctx.Vm.RenamePathAsync(path);

        Assert.Empty(ctx.Shell.CallsOf(FakeShellFileService.RenameOperation));
        Assert.Equal(2, ctx.Dialogs.Errors.Count);
        Assert.Equal(path, ctx.Doc(path).FilePath);
    });

    [Fact]
    public void FailedRenameReportsTheErrorAndKeepsTheTab() => Run(async ctx =>
    {
        var path = ctx.Write("docs", "a.txt", "a");
        await ctx.Vm.OpenPathsAsync([path]);
        ctx.Dialogs.Reply = "b.txt";
        ctx.Shell.Inject(FakeShellFileService.RenameOperation, ShellOperationOutcome.Failed, "拒否");

        await ctx.Vm.RenamePathAsync(path);

        Assert.Equal(path, ctx.Doc(path).FilePath);
        Assert.Equal(["拒否"], ctx.Dialogs.Errors);
    });

    [Fact]
    public void CreatingAFileOpensItAndCreatingAFolderDoesNotOpenATab() => Run(async ctx =>
    {
        var anchor = ctx.Write("docs", "a.txt", "a");

        ctx.Dialogs.Reply = "new.txt";
        await ctx.Vm.CreateItemAsync(anchor, isFolder: false);
        ctx.Dialogs.Reply = "newdir";
        await ctx.Vm.CreateItemAsync(ctx.Folder("docs"), isFolder: true);

        Assert.Contains(ctx.Vm.Documents, document => document.FilePath == Path.Combine(ctx.Folder("docs"), "new.txt"));
        Assert.True(Directory.Exists(Path.Combine(ctx.Folder("docs"), "newdir")));
        Assert.Equal(1, ctx.Vm.Documents.Count(document => document.FilePath is not null));
    });

    private void Run(Func<Context, Task> body) => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var root = Path.Combine(storage.Path, "work");
        Directory.CreateDirectory(root);
        var shell = new FakeShellFileService(Path.Combine(storage.Path, "trash"));
        var dialogs = new StubDialogs();
        var vm = new MainWindowViewModel(new DocumentFileService(), dialogs, new AppSettings(), shellFiles: shell);
        SingleThreadedAsync.Run(() => body(new Context(root, vm, shell, dialogs)));
    });

    private sealed class Context(string root, MainWindowViewModel vm, FakeShellFileService shell, StubDialogs dialogs)
    {
        public string Root { get; } = root;

        public MainWindowViewModel Vm { get; } = vm;

        public FakeShellFileService Shell { get; } = shell;

        public StubDialogs Dialogs { get; } = dialogs;

        public string Folder(string relative) => Path.GetFullPath(Path.Combine(Root, relative));

        public string Write(string folder, string name, string text)
        {
            var directory = Path.Combine(Root, folder);
            Directory.CreateDirectory(directory);
            var path = Path.GetFullPath(Path.Combine(directory, name));
            File.WriteAllText(path, text);
            return path;
        }

        public DocumentViewModel Doc(string path)
            => Vm.Documents.Single(document => string.Equals(document.FilePath, path, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StubDialogs : IEditorDialogService
    {
        public string? Reply { get; set; }

        public List<string> Errors { get; } = [];

        public Task<IReadOnlyList<string>> PickOpenPathsAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);

        public Task<UnsavedDocumentDecision> ConfirmUnsavedAsync(string documentName)
            => Task.FromResult(UnsavedDocumentDecision.Discard);

        public Task ShowErrorAsync(string title, string message)
        {
            Errors.Add(message);
            return Task.CompletedTask;
        }

        public Task<int?> PickLineNumberAsync(int currentLine, int maximumLine) => Task.FromResult<int?>(null);

        public Task<string?> PromptTextAsync(string title, string message, string initialText)
            => Task.FromResult(Reply);

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<GrepQuery?> PickGrepQueryAsync(GrepQuery initial) => Task.FromResult<GrepQuery?>(null);

        public Task CheckForUpdatesAsync(bool manually) => Task.CompletedTask;

        public Task CopyTextAsync(string text) => Task.CompletedTask;
    }
}
