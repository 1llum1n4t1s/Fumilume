using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

public sealed class EditorFeaturesTests
{
    [Fact]
    public void LineNumbersAreVisibleByDefault()
    {
        var viewModel = CreateViewModel(new FakeDialogService());

        Assert.True(viewModel.Options.ShowLineNumbers);
        Assert.False(viewModel.Options.WordWrap);
        Assert.False(viewModel.Options.ShowSpaces);
        Assert.False(viewModel.Options.ShowTabs);
        Assert.False(viewModel.Options.ShowEndOfLine);
    }

    [Fact]
    public void UndoHistoryBelongsToEachDocument()
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);

        document.Text = "編集後";
        document.EditorDocument.UndoStack.Undo();

        Assert.Equal(string.Empty, document.Text);
        Assert.False(document.IsModified);
    }

    [Fact]
    public async Task GoToLineMovesCaretToRequestedLogicalLine()
    {
        var dialogs = new FakeDialogService { RequestedLine = 3 };
        var viewModel = CreateViewModel(dialogs);
        var document = Assert.IsType<DocumentViewModel>(viewModel.SelectedDocument);
        document.Text = "one\r\ntwo\r\nthree";

        await viewModel.GoToLineCommand.ExecuteAsync(null);

        Assert.Equal(document.Text.IndexOf("three", StringComparison.Ordinal), document.CaretIndex);
        Assert.Equal("行 3、列 1", document.LineColumnText);
        Assert.Equal("行 3 へ移動しました", viewModel.StatusMessage);
    }

    [Theory]
    [InlineData(EditorCommandId.SortCsvAscending, "b,2\na,10\nname,value")]
    [InlineData(EditorCommandId.SortCsvDescending, "name,value\na,10\nb,2")]
    [InlineData(EditorCommandId.SortCsvAscendingWithHeader, "name,value\nb,2\na,10")]
    [InlineData(EditorCommandId.SortCsvDescendingWithHeader, "name,value\na,10\nb,2")]
    public async Task CsvPaletteSortUsesRequestedColumnAndSupportsUndo(EditorCommandId command, string expected)
    {
        var viewModel = CreateViewModel(new FakeDialogService { Answer = "2" });
        var document = viewModel.SelectedDocument!;
        const string source = "name,value\na,10\nb,2";
        document.Load(@"C:\tmp\sort.csv", new TextDocumentContent(source, DocumentEncoding.Utf8, "\n"));
        document.IsCsvPreview = true;
        Assert.True(viewModel.RunEditorCommandCommand.CanExecute(command));
        viewModel.OpenCommandPaletteCommand.Execute(null);
        Assert.Contains(viewModel.CommandPaletteResults, entry => entry.Title == EditorCommandCatalog.TitleOf(command));
        await viewModel.RunEditorCommandCommand.ExecuteAsync(command);
        Assert.Equal(expected, document.Text);
        Assert.True(document.IsCsvPreview);
        if (expected != source)
        {
            document.EditorDocument.UndoStack.Undo();
            Assert.Equal(source, document.Text);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("invalid")]
    [InlineData("999")]
    public async Task CsvSortRejectsCanceledOrInvalidColumn(string? answer)
    {
        var viewModel = CreateViewModel(new FakeDialogService { Answer = answer });
        var document = viewModel.SelectedDocument!;
        document.Load(@"C:\tmp\sort.csv", new TextDocumentContent("b\na", DocumentEncoding.Utf8, "\n"));
        await viewModel.RunEditorCommandCommand.ExecuteAsync(EditorCommandId.SortCsvAscending);
        Assert.Equal("b\na", document.Text);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public async Task CsvSortRejectsChangesWhileColumnDialogIsOpen()
    {
        var dialogs = new FakeDialogService { Answer = "1" };
        var viewModel = CreateViewModel(dialogs);
        var document = viewModel.SelectedDocument!;
        document.Load(@"C:\tmp\sort.csv", new TextDocumentContent("b\na", DocumentEncoding.Utf8, "\n"));
        dialogs.OnPrompt = () => { document.Text = "changed"; document.Text = "b\na"; };
        await viewModel.RunEditorCommandCommand.ExecuteAsync(EditorCommandId.SortCsvAscending);
        Assert.Equal("b\na", document.Text);
        document.MarkSaved(@"C:\tmp\sort.txt");
        Assert.False(viewModel.RunEditorCommandCommand.CanExecute(EditorCommandId.SortCsvAscending));
    }

    private static MainWindowViewModel CreateViewModel(FakeDialogService dialogs)
        => new(new FakeFileService(), dialogs);

    private sealed class FakeFileService : IDocumentFileService
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

    private sealed class FakeDialogService : IEditorDialogService
    {
        public int? RequestedLine { get; init; }
        public string? Answer { get; init; }
        public Action? OnPrompt { get; set; }

        public Task<IReadOnlyList<string>> PickOpenPathsAsync()
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSavePathAsync(string suggestedFileName)
            => Task.FromResult<string?>(null);

        public Task<UnsavedDocumentDecision> ConfirmUnsavedAsync(string documentName)
            => Task.FromResult(UnsavedDocumentDecision.Cancel);

        public Task ShowErrorAsync(string title, string message)
            => Task.CompletedTask;

        public Task<int?> PickLineNumberAsync(int currentLine, int maximumLine)
            => Task.FromResult(RequestedLine);

        public Task<string?> PromptTextAsync(string title, string message, string initialText)
        {
            OnPrompt?.Invoke();
            return Task.FromResult(Answer);
        }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<GrepQuery?> PickGrepQueryAsync(GrepQuery initial) => Task.FromResult<GrepQuery?>(null);

        public Task CheckForUpdatesAsync(bool manually)
            => Task.CompletedTask;
    }
}
