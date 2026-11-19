using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
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

    private const string Path1 = @"C:\tmp\guide.pdf";

    /// <summary>View が要求どおりに位置を適用した状態を作る（ViewModel 単体のテストには View がない）。</summary>
    internal static void ApplyPendingScroll(PdfDocumentViewModel pdf)
    {
        if (pdf.ScrollRequest is { } request)
        {
            pdf.CompleteScrollRequest(request.Id, request.OffsetX, request.OffsetY);
        }
    }

    /// <summary>利用者がスクロールした状態を作る。</summary>
    internal static void ScrollTo(PdfDocumentViewModel pdf, double offsetY)
    {
        ApplyPendingScroll(pdf);
        pdf.ReportScroll(0, offsetY);
    }

    private static async Task<PdfDocumentViewModel> OpenWithViewportAsync(
        IPdfRenderer renderer, double width = 300, double height = 500, PdfTextService? text = null)
    {
        var document = new PdfDocumentViewModel(Path1, renderer, _ => Task.CompletedTask, text);
        await document.UpdateViewportAsync(new(width, height));
        ApplyPendingScroll(document);
        return document;
    }

    [Fact]
    public async Task NavigationStaysInsideThePdfPageRange()
    {
        fixture.Run(() => { });
        using var renderer = new StubPdfRenderer(pageCount: 3);
        using var document = await OpenWithViewportAsync(renderer);

        await document.NavigateAsync(2);
        ApplyPendingScroll(document);
        Assert.Equal(2, document.CurrentPage);
        await document.NavigateAsync(99);
        ApplyPendingScroll(document);

        // 最終ページの先頭までは下げ切れなくても、明示した移動先を現在のページとして保つ。
        Assert.Equal(3, document.CurrentPage);
        Assert.Equal("3 / 3", document.PageStatus);
        Assert.Equal("3", document.PageInputText);
        Assert.True(document.IsPdfTab);
        Assert.False(document.IsDocumentTab);
        Assert.False(document.NextPageCommand.CanExecute(null));
        Assert.True(document.PreviousPageCommand.CanExecute(null));
    }

    /// <summary>
    /// 現在のページは表示範囲の中心にあるページ。ページ間隔は次のページとし、最上端は 1 ページ目、最下端は最終ページ。
    /// Stub は 600×800 と 800×600 のページで、(300, 500) のフィットは 0.375 倍になる。
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(20, 1)]
    [InlineData(100, 2)]
    [InlineData(291, 3)]
    [InlineData(984, 5)]
    [InlineData(985, 6)]
    public async Task CurrentPageFollowsTheCenterOfTheViewport(double offset, int expected)
    {
        fixture.Run(() => { });
        using var document = await OpenWithViewportAsync(new StubPdfRenderer(6));

        ScrollTo(document, offset);

        Assert.Equal(expected, document.CurrentPage);
        Assert.Equal(expected.ToString(), document.PageInputText);
    }

    [Fact]
    public async Task ExplicitNavigationHoldsUntilTheUserScrolls()
    {
        fixture.Run(() => { });
        using var document = await OpenWithViewportAsync(new StubPdfRenderer(6));

        await document.NavigateAsync(4);
        ApplyPendingScroll(document);
        Assert.Equal(4, document.CurrentPage);
        // 適用値と同じ位置の通知では動かない。
        document.ReportScroll(0, 798);
        Assert.Equal(4, document.CurrentPage);

        ScrollTo(document, 0);
        Assert.Equal(1, document.CurrentPage);
    }

    [Fact]
    public async Task PagesOutsideTheViewWindowReleaseTheirImages()
    {
        fixture.Run(() => { });
        using var renderer = new StubPdfRenderer(30);
        using var document = await OpenWithViewportAsync(renderer);

        Assert.Equal([1, 2, 3], document.VisiblePages.Select(page => page.Number));
        Assert.All(document.VisiblePages, page => Assert.NotNull(page.Image));
        var first = document.GetPage(1)!;
        Assert.NotNull(first.Image);

        for (var offset = 300; offset <= 4500; offset += 300)
        {
            ScrollTo(document, offset);
            await document.WhenIdleAsync();
        }

        Assert.Null(first.Image);
        Assert.Null(document.GetPage(1));
        Assert.InRange(document.VisiblePages.Count, 1, 6);
        Assert.All(document.VisiblePages, page => Assert.NotNull(page.Image));
        // 表示範囲（前後 1 ページを含む）の外側 2 ページ（表示範囲から 3 ページ）までは、画像を保持するために残す。
        var nearest = document.VisiblePages.Min(page => page.Number);
        Assert.NotNull(document.GetPage(nearest - 2));
        Assert.Null(document.GetPage(nearest - 3));
    }

    [Fact]
    public async Task RetainedImagesAreTrimmedToThePixelBudgetFarthestFirst()
    {
        fixture.Run(() => { });
        using var renderer = new StubPdfRenderer(30);
        using var document = new PdfDocumentViewModel(Path1, renderer, _ => Task.CompletedTask)
        {
            RetainedPixelLimit = 2,
        };
        await document.UpdateViewportAsync(new(300, 500));
        ApplyPendingScroll(document);

        for (var offset = 200; offset <= 1500; offset += 200)
        {
            ScrollTo(document, offset);
            await document.WhenIdleAsync();
        }

        var visible = document.VisiblePages.Select(page => page.Number).ToHashSet();
        for (var number = 1; number <= 30; number++)
        {
            if (visible.Contains(number)) continue;
            Assert.Null(document.GetPage(number)?.Image);
        }

        Assert.All(document.VisiblePages, page => Assert.NotNull(page.Image));
    }

    [Fact]
    public async Task AFailedPageIsNotRetriedByScrollingButIsRetriedWhenTheZoomChanges()
    {
        fixture.Run(() => { });
        using var renderer = new StubPdfRenderer(pageCount: 3) { FailFromPageIndex = 1 };
        using var document = await OpenWithViewportAsync(renderer);

        Assert.NotNull(document.GetPage(1)!.Image);
        Assert.Null(document.GetPage(1)!.ErrorMessage);
        Assert.Null(document.GetPage(2)!.Image);
        Assert.NotNull(document.GetPage(2)!.ErrorMessage);
        var count = renderer.Renders.Count;

        ScrollTo(document, 10);
        await document.WhenIdleAsync();
        ScrollTo(document, 0);
        await document.UpdateViewportAsync(new(300, 500));
        Assert.Equal(count, renderer.Renders.Count);
        Assert.NotNull(document.GetPage(2)!.ErrorMessage);

        await document.ZoomInCommand.ExecuteAsync(null);

        Assert.Equal(2, renderer.Renders.Count(render => render.PageIndex == 1));
        Assert.NotNull(document.GetPage(1)!.Image);
    }

    [Fact]
    public async Task ZoomKeepsTheViewportCenterOnTheSamePagePosition()
    {
        fixture.Run(() => { });
        using var document = await OpenWithViewportAsync(new StubPdfRenderer(6));
        // 0.375 倍で、2 ページ目の中央（上端 336、高さ 225）が表示中心に来る位置。
        ScrollTo(document, 336 + 112.5 - 274);
        Assert.Equal(2, document.CurrentPage);

        await document.ZoomInCommand.ExecuteAsync(null);

        Assert.Equal(PdfZoomMode.Manual, document.ZoomMode);
        Assert.Equal(0.46875, document.Zoom, precision: 10);
        var layout = new PdfPageLayout([new(600, 800), .. Enumerable.Repeat(new Avalonia.Size(800, 600), 5)], 0.46875, 300);
        var expected = layout.Top(1) + layout.Height(1) / 2 - 274;
        Assert.Equal(expected, document.ScrollRequest!.OffsetY, precision: 6);
        ApplyPendingScroll(document);
        Assert.Equal(2, document.CurrentPage);
    }

    [Fact]
    public async Task RestoringAPdfViewStateRedrawsAtThatPageAndZoom()
    {
        fixture.Run(() => { });
        using var renderer = new StubPdfRenderer(pageCount: 3);
        using var document = new PdfDocumentViewModel(Path1, renderer, _ => Task.CompletedTask);
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
        fixture.Run(() => { });
        using var renderer = new StubPdfRenderer(pageCount: 1);
        using var document = new PdfDocumentViewModel(Path1, renderer, _ => Task.CompletedTask);

        await MainWindowViewModel.ApplyPdfViewStateAsync(
            document,
            new SessionTabState { PdfPage = 9, PdfZoom = 99 });

        Assert.Equal(1, document.CurrentPage);
        Assert.Equal(4.0, document.Zoom);
    }

    [Fact]
    public async Task TheTopPositionRoundTripsThroughTheSessionState()
    {
        fixture.Run(() => { });
        using var document = await OpenWithViewportAsync(new StubPdfRenderer(6));

        await MainWindowViewModel.ApplyPdfViewStateAsync(document,
            new SessionTabState { PdfPage = 3, PdfPageOffset = 0.5, PdfZoom = 0.375, PdfZoomMode = "Manual" });

        // 0.375 倍で、3 ページ目（上端 573、高さ 225）の中ほどが表示上端に来る。
        Assert.Equal(573 + 112.5, document.ScrollRequest!.OffsetY, precision: 6);
        ApplyPendingScroll(document);
        Assert.Equal(3, document.CurrentPage);
        var (page, fraction) = document.CaptureTopPosition();
        Assert.Equal(2, page);
        Assert.Equal(0.5, fraction, precision: 6);

        // 旧形式（位置の割合なし）はそのページの先頭。範囲外・非数は 0。
        await MainWindowViewModel.ApplyPdfViewStateAsync(document, new SessionTabState { PdfPage = 4, PdfZoom = 0.375, PdfZoomMode = "Manual" });
        Assert.Equal(810, document.ScrollRequest!.OffsetY, precision: 6);
        foreach (var invalid in new[] { double.NaN, -1, 7, double.PositiveInfinity })
        {
            await MainWindowViewModel.ApplyPdfViewStateAsync(document,
                new SessionTabState { PdfPage = 4, PdfPageOffset = invalid, PdfZoom = 0.375, PdfZoomMode = "Manual" });
            Assert.Equal(810, document.ScrollRequest!.OffsetY, precision: 6);
        }

        await MainWindowViewModel.ApplyPdfViewStateAsync(document, new SessionTabState { PdfPage = 99, PdfZoom = 0.375, PdfZoomMode = "Manual" });
        Assert.Equal(1, document.CurrentPage);
        Assert.Equal(0, document.ScrollRequest!.OffsetY);
    }

    [Fact]
    public async Task WidthFitFollowsViewportAndDifferentPageSizes()
    {
        fixture.Run(() => { });
        using var document = await OpenWithViewportAsync(new StubPdfRenderer(2));

        // 全ページ共通の倍率で、最も幅の広いページ（800）が表示幅に収まる。
        Assert.Equal(PdfZoomMode.FitWidth, document.ZoomMode);
        Assert.Equal(0.375, document.Zoom);
        var first = document.GetPage(1)!;
        var second = document.GetPage(2)!;
        Assert.Equal((225.0, 300.0), (first.Width, first.Height));
        Assert.Equal((300.0, 225.0), (second.Width, second.Height));
        Assert.Equal(300 + 48, document.ContentWidth);
        Assert.Equal(61.5, first.Left);
        Assert.Equal(24, second.Left);

        await document.UpdateViewportAsync(new(480, 500));
        Assert.Equal(0.6, document.Zoom);
        Assert.Equal((480.0, 360.0), (second.Width, second.Height));
        Assert.Equal((360.0, 480.0), (first.Width, first.Height));
    }

    [Fact]
    public async Task HeightFitAndManualZoomSwitchWithoutLosingTheirMeaning()
    {
        fixture.Run(() => { });
        using var document = await OpenWithViewportAsync(new StubPdfRenderer(2), 600, 400);
        await document.FitHeightCommand.ExecuteAsync(null);
        Assert.Equal(PdfZoomMode.FitHeight, document.ZoomMode);
        // 最も高いページ（800）が表示高さに収まる。
        Assert.Equal(0.5, document.Zoom);
        Assert.Equal(400, document.GetPage(1)!.Height);

        await document.ActualSizeCommand.ExecuteAsync(null);
        await document.UpdateViewportAsync(new(300, 200));
        Assert.Equal(PdfZoomMode.Manual, document.ZoomMode);
        Assert.Equal(1, document.Zoom);
        await document.FitWidthCommand.ExecuteAsync(null);
        Assert.Equal(300, document.GetPage(2)!.Width);
        await document.ZoomInCommand.ExecuteAsync(null);
        Assert.Equal(PdfZoomMode.Manual, document.ZoomMode);
        Assert.Equal(0.46875, document.Zoom);
    }

    [Fact]
    public async Task FitHandlesSmallViewportsAndIgnoresInvalidOrUnchangedSizes()
    {
        fixture.Run(() => { });
        using var renderer = new StubPdfRenderer(1);
        using var document = await OpenWithViewportAsync(renderer, 60, 80);
        Assert.Equal(0.1, document.Zoom);
        Assert.NotNull(document.GetPage(1)!.Image);
        Assert.Null(document.GetPage(1)!.ErrorMessage);
        var count = renderer.Renders.Count;
        await document.UpdateViewportAsync(new(60, 80));
        await document.UpdateViewportAsync(new(0, 0));
        await document.UpdateViewportAsync(new(double.NaN, 80));
        await document.UpdateViewportAsync(new(60, double.PositiveInfinity));
        Assert.Equal(count, renderer.Renders.Count);
        await document.UpdateViewportAsync(new(3000, 4000));
        Assert.Equal(5, document.Zoom);
        Assert.Equal(3000, document.GetPage(1)!.Width);

        // 同じ倍率への切り替えでもフィットを解除する。
        await document.UpdateViewportAsync(new(600, 800));
        await document.ActualSizeCommand.ExecuteAsync(null);
        await document.UpdateViewportAsync(new(300, 400));
        Assert.Equal(1, document.Zoom);
    }

    // 表示領域 (300, 200) では、幅に合わせると 0.375 倍、高さに合わせると 0.25 倍になる。
    [Theory]
    [InlineData("FitWidth", 300, 225)]
    [InlineData("FitHeight", 200, 150)]
    [InlineData("unknown", 300, 225)]
    public async Task RestoredFitUsesTheCurrentViewport(string mode, double width, double height)
    {
        fixture.Run(() => { });
        using var document = new PdfDocumentViewModel(Path1, new StubPdfRenderer(2), _ => Task.CompletedTask);
        await MainWindowViewModel.ApplyPdfViewStateAsync(document,
            new SessionTabState { PdfPage = 2, PdfZoom = 2, PdfZoomMode = mode });
        await document.UpdateViewportAsync(new(300, 200));
        var second = document.GetPage(2)!;
        Assert.Equal(width, second.Width);
        Assert.Equal(height, second.Height);
        Assert.Equal(2, document.CurrentPage);
    }

    [Fact]
    public async Task RenderFailureKeepsOtherPagesAndLeavesNoStaleImageUnderTheError()
    {
        fixture.Run(() => { });
        using var renderer = new StubPdfRenderer(pageCount: 2) { FailFromPageIndex = 1 };
        using var document = await OpenWithViewportAsync(renderer);

        Assert.NotNull(document.GetPage(1)!.Image);
        Assert.Null(document.GetPage(1)!.ErrorMessage);
        Assert.NotNull(document.GetPage(2)!.ErrorMessage);
        Assert.Null(document.GetPage(2)!.Image);
        Assert.False(document.IsLoading);
    }

    [Fact]
    public void OldGenerationResultsAndFailuresAreIgnored() => fixture.Run(() =>
    {
        foreach (var failureFirst in new[] { true, false })
        {
            var renderer = new PausedRenderer(2);
            using var document = new PdfDocumentViewModel(Path1, renderer, _ => Task.CompletedTask);
            var open = document.UpdateViewportAsync(new(300, 500));
            var old = renderer.Requests[^1];
            var zoom = document.ZoomInCommand.ExecuteAsync(null);
            Assert.True(old.Token.IsCancellationRequested);
            Assert.True(document.IsLoading);
            if (failureFirst)
            {
                old.Completion.SetException(new IOException("古い描画"));
                Dispatcher.UIThread.RunJobs();
                Assert.Null(document.GetPage(1)!.ErrorMessage);
                Assert.True(document.IsLoading);
            }

            if (failureFirst)
            {
                Drain(renderer, [zoom, open]);
            }
            else
            {
                Drain(renderer, [zoom], old.Completion);
                old.Completion.SetException(new IOException("古い描画"));
                Pump(open);
            }

            Assert.All(document.VisiblePages, page =>
            {
                Assert.Null(page.ErrorMessage);
                Assert.NotNull(page.Image);
            });
            Assert.False(document.IsLoading);
        }
    });

    [Fact]
    public void ResultsArrivingAfterDisposeAreDiscarded() => fixture.Run(() =>
    {
        var renderer = new PausedRenderer(2);
        var document = new PdfDocumentViewModel(Path1, renderer, _ => Task.CompletedTask);
        var open = document.UpdateViewportAsync(new(300, 500));
        var request = renderer.Requests[^1];
        var page = document.GetPage(1)!;
        var late = NewBitmap();

        document.Dispose();
        request.Completion.SetResult(late);
        Pump(open);

        Assert.Null(page.Image);
        Assert.Empty(document.VisiblePages);
        Assert.Null(document.GetPage(1));
        Assert.Null(page.ErrorMessage);
        Assert.Throws<ObjectDisposedException>(() => late.PixelSize);
    });

    [Fact]
    public async Task SelectionStaysOnItsPageAcrossScrollingAndKeepsItsTextWhileOutOfView()
    {
        fixture.Run(() => { });
        var path = Path.Combine(Path.GetTempPath(), "Codex", "Fumilume", $"pdf-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        var pdfPath = Path.Combine(path, "text.pdf");
        try
        {
            File.WriteAllBytes(pdfPath, PdfTextSelectionE2ETests.CreateFixturePdf());
            using var service = new PdfTextService(pdfPath);
            using var document = await OpenWithViewportAsync(new StubPdfRenderer(6), text: service);
            await document.WhenIdleAsync();

            var page = Assert.IsType<PdfTextPage>(document.CurrentTextPage);
            Assert.NotEmpty(page.Characters);
            Assert.Null(document.TextSelectionStatus);
            document.SelectText(0, 0, 4);
            var selected = document.SelectedText;
            Assert.Equal("Alpha", selected);
            Assert.Equal(0, document.SelectionPageIndex);

            // スクロールしても選択は解除されず、見えなくなったページの文字情報も控えられてコピーできる。
            ScrollTo(document, 985);
            await document.WhenIdleAsync();
            Assert.Equal(6, document.CurrentPage);
            Assert.True(document.HasTextSelection);
            Assert.Equal(selected, document.SelectedText);
            Assert.DoesNotContain(document.VisiblePages, visible => visible.Number == 1);

            document.SelectAllOnPage(4);
            Assert.Equal(4, document.SelectionPageIndex);
            // 文字のないページ（画像だけ）の全選択は選択を解除する。
            document.SelectAllOnPage(5);
            Assert.False(document.HasTextSelection);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>同じ PDF を二重に開いても、タブは 1 個のままにする。</summary>
    [Fact]
    public async Task OpeningTheSamePdfTwiceKeepsOneTab()
    {
        fixture.Run(() => { });
        var directory = Path.Combine(Path.GetTempPath(), "Codex", "Fumilume", $"pdf-open-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var pdfPath = Path.Combine(directory, "duplicate.pdf");
        try
        {
            File.WriteAllBytes(pdfPath, PdfTextSelectionE2ETests.CreateFixturePdf());
            var viewModel = new MainWindowViewModel(new EmptyDocumentFileService(), new MainWindowE2ETestHelper.Dialogs());

            await viewModel.OpenPathsAsync([pdfPath, pdfPath.ToUpperInvariant()]);

            var pdf = Assert.Single(viewModel.Tabs.OfType<PdfDocumentViewModel>());
            Assert.Same(pdf, viewModel.SelectedTab);
            Assert.True(pdf.PageCount > 1);
            pdf.Dispose();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static void Drain(PausedRenderer renderer, Task[] tasks, params TaskCompletionSource<Bitmap>[] except)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!tasks.All(task => task.IsCompleted) && timer.Elapsed < TimeSpan.FromSeconds(30))
        {
            renderer.CompletePending(except);
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

        Assert.All(tasks.Where(task => task.IsFaulted).ToArray(), task => Assert.Fail("想定外の失敗"));
        Assert.All(tasks, task => Assert.True(task.IsCompleted, "描画が30秒以内に終わりませんでした。"));
    }

    private static void Pump(Task task) => MainWindowE2ETestHelper.Pump(task);

    private static Bitmap NewBitmap()
        => new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    /// <summary>描画の完了を手で制御する。ネイティブ描画が取消に応答せず、古い処理が後から終わる順序を再現する。</summary>
    internal sealed class PausedRenderer(int pageCount) : IPdfRenderer
    {
        public int PageCount { get; } = pageCount;

        public List<(int PageIndex, CancellationToken Token, TaskCompletionSource<Bitmap> Completion)> Requests { get; } = [];

        public Avalonia.Size GetPageSize(int pageIndex) => pageIndex == 0 ? new(600, 800) : new(800, 600);

        public Task<Bitmap> RenderAsync(int pageIndex, double zoom, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add((pageIndex, cancellationToken, completion));
            return completion.Task;
        }

        /// <summary>まだ終わっていない要求を、すべて成功させる。</summary>
        public void CompletePending(params TaskCompletionSource<Bitmap>[] except)
        {
            foreach (var request in Requests.ToArray())
            {
                if (except.Contains(request.Completion)) continue;
                request.Completion.TrySetResult(NewBitmap());
            }
        }

        public void Dispose()
        {
        }
    }


    internal sealed class StubPdfRenderer(int pageCount) : IPdfRenderer
    {
        /// <summary>このページ番号（0 始まり）以降の描画を失敗させる。</summary>
        public int? FailFromPageIndex { get; init; }

        public int PageCount { get; } = pageCount;

        public Avalonia.Size GetPageSize(int pageIndex) => pageIndex == 0 ? new(600, 800) : new(800, 600);

        /// <summary>描画要求の記録。復元が実際に描き直したかを見るために使う。</summary>
        public List<(int PageIndex, double Zoom)> Renders { get; } = [];

        public Task<Bitmap> RenderAsync(int pageIndex, double zoom, CancellationToken cancellationToken = default)
        {
            Renders.Add((pageIndex, zoom));
            if (FailFromPageIndex is { } failFrom && pageIndex >= failFrom)
            {
                throw new InvalidOperationException("描画に失敗しました");
            }

            using var stream = new MemoryStream(
            [
                137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
                0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 31, 21, 196, 137,
                0, 0, 0, 13, 73, 68, 65, 84, 8, 215, 99, 248, 207, 192, 240,
                31, 0, 5, 0, 1, 255, 114, 156, 82, 103, 0, 0, 0, 0, 73, 69,
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
