using Fumilume.Services;

namespace Fumilume.Tests;

public sealed class CsvDocumentParserTests
{
    [Fact]
    public void FirstRowAndUnevenRowsRemainOrdinaryData()
    {
        var document = CsvDocumentParser.Parse("name,age,city\r\nAlice,30\r\nBob,,Tokyo", TestContext.Current.CancellationToken);

        Assert.Equal(3, document.TotalRowCount);
        Assert.Equal(3, document.TotalColumnCount);
        Assert.Equal(["name", "age", "city"], document.Rows[0]);
        Assert.Equal(["Alice", "30"], document.Rows[1]);
        Assert.Equal(["Bob", "", "Tokyo"], document.Rows[2]);
        Assert.False(document.IsTruncated);
        Assert.False(document.HasUnterminatedQuotedField);
    }

    [Fact]
    public void QuotedCommasNewLinesAndEscapedQuotesAreDecoded()
    {
        var document = CsvDocumentParser.Parse(
            "\"商品,名称\",説明\r\n\"鉛筆\",\"1行目\r\n2行目\"\r\n\"彼は\"\"はい\"\"と言った\",末尾", TestContext.Current.CancellationToken);

        Assert.Collection(
            document.Rows,
            row => Assert.Equal(["商品,名称", "説明"], row),
            row => Assert.Equal(["鉛筆", "1行目\r\n2行目"], row),
            row => Assert.Equal(["彼は\"はい\"と言った", "末尾"], row));
    }

    [Fact]
    public void CellSourceRangesMatchTheOriginalFieldsIncludingEmptyValues()
    {
        const string source = "\"a,b\",second\r\nthird,";

        var document = CsvDocumentParser.Parse(source, TestContext.Current.CancellationToken);

        Assert.Equal("\"a,b\"", SourceOf(0, 0));
        Assert.Equal("second", SourceOf(0, 1));
        Assert.Equal("third", SourceOf(1, 0));
        Assert.Equal(string.Empty, SourceOf(1, 1));

        string SourceOf(int row, int column)
        {
            var range = document.CellSourceRanges[row][column];
            return source.Substring(range.Offset, range.Length);
        }
    }

    [Theory]
    [InlineData("a,b\nc,d")]
    [InlineData("a,b\rc,d")]
    [InlineData("a,b\r\nc,d")]
    public void CommonNewLineStylesSeparateRecords(string csv)
    {
        var document = CsvDocumentParser.Parse(csv, TestContext.Current.CancellationToken);

        Assert.Equal(2, document.TotalRowCount);
        Assert.Equal(["a", "b"], document.Rows[0]);
        Assert.Equal(["c", "d"], document.Rows[1]);
    }

    [Fact]
    public void EmptyFieldsBlankRowsAndTrailingSeparatorsArePreserved()
    {
        var document = CsvDocumentParser.Parse(",\n\nvalue,", TestContext.Current.CancellationToken);

        Assert.Collection(
            document.Rows,
            row => Assert.Equal(["", ""], row),
            row => Assert.Equal([""], row),
            row => Assert.Equal(["value", ""], row));
    }

    [Fact]
    public void TerminalRecordSeparatorDoesNotCreateAnExtraRow()
    {
        var document = CsvDocumentParser.Parse("one,two\r\n", TestContext.Current.CancellationToken);

        Assert.Single(document.Rows);
        Assert.Equal(["one", "two"], document.Rows[0]);
    }

    [Fact]
    public void NullAndEmptyInputsProduceAnEmptyDocument()
    {
        Assert.Empty(CsvDocumentParser.Parse(null, TestContext.Current.CancellationToken).Rows);
        Assert.Empty(CsvDocumentParser.Parse(string.Empty, TestContext.Current.CancellationToken).Rows);
    }

    [Fact]
    public void PreviewLimitsStoredRowsAndColumnsButReportsTheFullShape()
    {
        var wideRow = string.Join(',', Enumerable.Range(1, CsvDocumentParser.MaxPreviewColumns + 3));
        var csv = string.Join(
            '\n',
            Enumerable.Repeat(wideRow, CsvDocumentParser.MaxPreviewRows + 2));

        var document = CsvDocumentParser.Parse(csv, TestContext.Current.CancellationToken);

        Assert.Equal(CsvDocumentParser.MaxPreviewRows + 2, document.TotalRowCount);
        Assert.Equal(CsvDocumentParser.MaxPreviewColumns + 3, document.TotalColumnCount);
        Assert.Equal(CsvDocumentParser.MaxPreviewRows, document.Rows.Count);
        Assert.All(document.Rows, row => Assert.Equal(CsvDocumentParser.MaxPreviewColumns, row.Count));
        Assert.True(document.IsTruncated);
    }

    [Fact]
    public void UnterminatedQuotedFieldKeepsTheAvailableText()
    {
        var document = CsvDocumentParser.Parse("one,\"two\ncontinued", TestContext.Current.CancellationToken);

        Assert.Single(document.Rows);
        Assert.Equal(["one", "two\ncontinued"], document.Rows[0]);
        Assert.True(document.HasUnterminatedQuotedField);
    }

    [Fact]
    public void QuotedFieldOutsideRowLimitStillDeterminesFullShape()
    {
        var visibleRows = string.Join(
            '\n',
            Enumerable.Repeat("visible", CsvDocumentParser.MaxPreviewRows));
        var csv = visibleRows + "\n\"hidden\nline with \"\"quote\"\"\",tail\nlast";

        var document = CsvDocumentParser.Parse(csv, TestContext.Current.CancellationToken);

        Assert.Equal(CsvDocumentParser.MaxPreviewRows + 2, document.TotalRowCount);
        Assert.Equal(2, document.TotalColumnCount);
        Assert.Equal(CsvDocumentParser.MaxPreviewRows, document.Rows.Count);
        Assert.False(document.HasUnterminatedQuotedField);
    }

    [Fact]
    public void UnterminatedQuotedFieldOutsideRowLimitIsReported()
    {
        var visibleRows = string.Join(
            '\n',
            Enumerable.Repeat("visible", CsvDocumentParser.MaxPreviewRows));
        var csv = visibleRows + "\n\"hidden\ncontinued";

        var document = CsvDocumentParser.Parse(csv, TestContext.Current.CancellationToken);

        Assert.Equal(CsvDocumentParser.MaxPreviewRows + 1, document.TotalRowCount);
        Assert.Equal(CsvDocumentParser.MaxPreviewRows, document.Rows.Count);
        Assert.True(document.HasUnterminatedQuotedField);
    }

    [Fact]
    public void QuoteDetectionOutsideColumnLimitUsesDecodedFieldLength()
    {
        var visibleColumns = string.Join(
            ',',
            Enumerable.Repeat("visible", CsvDocumentParser.MaxPreviewColumns));
        var csv = visibleColumns + ",\"\"x\"\ncontinued";

        var document = CsvDocumentParser.Parse(csv, TestContext.Current.CancellationToken);

        Assert.Equal(2, document.TotalRowCount);
        Assert.Equal(CsvDocumentParser.MaxPreviewColumns + 1, document.TotalColumnCount);
        Assert.Equal(["continued"], document.Rows[1]);
        Assert.False(document.HasUnterminatedQuotedField);
    }

    [Fact]
    public void RowsOutsidePreviewLimitDoNotAllocatePerRowStorage()
    {
        var baselineCsv = string.Join(
            '\n',
            Enumerable.Repeat("value", CsvDocumentParser.MaxPreviewRows + 1));
        var largeCsv = string.Join('\n', Enumerable.Repeat("value", 50_000));
        _ = CsvDocumentParser.Parse("warmup", TestContext.Current.CancellationToken);

        var baselineAllocation = MeasureParseAllocations(baselineCsv);
        var largeAllocation = MeasureParseAllocations(largeCsv);

        Assert.True(
            largeAllocation <= baselineAllocation + 256_000,
            $"表示外の行による割り当てが多すぎます: baseline={baselineAllocation}, large={largeAllocation}");
    }

    [Fact]
    public void FieldOutsidePreviewLimitDoesNotAllocateItsDecodedValue()
    {
        var visibleColumns = string.Join(
            ',',
            Enumerable.Repeat("visible", CsvDocumentParser.MaxPreviewColumns));
        var baselineCsv = visibleColumns + ",\"" + new string('x', 1_024) + "\"";
        var largeCsv = visibleColumns + ",\"" + new string('x', 4 * 1024 * 1024) + "\"";
        _ = CsvDocumentParser.Parse("warmup", TestContext.Current.CancellationToken);

        var baselineAllocation = MeasureParseAllocations(baselineCsv);
        var largeAllocation = MeasureParseAllocations(largeCsv);

        Assert.True(
            largeAllocation <= baselineAllocation + 256_000,
            $"表示外のフィールドによる割り当てが多すぎます: baseline={baselineAllocation}, large={largeAllocation}");
    }

    [Fact]
    public void ParseObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => CsvDocumentParser.Parse("value", cancellation.Token));
    }

    [Fact]
    public void FullSourceParsingRejectsOversizedInputBeforeDecodedValueAllocation()
    {
        var source = new string('x', CsvTableEditingService.MaximumSourceLength + 1);
        _ = CsvDocumentParser.ParseSource("warmup", TestContext.Current.CancellationToken);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var document = CsvDocumentParser.ParseSource(source, TestContext.Current.CancellationToken);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Null(document);
        Assert.True(allocated <= 16_384, $"上限超過後の割り当てが多すぎます: {allocated}");
    }

    [Fact]
    public void FullSourceParsingRejectsCellCountOverTheLimitDuringShapeScan()
    {
        var source = new string(',', CsvTableEditingService.MaximumCellCount);
        _ = CsvDocumentParser.ParseSource("warmup", TestContext.Current.CancellationToken);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var document = CsvDocumentParser.ParseSource(source, TestContext.Current.CancellationToken);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Null(document);
        Assert.True(allocated <= 16_384, $"セル上限超過後の割り当てが多すぎます: {allocated}");
    }

    [Fact]
    public void FullSourceParsersObserveCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => CsvDocumentParser.ParseSource("value", cancellation.Token));
        Assert.Throws<OperationCanceledException>(
            () => CsvDocumentParser.ParseTsvSource("value", cancellation.Token));
    }

    private static long MeasureParseAllocations(string csv)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var document = CsvDocumentParser.Parse(csv, TestContext.Current.CancellationToken);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(document);
        return allocated;
    }
}
