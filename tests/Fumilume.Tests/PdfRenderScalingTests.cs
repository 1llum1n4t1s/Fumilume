using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

/// <summary>PDF の画像が、表示の拡大率に合わせた画素数で描かれ、デバイス画素へ 1 対 1 で置かれることを確認する。</summary>
[Collection(HeadlessAppCollection.Name)]
public sealed class PdfRenderScalingTests(HeadlessAppFixture fixture)
{
    public static bool IsRealRendering => HeadlessAppFixture.UsesSkia;

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void PageImagesMatchTheirDevicePixelsAtEachScaling(double scaling) => fixture.Run(() =>
    {
        using var scope = new Scope();
        scope.Window.SetRenderScaling(scaling);
        var renderer = new SizedRenderer();
        using var pdf = scope.Add(renderer);

        foreach (var fit in new[] { true, false })
        {
            // 幅に合わせた倍率と、手動の端数の倍率の両方で確かめる。
            MainWindowE2ETestHelper.Pump(fit
                ? pdf.FitWidthCommand.ExecuteAsync(null)
                : pdf.ZoomOutCommand.ExecuteAsync(null));
            Settle(scope.Window, pdf);
            scope.Window.FindControl<ScrollViewer>("PdfScrollViewer")!.Offset = new Vector(0, 13.3);
            Settle(scope.Window, pdf);

            var images = PageImages(scope.Window, pdf).ToArray();
            Assert.NotEmpty(images);
            Assert.All(images, image =>
            {
                var bitmap = Assert.IsAssignableFrom<Bitmap>(image.Source);
                var page = (PdfPageViewModel)image.DataContext!;
                Assert.Equal(
                    PdfPageLayout.RenderPixels(renderer.GetPageSize(page.Index), pdf.Zoom * scaling),
                    bitmap.PixelSize);
                var origin = image.TranslatePoint(default, scope.Window)!.Value;
                Assert.Equal(Math.Round(origin.X * scaling), origin.X * scaling, 3);
                Assert.Equal(Math.Round(origin.Y * scaling), origin.Y * scaling, 3);
                Assert.Equal(bitmap.PixelSize.Width, image.Bounds.Width * scaling, 3);
                Assert.Equal(bitmap.PixelSize.Height, image.Bounds.Height * scaling, 3);
            });
        }
    });

    [Fact]
    public void ScalingChangesRedrawTheSelectedTabAndBackgroundTabsWhenTheyAreSelected() => fixture.Run(() =>
    {
        using var scope = new Scope();
        scope.Window.SetRenderScaling(1);
        var firstRenderer = new SizedRenderer();
        var secondRenderer = new SizedRenderer();
        using var first = scope.Add(firstRenderer);
        Settle(scope.Window, first);
        using var second = scope.Add(secondRenderer);
        Settle(scope.Window, second);
        var zoom = second.Zoom;
        var width = second.GetPage(1)!.Width;
        var firstCount = firstRenderer.Zooms.Count;

        // 倍率の操作もウィンドウの大きさの変更もなく、拡大率の通知だけで描き直す。
        scope.Window.SetRenderScaling(2);
        Settle(scope.Window, second);

        Assert.Equal(zoom, second.Zoom, 3);
        Assert.InRange(Math.Abs(width - second.GetPage(1)!.Width), 0, 1);
        Assert.Equal(second.Zoom * 2, secondRenderer.Zooms[^1], 10);
        Assert.Equal(
            PdfPageLayout.RenderPixels(secondRenderer.GetPageSize(0), second.Zoom * 2),
            second.GetPage(1)!.Image!.PixelSize);
        // 選択していないタブは、選ぶまで描き直さない。
        Assert.Equal(firstCount, firstRenderer.Zooms.Count);

        var secondCount = secondRenderer.Zooms.Count;
        scope.Window.FindControl<ScrollViewer>("PdfScrollViewer")!.Offset = new Vector(0, 5);
        Settle(scope.Window, second);
        Assert.Equal(secondCount, secondRenderer.Zooms.Count);

        scope.ViewModel.SelectedTab = first;
        Settle(scope.Window, first);
        Assert.Equal(first.Zoom * 2, firstRenderer.Zooms[^1], 10);
        Assert.Equal(
            PdfPageLayout.RenderPixels(firstRenderer.GetPageSize(0), first.Zoom * 2),
            first.GetPage(1)!.Image!.PixelSize);
    });

    /// <summary>実際の Windows レンダラーと Skia で描き、画素寸法と画面を成果物に残す。</summary>
    [Fact(SkipUnless = nameof(IsRealRendering), Skip = "実描画E2E: FUMILUME_E2E_RENDER=1 で個別実行")]
    public void RealPdfIsRenderedAtTheDevicePixelsOfItsImage() => fixture.Run(() =>
    {
        var directory = Environment.GetEnvironmentVariable("FUMILUME_E2E_ARTIFACTS");
        Assert.False(string.IsNullOrEmpty(directory), "FUMILUME_E2E_ARTIFACTS に保存先を指定してください。");
        Directory.CreateDirectory(directory);
        using var storage = new TemporaryStorage();
        var path = Path.Combine(directory, "sharpness.pdf");
        File.WriteAllBytes(path, PdfTextSelectionE2ETests.CreateFixturePdf());
        var window = new MainWindow(new AppSettings { RestoreSession = false, CheckUpdatesOnStartup = false });
        var viewModel = (MainWindowViewModel)window.DataContext!;
        var records = new List<object>();
        var mismatches = new List<string>();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            MainWindowE2ETestHelper.Pump(viewModel.OpenPathsAsync([path]));
            var pdf = viewModel.SelectedPdf!;
            foreach (var scaling in new[] { 2.0, 1.0, 1.5, 1.25, 1.75 })
            {
                window.SetRenderScaling(scaling);
                foreach (var mode in new[] { "actual", "fit-width" })
                {
                    MainWindowE2ETestHelper.Pump(mode == "actual"
                        ? pdf.ActualSizeCommand.ExecuteAsync(null)
                        : pdf.FitWidthCommand.ExecuteAsync(null));
                    MainWindowE2ETestHelper.Pump(pdf.NavigateAsync(1));
                    Settle(window, pdf);
                    var viewer = window.FindControl<ScrollViewer>("PdfScrollViewer")!;
                    // 端数のスクロール位置でも、画像はデバイス画素の境界に置かれる。
                    viewer.Offset = new Vector(0, 13.3);
                    Settle(window, pdf);
                    foreach (var image in PageImages(window, pdf))
                    {
                        var page = (PdfPageViewModel)image.DataContext!;
                        if (image.Source is not Bitmap bitmap) continue;
                        var origin = image.TranslatePoint(default, window)!.Value;
                        var record = new
                        {
                            scaling, mode, pdf.Zoom, page = page.Number,
                            BoundsWidth = image.Bounds.Width, BoundsHeight = image.Bounds.Height,
                            DeviceX = origin.X * scaling, DeviceY = origin.Y * scaling,
                            DeviceWidth = image.Bounds.Width * scaling, DeviceHeight = image.Bounds.Height * scaling,
                            BitmapWidth = bitmap.PixelSize.Width, BitmapHeight = bitmap.PixelSize.Height,
                        };
                        records.Add(record);
                        if (!IsDevicePixel(record.DeviceX, 0) || !IsDevicePixel(record.DeviceY, 0)
                            || !IsDevicePixel(record.DeviceWidth, record.BitmapWidth)
                            || !IsDevicePixel(record.DeviceHeight, record.BitmapHeight))
                            mismatches.Add(JsonSerializer.Serialize(record));
                        if (page.Number == 1) bitmap.Save(Path.Combine(directory, $"bitmap-{mode}-x{scaling}.png"), PngBitmapEncoderOptions.Default);
                    }

                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    frame.Save(Path.Combine(directory, $"window-{mode}-x{scaling}.png"), PngBitmapEncoderOptions.Default);
                }
            }

            // 上限の確認。4 倍を超える倍率はそのまま描き、1 辺の上限を超えるときは比率を保って縮める。
            var limits = new List<object>();
            var opening = WindowsPdfRenderer.OpenAsync(path);
            // UI スレッドで同期的に待つと継続が戻れないため、ディスパッチャーを回して待つ。
            MainWindowE2ETestHelper.Pump(opening);
            using (var renderer = opening.GetAwaiter().GetResult())
            {
                foreach (var zoom in new[] { 6.0, 40.0 })
                {
                    var task = renderer.RenderAsync(0, zoom);
                    MainWindowE2ETestHelper.Pump(task);
                    using var bitmap = task.GetAwaiter().GetResult();
                    var size = renderer.GetPageSize(0);
                    limits.Add(new { zoom, PageWidth = size.Width, PageHeight = size.Height,
                        bitmap.PixelSize.Width, bitmap.PixelSize.Height });
                    var factor = Math.Min(1, 8192 / (Math.Max(size.Width, size.Height) * zoom));
                    if (Math.Abs(bitmap.PixelSize.Width - size.Width * zoom * factor) > 1
                        || Math.Abs(bitmap.PixelSize.Height - size.Height * zoom * factor) > 1)
                        mismatches.Add($"limit zoom={zoom}: {bitmap.PixelSize}");
                }
            }

            File.WriteAllText(Path.Combine(directory, "pixels.json"),
                JsonSerializer.Serialize(new { records, limits, mismatches }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.NotEmpty(records);
            Assert.Empty(mismatches);
        }
        finally
        {
            viewModel.Options.RestoreSession = true;
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    private static bool IsDevicePixel(double value, int expected)
        => expected == 0 ? Math.Abs(value - Math.Round(value)) < 0.001 : Math.Abs(value - expected) < 0.001;

    private static IEnumerable<Image> PageImages(Window window, PdfDocumentViewModel pdf)
        => window.GetVisualDescendants().OfType<Image>()
            .Where(image => image.DataContext is PdfPageViewModel page && ReferenceEquals(page.Owner, pdf));

    private static void Settle(Window window, PdfDocumentViewModel pdf)
    {
        var timer = Stopwatch.StartNew();
        var stable = 0;
        while (stable < 5)
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), "PDF の表示が30秒以内に安定しませんでした。");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            Dispatcher.UIThread.RunJobs();
            stable = pdf.ScrollRequest is null && pdf.WhenIdleAsync().IsCompleted ? stable + 1 : 0;
            Thread.Yield();
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly TemporaryStorage _storage = new();

        public Scope()
        {
            Window = new MainWindow(new AppSettings { RestoreSession = false, CheckUpdatesOnStartup = false });
            ViewModel = (MainWindowViewModel)Window.DataContext!;
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public MainWindow Window { get; }

        public MainWindowViewModel ViewModel { get; }

        public PdfDocumentViewModel Add(IPdfRenderer renderer)
        {
            var pdf = new PdfDocumentViewModel(
                Path.Combine(_storage.Path, $"{Guid.NewGuid():N}.pdf"), renderer, _ => Task.CompletedTask);
            ViewModel.Tabs.Add(pdf);
            ViewModel.SelectedTab = pdf;
            return pdf;
        }

        public void Dispose()
        {
            ViewModel.Options.RestoreSession = true;
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            _storage.Dispose();
        }
    }

    /// <summary>実レンダラーと同じ寸法の画像を返す。寸法が端数になるページを使う。</summary>
    private sealed class SizedRenderer : IPdfRenderer
    {
        public List<double> Zooms { get; } = [];

        public int PageCount => 4;

        public Size GetPageSize(int pageIndex) => pageIndex % 2 == 0 ? new(595.3, 841.9) : new(841.9, 595.3);

        public Task<Bitmap> RenderAsync(int pageIndex, double zoom, CancellationToken cancellationToken = default)
        {
            Zooms.Add(zoom);
            return Task.FromResult<Bitmap>(new WriteableBitmap(
                PdfPageLayout.RenderPixels(GetPageSize(pageIndex), zoom), new Vector(96, 96)));
        }

        public void Dispose()
        {
        }
    }
}
