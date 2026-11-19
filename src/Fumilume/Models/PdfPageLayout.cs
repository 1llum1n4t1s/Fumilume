using Avalonia;

namespace Fumilume.Models;

/// <summary>
/// 表示位置をページ基準で表す。倍率や配置が変わっても、同じ場所を指し続けるために使う。
/// 割合はページ内の位置で、ページの外（余白・ページ間隔）では範囲外の値も取る。
/// </summary>
public readonly record struct PdfScrollAnchor(int PageIndex, double FractionY, double FractionX);

/// <summary>
/// 全ページを縦につなげた配置を計算する。UI に依存しない純粋な計算で、
/// 各ページの位置・全体の寸法・表示範囲・スクロール位置との相互変換を担う。
/// </summary>
public sealed class PdfPageLayout
{
    /// <summary>全ページを囲む外側の余白。</summary>
    public const double Margin = 24;

    /// <summary>ページとページの間隔。</summary>
    public const double PageGap = 12;

    private readonly double[] _tops;
    private readonly double[] _lefts;
    private readonly double[] _widths;
    private readonly double[] _heights;

    public PdfPageLayout(IReadOnlyList<Size> pageSizes, double zoom, double availableWidth)
    {
        ArgumentNullException.ThrowIfNull(pageSizes);
        PageCount = pageSizes.Count;
        Zoom = zoom;
        _tops = new double[PageCount];
        _lefts = new double[PageCount];
        _widths = new double[PageCount];
        _heights = new double[PageCount];

        var widest = 0.0;
        for (var index = 0; index < PageCount; index++)
        {
            _widths[index] = Scale(pageSizes[index].Width, zoom);
            _heights[index] = Scale(pageSizes[index].Height, zoom);
            widest = Math.Max(widest, _widths[index]);
        }

        // 表示領域より狭いページ群は中央に寄せ、広い場合は横スクロールにする。
        InnerWidth = Math.Max(widest, double.IsFinite(availableWidth) ? Math.Max(availableWidth, 0) : 0);
        ContentWidth = InnerWidth + 2 * Margin;
        var y = Margin;
        for (var index = 0; index < PageCount; index++)
        {
            _tops[index] = y;
            _lefts[index] = Margin + (InnerWidth - _widths[index]) / 2;
            y += _heights[index] + (index < PageCount - 1 ? PageGap : 0);
        }

        ContentHeight = y + Margin;
    }

    public int PageCount { get; }

    public double Zoom { get; }

    /// <summary>余白を除いた、ページを並べる領域の幅。</summary>
    public double InnerWidth { get; }

    public double ContentWidth { get; }

    public double ContentHeight { get; }

    public double Top(int index) => _tops[index];

    public double Left(int index) => _lefts[index];

    public double Width(int index) => _widths[index];

    public double Height(int index) => _heights[index];

    public double MaxOffsetX(double viewportWidth) => Math.Max(0, ContentWidth - viewportWidth);

    public double MaxOffsetY(double viewportHeight) => Math.Max(0, ContentHeight - viewportHeight);

    /// <summary>縦位置 <paramref name="y"/> を含むページ。ページ間隔は次のページに含め、範囲外は端のページへ寄せる。</summary>
    public int IndexAt(double y)
    {
        if (PageCount == 0) return -1;
        var index = LastIndexWithTopAtOrBefore(y);
        if (index < 0) return 0;
        return y >= _tops[index] + _heights[index] && index < PageCount - 1 ? index + 1 : index;
    }

    /// <summary>縦 <paramref name="offset"/> から <paramref name="height"/> の範囲に掛かるページの範囲。</summary>
    public (int First, int Last) VisibleRange(double offset, double height)
    {
        if (PageCount == 0) return (0, -1);
        var first = IndexAt(offset);
        var last = Math.Max(first, LastIndexWithTopAtOrBefore(offset + height - 0.0001));
        return (first, last);
    }

    /// <summary>表示領域の基準点（0 = 上端・左端、0.5 = 中心）にあるページ内の位置を求める。</summary>
    public PdfScrollAnchor AnchorAt(
        double offsetX, double offsetY, double viewportWidth, double viewportHeight, double reference)
    {
        if (PageCount == 0) return new PdfScrollAnchor(0, 0, 0);
        var y = offsetY + viewportHeight * reference;
        var x = offsetX + viewportWidth * reference;
        var index = IndexAt(y);
        return new PdfScrollAnchor(index, (y - _tops[index]) / _heights[index], (x - _lefts[index]) / _widths[index]);
    }

    /// <summary>基準点が <paramref name="anchor"/> の位置に来るスクロール量。範囲内へ丸める。</summary>
    public Vector OffsetFor(
        PdfScrollAnchor anchor, double viewportWidth, double viewportHeight, double reference)
    {
        if (PageCount == 0) return default;
        var index = Math.Clamp(anchor.PageIndex, 0, PageCount - 1);
        var y = _tops[index] + anchor.FractionY * _heights[index] - viewportHeight * reference;
        var x = _lefts[index] + anchor.FractionX * _widths[index] - viewportWidth * reference;
        return new Vector(
            Math.Clamp(x, 0, MaxOffsetX(viewportWidth)),
            Math.Clamp(y, 0, MaxOffsetY(viewportHeight)));
    }

    /// <summary>ページの先頭を表示領域の上端へ出すスクロール量（少し余白を残す）。</summary>
    public double OffsetToPageTop(int index, double viewportHeight)
    {
        if (PageCount == 0) return 0;
        index = Math.Clamp(index, 0, PageCount - 1);
        if (index == 0) return 0;
        return Math.Clamp(_tops[index] - PageGap, 0, MaxOffsetY(viewportHeight));
    }

    private int LastIndexWithTopAtOrBefore(double y)
    {
        int low = 0, high = PageCount - 1, found = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (_tops[middle] <= y)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }

    private static double Scale(double size, double zoom)
    {
        var value = size * zoom;
        return double.IsFinite(value) && value > 0 ? value : 1;
    }
}
