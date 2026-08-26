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

        Assert.True(viewModel.ShowLineNumbers);
        Assert.False(viewModel.WordWrap);
        Assert.False(viewModel.ShowWhitespace);
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

    private static MainWindowViewModel CreateViewModel(FakeDialogService dialogs)
        => new(new FakeFileService(), dialogs);

    private sealed class FakeFileService : IDocumentFileService
    {
        public Task<TextDocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(new TextDocumentContent(string.Empty, DocumentEncoding.Utf8, Environment.NewLine));

        public Task WriteAsync(
            string path,
            TextDocumentContent content,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeDialogService : IEditorDialogService
    {
        public int? RequestedLine { get; init; }

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

        public Task ConfigureFileAssociationsAsync()
            => Task.CompletedTask;

        public Task CheckForUpdatesAsync(bool manually)
            => Task.CompletedTask;
    }
}
