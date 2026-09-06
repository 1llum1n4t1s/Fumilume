using Avalonia.Media.Imaging;
using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class PdfDocumentViewModelTests(HeadlessAppFixture fixture)
{
    [Fact]
    public async Task WindowsRendererLoadsAndRendersARealOnePagePdf()
    {
        // Bitmap のデコードサービスは Avalonia 初期化後に解決される。
        fixture.Run(() => { });
        var path = Path.Combine(Path.GetTempPath(), $"fumilume-pdf-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, CreateMinimalPdf());
            using var renderer = await WindowsPdfRenderer.OpenAsync(path);
            using var bitmap = await renderer.RenderAsync(0, 1.0, TestContext.Current.CancellationToken);

            Assert.Equal(1, renderer.PageCount);
            Assert.True(bitmap.PixelSize.Width > 0);
            Assert.True(bitmap.PixelSize.Height > 0);
            var size = renderer.GetPageSize(0);
            Assert.True(size.Width > 0 && size.Height > 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => renderer.GetPageSize(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => renderer.GetPageSize(1));
            using var small = await renderer.RenderAsync(0, 0.1, TestContext.Current.CancellationToken);
            // Headless の Bitmap は固定サイズの代替画像なので、描画成功を確認する。
            Assert.True(small.PixelSize.Width > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NavigationStaysInsideThePdfPageRange()
    {
        using var renderer = new StubPdfRenderer(pageCount: 3);
        using var document = new PdfDocumentViewModel(@"C:\tmp\guide.pdf", renderer, _ => Task.CompletedTask);

        await document.NavigateAsync(2);
        await document.NavigateAsync(99);

        Assert.Equal(3, document.CurrentPage);
        Assert.Equal("3 / 3", document.PageStatus);
        Assert.True(document.IsPdfTab);
        Assert.False(document.IsDocumentTab);
    }

    /// <summary>
    /// セッション復元は値を入れるだけでは足りない。ページ送りと拡大のコマンドは「今と同じ値」で
    /// 早期に戻るため、復元後に描き直さないと画像が 1 ページ目・等倍のまま残る。
    /// </summary>
    [Fact]
    public async Task RestoringAPdfViewStateRedrawsAtThatPageAndZoom()
    {
        using var renderer = new StubPdfRenderer(pageCount: 3);
        using var document = new PdfDocumentViewModel(@"C:\tmp\guide.pdf", renderer, _ => Task.CompletedTask);
        var rendersWhileOpening = renderer.Renders.Count;

        await MainWindowViewModel.ApplyPdfViewStateAsync(
            document,
            new SessionTabState { PdfPage = 3, PdfZoom = 2.0 });

        Assert.Equal(3, document.CurrentPage);
        Assert.Equal(2.0, document.Zoom);
        Assert.True(renderer.Renders.Count > rendersWhileOpening, "復元後に描き直していません。");
        Assert.Equal((2, 2.0), renderer.Renders[^1]);
    }

    [Fact]
    public async Task ARestoredPdfZoomStaysInsideTheSupportedRange()
    {
        using var renderer = new StubPdfRenderer(pageCount: 1);
        using var document = new PdfDocumentViewModel(@"C:\tmp\guide.pdf", renderer, _ => Task.CompletedTask);

        await MainWindowViewModel.ApplyPdfViewStateAsync(
            document,
            new SessionTabState { PdfPage = 9, PdfZoom = 99 });

        Assert.Equal(1, document.CurrentPage);
        Assert.Equal(4.0, document.Zoom);
    }

    [Fact]
    public async Task WidthFitFollowsViewportAndDifferentPageSizes()
    {
        fixture.Run(() => { });
        using var document = new PdfDocumentViewModel(@"C:\tmp\guide.pdf", new StubPdfRenderer(2), _ => Task.CompletedTask);

        await document.UpdateViewportAsync(new(300, 500));
        Assert.Equal(PdfZoomMode.FitWidth, document.ZoomMode);
        Assert.Equal(0.5, document.Zoom);
        Assert.Equal(300, document.PageWidth);
        Assert.Equal(400, document.PageHeight);

        await document.NavigateAsync(2);
        Assert.Equal(300, document.PageWidth);
        Assert.Equal(225, document.PageHeight);
        await document.UpdateViewportAsync(new(480, 500));
        Assert.Equal(480, document.PageWidth);
        Assert.Equal(360, document.PageHeight);
    }

    [Fact]
    public async Task HeightFitAndManualZoomSwitchWithoutLosingTheirMeaning()
    {
        fixture.Run(() => { });
        using var document = new PdfDocumentViewModel(@"C:\tmp\guide.pdf", new StubPdfRenderer(2), _ => Task.CompletedTask);
        await document.UpdateViewportAsync(new(600, 400));
        await document.FitHeightCommand.ExecuteAsync(null);
        Assert.Equal(PdfZoomMode.FitHeight, document.ZoomMode);
        Assert.Equal(400, document.PageHeight);
        await document.NavigateAsync(2);
        Assert.Equal(400, document.PageHeight);

        await document.ActualSizeCommand.ExecuteAsync(null);
        await document.UpdateViewportAsync(new(300, 200));
        Assert.Equal(PdfZoomMode.Manual, document.ZoomMode);
        Assert.Equal(1, document.Zoom);
        await document.FitWidthCommand.ExecuteAsync(null);
        Assert.Equal(300, document.PageWidth);
        await document.ZoomInCommand.ExecuteAsync(null);
        Assert.Equal(PdfZoomMode.Manual, document.ZoomMode);
        Assert.Equal(0.46875, document.Zoom);
    }

    [Fact]
    public async Task FitHandlesSmallViewportsAndIgnoresInvalidOrUnchangedSizes()
    {
        fixture.Run(() => { });
        using var renderer = new StubPdfRenderer(1);
        using var document = new PdfDocumentViewModel(@"C:\tmp\guide.pdf", renderer, _ => Task.CompletedTask);
        await document.UpdateViewportAsync(new(60, 80));
        Assert.Equal(0.1, document.Zoom);
        var count = renderer.Renders.Count;
        await document.UpdateViewportAsync(new(60, 80));
        await document.UpdateViewportAsync(new(0, 0));
        await document.UpdateViewportAsync(new(double.NaN, 80));
        await document.UpdateViewportAsync(new(60, double.PositiveInfinity));
        Assert.Equal(count, renderer.Renders.Count);
        await document.UpdateViewportAsync(new(3000, 4000));
        Assert.Equal(5, document.Zoom);
        Assert.Equal(3000, document.PageWidth);

        // 同じ倍率への切り替えでもフィットを解除する。
        await document.UpdateViewportAsync(new(600, 800));
        await document.ActualSizeCommand.ExecuteAsync(null);
        await document.UpdateViewportAsync(new(300, 400));
        Assert.Equal(1, document.Zoom);
    }

    [Theory]
    [InlineData("FitWidth", 300, 225)]
    [InlineData("FitHeight", 400, 300)]
    [InlineData("unknown", 300, 225)]
    public async Task RestoredFitUsesTheCurrentViewport(string mode, double width, double height)
    {
        fixture.Run(() => { });
        using var document = new PdfDocumentViewModel(@"C:\tmp\guide.pdf", new StubPdfRenderer(2), _ => Task.CompletedTask);
        await MainWindowViewModel.ApplyPdfViewStateAsync(document,
            new SessionTabState { PdfPage = 2, PdfZoom = 2, PdfZoomMode = mode });
        await document.UpdateViewportAsync(new(300, 300));
        Assert.Equal(width, document.PageWidth);
        Assert.Equal(height, document.PageHeight);
    }

    internal sealed class StubPdfRenderer(int pageCount) : IPdfRenderer
    {
        public int PageCount { get; } = pageCount;

        public Avalonia.Size GetPageSize(int pageIndex) => pageIndex == 0 ? new(600, 800) : new(800, 600);

        /// <summary>描画要求の記録。復元が実際に描き直したかを見るために使う。</summary>
        public List<(int PageIndex, double Zoom)> Renders { get; } = [];

        public Task<Bitmap> RenderAsync(int pageIndex, double zoom, CancellationToken cancellationToken = default)
        {
            Renders.Add((pageIndex, zoom));
            using var stream = new MemoryStream(
            [
                137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
                0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 31, 21, 196, 137,
                0, 0, 0, 13, 73, 68, 65, 84, 8, 215, 99, 248, 207, 192, 240,
                31, 0, 5, 0, 1, 255, 137, 153, 61, 29, 0, 0, 0, 0, 73, 69,
                78, 68, 174, 66, 96, 130,
            ]);
            return Task.FromResult(new Bitmap(stream));
        }

        public void Dispose()
        {
        }
    }

    private static byte[] CreateMinimalPdf()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
        ];
        var builder = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(System.Text.Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }

        var xrefOffset = System.Text.Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture))
                .Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n")
            .Append(xrefOffset)
            .Append("\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(builder.ToString());
    }
}
