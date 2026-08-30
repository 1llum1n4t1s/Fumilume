using Fumilume.Views;

namespace Fumilume.Tests;

public sealed class EditorMinimapTests
{
    [Theory]
    [InlineData(-20, 100, 200, 1)]
    [InlineData(0, 100, 200, 1)]
    [InlineData(50, 100, 200, 101)]
    [InlineData(100, 100, 200, 200)]
    [InlineData(120, 100, 200, 200)]
    public void PointerPositionMapsToAClampedDocumentLine(
        double y,
        double height,
        int lineCount,
        int expected)
        => Assert.Equal(expected, EditorMinimap.MapPointToLine(y, height, lineCount));

    [Theory]
    [InlineData("    value", 4, 5)]
    [InlineData("\tvalue", 4, 5)]
    [InlineData("   ", 3, 0)]
    public void LineShapeKeepsIndentAndVisibleLength(string text, int indent, int length)
    {
        var metrics = EditorMinimap.MeasureLine(text);

        Assert.Equal(indent, metrics.Indent);
        Assert.Equal(length, metrics.Length);
    }
}
