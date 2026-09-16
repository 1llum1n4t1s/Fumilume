using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class CsvOperationAsyncTests(HeadlessAppFixture fixture)
{
    [Fact]
    public void SmallStructurePreparationCompletesSynchronouslyAndAppliesAfterAwait() => fixture.Run(() =>
        SingleThreadedAsync.Run(async () =>
    {
        const string source = "a,b\nc,d";
        var document = LoadCsv(source, DocumentNewLines.Lf);

        var preparation = document.PrepareCsvStructureEditAsync(
            source,
            CsvTableOperation.InsertColumns,
            index: 1,
            count: 1,
            TestContext.Current.CancellationToken);

        Assert.True(preparation.IsCompletedSuccessfully);
        var edit = await preparation;
        Assert.NotNull(edit);
        Assert.True(document.TryApplyPreparedCsvEdit(edit!));
        Assert.Equal("a,,b\nc,,d", document.Text);

        document.EditorDocument.UndoStack.Undo();
        Assert.Equal(source, document.Text);
    }));

    [Fact]
    public void PreparedRangeEditIsDiscardedWhenSourceChangesAfterAwait() => fixture.Run(() =>
        SingleThreadedAsync.Run(async () =>
    {
        const string source = "clear,keep\nclear,keep";
        var document = LoadCsv(source, DocumentNewLines.Lf);
        var edit = await document.PrepareCsvClearAsync(
            source,
            new CsvCellRange(0, 0, 2, 1),
            TestContext.Current.CancellationToken);
        Assert.NotNull(edit);

        document.Text = "newer,value";

        Assert.False(document.TryApplyPreparedCsvEdit(edit!));
        Assert.Equal("newer,value", document.Text);
    }));

    [Fact]
    public void PreparedPasteIsDiscardedWhenDocumentNewLineChangesAfterAwait() => fixture.Run(() =>
        SingleThreadedAsync.Run(async () =>
    {
        const string source = "a";
        var document = LoadCsv(source, DocumentNewLines.Lf);
        var edit = await document.PrepareCsvPasteAsync(
            source,
            row: 0,
            column: 0,
            "x\ty\n1\t2",
            TestContext.Current.CancellationToken);
        Assert.NotNull(edit);

        document.NewLine = DocumentNewLines.CrLf;

        Assert.False(document.TryApplyPreparedCsvEdit(edit!));
        Assert.Equal(source, document.Text);
    }));

    [Fact]
    public void CanceledPreparationDoesNotMutateTheDocument() => fixture.Run(() =>
        SingleThreadedAsync.Run(async () =>
    {
        var source = new string('x', 4 * 1024 * 1024);
        var document = LoadCsv(source, DocumentNewLines.Lf);
        using var cancellation = new CancellationTokenSource();

        var preparation = document.PrepareCsvClearAsync(
            source,
            new CsvCellRange(0, 0, 1, 1),
            cancellation.Token);
        Assert.False(preparation.IsCompleted);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await preparation);
        Assert.Equal(source, document.Text);
        Assert.False(document.CanUndo);
    }));

    [Fact]
    public void LargeRangeCopyRunsAsBackgroundWork() => fixture.Run(() =>
        SingleThreadedAsync.Run(async () =>
    {
        var source = new string('x', 4 * 1024 * 1024);
        var document = LoadCsv(source, DocumentNewLines.Lf);

        var copy = document.GetCsvCellRangeTextAsync(
            source,
            new CsvCellRange(0, 0, 1, 1),
            TestContext.Current.CancellationToken);

        Assert.False(copy.IsCompleted);
        Assert.Equal(source, await copy);
        Assert.Equal(source, document.Text);
    }));

    private static DocumentViewModel LoadCsv(string source, string newLine)
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Load(
            @"C:\tmp\data.csv",
            new TextDocumentContent(source, DocumentEncoding.Utf8, newLine));
        return document;
    }
}
