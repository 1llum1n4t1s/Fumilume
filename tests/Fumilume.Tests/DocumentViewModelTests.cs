using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

public sealed class DocumentViewModelTests
{
    [Fact]
    public void CsvPreviewCacheRejectsLateResultsAndInvalidatesOnUndo()
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        const string source = "a,b";
        document.Load(@"C:\tmp\cache.csv", new TextDocumentContent(source, DocumentEncoding.Utf8, "\n"));
        var parsed = document.GetCsvPreview(source);
        Assert.Same(parsed, document.GetCsvPreview(source));
        Assert.True(document.TryUpdateCsvCell(source, 0, 0, "new"));
        Assert.False(document.TryGetCsvPreview(source, out _));
        document.CacheCsvPreview(source, parsed);
        Assert.False(document.TryGetCsvPreview(source, out _));
        var updated = document.GetCsvPreview(document.Text);
        Assert.Equal("new", updated.Rows[0][0]);
        document.EditorDocument.UndoStack.Undo();
        Assert.False(document.TryGetCsvPreview("new,b", out _));
        Assert.Equal("a", document.GetCsvPreview(document.Text).Rows[0][0]);
    }

    [Fact]
    public void CachedCsvCellUpdateDoesNotMaterializeThirtyThousandHiddenRows()
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        var source = string.Join('\n', Enumerable.Repeat("a,b", 30_000));
        document.Load(@"C:\tmp\large.csv", new TextDocumentContent(source, DocumentEncoding.Utf8, "\n"));
        document.CacheCsvPreview(source, CsvDocumentParser.Parse(source, TestContext.Current.CancellationToken));
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(document.TryUpdateCsvCell(source, 0, 0, "new"));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 2_000_000, $"単一セルの更新で {allocated:N0} bytes を確保しました");
        Assert.StartsWith("new,b\na,b", document.Text);
        Assert.EndsWith("a,b", document.Text);
        document.EditorDocument.UndoStack.Undo();
        Assert.Equal(source, document.Text);
    }

    [Fact]
    public void CsvCellUpdateRejectsUnterminatedQuoteBeyondTheDisplayedRows()
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        var source = string.Join('\n', Enumerable.Repeat("a,b", CsvDocumentParser.MaxPreviewRows)) + "\n\"broken";
        document.Load(@"C:\tmp\broken.csv", new TextDocumentContent(source, DocumentEncoding.Utf8, "\n"));
        Assert.True(document.GetCsvPreview(source).HasUnterminatedQuotedField);
        Assert.False(document.TryUpdateCsvCell(source, 0, 0, "new"));
        Assert.Equal(source, document.Text);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void CsvCellUpdateReplacesOnlyTheSourceFieldAndSupportsUndoRedo()
    {
        const string source = "first,\"old\",third\r\n,tail,\r\n";
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Load(
            @"C:\tmp\data.csv",
            new TextDocumentContent(source, DocumentEncoding.ShiftJis, DocumentNewLines.CrLf));

        var updated = document.TryUpdateCsvCell(source, 0, 1, "new,\"line\ncontinued");

        Assert.True(updated);
        Assert.Equal("first,\"new,\"\"line\r\ncontinued\",third\r\n,tail,\r\n", document.Text);
        Assert.Equal(DocumentEncoding.ShiftJis, document.Encoding);
        Assert.Equal(DocumentNewLines.CrLf, document.NewLine);
        Assert.True(document.IsModified);
        document.EditorDocument.UndoStack.Undo();
        Assert.Equal(source, document.Text);
        Assert.False(document.IsModified);
        document.EditorDocument.UndoStack.Redo();
        Assert.Equal("first,\"new,\"\"line\r\ncontinued\",third\r\n,tail,\r\n", document.Text);
    }

    [Fact]
    public void CsvCellUpdateAcceptsExistingEmptyFieldsAndPadsMissingUnevenFields()
    {
        const string source = ",second\nonly-one-column";
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Load(
            @"C:\tmp\data.csv",
            new TextDocumentContent(source, DocumentEncoding.Utf8, DocumentNewLines.Lf));

        Assert.True(document.TryUpdateCsvCell(source, 0, 0, "first"));
        Assert.Equal("first,second\nonly-one-column", document.Text);

        var current = document.Text;
        Assert.True(document.TryUpdateCsvCell(current, 1, 1, "missing"));
        Assert.Equal("first,second\nonly-one-column,missing", document.Text);
    }

    [Fact]
    public void CsvCellUpdateCanClearAFieldAndLeavesEquivalentQuotedValueUnchanged()
    {
        const string source = "\"same\",remove";
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Load(
            @"C:\tmp\data.csv",
            new TextDocumentContent(source, DocumentEncoding.Utf8, DocumentNewLines.Lf));

        Assert.True(document.TryUpdateCsvCell(source, 0, 0, "same"));
        Assert.Equal(source, document.Text);
        Assert.False(document.CanUndo);

        Assert.True(document.TryUpdateCsvCell(source, 0, 1, string.Empty));
        Assert.Equal("\"same\",\"\"", document.Text);
    }

    [Theory]
    [InlineData("value", "\"\"")]
    [InlineData("value\r\n", "\"\"\r\n")]
    public void ClearingOnlyCellKeepsTheCsvRecord(string source, string expected)
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Load(
            @"C:\tmp\data.csv",
            new TextDocumentContent(source, DocumentEncoding.Utf8, DocumentNewLines.CrLf));

        Assert.True(document.TryUpdateCsvCell(source, 0, 0, string.Empty));
        Assert.Equal(expected, document.Text);
        Assert.Single(CsvDocumentParser.Parse(document.Text, TestContext.Current.CancellationToken).Rows);
        Assert.Equal(string.Empty, CsvDocumentParser.Parse(document.Text, TestContext.Current.CancellationToken).Rows[0][0]);
    }

    [Fact]
    public void EquivalentCellValueWithMixedNewLinesDoesNotCreateAnUndoEntry()
    {
        const string source = "\"first\nsecond\"";
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Load(
            @"C:\tmp\data.csv",
            new TextDocumentContent(source, DocumentEncoding.Utf8, DocumentNewLines.CrLf));

        Assert.True(document.TryUpdateCsvCell(source, 0, 0, "first\nsecond"));
        Assert.Equal(source, document.Text);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void CsvCellUpdateRejectsStaleSourceOtherFormatsAndPreviewLimitOverflow()
    {
        const string source = "a,b";
        var csv = new DocumentViewModel("無題", _ => Task.CompletedTask);
        csv.Load(
            @"C:\tmp\data.csv",
            new TextDocumentContent(source, DocumentEncoding.Utf8, DocumentNewLines.Lf));

        Assert.False(csv.TryUpdateCsvCell("stale", 0, 0, "changed"));
        Assert.False(csv.TryUpdateCsvCell(source, CsvDocumentParser.MaxPreviewRows, 0, "changed"));
        Assert.False(csv.TryUpdateCsvCell(source, 0, CsvDocumentParser.MaxPreviewColumns, "changed"));
        Assert.False(csv.TryUpdateCsvCell(source, 0, 2, "changed"));
        Assert.Equal(source, csv.Text);

        var text = new DocumentViewModel("無題", _ => Task.CompletedTask);
        text.Load(
            @"C:\tmp\data.txt",
            new TextDocumentContent(source, DocumentEncoding.Utf8, DocumentNewLines.Lf));

        Assert.False(text.TryUpdateCsvCell(source, 0, 0, "changed"));
        Assert.Equal(source, text.Text);
    }

    [Fact]
    public void CsvCellUpdateSafelyReplacesMalformedQuotedFieldRange()
    {
        const string source = "a\"b,c";
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Load(
            @"C:\tmp\data.csv",
            new TextDocumentContent(source, DocumentEncoding.Utf8, DocumentNewLines.Lf));

        Assert.True(document.TryUpdateCsvCell(source, 0, 0, "fixed"));
        Assert.Equal("fixed,c", document.Text);
    }

    [Fact]
    public void CsvCellUpdateRejectsAnUnterminatedQuotedSourceWithoutPartialChanges()
    {
        const string source = "a,\"unterminated\nvalue";
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Load(
            @"C:\tmp\data.csv",
            new TextDocumentContent(source, DocumentEncoding.Utf8, DocumentNewLines.Lf));

        Assert.False(document.TryUpdateCsvCell(source, 0, 0, "changed"));
        Assert.Equal(source, document.Text);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void CsvCellUpdatePreservesBookmarksOnUntouchedRows()
    {
        const string source = "old,value\nmiddle,row\nbookmark,row";
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Load(
            @"C:\tmp\data.csv",
            new TextDocumentContent(source, DocumentEncoding.Utf8, DocumentNewLines.Lf));
        document.CaretIndex = document.GetLineStartOffset(3);
        Assert.True(document.ToggleBookmark());

        Assert.True(document.TryUpdateCsvCell(source, 0, 0, "new"));

        Assert.Equal("new,value\nmiddle,row\nbookmark,row", document.Text);
        Assert.Equal([3], document.Bookmarks.Lines);
    }

    [Fact]
    public void CsvPreviewPreservesSourceAndResetsWhenSavedAsAnotherFormat()
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Load(@"C:\tmp\data.CSV", new TextDocumentContent("001,\"a,b\"\r\n", DocumentEncoding.ShiftJis, "\r\n"));
        document.TogglePreview();
        Assert.True(document.IsCsvPreview);
        Assert.False(document.IsMarkdownPreview);
        Assert.False(document.IsEditorVisible);
        Assert.False(document.IsModified);
        Assert.Equal("001,\"a,b\"\r\n", document.Text);
        Assert.Equal(DocumentEncoding.ShiftJis, document.Encoding);
        Assert.Equal("\r\n", document.NewLine);
        document.TogglePreview();
        Assert.True(document.IsEditorVisible);
        document.TogglePreview();
        document.MarkSaved(@"C:\tmp\data.txt");
        Assert.False(document.IsCsvPreview);
        Assert.False(document.CanShowPreview);
        Assert.True(document.IsEditorVisible);
        document.TogglePreview();
        Assert.True(document.IsEditorVisible);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MetadataChangesSurviveTextUndoAndResetAfterSaving(bool encoding)
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        const string path = @"C:\tmp\metadata.txt";
        document.Load(path, new TextDocumentContent("hello", DocumentEncoding.Utf8, "\n"));
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
        Assert.Equal("hello", document.Text);
        Assert.True(document.IsModified);

        document.MarkSaved(path);
        document.EditorDocument.Insert(0, "x");
        document.EditorDocument.UndoStack.Undo();
        Assert.False(document.IsModified);
    }

    [Fact]
    public void EditingUpdatesDirtyStateAndStatistics()
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);

        document.Text = "alpha\nbeta";
        document.CaretIndex = 8;

        Assert.True(document.IsModified);
        Assert.Equal(2, document.LineCount);
        Assert.Equal(10, document.CharacterCount);
        Assert.Equal("行 2、列 3", document.LineColumnText);
        Assert.Contains("●", document.DisplayTitle);
    }

    [Fact]
    public void RestoringSavedMetadataDoesNotHideTextChanges()
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Load(@"C:\tmp\metadata.txt", new TextDocumentContent("text", DocumentEncoding.Utf8, "\n"));
        document.Encoding = DocumentEncoding.Utf8Bom;
        document.NewLine = "\r\n";
        document.Encoding = DocumentEncoding.Utf8;
        Assert.True(document.IsModified);
        document.NewLine = "\n";
        Assert.False(document.IsModified);

        document.EditorDocument.Insert(0, "x");
        document.NewLine = "\r\n";
        document.NewLine = "\n";
        Assert.True(document.IsModified);
    }

    [Theory]
    [InlineData("alpha\rbeta", 2)]
    [InlineData("alpha\r\nbeta\ngamma", 3)]
    public void StatisticsCountEverySupportedNewLine(string text, int expectedLineCount)
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask)
        {
            Text = text,
        };

        Assert.Equal(expectedLineCount, document.LineCount);
        Assert.Equal(document.EditorDocument.LineCount, document.LineCount);
    }

    [Fact]
    public void LoadingDocumentResetsDirtyStateAndKeepsMetadata()
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        var path = Path.Combine(Path.GetTempPath(), "sample.txt");

        document.Load(path, new TextDocumentContent("hello\nworld", DocumentEncoding.Utf8Bom, "\n"));

        Assert.False(document.IsModified);
        Assert.Equal("sample.txt", document.DisplayName);
        Assert.Equal("UTF-8 BOM", document.EncodingLabel);
        Assert.Equal("LF", document.NewLineLabel);
    }

    [Fact]
    public void ChangingEncodingOrNewLineMarksTheDocumentAsModified()
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Load(
            Path.Combine(Path.GetTempPath(), "metadata.txt"),
            new TextDocumentContent("hello\n", DocumentEncoding.Utf8, DocumentNewLines.Lf));

        document.Encoding = DocumentEncoding.Utf16LittleEndian;

        Assert.True(document.IsModified);
        Assert.Equal("UTF-16 LE", document.EncodingLabel);

        document.Load(
            Path.Combine(Path.GetTempPath(), "metadata.txt"),
            new TextDocumentContent("hello\n", DocumentEncoding.Utf8, DocumentNewLines.Lf));
        document.NewLine = DocumentNewLines.CrLf;

        Assert.True(document.IsModified);
        Assert.Equal("CRLF", document.NewLineLabel);
    }
}
