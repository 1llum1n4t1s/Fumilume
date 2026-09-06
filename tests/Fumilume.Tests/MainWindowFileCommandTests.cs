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
        public string ReadText { get; init; } = string.Empty;
        public Task? ReadGate { get; init; }
        public Task? WriteGate { get; init; }
        public int Reads { get; private set; }

        public List<string> Writes { get; } = [];

        public async Task<TextDocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (ReadGate is { } gate)
            {
                await gate;
            }
            return new TextDocumentContent(ReadText, DocumentEncoding.Utf8, Environment.NewLine);
        }

        public async Task WriteAsync(
            string path,
            TextDocumentContent content,
            bool createBackup = false,
            CancellationToken cancellationToken = default)
        {
            Writes.Add(path);
            if (WriteGate is { } gate)
            {
                await gate;
            }
        }
    }

    private sealed class StubDialogService(IReadOnlyList<string> savePaths) : IEditorDialogService
    {
        private int _saveIndex;

        public Task<IReadOnlyList<string>> PickOpenPathsAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSavePathAsync(string suggestedFileName)
            => Task.FromResult<string?>(_saveIndex < savePaths.Count ? savePaths[_saveIndex++] : null);

        public Task<UnsavedDocumentDecision> ConfirmUnsavedAsync(string documentName)
            => Task.FromResult(UnsavedDocumentDecision.Discard);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<int?> PickLineNumberAsync(int currentLine, int maximumLine) => Task.FromResult<int?>(null);

        public Task<string?> PromptTextAsync(string title, string message, string initialText)
            => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<GrepQuery?> PickGrepQueryAsync(GrepQuery initial) => Task.FromResult<GrepQuery?>(null);

        public Task CheckForUpdatesAsync(bool manually) => Task.CompletedTask;
    }
}
