using System.Text;

namespace Fumilume.Services;

public enum CsvTableOperation
{
    InsertRows,
    DeleteRows,
    InsertColumns,
    DeleteColumns,
}

internal readonly record struct CsvSourceEdit(int Offset, int Length, string Replacement);

/// <summary>CSVの元表記を保ちながら、行・列の構造編集と表形式コピーを行う。</summary>
public static class CsvTableEditingService
{
    public const int MaximumSourceLength = CsvDocumentParser.MaximumEditableSourceLength;
    public const int MaximumClipboardLength = MaximumSourceLength;
    public const int MaximumCellCount = CsvDocumentParser.MaximumEditableCellCount;
    public const int MaximumResultLength = 8 * 1024 * 1024;

    public static bool TryEdit(
        string source,
        CsvTableOperation operation,
        int index,
        int count,
        string newLine,
        out string editedSource,
        CancellationToken cancellationToken = default)
        => TryEdit(
            source,
            operation,
            index,
            count,
            newLine,
            out editedSource,
            out _,
            cancellationToken);

    internal static bool TryEdit(
        string source,
        CsvTableOperation operation,
        int index,
        int count,
        string newLine,
        out string editedSource,
        out IReadOnlyList<CsvSourceEdit> sourceEdits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(newLine);
        cancellationToken.ThrowIfCancellationRequested();

        editedSource = source;
        sourceEdits = [];
        if (source.Length > MaximumSourceLength
            || index < 0
            || count <= 0
            || count > MaximumCellCount)
        {
            return false;
        }

        var document = CsvDocumentParser.ParseSource(source, cancellationToken);
        if (document is null || document.HasUnterminatedQuotedField)
        {
            return false;
        }

        return operation switch
        {
            CsvTableOperation.InsertRows => TryInsertRows(
                source, document, index, count, newLine, out editedSource, out sourceEdits, cancellationToken),
            CsvTableOperation.DeleteRows => TryDeleteRows(
                source, document, index, count, out editedSource, out sourceEdits, cancellationToken),
            CsvTableOperation.InsertColumns => TryInsertColumns(
                source, document, index, count, out editedSource, out sourceEdits, cancellationToken),
            CsvTableOperation.DeleteColumns => TryDeleteColumns(
                source, document, index, count, out editedSource, out sourceEdits, cancellationToken),
            _ => false,
        };
    }

    public static string? GetSelectionText(
        string source,
        bool rows,
        int index,
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Length > MaximumSourceLength || index < 0 || count <= 0)
        {
            return null;
        }

        var document = CsvDocumentParser.ParseSource(source, cancellationToken);
        if (document is null || document.HasUnterminatedQuotedField)
        {
            return null;
        }

        var extent = rows ? document.Rows.Count : document.TotalColumnCount;
        if (index > extent || count > extent - index)
        {
            return null;
        }

        var range = rows
            ? new CsvCellRange(index, 0, count, document.TotalColumnCount)
            : new CsvCellRange(0, index, document.Rows.Count, count);
        return GetRangeText(document, range, cancellationToken);
    }

    /// <summary>矩形のセル範囲を、必要な値を引用符で囲んだTSVとして返す。</summary>
    public static string? GetRangeText(
        string source,
        CsvCellRange range,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Length > MaximumSourceLength)
        {
            return null;
        }

        var document = CsvDocumentParser.ParseSource(source, cancellationToken);
        if (document is null || document.HasUnterminatedQuotedField)
        {
            return null;
        }

        return GetRangeText(document, range, cancellationToken);
    }

    /// <summary>指定範囲の値だけを空にし、行列の構造は維持する。</summary>
    public static bool TryClear(
        string source,
        CsvCellRange range,
        out string editedSource,
        CancellationToken cancellationToken = default)
        => TryClear(source, range, out editedSource, out _, cancellationToken);

    internal static bool TryClear(
        string source,
        CsvCellRange range,
        out string editedSource,
        out IReadOnlyList<CsvSourceEdit> sourceEdits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        editedSource = source;
        sourceEdits = [];
        if (source.Length > MaximumSourceLength)
        {
            return false;
        }

        var document = CsvDocumentParser.ParseSource(source, cancellationToken);
        if (document is null
            || document.HasUnterminatedQuotedField
            || !TryValidateRange(document, range, out var rowEnd, out var columnEnd))
        {
            return false;
        }

        var values = new Dictionary<int, Dictionary<int, string>>();
        for (var rowIndex = range.Row; rowIndex < rowEnd; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRow = document.Rows[rowIndex];
            var existingEnd = Math.Min(columnEnd, sourceRow.Values.Count);
            for (var columnIndex = range.Column; columnIndex < existingEnd; columnIndex++)
            {
                if (sourceRow.Values[columnIndex].Length > 0)
                {
                    AddValue(values, rowIndex, columnIndex, string.Empty);
                }
            }
        }

        return TryApplyCellValues(
            source,
            document,
            values,
            ensureColumnCount: 0,
            materializeEmptyCells: false,
            writeEmptyAsBlank: true,
            out editedSource,
            out sourceEdits,
            cancellationToken);
    }

    /// <summary>範囲の上端を下方向へ、または左端を右方向へコピーする。</summary>
    public static bool TryFill(
        string source,
        CsvCellRange range,
        bool down,
        out string editedSource,
        CancellationToken cancellationToken = default)
        => TryFill(source, range, down, out editedSource, out _, cancellationToken);

    internal static bool TryFill(
        string source,
        CsvCellRange range,
        bool down,
        out string editedSource,
        out IReadOnlyList<CsvSourceEdit> sourceEdits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        editedSource = source;
        sourceEdits = [];
        if (source.Length > MaximumSourceLength)
        {
            return false;
        }

        var document = CsvDocumentParser.ParseSource(source, cancellationToken);
        if (document is null
            || document.HasUnterminatedQuotedField
            || !TryValidateRange(document, range, out var rowEnd, out var columnEnd))
        {
            return false;
        }

        var values = new Dictionary<int, Dictionary<int, string>>();
        if (down)
        {
            for (var rowIndex = range.Row + 1; rowIndex < rowEnd; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var columnIndex = range.Column; columnIndex < columnEnd; columnIndex++)
                {
                    AddValue(
                        values,
                        rowIndex,
                        columnIndex,
                        GetCellValue(document.Rows[range.Row], columnIndex));
                }
            }
        }
        else
        {
            for (var rowIndex = range.Row; rowIndex < rowEnd; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = GetCellValue(document.Rows[rowIndex], range.Column);
                for (var columnIndex = range.Column + 1; columnIndex < columnEnd; columnIndex++)
                {
                    AddValue(values, rowIndex, columnIndex, value);
                }
            }
        }

        return TryApplyCellValues(
            source,
            document,
            values,
            columnEnd,
            materializeEmptyCells: false,
            writeEmptyAsBlank: false,
            out editedSource,
            out sourceEdits,
            cancellationToken);
    }

    /// <summary>Excel形式のTSVを起点セルへ貼り付け、足りない行列を補完する。</summary>
    public static bool TryPaste(
        string source,
        int rowIndex,
        int columnIndex,
        string clipboardText,
        string newLine,
        out string editedSource,
        CancellationToken cancellationToken = default)
        => TryPaste(
            source,
            rowIndex,
            columnIndex,
            clipboardText,
            newLine,
            out editedSource,
            out _,
            cancellationToken);

    internal static bool TryPaste(
        string source,
        int rowIndex,
        int columnIndex,
        string clipboardText,
        string newLine,
        out string editedSource,
        out IReadOnlyList<CsvSourceEdit> sourceEdits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clipboardText);
        ArgumentNullException.ThrowIfNull(newLine);
        cancellationToken.ThrowIfCancellationRequested();
        editedSource = source;
        sourceEdits = [];
        if (source.Length > MaximumSourceLength
            || clipboardText.Length > MaximumClipboardLength
            || rowIndex < 0
            || columnIndex < 0)
        {
            return false;
        }

        var clipboard = CsvDocumentParser.ParseTsvSource(clipboardText, cancellationToken);
        if (clipboard is null
            || clipboard.HasUnterminatedQuotedField
            || clipboard.Rows.Count == 0
            || clipboard.TotalColumnCount == 0
            || !TryGetEnd(rowIndex, clipboard.Rows.Count, out var rowEnd)
            || !TryGetEnd(columnIndex, clipboard.TotalColumnCount, out var columnEnd)
            || !IsSafeArea(clipboard.Rows.Count, clipboard.TotalColumnCount))
        {
            return false;
        }

        var document = CsvDocumentParser.ParseSource(source, cancellationToken);
        if (document is null || document.HasUnterminatedQuotedField)
        {
            return false;
        }

        var missingRowCount = Math.Max(0, rowEnd - document.Rows.Count);
        if (!IsSafeArea(missingRowCount, Math.Max(columnEnd, 1)))
        {
            return false;
        }

        var values = new Dictionary<int, Dictionary<int, string>>();
        for (var clipboardRowIndex = 0; clipboardRowIndex < clipboard.Rows.Count; clipboardRowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetRowIndex = rowIndex + clipboardRowIndex;
            if (targetRowIndex >= document.Rows.Count)
            {
                continue;
            }

            var clipboardRow = clipboard.Rows[clipboardRowIndex];
            for (var clipboardColumnIndex = 0;
                 clipboardColumnIndex < clipboard.TotalColumnCount;
                 clipboardColumnIndex++)
            {
                var value = GetCellValue(clipboardRow, clipboardColumnIndex);
                AddValue(
                    values,
                    targetRowIndex,
                    columnIndex + clipboardColumnIndex,
                    DocumentFileService.NormalizeNewLines(value, newLine));
            }
        }

        if (!TryGetResultCellCount(
                document,
                values,
                columnEnd,
                materializeEmptyCells: true,
                cancellationToken,
                out var existingResultCellCount)
            || existingResultCellCount + ((long)missingRowCount * columnEnd) > MaximumCellCount)
        {
            return false;
        }

        if (!TryApplyCellValues(
            source,
            document,
            values,
            columnEnd,
            materializeEmptyCells: true,
            writeEmptyAsBlank: false,
            out _,
            out var existingEdits,
            cancellationToken))
        {
            return false;
        }

        var edits = existingEdits.ToList();
        if (missingRowCount > 0)
        {
            if (!TryGetFinalLength(source, edits, cancellationToken, out var existingResultLength))
            {
                return false;
            }

            var prefix = document.Rows.Count > 0 && document.Rows[^1].DelimiterLength == 0
                ? newLine
                : string.Empty;
            var newRowFields = new List<string[]>(missingRowCount);
            var encodedLengths = new Dictionary<string, int>(ReferenceEqualityComparer.Instance);
            long addedLength = prefix.Length + ((long)newLine.Length * (missingRowCount - 1));
            for (var targetRowIndex = document.Rows.Count; targetRowIndex < rowEnd; targetRowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fields = new string[columnEnd];
                Array.Fill(fields, string.Empty);
                if (targetRowIndex >= rowIndex)
                {
                    var clipboardRow = clipboard.Rows[targetRowIndex - rowIndex];
                    for (var clipboardColumnIndex = 0;
                         clipboardColumnIndex < clipboard.TotalColumnCount;
                         clipboardColumnIndex++)
                    {
                        fields[columnIndex + clipboardColumnIndex] = DocumentFileService.NormalizeNewLines(
                            GetCellValue(clipboardRow, clipboardColumnIndex),
                            newLine);
                    }
                }

                addedLength += GetCsvRecordLength(fields, encodedLengths, cancellationToken);
                if (existingResultLength + addedLength > MaximumResultLength)
                {
                    return false;
                }

                newRowFields.Add(fields);
            }

            var newRows = new List<string>(newRowFields.Count);
            foreach (var fields in newRowFields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                newRows.Add(CreateRecord(fields, cancellationToken));
            }

            edits.Add(new CsvSourceEdit(
                source.Length,
                0,
                prefix + string.Join(newLine, newRows)));
        }

        if (!TryApplyEdits(source, edits, out editedSource, cancellationToken))
        {
            return false;
        }

        sourceEdits = edits;
        return true;
    }

    private static string? GetRangeText(
        CsvSourceDocument document,
        CsvCellRange range,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRange(document, range, out var rowEnd, out var columnEnd))
        {
            return null;
        }

        var encodedLengths = new Dictionary<string, int>(ReferenceEqualityComparer.Instance);
        long outputLength = 0;
        for (var rowIndex = range.Row; rowIndex < rowEnd; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rowIndex > range.Row)
            {
                outputLength += 2;
            }

            var sourceRow = document.Rows[rowIndex];
            for (var columnIndex = range.Column; columnIndex < columnEnd; columnIndex++)
            {
                if ((columnIndex & 4_095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (columnIndex > range.Column)
                {
                    outputLength++;
                }

                outputLength += GetTsvFieldLength(
                    GetCellValue(sourceRow, columnIndex),
                    encodedLengths,
                    cancellationToken);
                if (outputLength > MaximumResultLength)
                {
                    return null;
                }
            }
        }

        var output = new StringBuilder((int)outputLength);
        var firstOutputRow = true;
        for (var rowIndex = range.Row; rowIndex < rowEnd; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!firstOutputRow)
            {
                output.Append("\r\n");
            }

            firstOutputRow = false;
            var sourceRow = document.Rows[rowIndex];
            for (var columnIndex = range.Column; columnIndex < columnEnd; columnIndex++)
            {
                if ((columnIndex & 4_095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (columnIndex > range.Column)
                {
                    output.Append('\t');
                }

                AppendTsvField(output, GetCellValue(sourceRow, columnIndex));
            }
        }

        return output.ToString();
    }

    private static bool TryInsertRows(
        string source,
        CsvSourceDocument document,
        int index,
        int count,
        string newLine,
        out string editedSource,
        out IReadOnlyList<CsvSourceEdit> sourceEdits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        editedSource = source;
        sourceEdits = [];
        if (index > document.Rows.Count)
        {
            return false;
        }

        var columnCount = Math.Max(document.TotalColumnCount, 1);
        if (GetDocumentCellCount(document, cancellationToken) + ((long)columnCount * count)
            > MaximumCellCount)
        {
            return false;
        }

        var rowLength = columnCount == 1 ? 2L : columnCount - 1L;
        var insertedLength = (rowLength * count) + ((long)newLine.Length * (count - 1));
        var separatorLength = document.Rows.Count > 0
            && index == document.Rows.Count
            && document.Rows[^1].DelimiterLength == 0
                ? newLine.Length
                : index < document.Rows.Count ? newLine.Length : 0;
        if (source.Length + insertedLength + separatorLength > MaximumResultLength)
        {
            return false;
        }

        var row = CreateEmptyRecord(columnCount);
        var insertedRows = string.Join(newLine, Enumerable.Repeat(row, count));
        if (document.Rows.Count == 0)
        {
            return TryApplySingleEdit(
                source,
                new CsvSourceEdit(0, 0, insertedRows),
                out editedSource,
                out sourceEdits,
                cancellationToken);
        }

        if (index < document.Rows.Count)
        {
            var offset = document.Rows[index].Offset;
            return TryApplySingleEdit(
                source,
                new CsvSourceEdit(offset, 0, insertedRows + newLine),
                out editedSource,
                out sourceEdits,
                cancellationToken);
        }

        var lastRow = document.Rows[^1];
        var prefix = lastRow.DelimiterLength > 0 ? string.Empty : newLine;
        return TryApplySingleEdit(
            source,
            new CsvSourceEdit(source.Length, 0, prefix + insertedRows),
            out editedSource,
            out sourceEdits,
            cancellationToken);
    }

    private static bool TryDeleteRows(
        string source,
        CsvSourceDocument document,
        int index,
        int count,
        out string editedSource,
        out IReadOnlyList<CsvSourceEdit> sourceEdits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        editedSource = source;
        sourceEdits = [];
        if (index >= document.Rows.Count || count > document.Rows.Count - index)
        {
            return false;
        }

        var first = document.Rows[index];
        var last = document.Rows[index + count - 1];
        var end = last.Offset + last.ContentLength + last.DelimiterLength;
        return TryApplySingleEdit(
            source,
            new CsvSourceEdit(first.Offset, end - first.Offset, string.Empty),
            out editedSource,
            out sourceEdits,
            cancellationToken);
    }

    private static bool TryInsertColumns(
        string source,
        CsvSourceDocument document,
        int index,
        int count,
        out string editedSource,
        out IReadOnlyList<CsvSourceEdit> sourceEdits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        editedSource = source;
        sourceEdits = [];
        if (index > document.TotalColumnCount)
        {
            return false;
        }

        if (document.Rows.Count == 0)
        {
            return TryApplySingleEdit(
                source,
                new CsvSourceEdit(0, 0, CreateEmptyRecord(count)),
                out editedSource,
                out sourceEdits,
                cancellationToken);
        }

        long addedCharacterCount = 0;
        long resultCellCount = 0;
        foreach (var row in document.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actualColumnCount = row.CellSourceRanges.Count;
            resultCellCount += Math.Max(actualColumnCount, index) + (long)count;
            if (resultCellCount > MaximumCellCount)
            {
                return false;
            }

            addedCharacterCount += index < actualColumnCount
                ? count
                : (long)index - actualColumnCount + count;
            if (source.Length + addedCharacterCount > MaximumResultLength)
            {
                return false;
            }
        }

        var edits = new List<CsvSourceEdit>(document.Rows.Count);
        foreach (var row in document.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actualColumnCount = row.CellSourceRanges.Count;
            var addedSeparators = index < actualColumnCount
                ? count
                : checked(index - actualColumnCount + count);
            var offset = index < actualColumnCount
                ? row.CellSourceRanges[index].Offset
                : row.Offset + row.ContentLength;
            edits.Add(new CsvSourceEdit(offset, 0, new string(',', addedSeparators)));
        }

        if (!TryApplyEdits(source, edits, out editedSource, cancellationToken))
        {
            return false;
        }

        sourceEdits = edits;
        return true;
    }

    private static bool TryDeleteColumns(
        string source,
        CsvSourceDocument document,
        int index,
        int count,
        out string editedSource,
        out IReadOnlyList<CsvSourceEdit> sourceEdits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        editedSource = source;
        sourceEdits = [];
        if (index >= document.TotalColumnCount || count > document.TotalColumnCount - index)
        {
            return false;
        }

        if (count == document.TotalColumnCount)
        {
            return TryApplySingleEdit(
                source,
                new CsvSourceEdit(0, source.Length, string.Empty),
                out editedSource,
                out sourceEdits,
                cancellationToken);
        }

        var edits = new List<CsvSourceEdit>(document.Rows.Count);
        foreach (var row in document.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actualColumnCount = row.CellSourceRanges.Count;
            if (index >= actualColumnCount)
            {
                continue;
            }

            var endColumn = Math.Min(index + count, actualColumnCount);
            if (index == 0 && endColumn == actualColumnCount)
            {
                edits.Add(new CsvSourceEdit(row.Offset, row.ContentLength, "\"\""));
            }
            else if (index == 0)
            {
                var end = row.CellSourceRanges[endColumn].Offset;
                var replacement = end == row.Offset + row.ContentLength ? "\"\"" : string.Empty;
                edits.Add(new CsvSourceEdit(row.Offset, end - row.Offset, replacement));
            }
            else if (endColumn == actualColumnCount)
            {
                var start = row.CellSourceRanges[index - 1];
                var offset = start.Offset + start.Length;
                var replacement = offset == row.Offset ? "\"\"" : string.Empty;
                edits.Add(new CsvSourceEdit(offset, row.Offset + row.ContentLength - offset, replacement));
            }
            else
            {
                var offset = row.CellSourceRanges[index].Offset;
                var end = row.CellSourceRanges[endColumn].Offset;
                edits.Add(new CsvSourceEdit(offset, end - offset, string.Empty));
            }
        }

        if (!TryApplyEdits(source, edits, out editedSource, cancellationToken))
        {
            return false;
        }

        sourceEdits = edits;
        return true;
    }

    private static bool TryApplyCellValues(
        string source,
        CsvSourceDocument document,
        IReadOnlyDictionary<int, Dictionary<int, string>> values,
        int ensureColumnCount,
        bool materializeEmptyCells,
        bool writeEmptyAsBlank,
        out string editedSource,
        out IReadOnlyList<CsvSourceEdit> sourceEdits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        editedSource = source;
        sourceEdits = [];
        if (ensureColumnCount < 0 || ensureColumnCount > MaximumCellCount)
        {
            return false;
        }

        if (!TryGetResultCellCount(
                document,
                values,
                ensureColumnCount,
                materializeEmptyCells,
                cancellationToken,
                out _))
        {
            return false;
        }

        var encodedLengths = new Dictionary<string, int>(ReferenceEqualityComparer.Instance);
        long finalLength = source.Length;
        foreach (var rowValues in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rowValues.Key < 0 || rowValues.Key >= document.Rows.Count)
            {
                return false;
            }

            var row = document.Rows[rowValues.Key];
            foreach (var cellValue in rowValues.Value)
            {
                if (cellValue.Key < 0
                    || cellValue.Key >= row.Values.Count && cellValue.Key >= ensureColumnCount)
                {
                    return false;
                }

                if (cellValue.Key >= row.Values.Count
                    || string.Equals(row.Values[cellValue.Key], cellValue.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                var range = row.CellSourceRanges[cellValue.Key];
                var replacementLength = writeEmptyAsBlank
                    && cellValue.Value.Length == 0
                    && row.Values.Count > 1
                        ? 0
                        : GetCsvFieldLength(cellValue.Value, encodedLengths, cancellationToken);
                finalLength += replacementLength - (long)range.Length;
            }

            var rowColumnCount = GetMaterializedColumnCount(
                row,
                rowValues.Value,
                ensureColumnCount,
                materializeEmptyCells,
                cancellationToken);
            if (rowColumnCount > row.Values.Count)
            {
                finalLength += rowColumnCount - (long)row.Values.Count;
                foreach (var cellValue in rowValues.Value)
                {
                    if (cellValue.Key >= row.Values.Count
                        && cellValue.Key < rowColumnCount
                        && cellValue.Value.Length > 0)
                    {
                        finalLength += GetCsvFieldLength(
                            cellValue.Value,
                            encodedLengths,
                            cancellationToken);
                    }
                }
            }
        }

        if (finalLength > MaximumResultLength)
        {
            return false;
        }

        var edits = new List<CsvSourceEdit>();
        foreach (var rowValues in values.OrderBy(pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rowValues.Key < 0 || rowValues.Key >= document.Rows.Count)
            {
                return false;
            }

            var row = document.Rows[rowValues.Key];
            foreach (var cellValue in rowValues.Value.OrderBy(pair => pair.Key))
            {
                if (cellValue.Key < 0
                    || cellValue.Key >= row.Values.Count && cellValue.Key >= ensureColumnCount)
                {
                    return false;
                }

                if (cellValue.Key >= row.Values.Count
                    || string.Equals(row.Values[cellValue.Key], cellValue.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                var range = row.CellSourceRanges[cellValue.Key];
                var replacement = writeEmptyAsBlank
                    && cellValue.Value.Length == 0
                    && row.Values.Count > 1
                        ? string.Empty
                        : EncodeCsvField(cellValue.Value);
                edits.Add(new CsvSourceEdit(range.Offset, range.Length, replacement));
            }

            var rowColumnCount = GetMaterializedColumnCount(
                row,
                rowValues.Value,
                ensureColumnCount,
                materializeEmptyCells,
                cancellationToken);
            if (rowColumnCount <= row.Values.Count)
            {
                continue;
            }

            var suffix = new StringBuilder();
            for (var columnIndex = row.Values.Count; columnIndex < rowColumnCount; columnIndex++)
            {
                if ((columnIndex & 4_095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                suffix.Append(',');
                if (rowValues.Value.TryGetValue(columnIndex, out var value) && value.Length > 0)
                {
                    suffix.Append(EncodeCsvField(value));
                }
            }

            edits.Add(new CsvSourceEdit(row.Offset + row.ContentLength, 0, suffix.ToString()));
        }

        if (!TryApplyEdits(source, edits, out editedSource, cancellationToken))
        {
            return false;
        }

        sourceEdits = edits;
        return true;
    }

    private static int GetMaterializedColumnCount(
        CsvSourceRow row,
        IReadOnlyDictionary<int, string> rowValues,
        int ensureColumnCount,
        bool materializeEmptyCells,
        CancellationToken cancellationToken)
    {
        if (materializeEmptyCells)
        {
            return ensureColumnCount;
        }

        var columnCount = row.Values.Count;
        foreach (var cellValue in rowValues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cellValue.Value.Length > 0 && cellValue.Key >= columnCount)
            {
                columnCount = cellValue.Key + 1;
            }
        }

        return columnCount;
    }

    private static bool TryGetResultCellCount(
        CsvSourceDocument document,
        IReadOnlyDictionary<int, Dictionary<int, string>> values,
        int ensureColumnCount,
        bool materializeEmptyCells,
        CancellationToken cancellationToken,
        out long resultCellCount)
    {
        resultCellCount = GetDocumentCellCount(document, cancellationToken);
        foreach (var rowValues in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rowValues.Key < 0 || rowValues.Key >= document.Rows.Count)
            {
                return false;
            }

            var row = document.Rows[rowValues.Key];
            var materializedColumnCount = GetMaterializedColumnCount(
                row,
                rowValues.Value,
                ensureColumnCount,
                materializeEmptyCells,
                cancellationToken);
            resultCellCount += Math.Max(0, materializedColumnCount - row.Values.Count);
            if (resultCellCount > MaximumCellCount)
            {
                return false;
            }
        }

        return true;
    }

    private static long GetDocumentCellCount(
        CsvSourceDocument document,
        CancellationToken cancellationToken)
    {
        long cellCount = 0;
        foreach (var row in document.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cellCount += row.Values.Count;
        }

        return cellCount;
    }

    private static bool TryValidateRange(
        CsvSourceDocument document,
        CsvCellRange range,
        out int rowEnd,
        out int columnEnd)
    {
        rowEnd = 0;
        columnEnd = 0;
        return TryGetEnd(range.Row, range.RowCount, out rowEnd)
            && TryGetEnd(range.Column, range.ColumnCount, out columnEnd)
            && IsSafeArea(range.RowCount, range.ColumnCount)
            && rowEnd <= document.Rows.Count
            && columnEnd <= document.TotalColumnCount;
    }

    private static bool TryGetEnd(int start, int count, out int end)
    {
        end = 0;
        if (start < 0 || count <= 0 || count > MaximumCellCount)
        {
            return false;
        }

        var longEnd = (long)start + count;
        if (longEnd > int.MaxValue)
        {
            return false;
        }

        end = (int)longEnd;
        return true;
    }

    private static bool IsSafeArea(int rowCount, int columnCount)
        => rowCount >= 0
            && columnCount >= 0
            && (long)rowCount * columnCount <= MaximumCellCount;

    private static void AddValue(
        IDictionary<int, Dictionary<int, string>> values,
        int rowIndex,
        int columnIndex,
        string value)
    {
        if (!values.TryGetValue(rowIndex, out var rowValues))
        {
            rowValues = [];
            values.Add(rowIndex, rowValues);
        }

        rowValues[columnIndex] = value;
    }

    private static string GetCellValue(CsvSourceRow row, int columnIndex)
        => columnIndex < row.Values.Count ? row.Values[columnIndex] : string.Empty;

    internal static string EncodeCsvField(string value)
        => value.Length == 0 || value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;

    private static int GetCsvFieldLength(
        string value,
        IDictionary<string, int> encodedLengths,
        CancellationToken cancellationToken)
    {
        if (encodedLengths.TryGetValue(value, out var encodedLength))
        {
            return encodedLength;
        }

        var requiresQuotes = value.Length == 0;
        var quoteCount = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if ((index & 4_095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var character = value[index];
            requiresQuotes |= character is ',' or '"' or '\r' or '\n';
            if (character == '"')
            {
                quoteCount++;
            }
        }

        if (!requiresQuotes)
        {
            encodedLength = value.Length;
        }
        else
        {
            encodedLength = value.Length + quoteCount + 2;
        }

        encodedLengths[value] = encodedLength;
        return encodedLength;
    }

    private static int GetTsvFieldLength(
        string value,
        IDictionary<string, int> encodedLengths,
        CancellationToken cancellationToken)
    {
        if (encodedLengths.TryGetValue(value, out var encodedLength))
        {
            return encodedLength;
        }

        var requiresQuotes = value.Length == 0;
        var quoteCount = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if ((index & 4_095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var character = value[index];
            requiresQuotes |= character is '\t' or '\r' or '\n' or '"';
            if (character == '"')
            {
                quoteCount++;
            }
        }

        if (!requiresQuotes)
        {
            encodedLength = value.Length;
        }
        else
        {
            encodedLength = value.Length + quoteCount + 2;
        }

        encodedLengths[value] = encodedLength;
        return encodedLength;
    }

    private static long GetCsvRecordLength(
        IReadOnlyList<string> fields,
        IDictionary<string, int> encodedLengths,
        CancellationToken cancellationToken)
    {
        if (fields.Count == 1 && fields[0].Length == 0)
        {
            return 2;
        }

        long length = Math.Max(0, fields.Count - 1);
        foreach (var value in fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value.Length > 0)
            {
                length += GetCsvFieldLength(value, encodedLengths, cancellationToken);
            }
        }

        return length;
    }

    private static string CreateRecord(
        IReadOnlyList<string> fields,
        CancellationToken cancellationToken)
    {
        if (fields.Count == 1 && fields[0].Length == 0)
        {
            return "\"\"";
        }

        var output = new StringBuilder();
        for (var index = 0; index < fields.Count; index++)
        {
            if ((index & 4_095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (index > 0)
            {
                output.Append(',');
            }

            if (fields[index].Length > 0)
            {
                output.Append(EncodeCsvField(fields[index]));
            }
        }

        return output.ToString();
    }

    private static string CreateEmptyRecord(int columnCount)
        => columnCount <= 1 ? "\"\"" : new string(',', columnCount - 1);

    private static string ApplyEdits(
        string source,
        IEnumerable<CsvSourceEdit> sourceEdits,
        int finalLength,
        CancellationToken cancellationToken)
    {
        var edits = sourceEdits.OrderBy(edit => edit.Offset).ToArray();
        var result = new StringBuilder(finalLength);
        var sourceOffset = 0;
        foreach (var edit in edits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Append(source, sourceOffset, edit.Offset - sourceOffset);
            result.Append(edit.Replacement);
            sourceOffset = edit.Offset + edit.Length;
        }

        result.Append(source, sourceOffset, source.Length - sourceOffset);
        return result.ToString();
    }

    private static bool TryApplySingleEdit(
        string source,
        CsvSourceEdit sourceEdit,
        out string editedSource,
        out IReadOnlyList<CsvSourceEdit> sourceEdits,
        CancellationToken cancellationToken)
    {
        CsvSourceEdit[] edits = [sourceEdit];
        if (!TryApplyEdits(source, edits, out editedSource, cancellationToken))
        {
            sourceEdits = [];
            return false;
        }

        sourceEdits = edits;
        return true;
    }

    private static bool TryApplyEdits(
        string source,
        IReadOnlyCollection<CsvSourceEdit> sourceEdits,
        out string editedSource,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        editedSource = source;
        if (!TryGetFinalLength(source, sourceEdits, cancellationToken, out var finalLength))
        {
            return false;
        }

        if (sourceEdits.Count == 0)
        {
            return true;
        }

        editedSource = ApplyEdits(source, sourceEdits, finalLength, cancellationToken);
        return true;
    }

    private static bool TryGetFinalLength(
        string source,
        IEnumerable<CsvSourceEdit> sourceEdits,
        CancellationToken cancellationToken,
        out int finalLength)
    {
        long length = source.Length;
        foreach (var edit in sourceEdits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            length += edit.Replacement.Length - (long)edit.Length;
        }

        if (length < 0 || length > MaximumResultLength)
        {
            finalLength = 0;
            return false;
        }

        finalLength = (int)length;
        return true;
    }

    private static void AppendTsvField(StringBuilder output, string value)
    {
        if (value.Length > 0 && value.IndexOfAny(['\t', '\r', '\n', '"']) < 0)
        {
            output.Append(value);
            return;
        }

        output.Append('"');
        output.Append(value.Replace("\"", "\"\"", StringComparison.Ordinal));
        output.Append('"');
    }
}
