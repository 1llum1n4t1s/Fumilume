using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

public sealed class CsvTableEditingTests
{
    [Fact]
    public void InsertAndDeleteColumnPreserveOriginalFieldsAndMixedRecordSeparators()
    {
        const string source = "\"a,b\",keep\r\nshort\nlast,\"quoted\r\nline\",tail";
        var document = LoadCsv(source, DocumentNewLines.CrLf);

        Assert.True(document.TryEditCsvStructure(source, CsvTableOperation.InsertColumns, 1));
        const string inserted = "\"a,b\",,keep\r\nshort,\nlast,,\"quoted\r\nline\",tail";
        Assert.Equal(inserted, document.Text);

        Assert.True(document.TryEditCsvStructure(inserted, CsvTableOperation.DeleteColumns, 1));
        Assert.Equal(source, document.Text);
    }

    [Fact]
    public void InsertRowsUsesDocumentNewLineOnlyForAddedSeparators()
    {
        const string source = "a,b\nc,d\re,f";
        var document = LoadCsv(source, DocumentNewLines.CrLf);

        Assert.True(document.TryEditCsvStructure(source, CsvTableOperation.InsertRows, 1, 2));

        Assert.Equal("a,b\n,\r\n,\r\nc,d\re,f", document.Text);
    }

    [Theory]
    [InlineData("a,b", "a,b\r\n,")]
    [InlineData("a,b\n", "a,b\n,")]
    public void AppendingRowsRespectsAnExistingTerminalSeparator(string source, string expected)
    {
        var document = LoadCsv(source, DocumentNewLines.CrLf);

        Assert.True(document.TryEditCsvStructure(source, CsvTableOperation.InsertRows, 1));

        Assert.Equal(expected, document.Text);
    }

    [Fact]
    public void StructuralEditIsOneUndoableChangeAndKeepsDocumentMetadata()
    {
        const string source = "first,second\r\nthird,fourth";
        var document = LoadCsv(source, DocumentNewLines.CrLf, DocumentEncoding.ShiftJis);

        Assert.True(document.TryEditCsvStructure(source, CsvTableOperation.InsertColumns, 2, 2));
        var edited = document.Text;
        Assert.Equal("first,second,,\r\nthird,fourth,,", edited);
        Assert.Equal(DocumentEncoding.ShiftJis, document.Encoding);
        Assert.Equal(DocumentNewLines.CrLf, document.NewLine);
        Assert.True(document.IsModified);

        document.EditorDocument.UndoStack.Undo();
        Assert.Equal(source, document.Text);
        Assert.False(document.IsModified);

        document.EditorDocument.UndoStack.Redo();
        Assert.Equal(edited, document.Text);
    }

    [Fact]
    public void StructuralEditPreservesBookmarksOutsideTheInsertedRows()
    {
        const string source = "bookmark,row\nlast,row";
        var document = LoadCsv(source, DocumentNewLines.Lf);
        document.CaretIndex = document.GetLineStartOffset(1);
        Assert.True(document.ToggleBookmark());

        Assert.True(document.TryEditCsvStructure(
            source,
            CsvTableOperation.InsertRows,
            index: 2));

        Assert.Equal("bookmark,row\nlast,row\n,", document.Text);
        Assert.Equal([1], document.Bookmarks.Lines);
    }

    [Fact]
    public void EmptyCsvCanBecomeAnEditableRowOrColumn()
    {
        var rowDocument = LoadCsv(string.Empty, DocumentNewLines.Lf);
        Assert.True(rowDocument.TryEditCsvStructure(string.Empty, CsvTableOperation.InsertRows, 0));
        Assert.Equal("\"\"", rowDocument.Text);
        Assert.True(rowDocument.TryUpdateCsvCell(rowDocument.Text, 0, 0, "row value"));
        Assert.Equal("row value", rowDocument.Text);

        var columnDocument = LoadCsv(string.Empty, DocumentNewLines.Lf);
        Assert.True(columnDocument.TryEditCsvStructure(string.Empty, CsvTableOperation.InsertColumns, 0, 2));
        Assert.Equal(",", columnDocument.Text);
        Assert.True(columnDocument.TryUpdateCsvCell(columnDocument.Text, 0, 1, "column value"));
        Assert.Equal(",column value", columnDocument.Text);
    }

    [Fact]
    public void InsertingAColumnPadsUnevenRowsThroughTheInsertionPosition()
    {
        const string source = "a\nb,c,d";
        var document = LoadCsv(source, DocumentNewLines.Lf);

        Assert.True(document.TryEditCsvStructure(source, CsvTableOperation.InsertColumns, 2));

        Assert.Equal("a,,\nb,c,,d", document.Text);
        var parsed = CsvDocumentParser.Parse(document.Text, TestContext.Current.CancellationToken);
        Assert.Equal(["a", "", ""], parsed.Rows[0]);
        Assert.Equal(["b", "c", "", "d"], parsed.Rows[1]);
    }

    [Fact]
    public void DeletingColumnsHandlesMissingCellsAndCanRemoveTheLastColumn()
    {
        const string source = "a\nb,c,d";
        var document = LoadCsv(source, DocumentNewLines.Lf);

        Assert.True(document.TryEditCsvStructure(source, CsvTableOperation.DeleteColumns, 1));
        Assert.Equal("a\nb,d", document.Text);

        var oneColumn = LoadCsv("a\nb", DocumentNewLines.Lf);
        Assert.True(oneColumn.TryEditCsvStructure(oneColumn.Text, CsvTableOperation.DeleteColumns, 0));
        Assert.Equal(string.Empty, oneColumn.Text);

        var emptyPrefix = LoadCsv(",value", DocumentNewLines.Lf);
        Assert.True(emptyPrefix.TryEditCsvStructure(emptyPrefix.Text, CsvTableOperation.DeleteColumns, 1));
        Assert.Equal("\"\"", emptyPrefix.Text);

        var emptySuffix = LoadCsv("value,", DocumentNewLines.Lf);
        Assert.True(emptySuffix.TryEditCsvStructure(emptySuffix.Text, CsvTableOperation.DeleteColumns, 0));
        Assert.Equal("\"\"", emptySuffix.Text);
    }

    [Fact]
    public void DeletingRowsSupportsLastRangeAndAnEmptyResult()
    {
        var document = LoadCsv("a\r\nb\nc", DocumentNewLines.Lf);

        Assert.True(document.TryEditCsvStructure(document.Text, CsvTableOperation.DeleteRows, 2));
        Assert.Equal("a\r\nb\n", document.Text);
        Assert.True(document.TryEditCsvStructure(document.Text, CsvTableOperation.DeleteRows, 0, 2));
        Assert.Equal(string.Empty, document.Text);
    }

    [Fact]
    public void ColumnOperationsIncludeRowsAndColumnsOutsidePreviewLimits()
    {
        var source = string.Join(
            '\n',
            Enumerable.Range(0, CsvDocumentParser.MaxPreviewRows + 2)
                .Select(row => $"row-{row},value-{row}"));
        var document = LoadCsv(source, DocumentNewLines.Lf);

        Assert.True(document.TryEditCsvStructure(source, CsvTableOperation.InsertColumns, 1));

        var parsed = Assert.IsType<CsvSourceDocument>(CsvDocumentParser.ParseSource(
            document.Text,
            TestContext.Current.CancellationToken));
        Assert.Equal(CsvDocumentParser.MaxPreviewRows + 2, parsed.Rows.Count);
        Assert.Equal(
            ["row-1001", "", "value-1001"],
            parsed.Rows[CsvDocumentParser.MaxPreviewRows + 1].Values);
    }

    [Theory]
    [InlineData(CsvTableOperation.InsertRows, -1, 1)]
    [InlineData(CsvTableOperation.InsertRows, 3, 1)]
    [InlineData(CsvTableOperation.DeleteRows, 2, 1)]
    [InlineData(CsvTableOperation.DeleteRows, 0, 3)]
    [InlineData(CsvTableOperation.InsertColumns, 3, 1)]
    [InlineData(CsvTableOperation.DeleteColumns, 2, 1)]
    [InlineData(CsvTableOperation.DeleteColumns, 0, 3)]
    [InlineData(CsvTableOperation.InsertColumns, 0, 0)]
    public void StructuralEditRejectsOutOfRangeRequests(
        CsvTableOperation operation,
        int index,
        int count)
    {
        const string source = "a,b\nc,d";
        var document = LoadCsv(source, DocumentNewLines.Lf);

        Assert.False(document.TryEditCsvStructure(source, operation, index, count));
        Assert.Equal(source, document.Text);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void StructuralEditRejectsStaleSnapshotsAndNonCsvDocuments()
    {
        const string source = "a,b";
        var csv = LoadCsv(source, DocumentNewLines.Lf);
        Assert.False(csv.TryEditCsvStructure("stale", CsvTableOperation.InsertRows, 0));
        Assert.Equal(source, csv.Text);

        var text = new DocumentViewModel("無題", _ => Task.CompletedTask);
        text.Load(
            @"C:\tmp\data.txt",
            new TextDocumentContent(source, DocumentEncoding.Utf8, DocumentNewLines.Lf));
        Assert.False(text.TryEditCsvStructure(source, CsvTableOperation.InsertRows, 0));
        Assert.Null(text.GetCsvSelectionText(source, rows: true, 0));
    }

    [Fact]
    public void StructuralEditRejectsAnUnterminatedQuotedFieldAndExtremeCounts()
    {
        const string malformed = "a,\"unterminated\nvalue";
        var document = LoadCsv(malformed, DocumentNewLines.Lf);

        Assert.False(document.TryEditCsvStructure(malformed, CsvTableOperation.InsertRows, 1));
        Assert.False(document.TryEditCsvStructure(malformed, CsvTableOperation.InsertColumns, 1));
        Assert.Equal(malformed, document.Text);

        var valid = LoadCsv("a", DocumentNewLines.Lf);
        Assert.False(valid.TryEditCsvStructure(
            valid.Text,
            CsvTableOperation.InsertRows,
            0,
            int.MaxValue));
        Assert.Equal("a", valid.Text);
    }

    [Fact]
    public void RowCopyUsesAllColumnsAndEscapesTsvSpecialCharacters()
    {
        const string source = "\"a\tb\",\"line\nx\",\"say \"\"hi\"\"\",comma\r\nshort";
        var document = LoadCsv(source, DocumentNewLines.CrLf);

        var copied = document.GetCsvSelectionText(source, rows: true, 0, 2);

        Assert.Equal(
            "\"a\tb\"\t\"line\nx\"\t\"say \"\"hi\"\"\"\tcomma\r\n"
            + "short\t\"\"\t\"\"\t\"\"",
            copied);
        Assert.Equal(source, document.Text);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void ColumnCopyUsesEveryRowIncludingPreviewOverflowAndCrLfRecords()
    {
        var source = string.Join(
            '\n',
            Enumerable.Range(0, CsvDocumentParser.MaxPreviewRows + 2)
                .Select(row => $"left-{row},right-{row}"));
        var document = LoadCsv(source, DocumentNewLines.Lf);

        var copied = document.GetCsvSelectionText(source, rows: false, 1);

        Assert.NotNull(copied);
        var copiedRows = copied.Split("\r\n", StringSplitOptions.None);
        Assert.Equal(CsvDocumentParser.MaxPreviewRows + 2, copiedRows.Length);
        Assert.Equal("right-0", copiedRows[0]);
        Assert.Equal("right-1001", copiedRows[^1]);
    }

    [Fact]
    public void CopyRejectsStaleEmptyAndOutOfRangeSelections()
    {
        const string source = "a,b\nc,d";
        var document = LoadCsv(source, DocumentNewLines.Lf);

        Assert.Null(document.GetCsvSelectionText("stale", rows: true, 0));
        Assert.Null(document.GetCsvSelectionText(source, rows: true, 2));
        Assert.Null(document.GetCsvSelectionText(source, rows: false, 1, 2));
        Assert.Null(document.GetCsvSelectionText(source, rows: false, 0, 0));

        var empty = LoadCsv(string.Empty, DocumentNewLines.Lf);
        Assert.Null(empty.GetCsvSelectionText(string.Empty, rows: true, 0));
        Assert.Null(empty.GetCsvSelectionText(string.Empty, rows: false, 0));
    }

    [Fact]
    public void CellRangeCopyIncludesMissingCellsAndQuotesTsvSpecialCharacters()
    {
        const string source = "a,\"b\tc\"\nshort";
        var document = LoadCsv(source, DocumentNewLines.Lf);

        var copied = document.GetCsvCellRangeText(source, new CsvCellRange(0, 0, 2, 2));

        Assert.Equal("a\t\"b\tc\"\r\nshort\t\"\"", copied);
        Assert.Equal(source, document.Text);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void EmptyCellCopyRoundTripsAsOneCellAndAsTheLastSelectedRow()
    {
        const string source = "a\n\"\"";
        var document = LoadCsv(source, DocumentNewLines.Lf);

        var emptyCell = document.GetCsvCellRangeText(source, new CsvCellRange(1, 0, 1, 1));
        var twoRows = document.GetCsvCellRangeText(source, new CsvCellRange(0, 0, 2, 1));

        Assert.Equal("\"\"", emptyCell);
        Assert.Equal("a\r\n\"\"", twoRows);

        var singleTarget = LoadCsv("old", DocumentNewLines.Lf);
        Assert.True(singleTarget.TryPasteCsvCells("old", 0, 0, emptyCell!));
        Assert.Equal("\"\"", singleTarget.Text);

        const string targetSource = "old-1\nold-2";
        var target = LoadCsv(targetSource, DocumentNewLines.Lf);
        Assert.True(target.TryPasteCsvCells(targetSource, 0, 0, twoRows!));
        Assert.Equal("a\n\"\"", target.Text);
    }

    [Fact]
    public void ClearChangesOnlyExistingValuesAndIsOneUndoableChange()
    {
        const string source = "\"keep\",remove\r\nshort,\"quoted\"";
        var document = LoadCsv(source, DocumentNewLines.CrLf);

        Assert.True(document.TryClearCsvCells(source, new CsvCellRange(0, 1, 2, 1)));
        Assert.Equal("\"keep\",\r\nshort,", document.Text);

        document.EditorDocument.UndoStack.Undo();
        Assert.Equal(source, document.Text);
        document.EditorDocument.UndoStack.Redo();
        Assert.Equal("\"keep\",\r\nshort,", document.Text);
    }

    [Fact]
    public void ClearingAlreadyEmptyAndMissingCellsIsANoOp()
    {
        const string source = "a,\nshort";
        var document = LoadCsv(source, DocumentNewLines.Lf);

        Assert.True(document.TryClearCsvCells(source, new CsvCellRange(0, 1, 2, 1)));

        Assert.Equal(source, document.Text);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void FillDownAndRightPadUnevenRowsWithoutChangingOutsideColumns()
    {
        const string source = "top,\"x,y\"\nold\nlast,keep,hidden";
        var down = LoadCsv(source, DocumentNewLines.Lf);

        Assert.True(down.TryFillCsvCells(source, new CsvCellRange(0, 0, 3, 2), down: true));
        Assert.Equal("top,\"x,y\"\ntop,\"x,y\"\ntop,\"x,y\",hidden", down.Text);

        const string rightSource = "seed,a,b\n\"q,r\",x";
        var right = LoadCsv(rightSource, DocumentNewLines.Lf);
        Assert.True(right.TryFillCsvCells(
            rightSource,
            new CsvCellRange(0, 0, 2, 3),
            down: false));
        Assert.Equal("seed,seed,seed\n\"q,r\",\"q,r\",\"q,r\"", right.Text);
    }

    [Fact]
    public void PasteParsesQuotedTsvAndAddsMissingRowsAndColumnsWithDocumentNewLines()
    {
        const string source = "\"keep\",old\r\nshort";
        var document = LoadCsv(source, DocumentNewLines.CrLf);
        const string clipboard = "one\ttwo\r\n\"three\nline\"\tfour";

        Assert.True(document.TryPasteCsvCells(source, 1, 1, clipboard));

        const string expected = "\"keep\",old\r\nshort,one,two\r\n,\"three\r\nline\",four";
        Assert.Equal(expected, document.Text);
        document.EditorDocument.UndoStack.Undo();
        Assert.Equal(source, document.Text);
        document.EditorDocument.UndoStack.Redo();
        Assert.Equal(expected, document.Text);
    }

    [Fact]
    public void PasteExpandingTheLastExistingRowKeepsItsSuffixBeforeNewRows()
    {
        const string source = "a";
        var document = LoadCsv(source, DocumentNewLines.Lf);

        Assert.True(document.TryPasteCsvCells(source, 0, 0, "x\ty\n1\t2"));

        Assert.Equal("x,y\n1,2", document.Text);
        document.EditorDocument.UndoStack.Undo();
        Assert.Equal(source, document.Text);
    }

    [Fact]
    public void CellRangeEditsPreserveBookmarksOnUntouchedRows()
    {
        const string source = "edit,here\nmiddle,keep\nbookmark,row";
        var document = LoadCsv(source, DocumentNewLines.Lf);
        document.CaretIndex = document.GetLineStartOffset(3);
        Assert.True(document.ToggleBookmark());

        Assert.True(document.TryClearCsvCells(source, new CsvCellRange(0, 0, 2, 1)));

        Assert.Equal(",here\n,keep\nbookmark,row", document.Text);
        Assert.Equal([3], document.Bookmarks.Lines);
    }

    [Fact]
    public void PasteTreatsCommaOnlyTextAsOneCellAndPreservesPreviewOverflowRows()
    {
        var source = string.Join(
            '\n',
            Enumerable.Range(0, CsvDocumentParser.MaxPreviewRows + 2)
                .Select(row => $"row-{row},keep-{row}"));
        var document = LoadCsv(source, DocumentNewLines.Lf);

        Assert.True(document.TryPasteCsvCells(source, 0, 0, "comma,value"));

        var parsed = Assert.IsType<CsvSourceDocument>(CsvDocumentParser.ParseSource(
            document.Text,
            TestContext.Current.CancellationToken));
        Assert.Equal(["comma,value", "keep-0"], parsed.Rows[0].Values);
        Assert.Equal(
            ["row-1001", "keep-1001"],
            parsed.Rows[CsvDocumentParser.MaxPreviewRows + 1].Values);
    }

    [Fact]
    public void EquivalentFillAndPasteDoNotCreateUndoEntries()
    {
        const string source = "a,b";
        var document = LoadCsv(source, DocumentNewLines.Lf);

        Assert.True(document.TryFillCsvCells(
            source,
            new CsvCellRange(0, 0, 1, 2),
            down: true));
        Assert.True(document.TryPasteCsvCells(source, 0, 0, "a\tb"));

        Assert.Equal(source, document.Text);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void RangeOperationsRejectStaleMalformedAndOverflowingRequestsWithoutPartialChanges()
    {
        const string source = "a,b\nc,d";
        var document = LoadCsv(source, DocumentNewLines.Lf);
        var validRange = new CsvCellRange(0, 0, 1, 1);

        Assert.Null(document.GetCsvCellRangeText("stale", validRange));
        Assert.False(document.TryClearCsvCells("stale", validRange));
        Assert.False(document.TryFillCsvCells("stale", validRange, down: true));
        Assert.False(document.TryPasteCsvCells("stale", 0, 0, "x"));
        Assert.Null(document.GetCsvCellRangeText(
            source,
            new CsvCellRange(0, 0, int.MaxValue, 1)));
        Assert.False(document.TryPasteCsvCells(source, 0, 0, "\"unterminated"));
        Assert.Equal(source, document.Text);
        Assert.False(document.CanUndo);

        const string malformedSource = "a,\"unterminated\nvalue";
        var malformed = LoadCsv(malformedSource, DocumentNewLines.Lf);
        Assert.Null(malformed.GetCsvCellRangeText(malformedSource, validRange));
        Assert.False(malformed.TryClearCsvCells(malformedSource, validRange));
        Assert.False(malformed.TryFillCsvCells(malformedSource, validRange, down: true));
        Assert.False(malformed.TryPasteCsvCells(malformedSource, 0, 0, "x"));
        Assert.Equal(malformedSource, malformed.Text);
    }

    [Fact]
    public void FullDocumentOperationsRejectOversizedSourcesAndClipboardBeforeParsing()
    {
        var oversized = new string('x', CsvTableEditingService.MaximumSourceLength + 1);
        var range = new CsvCellRange(0, 0, 1, 1);

        Assert.False(CsvTableEditingService.TryEdit(
            oversized,
            CsvTableOperation.InsertRows,
            0,
            1,
            "\n",
            out var edited,
            TestContext.Current.CancellationToken));
        Assert.Same(oversized, edited);
        Assert.Null(CsvTableEditingService.GetSelectionText(
            oversized,
            rows: true,
            0,
            1,
            TestContext.Current.CancellationToken));
        Assert.Null(CsvTableEditingService.GetRangeText(
            oversized,
            range,
            TestContext.Current.CancellationToken));
        Assert.False(CsvTableEditingService.TryClear(
            oversized,
            range,
            out edited,
            TestContext.Current.CancellationToken));
        Assert.Same(oversized, edited);
        Assert.False(CsvTableEditingService.TryFill(
            oversized,
            range,
            down: true,
            out edited,
            TestContext.Current.CancellationToken));
        Assert.Same(oversized, edited);
        Assert.False(CsvTableEditingService.TryPaste(
            "value",
            0,
            0,
            oversized,
            "\n",
            out edited,
            TestContext.Current.CancellationToken));
        Assert.Equal("value", edited);
    }

    [Fact]
    public void StructuralEditAllowsTheCellLimitAndRejectsTheNextCellWithoutPartialOutput()
    {
        Assert.True(CsvTableEditingService.TryEdit(
            string.Empty,
            CsvTableOperation.InsertColumns,
            0,
            CsvTableEditingService.MaximumCellCount,
            "\n",
            out var atLimit,
            TestContext.Current.CancellationToken));
        Assert.Equal(CsvTableEditingService.MaximumCellCount, atLimit.Length + 1);

        const string source = "a";
        Assert.False(CsvTableEditingService.TryEdit(
            source,
            CsvTableOperation.InsertColumns,
            0,
            CsvTableEditingService.MaximumCellCount,
            "\n",
            out var rejected,
            TestContext.Current.CancellationToken));
        Assert.Same(source, rejected);
    }

    [Fact]
    public void FillRejectsAResultBeyondTheCharacterBudgetWithoutPartialOutput()
    {
        var seed = new string('x', 1024 * 1024);
        var source = seed + "\na\nb\nc\nd\ne\nf\ng\nh";

        Assert.False(CsvTableEditingService.TryFill(
            source,
            new CsvCellRange(0, 0, 9, 1),
            down: true,
            out var edited,
            TestContext.Current.CancellationToken));

        Assert.Same(source, edited);
    }

    [Fact]
    public void PasteRejectsMaterializingMoreThanTheCellLimit()
    {
        const string source = "a";

        Assert.False(CsvTableEditingService.TryPaste(
            source,
            0,
            CsvTableEditingService.MaximumCellCount,
            "x",
            "\n",
            out var edited,
            TestContext.Current.CancellationToken));

        Assert.Same(source, edited);
    }

    [Fact]
    public void FullDocumentOperationsObserveCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var range = new CsvCellRange(0, 0, 1, 1);

        Assert.Throws<OperationCanceledException>(() =>
            CsvTableEditingService.TryEdit(
                "value",
                CsvTableOperation.InsertRows,
                0,
                1,
                "\n",
                out _,
                cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            CsvTableEditingService.GetSelectionText("value", rows: true, 0, 1, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            CsvTableEditingService.GetRangeText("value", range, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            CsvTableEditingService.TryClear("value", range, out _, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            CsvTableEditingService.TryFill("value", range, down: true, out _, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            CsvTableEditingService.TryPaste("value", 0, 0, "new", "\n", out _, cancellation.Token));
    }

    private static DocumentViewModel LoadCsv(
        string source,
        string newLine,
        DocumentEncoding encoding = DocumentEncoding.Utf8)
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Load(
            @"C:\tmp\data.csv",
            new TextDocumentContent(source, encoding, newLine));
        return document;
    }
}
