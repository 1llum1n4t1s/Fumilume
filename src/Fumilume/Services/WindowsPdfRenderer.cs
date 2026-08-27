using Avalonia.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Fumilume.Services;

public interface IPdfRenderer : IDisposable
{
    int PageCount { get; }

    Task<Bitmap> RenderAsync(int pageIndex, double zoom, CancellationToken cancellationToken = default);
}

/// <summary>Windows 標準の PDF レンダラーを使い、表示中の 1 ページだけを画像化する。</summary>
public sealed class WindowsPdfRenderer : IPdfRenderer
{
    private const uint MaximumDimension = 8192;
    private readonly PdfDocument _document;

    private WindowsPdfRenderer(PdfDocument document) => _document = document;

    public int PageCount => checked((int)_document.PageCount);

    public static async Task<WindowsPdfRenderer> OpenAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var document = await PdfDocument.LoadFromFileAsync(file);
        return new WindowsPdfRenderer(document);
    }

    public async Task<Bitmap> RenderAsync(
        int pageIndex,
        double zoom,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pageIndex < 0 || pageIndex >= PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        using var page = _document.GetPage(checked((uint)pageIndex));
        using var randomAccessStream = new InMemoryRandomAccessStream();
        var safeZoom = Math.Clamp(zoom, 0.25, 4.0);
        var destinationWidth = (uint)Math.Clamp(
            Math.Round(page.Size.Width * safeZoom),
            1,
            MaximumDimension);
        var destinationHeight = (uint)Math.Clamp(
            Math.Round(page.Size.Height * safeZoom),
            1,
            MaximumDimension);
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = destinationWidth,
            DestinationHeight = destinationHeight,
        };

        await page.RenderToStreamAsync(randomAccessStream, options);
        cancellationToken.ThrowIfCancellationRequested();
        randomAccessStream.Seek(0);
        using var stream = randomAccessStream.AsStreamForRead();
        return new Bitmap(stream);
    }

    public void Dispose()
    {
        // PdfDocument 自体は IDisposable ではない。各 PdfPage と描画ストリームを都度破棄する。
    }
}
