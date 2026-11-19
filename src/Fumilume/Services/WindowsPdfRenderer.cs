using Avalonia.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Fumilume.Services;

public interface IPdfRenderer : IDisposable
{
    int PageCount { get; }

    Avalonia.Size GetPageSize(int pageIndex);

    Task<Bitmap> RenderAsync(int pageIndex, double zoom, CancellationToken cancellationToken = default);
}

/// <summary>Windows 標準の PDF レンダラーを使い、要求されたページを 1 枚ずつ画像化する。ページ寸法は開くときに読む。</summary>
public sealed class WindowsPdfRenderer : IPdfRenderer
{
    private const uint MaximumDimension = 8192;
    private readonly PdfDocument _document;
    private readonly Avalonia.Size[] _pageSizes;

    private WindowsPdfRenderer(PdfDocument document, Avalonia.Size[] pageSizes)
    {
        _document = document;
        _pageSizes = pageSizes;
    }

    public int PageCount => _pageSizes.Length;

    public Avalonia.Size GetPageSize(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
        return _pageSizes[pageIndex];
    }

    public static async Task<WindowsPdfRenderer> OpenAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var document = await PdfDocument.LoadFromFileAsync(file);
        // 連続表示は全ページの配置に寸法を使う。開くときに一度だけ読み、UI スレッドを塞がない。
        var pageSizes = await Task.Run(() =>
        {
            var sizes = new Avalonia.Size[checked((int)document.PageCount)];
            for (var index = 0; index < sizes.Length; index++)
            {
                using var page = document.GetPage((uint)index);
                sizes[index] = new Avalonia.Size(page.Size.Width, page.Size.Height);
            }

            return sizes;
        });
        return new WindowsPdfRenderer(document, pageSizes);
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

        // 画像化は表示範囲のページだけに限る。全ページを同時に画像化しない。
        using var page = _document.GetPage(checked((uint)pageIndex));
        using var randomAccessStream = new InMemoryRandomAccessStream();
        // フィット表示では 25% 未満も必要になる。画像の確保量は従来の上限で抑える。
        var safeZoom = Math.Clamp(zoom, 0.001, 4.0);
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
