using Fumilume.Services;

namespace Fumilume.ViewModels;

internal sealed record CsvPreparedEdit(
    string ExpectedSource,
    string EditedSource,
    IReadOnlyList<CsvSourceEdit> SourceEdits,
    string ExpectedNewLine);

public sealed partial class DocumentViewModel
{
    private const int CsvBackgroundWorkThreshold = 128 * 1024;

    internal Task<CsvPreparedEdit?> PrepareCsvStructureEditAsync(
        string expectedSource,
        CsvTableOperation operation,
        int index,
        int count,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentCsvSource(expectedSource))
        {
            return Task.FromResult<CsvPreparedEdit?>(null);
        }

        var expectedNewLine = NewLine;
        return RunCsvWorkAsync(
            expectedSource.Length,
            () => CreatePreparedCsvEdit(
                expectedSource,
                expectedNewLine,
                CsvTableEditingService.TryEdit(
                    expectedSource,
                    operation,
                    index,
                    count,
                    expectedNewLine,
                    out var editedSource,
                    out var sourceEdits,
                    cancellationToken),
                editedSource,
                sourceEdits),
            cancellationToken);
    }

    internal Task<CsvPreparedEdit?> PrepareCsvClearAsync(
        string expectedSource,
        CsvCellRange range,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentCsvSource(expectedSource))
        {
            return Task.FromResult<CsvPreparedEdit?>(null);
        }

        var expectedNewLine = NewLine;
        return RunCsvWorkAsync(
            expectedSource.Length,
            () => CreatePreparedCsvEdit(
                expectedSource,
                expectedNewLine,
                CsvTableEditingService.TryClear(
                    expectedSource,
                    range,
                    out var editedSource,
                    out var sourceEdits,
                    cancellationToken),
                editedSource,
                sourceEdits),
            cancellationToken);
    }

    internal Task<CsvPreparedEdit?> PrepareCsvFillAsync(
        string expectedSource,
        CsvCellRange range,
        bool down,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentCsvSource(expectedSource))
        {
            return Task.FromResult<CsvPreparedEdit?>(null);
        }

        var expectedNewLine = NewLine;
        return RunCsvWorkAsync(
            expectedSource.Length,
            () => CreatePreparedCsvEdit(
                expectedSource,
                expectedNewLine,
                CsvTableEditingService.TryFill(
                    expectedSource,
                    range,
                    down,
                    out var editedSource,
                    out var sourceEdits,
                    cancellationToken),
                editedSource,
                sourceEdits),
            cancellationToken);
    }

    internal Task<CsvPreparedEdit?> PrepareCsvPasteAsync(
        string expectedSource,
        int row,
        int column,
        string clipboardText,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentCsvSource(expectedSource))
        {
            return Task.FromResult<CsvPreparedEdit?>(null);
        }

        var expectedNewLine = NewLine;
        return RunCsvWorkAsync(
            Math.Max(expectedSource.Length, clipboardText.Length),
            () => CreatePreparedCsvEdit(
                expectedSource,
                expectedNewLine,
                CsvTableEditingService.TryPaste(
                    expectedSource,
                    row,
                    column,
                    clipboardText,
                    expectedNewLine,
                    out var editedSource,
                    out var sourceEdits,
                    cancellationToken),
                editedSource,
                sourceEdits),
            cancellationToken);
    }

    internal Task<CsvPreparedEdit?> PrepareCsvSortAsync(
        string expectedSource,
        int column,
        bool descending,
        bool keepFirstRow,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentCsvSource(expectedSource))
        {
            return Task.FromResult<CsvPreparedEdit?>(null);
        }

        var expectedNewLine = NewLine;
        return RunCsvWorkAsync(
            expectedSource.Length,
            () => CreatePreparedCsvEdit(
                expectedSource,
                expectedNewLine,
                CsvTableEditingService.TrySort(
                    expectedSource,
                    column,
                    descending,
                    keepFirstRow,
                    out var editedSource,
                    out var sourceEdits,
                    cancellationToken),
                editedSource,
                sourceEdits),
            cancellationToken);
    }

    internal Task<string?> GetCsvCellRangeTextAsync(
        string expectedSource,
        CsvCellRange range,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentCsvSource(expectedSource))
        {
            return Task.FromResult<string?>(null);
        }

        return RunCsvWorkAsync(
            expectedSource.Length,
            () => CsvTableEditingService.GetRangeText(
                expectedSource,
                range,
                cancellationToken),
            cancellationToken);
    }

    internal bool TryApplyPreparedCsvEdit(CsvPreparedEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (!IsCurrentCsvSource(edit.ExpectedSource)
            || !string.Equals(NewLine, edit.ExpectedNewLine, StringComparison.Ordinal))
        {
            return false;
        }

        return ApplyCsvSourceEdits(edit.ExpectedSource, edit.EditedSource, edit.SourceEdits);
    }

    private bool IsCurrentCsvSource(string expectedSource)
        => IsCsv && string.Equals(Text, expectedSource, StringComparison.Ordinal);

    private static CsvPreparedEdit? CreatePreparedCsvEdit(
        string expectedSource,
        string expectedNewLine,
        bool success,
        string editedSource,
        IReadOnlyList<CsvSourceEdit> sourceEdits)
        => success
            ? new CsvPreparedEdit(expectedSource, editedSource, sourceEdits, expectedNewLine)
            : null;

    private static Task<T> RunCsvWorkAsync<T>(
        int inputLength,
        Func<T> work,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        if (inputLength >= CsvBackgroundWorkThreshold)
        {
            return Task.Run(work, cancellationToken);
        }

        try
        {
            return Task.FromResult(work());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
    }

    private bool ApplyCsvSourceEdits(
        string expectedSource,
        string editedSource,
        IReadOnlyList<CsvSourceEdit> sourceEdits)
    {
        if (string.Equals(expectedSource, editedSource, StringComparison.Ordinal))
        {
            return true;
        }

        if (sourceEdits.Count == 0)
        {
            return false;
        }

        using (EditorDocument.RunUpdate())
        {
            // 元テキストのoffsetを保つため後方から適用する。同じoffsetの挿入は、
            // サービスで組み立てた順序が最終結果でも維持されるよう逆順に適用する。
            foreach (var item in sourceEdits
                         .Select((edit, index) => (Edit: edit, Index: index))
                         .OrderByDescending(item => item.Edit.Offset)
                         .ThenByDescending(item => item.Index))
            {
                EditorDocument.Replace(item.Edit.Offset, item.Edit.Length, item.Edit.Replacement);
            }
        }

        return string.Equals(Text, editedSource, StringComparison.Ordinal);
    }
}
