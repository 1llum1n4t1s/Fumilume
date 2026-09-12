using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class MainWindowFileCommandTests(HeadlessAppFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConcurrentSaveAndOpenKeepOneTabForThePath(bool openFirst) => fixture.Run(() =>
    {
        const string path = @"C:\tmp\concurrent.txt";
        var gate = new TaskCompletionSource();
        var files = new RecordingFileService
        {
            ReadGate = openFirst ? gate.Task : null,
            WriteGate = openFirst ? null : gate.Task,
        };
        var viewModel = new MainWindowViewModel(files, new StubDialogService([path]));
        var document = viewModel.SelectedDocument!;
        document.Text = "unsaved";
        Task open;
        Task save;
        if (openFirst)
        {
            open = viewModel.OpenPathsAsync([path]);
            save = viewModel.SaveAsCommand.ExecuteAsync(null);
        }
        else
        {
            save = viewModel.SaveAsCommand.ExecuteAsync(null);
            open = viewModel.OpenPathsAsync([path]);
        }
        gate.SetResult();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(open.IsCompleted);
        Assert.True(save.IsCompleted);
        open.GetAwaiter().GetResult();
        save.GetAwaiter().GetResult();

        Assert.Single(viewModel.Documents, item => item.FilePath is not null);
        Assert.Equal(openFirst ? 0 : 1, files.Writes.Count);
        Assert.Equal(openFirst ? 1 : 0, files.Reads);
        Assert.Equal(openFirst, document.IsModified);
    });

    [Fact]
    public void ReloadWaitsForAnInProgressSaveBeforeReadingTheFile() => fixture.Run(() =>
        SingleThreadedAsync.Run(async () =>
    {
        const string path = @"C:\tmp\reload-during-save.txt";
        var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = new RecordingFileService
        {
            ReadText = "before",
            WriteGate = writeGate.Task,
            CommitWriteAfterGate = true,
        };
        var viewModel = new MainWindowViewModel(files, new StubDialogService([]));
        await viewModel.OpenPathsAsync([path]);
        var document = viewModel.SelectedDocument!;
        document.Text = "saved after the gate";

        var save = viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Single(files.Writes);
        var reload = viewModel.ReloadCommand.ExecuteAsync(null);

        Assert.False(reload.IsCompleted);
        writeGate.SetResult();
        await save;
        await reload;

        Assert.Equal("saved after the gate", document.Text);
        Assert.False(document.IsModified);
    }));

    [Fact]
    public void ReloadKeepsItsOriginalTargetWhenSelectionChangesWhileWaiting() => fixture.Run(() =>
        SingleThreadedAsync.Run(async () =>
    {
        const string firstPath = @"C:\tmp\reload-first.txt";
        const string secondPath = @"C:\tmp\reload-second.txt";
        var readGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = new RecordingFileService { ReadGate = readGate.Task };
        files.ReadTexts[firstPath] = "first reloaded";
        files.ReadTexts[secondPath] = "second opened";
        var viewModel = new MainWindowViewModel(files, new StubDialogService([]));
        var first = viewModel.SelectedDocument!;
        first.Load(firstPath, new TextDocumentContent("first before", DocumentEncoding.Utf8, "\n"));

        var openSecond = viewModel.OpenPathsAsync([secondPath]);
        Assert.Equal([secondPath], files.ReadPaths);
        var reloadFirst = viewModel.ReloadCommand.ExecuteAsync(null);

        Assert.False(reloadFirst.IsCompleted);
        readGate.SetResult();
        await openSecond;
        await reloadFirst;

        Assert.Equal("first reloaded", first.Text);
        Assert.Equal(secondPath, viewModel.SelectedDocument!.FilePath);
        Assert.Equal([secondPath, firstPath], files.ReadPaths);
    }));

    [Fact]
    public async Task ATextFileTooLargeForTheReaderIsRejectedWithoutAContinuePrompt()
    {
        using var storage = new TemporaryStorage();
        var path = Path.Combine(storage.Path, "too-large.txt");
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.SetLength((long)Array.MaxLength + 1);
        }
        var files = new RecordingFileService();
        var dialogs = new StubDialogService([]);
        var viewModel = new MainWindowViewModel(
            files,
            dialogs,
            new AppSettings { WarnOnLargeFile = false, LargeFileThresholdMegabytes = 4096 });

        await viewModel.OpenPathsAsync([path]);

        Assert.Equal(0, files.Reads);
        Assert.Equal(0, dialogs.Confirmations);
        Assert.Equal(1, dialogs.Errors);
        Assert.Contains("読み込める上限", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SaveAsRefusesAnotherOpenDocumentsPathWithoutWriting()
    {
        const string path = @"C:\tmp\existing.txt";
        var files = new RecordingFileService { ReadText = "disk" };
        var viewModel = new MainWindowViewModel(files, new StubDialogService([path.ToUpperInvariant()]));
        await viewModel.OpenPathsAsync([path]);
        var existing = viewModel.SelectedDocument!;
        existing.Text = "first unsaved";
        viewModel.NewDocumentCommand.Execute(null);
        var other = viewModel.SelectedDocument!;
        other.Text = "second unsaved";

        await viewModel.SaveAsCommand.ExecuteAsync(null);

        Assert.Empty(files.Writes);
        Assert.Null(other.FilePath);
        Assert.True(other.IsModified);
        Assert.Equal("first unsaved", existing.Text);
        Assert.True(existing.IsModified);
        Assert.Equal(2, viewModel.Documents.Count());
    }

    [Fact]
    public async Task SaveAsRefusesAnOpenPdfPath()
    {
        const string path = @"C:\tmp\existing.pdf";
        var files = new RecordingFileService();
        var viewModel = new MainWindowViewModel(files, new StubDialogService([path]));
        using var pdf = new PdfDocumentViewModel(path, new UnusedPdfRenderer(), _ => Task.CompletedTask);
        viewModel.Tabs.Add(pdf);
        var document = viewModel.SelectedDocument!;
        document.Text = "unsaved";

        await viewModel.SaveAsCommand.ExecuteAsync(null);

        Assert.Empty(files.Writes);
        Assert.Null(document.FilePath);
        Assert.True(document.IsModified);
        Assert.Same(pdf, viewModel.SelectedTab);
    }

    private sealed class UnusedPdfRenderer : IPdfRenderer
    {
        public int PageCount => 1;
        public Avalonia.Size GetPageSize(int pageIndex) => new(100, 100);
        public Task<Avalonia.Media.Imaging.Bitmap> RenderAsync(
            int pageIndex, double zoom, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("パス照合では描画しない");
        public void Dispose() { }
    }

    [Fact]
    public async Task SaveAllWritesEveryModifiedDocument()
    {
        var files = new RecordingFileService();
        var dialogs = new StubDialogService([@"C:\tmp\one.txt", @"C:\tmp\two.md"]);
        var viewModel = new MainWindowViewModel(files, dialogs);
        viewModel.SelectedDocument!.Text = "one";
        viewModel.NewDocumentCommand.Execute(null);
        viewModel.SelectedDocument!.Text = "two";

        await viewModel.SaveAllCommand.ExecuteAsync(null);

        Assert.Equal(2, files.Writes.Count);
        Assert.All(viewModel.Documents, document => Assert.False(document.IsModified));
        Assert.Equal("2 件の文書を保存しました", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ReloadDiscardsConfirmedChangesAndKeepsTheCaretInRange()
    {
        var files = new RecordingFileService { ReadText = "disk" };
        var dialogs = new StubDialogService([]);
        var viewModel = new MainWindowViewModel(files, dialogs);
        var document = viewModel.SelectedDocument!;
        document.MarkSaved(@"C:\tmp\note.txt");
        document.Text = "unsaved changes";
        document.CaretIndex = document.Text.Length;

        await viewModel.ReloadCommand.ExecuteAsync(null);

        Assert.Equal("disk", document.Text);
        Assert.Equal(4, document.CaretIndex);
        Assert.False(document.IsModified);
        Assert.Equal("note.txt を開き直しました", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ReloadingADeletedFileKeepsItsTextAsUnsaved()
    {
        var files = new RecordingFileService { ReadText = "disk" };
        var dialogs = new StubDialogService([]);
        var viewModel = new MainWindowViewModel(files, dialogs);
        await viewModel.OpenPathsAsync([@"C:\tmp\note.txt"]);
        var document = viewModel.SelectedDocument!;
        files.ReadException = new FileNotFoundException();

        await viewModel.ReloadCommand.ExecuteAsync(null);

        Assert.Equal("disk", document.Text);
        Assert.True(document.IsModified);
        Assert.Equal(1, dialogs.Errors);
        Assert.Contains("ディスク上から削除", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ExternalChangeReloadsAnUnmodifiedDocumentAutomatically()
    {
        const string path = @"C:\tmp\external.txt";
        var files = new RecordingFileService { ReadText = "before" };
        var dialogs = new StubDialogService([]);
        var viewModel = new MainWindowViewModel(files, dialogs);
        await viewModel.OpenPathsAsync([path]);
        var document = viewModel.SelectedDocument!;
        document.CaretIndex = document.Text.Length;
        files.ReadText = "after";

        await viewModel.ProcessExternalFileChangeAsync(path);

        Assert.Equal("after", document.Text);
        Assert.Equal(5, document.CaretIndex);
        Assert.False(document.IsModified);
        Assert.Equal(0, dialogs.Confirmations);
        Assert.Contains("外部変更を読み込みました", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ExternalChangeDoesNotDiscardUnconfirmedEdits()
    {
        const string path = @"C:\tmp\external.txt";
        var files = new RecordingFileService { ReadText = "before" };
        var dialogs = new StubDialogService([]) { ConfirmResult = false };
        var viewModel = new MainWindowViewModel(files, dialogs);
        await viewModel.OpenPathsAsync([path]);
        var document = viewModel.SelectedDocument!;
        document.Text = "local edits";
        files.ReadText = "external edits";

        await viewModel.ProcessExternalFileChangeAsync(path);

        Assert.Equal("local edits", document.Text);
        Assert.True(document.IsModified);
        Assert.Equal(1, dialogs.Confirmations);
        Assert.Contains("編集中の内容を保持", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SaveNotificationDoesNotOverwriteNewerLocalEdits()
    {
        const string path = @"C:\tmp\external.txt";
        var files = new RecordingFileService { ReadText = "before" };
        var dialogs = new StubDialogService([]);
        var viewModel = new MainWindowViewModel(files, dialogs);
        await viewModel.OpenPathsAsync([path]);
        var document = viewModel.SelectedDocument!;
        document.Text = "saved";
        await viewModel.SaveCommand.ExecuteAsync(null);
        document.Text = "newer local edits";

        await viewModel.ProcessExternalFileChangeAsync(path);

        Assert.Equal("newer local edits", document.Text);
        Assert.True(document.IsModified);
        Assert.Equal(0, dialogs.Confirmations);
    }

    [Fact]
    public void EditsMadeWhileSavingStayUnsavedAndSurviveTheWatcherNotification() => fixture.Run(() =>
        SingleThreadedAsync.Run(async () =>
    {
        const string path = @"C:\tmp\external.txt";
        var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = new RecordingFileService { ReadText = "before", WriteGate = writeGate.Task };
        var dialogs = new StubDialogService([]);
        var viewModel = new MainWindowViewModel(files, dialogs);
        await viewModel.OpenPathsAsync([path]);
        var document = viewModel.SelectedDocument!;
        document.Text = "saving snapshot";

        var save = viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Single(files.Writes);
        document.Text = "newer local edits";
        writeGate.SetResult();
        await save;

        Assert.Equal("newer local edits", document.Text);
        Assert.True(document.IsModified);
        Assert.Contains("保存中の変更は未保存", viewModel.StatusMessage);

        await viewModel.ProcessExternalFileChangeAsync(path);

        Assert.Equal("newer local edits", document.Text);
        Assert.True(document.IsModified);
        Assert.Equal(0, dialogs.Confirmations);
    }));

    [Fact]
    public async Task ExternalDeletionKeepsTheOpenDocument()
    {
        const string path = @"C:\tmp\external.txt";
        var files = new RecordingFileService { ReadText = "before" };
        var viewModel = new MainWindowViewModel(files, new StubDialogService([]));
        await viewModel.OpenPathsAsync([path]);
        var document = viewModel.SelectedDocument!;
        files.ReadException = new FileNotFoundException();

        await viewModel.ProcessExternalFileChangeAsync(path);

        Assert.Same(document, viewModel.SelectedDocument);
        Assert.Equal("before", document.Text);
        Assert.Equal(path, document.FilePath, ignoreCase: true);
        Assert.True(document.IsModified);
        Assert.Contains("ディスク上から削除", viewModel.StatusMessage);
    }

    [Fact]
    public async Task RecreatedExternalFileMatchingTheSavedContentClearsTheMissingState()
    {
        const string path = @"C:\tmp\external.txt";
        var files = new RecordingFileService { ReadText = "before" };
        var viewModel = new MainWindowViewModel(files, new StubDialogService([]));
        await viewModel.OpenPathsAsync([path]);
        var document = viewModel.SelectedDocument!;
        files.ReadException = new FileNotFoundException();
        await viewModel.ProcessExternalFileChangeAsync(path);
        Assert.True(document.IsModified);

        files.ReadException = null;
        await viewModel.ProcessExternalFileChangeAsync(path);

        Assert.Equal("before", document.Text);
        Assert.False(document.IsModified);
    }

    [Fact]
    public async Task RepeatedExternalReadFailureShowsOnlyOneErrorUntilAReadSucceeds()
    {
        const string path = @"C:\tmp\external.txt";
        var files = new RecordingFileService { ReadText = "before" };
        var dialogs = new StubDialogService([]);
        var viewModel = new MainWindowViewModel(files, dialogs);
        await viewModel.OpenPathsAsync([path]);
        files.ReadException = new IOException("locked");

        Assert.False(await viewModel.ProcessExternalFileChangeAsync(path));
        Assert.False(await viewModel.ProcessExternalFileChangeAsync(path));

        Assert.Equal(1, dialogs.Errors);

        files.ReadException = null;
        files.ReadText = "after";
        Assert.True(await viewModel.ProcessExternalFileChangeAsync(path));
        Assert.Equal("after", viewModel.SelectedDocument!.Text);

        files.ReadException = new IOException("locked again");
        Assert.False(await viewModel.ProcessExternalFileChangeAsync(path));
        Assert.Equal(2, dialogs.Errors);
    }

    [Fact]
    public async Task ClosingAFailedExternalReloadAllowsAnErrorAfterTheFileIsReopened()
    {
        const string path = @"C:\tmp\external.txt";
        var files = new RecordingFileService { ReadText = "before" };
        var dialogs = new StubDialogService([]);
        var viewModel = new MainWindowViewModel(files, dialogs);
        await viewModel.OpenPathsAsync([path]);
        files.ReadException = new IOException("locked");
        Assert.False(await viewModel.ProcessExternalFileChangeAsync(path));
        Assert.Equal(1, dialogs.Errors);

        await viewModel.SelectedDocument!.CloseTabCommand.ExecuteAsync(null);
        files.ReadException = null;
        await viewModel.OpenPathsAsync([path]);
        files.ReadException = new IOException("locked again");

        Assert.False(await viewModel.ProcessExternalFileChangeAsync(path));
        Assert.Equal(2, dialogs.Errors);
    }

    [Fact]
    public async Task FirstCheckOfARestoredUnsavedDocumentOnlyRecordsTheDiskBaseline()
    {
        const string path = @"C:\tmp\restored.txt";
        var files = new RecordingFileService { ReadText = "disk baseline" };
        var dialogs = new StubDialogService([]) { ConfirmResult = false };
        var viewModel = new MainWindowViewModel(files, dialogs);
        var document = viewModel.SelectedDocument!;
        document.RestoreUnsaved(
            path,
            new TextDocumentContent("restored local edits", DocumentEncoding.Utf8, "\n"));

        await viewModel.ProcessExternalFileChangeAsync(path);

        Assert.Equal("restored local edits", document.Text);
        Assert.True(document.IsModified);
        Assert.Equal(0, dialogs.Confirmations);

        files.ReadText = "later external edits";
        await viewModel.ProcessExternalFileChangeAsync(path);

        Assert.Equal("restored local edits", document.Text);
        Assert.Equal(1, dialogs.Confirmations);
    }

    [Fact]
    public async Task OpenAndCloseKeepTheMonitoredPathListInSync()
    {
        const string path = @"C:\tmp\external.txt";
        var monitor = new RecordingFileChangeMonitor();
        var viewModel = new MainWindowViewModel(
            new RecordingFileService(),
            new StubDialogService([]),
            fileChangeMonitor: monitor);
        await viewModel.OpenPathsAsync([path]);
        var document = viewModel.SelectedDocument!;

        Assert.Contains(path, monitor.Paths, StringComparer.OrdinalIgnoreCase);

        await document.CloseTabCommand.ExecuteAsync(null);

        Assert.Empty(monitor.Paths);
    }

    [Fact]
    public void SelectLineMatchesTheSakuraLineSelectionContract()
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask)
        {
            Text = "first\r\nsecond\r\nthird",
            CaretIndex = 9,
        };

        document.SelectCurrentLine();

        Assert.Equal("second", document.SelectedText);
    }

    private sealed class RecordingFileService : IDocumentFileService
    {
        public string ReadText { get; set; } = string.Empty;
        public Exception? ReadException { get; set; }
        public Task? ReadGate { get; init; }
        public Task? WriteGate { get; init; }
        public bool CommitWriteAfterGate { get; init; }
        public int Reads { get; private set; }

        public Dictionary<string, string> ReadTexts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> ReadPaths { get; } = [];
        public List<string> Writes { get; } = [];

        public async Task<TextDocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            Reads++;
            ReadPaths.Add(path);
            if (ReadException is { } exception)
            {
                throw exception;
            }

            if (ReadGate is { } gate)
            {
                await gate;
            }
            var text = ReadTexts.TryGetValue(path, out var pathText) ? pathText : ReadText;
            return new TextDocumentContent(text, DocumentEncoding.Utf8, Environment.NewLine);
        }

        public async Task WriteAsync(
            string path,
            TextDocumentContent content,
            bool createBackup = false,
            CancellationToken cancellationToken = default)
        {
            Writes.Add(path);
            if (!CommitWriteAfterGate)
            {
                ReadText = content.Text;
            }

            if (WriteGate is { } gate)
            {
                await gate;
            }

            if (CommitWriteAfterGate)
            {
                ReadText = content.Text;
            }
        }
    }

    private sealed class RecordingFileChangeMonitor : IExternalFileChangeMonitor
    {
        public event EventHandler<ExternalFileChangedEventArgs>? FileChanged
        {
            add { }
            remove { }
        }

        public string[] Paths { get; private set; } = [];

        public void SetPaths(IEnumerable<string> paths) => Paths = [.. paths];

        public void Acknowledge(ExternalFileChangedEventArgs change) { }

        public void CheckForChanges() { }

        public void Dispose() { }
    }

    private sealed class StubDialogService(IReadOnlyList<string> savePaths) : IEditorDialogService
    {
        private int _saveIndex;

        public bool ConfirmResult { get; init; } = true;

        public int Confirmations { get; private set; }

        public int Errors { get; private set; }

        public Task<IReadOnlyList<string>> PickOpenPathsAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSavePathAsync(string suggestedFileName)
            => Task.FromResult<string?>(_saveIndex < savePaths.Count ? savePaths[_saveIndex++] : null);

        public Task<UnsavedDocumentDecision> ConfirmUnsavedAsync(string documentName)
            => Task.FromResult(UnsavedDocumentDecision.Discard);

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
