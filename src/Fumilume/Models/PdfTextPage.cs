using Avalonia;

namespace Fumilume.Models;

/// <summary>表示回転済みページの左上原点で表した、選択可能な文字。</summary>
public sealed record PdfTextCharacter(string Text, Rect Bounds, string SeparatorBefore = "");

public sealed record PdfTextPage(Size Size, IReadOnlyList<PdfTextCharacter> Characters)
{
    public static PdfTextPage Empty { get; } = new(default, Array.Empty<PdfTextCharacter>());
}
