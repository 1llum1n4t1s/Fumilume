namespace Fumilume.Services;

/// <summary>CSV表上の連続したセル範囲。</summary>
public readonly record struct CsvCellRange(
    int Row,
    int Column,
    int RowCount,
    int ColumnCount);
