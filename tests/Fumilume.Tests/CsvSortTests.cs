using System.Text;
using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class CsvSortTests(HeadlessAppFixture fixture)
{
    [Fact]
    public void SortUsesNumericValuesBeforeTextAndKeepsEqualRowsStable()
    {
        const string source = "10,first-ten\n2,two\n1e1,second-ten\napple,text";

        Assert.Equal(
            "2,two\n10,first-ten\n1e1,second-ten\napple,text",
            Sort(source, descending: false));
        Assert.Equal(
            "apple,text\n10,first-ten\n1e1,second-ten\n2,two",
            Sort(source, descending: true));
    }

    [Fact]
    public void SortKeepsEmptyAndMissingKeysLastInBothDirections()
    {
        const string source = "b,2\nmissing\nempty,\na,1";

        Assert.Equal("a,1\nb,2\nmissing\nempty,", Sort(source, column: 1, descending: false));
        Assert.Equal("b,2\na,1\nmissing\nempty,", Sort(source, column: 1, descending: true));
    }

    [Fact]
    public void SortCanPinTheFirstRecordAsAHeader()
    {
        const string source = "name,score\nb,2\na,1";

        Assert.Equal(
            "name,score\na,1\nb,2",
            Sort(source, column: 1, descending: false, keepFirstRow: true));
        Assert.Equal(
            "a,1\nb,2\nname,score",
            Sort(source, column: 1, descending: false, keepFirstRow: false));
    }

    [Fact]
    public void SortPreservesRawRecordsAndUsesTargetPositionDelimiters()
    {
        const string source = "z,\"2\"\r\n\"a,raw\",\"10\"\n\"m\nline\",1";

        Assert.Equal(
            "\"m\nline\",1\r\nz,\"2\"\n\"a,raw\",\"10\"",
            Sort(source, column: 1, descending: false));
    }

    [Fact]
    public void SortMaterializesARawEmptyRecordMovedToEndOfFile()
    {
        const string source = "z\n\na";

        Assert.Equal("a\nz\n\"\"", Sort(source, descending: false));
        var parsed = CsvDocumentParser.ParseSource(
            Sort(source, descending: false),
            TestContext.Current.CancellationToken);
        Assert.NotNull(parsed);
        Assert.Equal(3, parsed.Rows.Count);
        Assert.Empty(parsed.Rows[^1].Values[0]);
    }

    [Fact]
    public void SortMaterializesEmptyRecordsWhereSeparateCrAndLfWouldMerge()
    {
        const string source = "\nz\r\rq\nb\n";

        var sorted = Sort(source, descending: false);

        Assert.Equal("b\nq\rz\r\"\"\n\n", sorted);
        var parsed = Assert.IsType<CsvSourceDocument>(CsvDocumentParser.ParseSource(
            sorted,
            TestContext.Current.CancellationToken));
        Assert.Equal(5, parsed.Rows.Count);
        Assert.Equal(["b", "q", "z", "", ""], parsed.Rows.Select(row => row.Values[0]));
    }

    [Fact]
    public void SortIncludesRowsBeyondThePreviewLimit()
    {
        var source = string.Join(
            '\n',
            Enumerable.Range(0, CsvDocumentParser.MaxPreviewRows + 2)
                .Reverse()
                .Select(row => $"{row},value-{row}"));

        var sorted = Sort(source, descending: false);
        var parsed = Assert.IsType<CsvSourceDocument>(CsvDocumentParser.ParseSource(
            sorted,
            TestContext.Current.CancellationToken));

        Assert.Equal(CsvDocumentParser.MaxPreviewRows + 2, parsed.Rows.Count);
        Assert.Equal("0", parsed.Rows[0].Values[0]);
        Assert.Equal("1001", parsed.Rows[^1].Values[0]);
    }

    [Fact]
    public void SortRejectsSourceAndResultLimitsAndUnterminatedQuotes()
    {
        var oversized = new string('x', CsvTableEditingService.MaximumSourceLength + 1);
        Assert.False(CsvTableEditingService.TrySort(
            oversized,
            0,
            descending: false,
            keepFirstRow: false,
            out var oversizedResult,
            out var oversizedEdits,
            TestContext.Current.CancellationToken));
        Assert.Same(oversized, oversizedResult);
        Assert.Empty(oversizedEdits);

        const string malformed = "b\n\"unterminated";
        Assert.False(CsvTableEditingService.TrySort(
            malformed,
            0,
            descending: false,
            keepFirstRow: false,
            out var malformedResult,
            out var malformedEdits,
            TestContext.Current.CancellationToken));
        Assert.Same(malformed, malformedResult);
        Assert.Empty(malformedEdits);

        var maximumSource = new string('x', CsvTableEditingService.MaximumSourceLength - 3) + "\n\nz";
        Assert.False(CsvTableEditingService.TrySort(
            maximumSource,
            0,
            descending: false,
            keepFirstRow: false,
            out var maximumResult,
            out var maximumEdits,
            TestContext.Current.CancellationToken));
        Assert.Same(maximumSource, maximumResult);
        Assert.Empty(maximumEdits);
    }

    [Fact]
    public void SortPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => CsvTableEditingService.TrySort(
            "b\na",
            0,
            descending: false,
            keepFirstRow: false,
            out _,
            out _,
            cancellation.Token));
    }

    [Fact]
    public void PreparedSortRejectsAStaleDocument() => fixture.Run(() =>
        SingleThreadedAsync.Run(async () =>
    {
        const string source = "b\na";
        var document = LoadCsv(source);
        var edit = await document.PrepareCsvSortAsync(
            source,
            0,
            descending: false,
            keepFirstRow: false,
            TestContext.Current.CancellationToken);
        Assert.NotNull(edit);

        document.Text = "newer";

        Assert.False(document.TryApplyPreparedCsvEdit(edit!));
        Assert.Equal("newer", document.Text);
    }));

    [Fact]
    public void PinnedHeaderBookmarkSurvivesSortAndUndo() => fixture.Run(() =>
        SingleThreadedAsync.Run(async () =>
    {
        const string source = "name,score\nb,2\na,1";
        var document = LoadCsv(source);
        document.CaretIndex = document.GetLineStartOffset(1);
        Assert.True(document.ToggleBookmark());

        var edit = await document.PrepareCsvSortAsync(
            source,
            1,
            descending: false,
            keepFirstRow: true,
            TestContext.Current.CancellationToken);

        Assert.NotNull(edit);
        Assert.True(document.TryApplyPreparedCsvEdit(edit!));
        Assert.Equal("name,score\na,1\nb,2", document.Text);
        Assert.Equal([1], document.Bookmarks.Lines);

        document.EditorDocument.UndoStack.Undo();
        Assert.Equal(source, document.Text);
        Assert.Equal([1], document.Bookmarks.Lines);
    }));

    [Fact]
    public void MixedDelimiterEmptyRecordSortIsUndoableWithoutLosingARow() => fixture.Run(() =>
        SingleThreadedAsync.Run(async () =>
    {
        const string source = "\nz\ra\n";
        var document = LoadCsv(source);
        var edit = await document.PrepareCsvSortAsync(
            source,
            0,
            descending: false,
            keepFirstRow: false,
            TestContext.Current.CancellationToken);

        Assert.NotNull(edit);
        Assert.True(document.TryApplyPreparedCsvEdit(edit!));
        Assert.Equal("a\nz\r\"\"\n", document.Text);
        var parsed = Assert.IsType<CsvSourceDocument>(CsvDocumentParser.ParseSource(
            document.Text,
            TestContext.Current.CancellationToken));
        Assert.Equal(3, parsed.Rows.Count);

        document.EditorDocument.UndoStack.Undo();
        Assert.Equal(source, document.Text);
    }));

    [Fact]
    public void LargePreparedSortRunsInTheBackgroundAndAppliesAsOneUndo() => fixture.Run(() =>
        SingleThreadedAsync.Run(async () =>
    {
        var builder = new StringBuilder();
        for (var value = 40_000; value > 0; value--)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(",value");
        }

        var source = builder.ToString();
        var document = LoadCsv(source);
        var preparation = document.PrepareCsvSortAsync(
            source,
            0,
            descending: false,
            keepFirstRow: false,
            TestContext.Current.CancellationToken);

        Assert.False(preparation.IsCompleted);
        var edit = await preparation;
        Assert.NotNull(edit);
        Assert.True(document.TryApplyPreparedCsvEdit(edit!));
        Assert.StartsWith("000001,value\n", document.Text, StringComparison.Ordinal);
        Assert.EndsWith("\n040000,value", document.Text, StringComparison.Ordinal);
        Assert.True(document.CanUndo);

        document.EditorDocument.UndoStack.Undo();
        Assert.Equal(source, document.Text);
        Assert.False(document.CanUndo);
    }));

    private static string Sort(
        string source,
        int column = 0,
        bool descending = false,
        bool keepFirstRow = false)
    {
        Assert.True(CsvTableEditingService.TrySort(
            source,
            column,
            descending,
            keepFirstRow,
            out var edited,
            out _,
            TestContext.Current.CancellationToken));
        return edited;
    }

    private static DocumentViewModel LoadCsv(string source)
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Load(
            @"C:\tmp\data.csv",
            new TextDocumentContent(source, DocumentEncoding.Utf8, DocumentNewLines.Lf));
        return document;
    }
}
