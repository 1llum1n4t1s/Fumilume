using Avalonia;
using Fumilume.Models;

namespace Fumilume.Tests;

public sealed class PdfPageLayoutTests
{
    private static readonly Size[] Sizes = [new(600, 800), new(800, 600), new(800, 600)];

    [Fact]
    public void PagesAreStackedWithGapsMarginsAndCenteredHorizontally()
    {
        var layout = new PdfPageLayout(Sizes, 0.5, availableWidth: 300);

        Assert.Equal((300.0, 400.0), (layout.Width(0), layout.Height(0)));
        Assert.Equal((400.0, 300.0), (layout.Width(1), layout.Height(1)));
        Assert.Equal(24, layout.Top(0));
        Assert.Equal(24 + 400 + 12, layout.Top(1));
        Assert.Equal(24 + 400 + 12 + 300 + 12, layout.Top(2));
        // 内容の幅は、最も幅の広いページ（400）か表示領域（300）の大きい方に両側の余白を足した値。
        Assert.Equal(400 + 48, layout.ContentWidth);
        Assert.Equal(layout.Top(2) + 300 + 24, layout.ContentHeight);
        Assert.Equal(24, layout.Left(1));
        Assert.Equal(24 + 50, layout.Left(0));
    }

    [Fact]
    public void NarrowPagesAreCenteredInAWiderViewport()
    {
        var layout = new PdfPageLayout(Sizes, 0.25, availableWidth: 1000);

        Assert.Equal(1000 + 48, layout.ContentWidth);
        Assert.Equal(24 + (1000 - layout.Width(0)) / 2, layout.Left(0));
    }

    [Theory]
    [InlineData(-50, 0)]
    [InlineData(0, 0)]
    [InlineData(100, 0)]
    [InlineData(423, 0)]
    // ページ間隔は次のページに含める。
    [InlineData(425, 1)]
    [InlineData(437, 1)]
    [InlineData(740, 2)]
    [InlineData(750, 2)]
    [InlineData(99999, 2)]
    public void IndexAtResolvesGapsAndOutOfRangePositions(double y, int expected)
    {
        var layout = new PdfPageLayout(Sizes, 0.5, 300);

        // 0 ページ目は 24..424、1 ページ目は 436..736、2 ページ目は 748..1048。
        Assert.Equal(expected, layout.IndexAt(y));
    }

    [Fact]
    public void VisibleRangeCoversEveryPageTouchingTheViewport()
    {
        var layout = new PdfPageLayout(Sizes, 0.5, 300);

        Assert.Equal((0, 0), layout.VisibleRange(0, 300));
        Assert.Equal((0, 1), layout.VisibleRange(0, 500));
        Assert.Equal((1, 1), layout.VisibleRange(430, 300));
        Assert.Equal((0, 2), layout.VisibleRange(0, 2000));
        // ちょうど次のページの上端で終わる範囲は、次のページを含めない。
        Assert.Equal((0, 0), layout.VisibleRange(0, 436));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0)]
    public void AnchorsRoundTripAtTheSameZoom(double reference)
    {
        var layout = new PdfPageLayout(Sizes, 0.5, 300);

        var anchor = layout.AnchorAt(30, 500, 200, 300, reference);
        var offset = layout.OffsetFor(anchor, 200, 300, reference);

        Assert.Equal(30, offset.X, precision: 6);
        Assert.Equal(500, offset.Y, precision: 6);
    }

    [Fact]
    public void AnchorsKeepTheSamePagePositionWhenTheZoomChanges()
    {
        var before = new PdfPageLayout(Sizes, 0.5, 300);
        var after = new PdfPageLayout(Sizes, 1.0, 300);

        // 1 ページ目の中央が表示中心にある位置。
        var offsetY = before.Top(1) + before.Height(1) / 2 - 150;
        var anchor = before.AnchorAt(0, offsetY, 200, 300, 0.5);
        Assert.Equal(1, anchor.PageIndex);
        Assert.Equal(0.5, anchor.FractionY, precision: 6);

        var offset = after.OffsetFor(anchor, 200, 300, 0.5);
        Assert.Equal(after.Top(1) + after.Height(1) / 2 - 150, offset.Y, precision: 6);
    }

    [Fact]
    public void OffsetsAreClampedAndPageTopsLeaveASmallMargin()
    {
        var layout = new PdfPageLayout(Sizes, 0.5, 300);

        Assert.Equal(layout.ContentHeight - 300, layout.MaxOffsetY(300));
        Assert.Equal(0, layout.MaxOffsetY(100000));
        Assert.Equal(0, layout.OffsetToPageTop(0, 300));
        Assert.Equal(layout.Top(1) - 12, layout.OffsetToPageTop(1, 300));
        // 最終ページの下端を指す位置は、スクロールの限界へ丸める。
        Assert.Equal(736, layout.OffsetToPageTop(2, 300));
        Assert.Equal(layout.MaxOffsetY(300), layout.OffsetFor(new PdfScrollAnchor(2, 1, 0), 300, 300, 0).Y);
        Assert.Equal(0, layout.OffsetFor(new PdfScrollAnchor(0, -5, -5), 300, 300, 0).Y);
    }

    [Fact]
    public void EmptyDocumentsAndInvalidSizesDoNotBreakTheLayout()
    {
        var empty = new PdfPageLayout([], 1, 300);
        Assert.Equal(-1, empty.IndexAt(10));
        Assert.Equal((0, -1), empty.VisibleRange(0, 100));
        Assert.Equal(default(Vector), empty.OffsetFor(new PdfScrollAnchor(0, 0, 0), 100, 100, 0));

        var invalid = new PdfPageLayout([new Size(double.NaN, 0), new Size(100, 100)], 1, 100);
        Assert.True(double.IsFinite(invalid.ContentHeight) && invalid.ContentHeight > 0);
        Assert.Equal(1, invalid.Width(0));
    }
}
