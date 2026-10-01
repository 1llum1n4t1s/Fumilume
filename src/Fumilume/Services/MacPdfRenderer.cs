#if !WINDOWS
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Fumilume.Services;

public interface IPdfRenderer : IDisposable
{
    int PageCount { get; }

    Size GetPageSize(int pageIndex);

    Task<Bitmap> RenderAsync(int pageIndex, double zoom, CancellationToken cancellationToken = default);
}

/// <summary>macOS 標準の Core Graphics を使い、表示中の 1 ページだけを画像化する。</summary>
public sealed class MacPdfRenderer : IPdfRenderer
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const uint MaximumDimension = 8192;
    private const uint BitmapByteOrder32Little = 2U << 12;
    private const uint ImageAlphaPremultipliedFirst = 2;
    private readonly object _sync = new();
    private readonly Size[] _pageSizes;
    private nint _document;
    private int _disposed;

    private MacPdfRenderer(nint document, Size[] pageSizes)
    {
        _document = document;
        _pageSizes = pageSizes;
    }

    public int PageCount => _pageSizes.Length;

    public static Task<MacPdfRenderer> OpenAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("PDF ファイルが見つかりません。", fullPath);
        }

        // ページ数・サイズの読込も UI スレッドを占有しないようにする。
        return Task.Run(() => OpenCore(fullPath));
    }

    private static MacPdfRenderer OpenCore(string fullPath)
    {
        var provider = CGDataProviderCreateWithFilename(fullPath);
        if (provider == 0)
        {
            throw new InvalidDataException("PDF ファイルを読み込めませんでした。");
        }

        nint document;
        try
        {
            document = CGPDFDocumentCreateWithProvider(provider);
        }
        finally
        {
            CGDataProviderRelease(provider);
        }

        if (document == 0)
        {
            throw new InvalidDataException("有効な PDF ファイルではありません。");
        }

        try
        {
            var pageCount = checked((int)CGPDFDocumentGetNumberOfPages(document));
            if (pageCount == 0)
            {
                throw new InvalidDataException("PDF にページがありません。");
            }

            var pageSizes = new Size[pageCount];
            for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                pageSizes[pageIndex] = GetPageSizeCore(GetPage(document, pageIndex));
            }

            return new MacPdfRenderer(document, pageSizes);
        }
        catch
        {
            CGPDFDocumentRelease(document);
            throw;
        }
    }

    public Size GetPageSize(int pageIndex)
    {
        ValidatePageIndex(pageIndex);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // 不変のメタデータなので、ネイティブ描画の終了を待たずに取得できる。
        return _pageSizes[pageIndex];
    }

    public Task<Bitmap> RenderAsync(
        int pageIndex,
        double zoom,
        CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        if (!double.IsFinite(zoom) || zoom <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(zoom));
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => RenderCore(pageIndex, zoom, cancellationToken), cancellationToken);
    }

    private Bitmap RenderCore(int pageIndex, double zoom, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();

            var page = GetPage(_document, pageIndex);
            var pageSize = _pageSizes[pageIndex];
            var safeZoom = Math.Clamp(zoom, 0.001, 4.0);
            var width = checked((int)Math.Clamp(
                Math.Round(pageSize.Width * safeZoom),
                1,
                MaximumDimension));
            var height = checked((int)Math.Clamp(
                Math.Round(pageSize.Height * safeZoom),
                1,
                MaximumDimension));
            var bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            try
            {
                using var framebuffer = bitmap.Lock();
                var colorSpace = CGColorSpaceCreateDeviceRGB();
                if (colorSpace == 0)
                {
                    throw new InvalidOperationException("PDF 描画用の色空間を作成できませんでした。");
                }

                nint context = 0;
                try
                {
                    context = CGBitmapContextCreate(
                        framebuffer.Address,
                        checked((nuint)width),
                        checked((nuint)height),
                        8,
                        checked((nuint)framebuffer.RowBytes),
                        colorSpace,
                        BitmapByteOrder32Little | ImageAlphaPremultipliedFirst);
                    if (context == 0)
                    {
                        throw new InvalidOperationException("PDF 描画用のビットマップを作成できませんでした。");
                    }

                    var destination = new CGRect(0, 0, width, height);
                    CGContextSetRGBFillColor(context, 1, 1, 1, 1);
                    CGContextFillRect(context, destination);
                    // ビットマップコンテキストには画面用の上下反転を加えず、
                    // PDF の回転と出力サイズへの変換を Core Graphics に任せる。
                    var transform = CGPDFPageGetDrawingTransform(
                        page,
                        CGPDFBox.Crop,
                        destination,
                        0,
                        true);
                    CGContextConcatCTM(context, transform);
                    CGContextSetInterpolationQuality(context, 3);
                    CGContextDrawPDFPage(context, page);
                    CGContextFlush(context);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    if (context != 0)
                    {
                        CGContextRelease(context);
                    }

                    CGColorSpaceRelease(colorSpace);
                }

                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
    }

    private static Size GetPageSizeCore(nint page)
    {
        var box = GetPageBox(page);
        var width = Math.Abs(box.Size.Width);
        var height = Math.Abs(box.Size.Height);
        var rotation = NormalizeRotation(CGPDFPageGetRotationAngle(page));
        return rotation is 90 or 270
            ? new Size(height, width)
            : new Size(width, height);
    }

    private static CGRect GetPageBox(nint page)
    {
        var box = CGPDFPageGetBoxRect(page, CGPDFBox.Crop);
        if (box.Size.Width > 0 && box.Size.Height > 0)
        {
            return box;
        }

        box = CGPDFPageGetBoxRect(page, CGPDFBox.Media);
        if (box.Size.Width <= 0 || box.Size.Height <= 0)
        {
            throw new InvalidDataException("PDF ページのサイズを取得できませんでした。");
        }

        return box;
    }

    private static nint GetPage(nint document, int pageIndex)
    {
        var page = CGPDFDocumentGetPage(document, checked((nuint)(pageIndex + 1)));
        return page != 0
            ? page
            : throw new InvalidDataException("PDF ページを読み込めませんでした。");
    }

    private void ValidatePageIndex(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
    }

    private static int NormalizeRotation(int rotation)
    {
        rotation %= 360;
        return rotation < 0 ? rotation + 360 : rotation;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 描画中の document/page はその終了まで保持する。UI 上のタブ破棄は
        // 待たせず、以後の描画を拒否したうえで同じロック内で一度だけ解放する。
        _ = Task.Run(() =>
        {
            lock (_sync)
            {
                CGPDFDocumentRelease(_document);
                _document = 0;
            }
        });
    }

    private enum CGPDFBox
    {
        Media,
        Crop,
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGPoint(double x, double y)
    {
        public readonly double X = x;
        public readonly double Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGSize(double width, double height)
    {
        public readonly double Width = width;
        public readonly double Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGRect(double x, double y, double width, double height)
    {
        public readonly CGPoint Origin = new(x, y);
        public readonly CGSize Size = new(width, height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGAffineTransform
    {
        public readonly double A;
        public readonly double B;
        public readonly double C;
        public readonly double D;
        public readonly double Tx;
        public readonly double Ty;
    }

    [DllImport(CoreGraphics)]
    private static extern nint CGDataProviderCreateWithFilename(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename);

    [DllImport(CoreGraphics)]
    private static extern void CGDataProviderRelease(nint provider);

    [DllImport(CoreGraphics)]
    private static extern nint CGPDFDocumentCreateWithProvider(nint provider);

    [DllImport(CoreGraphics)]
    private static extern void CGPDFDocumentRelease(nint document);

    [DllImport(CoreGraphics)]
    private static extern nuint CGPDFDocumentGetNumberOfPages(nint document);

    [DllImport(CoreGraphics)]
    private static extern nint CGPDFDocumentGetPage(nint document, nuint pageNumber);

    [DllImport(CoreGraphics)]
    private static extern CGRect CGPDFPageGetBoxRect(nint page, CGPDFBox box);

    [DllImport(CoreGraphics)]
    private static extern int CGPDFPageGetRotationAngle(nint page);

    [DllImport(CoreGraphics)]
    private static extern CGAffineTransform CGPDFPageGetDrawingTransform(
        nint page,
        CGPDFBox box,
        CGRect rectangle,
        int rotate,
        [MarshalAs(UnmanagedType.I1)] bool preserveAspectRatio);

    [DllImport(CoreGraphics)]
    private static extern nint CGColorSpaceCreateDeviceRGB();

    [DllImport(CoreGraphics)]
    private static extern void CGColorSpaceRelease(nint colorSpace);

    [DllImport(CoreGraphics)]
    private static extern nint CGBitmapContextCreate(
        nint data,
        nuint width,
        nuint height,
        nuint bitsPerComponent,
        nuint bytesPerRow,
        nint colorSpace,
        uint bitmapInfo);

    [DllImport(CoreGraphics)]
    private static extern void CGContextRelease(nint context);

    [DllImport(CoreGraphics)]
    private static extern void CGContextSetRGBFillColor(
        nint context,
        double red,
        double green,
        double blue,
        double alpha);

    [DllImport(CoreGraphics)]
    private static extern void CGContextFillRect(nint context, CGRect rectangle);

    [DllImport(CoreGraphics)]
    private static extern void CGContextConcatCTM(nint context, CGAffineTransform transform);

    [DllImport(CoreGraphics)]
    private static extern void CGContextSetInterpolationQuality(nint context, int quality);

    [DllImport(CoreGraphics)]
    private static extern void CGContextDrawPDFPage(nint context, nint page);

    [DllImport(CoreGraphics)]
    private static extern void CGContextFlush(nint context);
}
#endif
