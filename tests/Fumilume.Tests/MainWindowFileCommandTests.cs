using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

public sealed class MainWindowFileCommandTests
{
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

        public List<string> Writes { get; } = [];

        public Task<TextDocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(new TextDocumentContent(ReadText, DocumentEncoding.Utf8, Environment.NewLine));

        public Task WriteAsync(
            string path,
            TextDocumentContent content,
            bool createBackup = false,
            CancellationToken cancellationToken = default)
        {
            Writes.Add(path);
            return Task.CompletedTask;
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
