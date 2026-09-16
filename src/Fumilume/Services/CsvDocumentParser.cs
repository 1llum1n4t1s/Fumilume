using System.Text;

namespace Fumilume.Services;

/// <summary>CSV プレビューへ渡す、表示上限適用後の表データ。</summary>
public sealed record CsvDocument(
    IReadOnlyList<IReadOnlyList<string>> Rows,
    int TotalRowCount,
    int TotalColumnCount)
{
    public bool HasUnterminatedQuotedField { get; init; }

    internal IReadOnlyList<IReadOnlyList<CsvCellSourceRange>> CellSourceRanges { get; init; } = [];

    public int DisplayedColumnCount => Math.Min(TotalColumnCount, CsvDocumentParser.MaxPreviewColumns);

    public bool IsTruncated =>
        TotalRowCount > Rows.Count || TotalColumnCount > CsvDocumentParser.MaxPreviewColumns;
}

internal readonly record struct CsvCellSourceRange(int Offset, int Length);

/// <summary>CSV 全文中の1レコード。プレビュー上限外の構造編集にも利用する。</summary>
internal sealed record CsvSourceRow(
    IReadOnlyList<string> Values,
    IReadOnlyList<CsvCellSourceRange> CellSourceRanges,
    int Offset,
    int ContentLength,
    int DelimiterLength);

/// <summary>表示上限を適用していないCSV全文の解析結果。</summary>
internal sealed record CsvSourceDocument(
    IReadOnlyList<CsvSourceRow> Rows,
    int TotalRowCount,
    int TotalColumnCount,
    bool HasUnterminatedQuotedField);

/// <summary>RFC 4180 形式を基礎に、Excel 由来の不揃いな CSV も表へ変換する。</summary>
public static class CsvDocumentParser
{
    public const int MaxPreviewRows = 1_000;
    public const int MaxPreviewColumns = 100;
    internal const int MaximumEditableSourceLength = 8 * 1024 * 1024;
    internal const int MaximumEditableCellCount = 1_048_576;
    private const int CancellationCheckInterval = 4_096;

    public static CsvDocument Parse(string? csv, CancellationToken cancellationToken = default)
    {
        var source = ParseCore(csv, ',', storeAll: false, cancellationToken: cancellationToken);
        var rows = source.Rows.Select(row => row.Values).ToArray();
        var cellSourceRanges = source.Rows.Select(row => row.CellSourceRanges).ToArray();

        return new CsvDocument(rows, source.TotalRowCount, source.TotalColumnCount)
        {
            HasUnterminatedQuotedField = source.HasUnterminatedQuotedField,
            CellSourceRanges = cellSourceRanges,
        };
    }

    internal static CsvSourceDocument? ParseSource(
        string? csv,
        CancellationToken cancellationToken = default)
        => TryValidateEditableSource(csv, ',', cancellationToken)
            ? ParseCore(csv, ',', storeAll: true, cancellationToken: cancellationToken)
            : null;

    /// <summary>Excelのクリップボード形式であるタブ区切りテキストを全文解析する。</summary>
    internal static CsvSourceDocument? ParseTsvSource(
        string? tsv,
        CancellationToken cancellationToken = default)
        => TryValidateEditableSource(tsv, '\t', cancellationToken)
            ? ParseCore(tsv, '\t', storeAll: true, cancellationToken: cancellationToken)
            : null;

    private static bool TryValidateEditableSource(
        string? source,
        char delimiter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(source))
        {
            return true;
        }

        // 全値を保持する解析の前に、文字数とセル数だけを割り当てなしで検査する。
        if (source.Length > MaximumEditableSourceLength)
        {
            return false;
        }

        var fieldLength = 0;
        var inQuotes = false;
        var cellCount = 0;
        long nextCancellationCheck = CancellationCheckInterval;
        for (var index = 0; index < source.Length; index++)
        {
            if (index >= nextCancellationCheck)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nextCancellationCheck = (long)index + CancellationCheckInterval;
            }

            var character = source[index];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < source.Length && source[index + 1] == '"')
                    {
                        fieldLength++;
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    fieldLength++;
                }

                continue;
            }

            if (character == '"' && fieldLength == 0)
            {
                inQuotes = true;
            }
            else if (character == delimiter)
            {
                if (++cellCount > MaximumEditableCellCount)
                {
                    return false;
                }

                fieldLength = 0;
            }
            else if (character is '\r' or '\n')
            {
                if (++cellCount > MaximumEditableCellCount)
                {
                    return false;
                }

                fieldLength = 0;
                if (character == '\r' && index + 1 < source.Length && source[index + 1] == '\n')
                {
                    index++;
                }
            }
            else
            {
                fieldLength++;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return source[^1] is '\r' or '\n' && !inQuotes
            || ++cellCount <= MaximumEditableCellCount;
    }

    private static CsvSourceDocument ParseCore(
        string? csv,
        char delimiter,
        bool storeAll,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(csv))
        {
            return new CsvSourceDocument([], 0, 0, false);
        }

        var rows = new List<CsvSourceRow>();
        List<string>? row = [];
        List<CsvCellSourceRange>? rowSourceRanges = [];
        var field = new StringBuilder();
        var fieldLength = 0;
        var fieldStart = 0;
        var rowStart = 0;
        var inQuotes = false;
        var totalRows = 0;
        var totalColumns = 0;
        var currentColumnCount = 0;
        long nextCancellationCheck = CancellationCheckInterval;

        for (var index = 0; index < csv.Length; index++)
        {
            if (index >= nextCancellationCheck)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nextCancellationCheck = (long)index + CancellationCheckInterval;
            }

            var character = csv[index];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < csv.Length && csv[index + 1] == '"')
                    {
                        AppendToField('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    AppendToField(character);
                }

                continue;
            }

            if (character == '"' && fieldLength == 0)
            {
                inQuotes = true;
            }
            else if (character == delimiter)
            {
                AddField(index);
                fieldStart = index + 1;
            }
            else if (character is '\r' or '\n')
            {
                AddField(index);
                var delimiterLength = character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n'
                    ? 2
                    : 1;
                AddRow(index, delimiterLength);
                if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n')
                {
                    index++;
                }

                fieldStart = index + 1;
                rowStart = index + 1;
            }
            else
            {
                AppendToField(character);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 行末の改行はレコード終端であり、その後ろに空レコードを作らない。
        if (csv[^1] is not ('\r' or '\n') || inQuotes)
        {
            AddField(csv.Length);
            AddRow(csv.Length, 0);
        }

        return new CsvSourceDocument(rows, totalRows, totalColumns, inQuotes);

        bool ShouldStoreCurrentField()
            => storeAll
                || totalRows < MaxPreviewRows && currentColumnCount < MaxPreviewColumns;

        void AppendToField(char character)
        {
            if (ShouldStoreCurrentField())
            {
                field.Append(character);
            }

            fieldLength++;
        }

        void AddField(int endOffset)
        {
            if (ShouldStoreCurrentField())
            {
                row!.Add(field.ToString());
                rowSourceRanges!.Add(new CsvCellSourceRange(fieldStart, endOffset - fieldStart));
            }

            currentColumnCount++;
            field.Clear();
            fieldLength = 0;
        }

        void AddRow(int contentEnd, int delimiterLength)
        {
            totalRows++;
            totalColumns = Math.Max(totalColumns, currentColumnCount);
            if (storeAll || totalRows <= MaxPreviewRows)
            {
                rows.Add(new CsvSourceRow(
                    row!.ToArray(),
                    rowSourceRanges!.ToArray(),
                    rowStart,
                    contentEnd - rowStart,
                    delimiterLength));
            }

            if (storeAll || totalRows < MaxPreviewRows)
            {
                row = [];
                rowSourceRanges = [];
            }
            else
            {
                row = null;
                rowSourceRanges = null;
            }

            currentColumnCount = 0;
        }
    }
}
