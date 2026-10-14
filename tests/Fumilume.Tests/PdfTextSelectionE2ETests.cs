using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

/// <summary>実PDFの読込・抽出・描画から入力とクリップボードまでを確認する。</summary>
[Collection(HeadlessAppCollection.Name)]
public sealed class PdfTextSelectionE2ETests(HeadlessAppFixture fixture)
{
    // 想定失敗: 正逆ドラッグ、改行/空白、日本語、回転/CropBox、拡大/スクロール、
    // ページ/タブ切替、文字なしページ、空選択コピー。暗号化とDispose競合は別途検証対象。
    private const string Expected = "Alpha beta\nGamma delta\n日本語";
    public static bool IsRealRendering => HeadlessAppFixture.UsesSkia;

    [Theory(SkipUnless = nameof(IsRealRendering), Skip = "実ポインタ検証にはFUMILUME_E2E_RENDER=1を指定してください。")]
    [InlineData(1, 0)]
    [InlineData(2, 90)]
    [InlineData(3, 180)]
    [InlineData(4, 270)]
    [InlineData(5, 0)]
    public void RealPdfSupportsDraggingKeyboardAndContextCopy(int pageNumber, int rotation) => fixture.Run(() =>
    {
        using var scope = new Scope();
        Pump(scope.ViewModel.OpenPathsAsync([scope.WritePdf("selection.pdf")]));
        var pdf = Assert.IsType<PdfDocumentViewModel>(scope.ViewModel.SelectedTab);
        Pump(pdf.NavigateAsync(pageNumber));
        Pump(pdf.ActualSizeCommand.ExecuteAsync(null));
        Layout(scope.Window);
        Assert.Null(pdf.ErrorMessage);
        Assert.NotNull(pdf.PageImage);
        var page = Assert.IsType<PdfTextPage>(pdf.TextPage);
        Assert.NotEmpty(page.Characters);
        Assert.All(page.Characters, character =>
        {
            Assert.True(character.Bounds.Width > 0 && character.Bounds.Height > 0);
            Assert.True(character.Bounds.Left >= -1 && character.Bounds.Top >= -1);
            Assert.True(character.Bounds.Right <= page.Size.Width + 1);
            Assert.True(character.Bounds.Bottom <= page.Size.Height + 1);
        });
        var layer = scope.Layer;
        Assert.InRange(Math.Abs(pdf.PageWidth - layer.Bounds.Width), 0, 1);
        Assert.InRange(Math.Abs(pdf.PageHeight - layer.Bounds.Height), 0, 1);
        DragAll(scope, reverse: false);
        var forward = pdf.SelectedText;
        Record(scope, $"page-{pageNumber}-forward", new { pageNumber, rotation, Expected, Actual = forward, pdf.Zoom,
            page.Size, Characters = page.Characters.Select(c => new { c.Text, c.Bounds, c.SeparatorBefore }) });
        Assert.Equal(Expected, Normalize(forward));
        scope.Window.KeyPress(Key.C, RawInputModifiers.Control, default, null);
        DrainCommand(scope.ViewModel.CopyPdfSelectionCommand.ExecutionTask);
        Assert.Equal(forward, ReadClipboard(scope.Window));
        DragAll(scope, reverse: true);
        Assert.Equal(forward, pdf.SelectedText);
        var point = layer.TranslatePoint(new Point(layer.Bounds.Width / 2, layer.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseDown(point, MouseButton.Right);
        scope.Window.MouseUp(point, MouseButton.Right);
        Layout(scope.Window);
        var menu = Assert.IsType<ContextMenu>(layer.ContextMenu);
        Assert.True(menu.IsOpen);
        var copy = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Name == "PdfCopyMenuItem");
        Assert.True(copy.IsEnabled);
        copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        DrainCommand(scope.ViewModel.CopyPdfSelectionCommand.ExecutionTask);
        Assert.Equal(forward, ReadClipboard(scope.Window));
        menu.Close();
        pdf.ClearSelection();
        Pump(scope.Window.Clipboard!.SetTextAsync("keep clipboard"));
        scope.Window.KeyPress(Key.C, RawInputModifiers.Control, default, null);
        DrainCommand(scope.ViewModel.CopyPdfSelectionCommand.ExecutionTask);
        Assert.Equal("keep clipboard", ReadClipboard(scope.Window));
        layer.Focus();
        scope.Window.KeyPress(Key.A, RawInputModifiers.Control, default, null);
        Layout(scope.Window);
        Assert.Equal(Expected, Normalize(pdf.SelectedText));
        Record(scope, $"page-{pageNumber}-copied", new { Expected, Actual = pdf.SelectedText,
            Clipboard = ReadClipboard(scope.Window), pdf.SelectionStart, pdf.SelectionEnd, pdf.HasTextSelection });
    });

    [Fact(SkipUnless = nameof(IsRealRendering), Skip = "実ポインタ検証にはFUMILUME_E2E_RENDER=1を指定してください。")]
    public void ZoomScrollPageAndTabChangesKeepSelectionAttachedToItsPdf() => fixture.Run(() =>
    {
        using var scope = new Scope();
        Pump(scope.ViewModel.OpenPathsAsync([scope.WritePdf("navigation.pdf")]));
        var pdf = scope.ViewModel.SelectedPdf!;
        Layout(scope.Window);
        DragAll(scope, reverse: false);
        var selected = pdf.SelectedText;
        Pump(pdf.ZoomInCommand.ExecuteAsync(null));
        Layout(scope.Window);
        Assert.Equal(selected, pdf.SelectedText);
        var scroll = scope.Layer.GetVisualAncestors().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(0, 15);
        Layout(scope.Window);
        DragAll(scope, reverse: true);
        Assert.Equal(selected, pdf.SelectedText);
        var documentPath = Path.Combine(scope.StoragePath, "other.txt");
        File.WriteAllText(documentPath, "other tab");
        Pump(scope.ViewModel.OpenPathsAsync([documentPath]));
        Assert.Null(scope.ViewModel.SelectedPdf);
        Assert.False(scope.ViewModel.CopyPdfSelectionCommand.CanExecute(null));
        scope.ViewModel.SelectedTab = pdf;
        Layout(scope.Window);
        Assert.Equal(selected, pdf.SelectedText);
        Pump(pdf.NextPageCommand.ExecuteAsync(null));
        Layout(scope.Window);
        Assert.False(pdf.HasTextSelection);
        Assert.Equal(string.Empty, pdf.SelectedText);
        Record(scope, "navigation", new { pdf.CurrentPage, pdf.Zoom, pdf.SelectedText, pdf.HasTextSelection, scroll.Offset });
    });

    [Fact(SkipUnless = nameof(IsRealRendering), Skip = "実ポインタ検証にはFUMILUME_E2E_RENDER=1を指定してください。")]
    public void ImageOnlyPageCannotCopyAndDoesNotReusePreviousPageText() => fixture.Run(() =>
    {
        using var scope = new Scope();
        Pump(scope.ViewModel.OpenPathsAsync([scope.WritePdf("image-only.pdf")]));
        var pdf = scope.ViewModel.SelectedPdf!;
        Layout(scope.Window);
        DragAll(scope, reverse: false);
        Pump(scope.Window.Clipboard!.SetTextAsync("unchanged"));
        Pump(pdf.NavigateAsync(6));
        Layout(scope.Window);
        Assert.NotNull(pdf.PageImage);
        Assert.False(pdf.HasTextSelection);
        Assert.True(pdf.TextPage is null || pdf.TextPage.Characters.Count == 0);
        Assert.False(scope.ViewModel.CopyPdfSelectionCommand.CanExecute(null));
        scope.Layer.Focus();
        scope.Window.KeyPress(Key.A, RawInputModifiers.Control, default, null);
        scope.Window.KeyPress(Key.C, RawInputModifiers.Control, default, null);
        DrainCommand(scope.ViewModel.CopyPdfSelectionCommand.ExecutionTask);
        Assert.Equal("unchanged", ReadClipboard(scope.Window));
        Record(scope, "image-only", new { pdf.CurrentPage, pdf.SelectedText, pdf.HasTextSelection, Clipboard = ReadClipboard(scope.Window) });
    });

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\r', '\n');

    private static void DragAll(Scope scope, bool reverse)
    {
        Layout(scope.Window);
        var pdf = scope.ViewModel.SelectedPdf!;
        var layer = scope.Layer;
        // fixtureの本文行列から決めたglyph内の点。抽出結果のBoundsには依存させない。
        // HelveticaのAは(40,220)から幅約10.67、最後の日本語glyphは(72,150)から幅16。
        // PDF座標は左下原点。CropBoxで原点を移し、Rotateで時計回りに表示座標へ変換する。
        var crop = pdf.CurrentPage == 5 ? new Rect(20, 20, 280, 240) : new Rect(0, 0, 320, 280);
        var rotation = pdf.CurrentPage <= 4 ? (pdf.CurrentPage - 1) * 90 : 0;
        var displaySize = rotation is 90 or 270
            ? new Size(crop.Height, crop.Width)
            : crop.Size;
        Point ToWindow(Point pdfPoint)
        {
            var x = pdfPoint.X - crop.X;
            var y = pdfPoint.Y - crop.Y;
            var displayPoint = rotation switch
            {
                90 => new Point(y, x),
                180 => new Point(crop.Width - x, y),
                270 => new Point(crop.Height - y, crop.Width - x),
                _ => new Point(x, crop.Height - y),
            };
            return layer.TranslatePoint(new Point(
                displayPoint.X * layer.Bounds.Width / displaySize.Width,
                displayPoint.Y * layer.Bounds.Height / displaySize.Height), scope.Window)!.Value;
        }
        var first = new Point(45, 225);
        var last = new Point(80, 155);
        var start = ToWindow(reverse ? last : first);
        var end = ToWindow(reverse ? first : last);
        scope.Window.MouseDown(start, MouseButton.Left);
        scope.Window.MouseMove(end);
        scope.Window.MouseUp(end, MouseButton.Left);
        Layout(scope.Window);
        Assert.True(pdf.HasTextSelection);
    }

    private static string? ReadClipboard(MainWindow window)
    {
        var task = window.Clipboard!.TryGetTextAsync();
        Pump(task);
        return task.GetAwaiter().GetResult();
    }

    private static void DrainCommand(Task? task)
    {
        if (task is not null) Pump(task);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Layout(MainWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Pump(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(30))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Yield();
        }
        Assert.True(task.IsCompleted, "PDF E2E処理が30秒以内に完了しませんでした。");
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Record(Scope scope, string name, object result)
    {
        Directory.CreateDirectory(EvidenceDirectory);
        File.WriteAllText(Path.Combine(EvidenceDirectory, $"pdf-selection-{name}.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        if (!HeadlessAppFixture.UsesSkia) return;
        using var frame = scope.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(EvidenceDirectory, $"pdf-selection-{name}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static string EvidenceDirectory => Environment.GetEnvironmentVariable("FUMILUME_E2E_ARTIFACTS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Codex", "TaskArtifacts", "Fumilume", "pdf-selection-20261014-01", "evidence");

    private sealed class Scope : IDisposable
    {
        public string StoragePath { get; } = Path.Combine(Path.GetTempPath(), "Codex", "Fumilume",
            "pdf-selection-20261014-01", $"e2e-{Guid.NewGuid():N}");
        public MainWindow Window { get; }
        public MainWindowViewModel ViewModel { get; }
        public Control Layer => Window.FindControl<Control>("PdfTextSelectionLayer")!;

        public Scope()
        {
            Directory.CreateDirectory(StoragePath);
            AppStoragePaths.OverrideDirectory(StoragePath);
            Window = new MainWindow(new AppSettings { RestoreSession = false, CheckUpdatesOnStartup = false });
            ViewModel = (MainWindowViewModel)Window.DataContext!;
            Window.Show();
            Layout(Window);
        }

        public string WritePdf(string name)
        {
            var path = Path.Combine(StoragePath, name);
            var bytes = CreatePdf();
            File.WriteAllBytes(path, bytes);
            Directory.CreateDirectory(EvidenceDirectory);
            File.WriteAllBytes(Path.Combine(EvidenceDirectory, name), bytes);
            File.WriteAllText(Path.Combine(EvidenceDirectory, "REPRODUCE.txt"),
                "Windows / fixed global.json SDK. Set FUMILUME_E2E_RENDER=1 and FUMILUME_E2E_ARTIFACTS to this directory.\n" +
                "dotnet test Fumilume.slnx -c Release --no-restore --filter FullyQualifiedName~PdfTextSelectionE2ETests\n" +
                "PDF fixtures, expected/actual JSON and PNG are retained for reproduction.\n" +
                "Temporary storage: Temp/Codex/Fumilume/pdf-selection-20261014-01/e2e-*; clean this run with Prunlet after tests.\n");
            return path;
        }

        public void Dispose()
        {
            ViewModel.Options.RestoreSession = true;
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            AppStoragePaths.OverrideDirectory(null);
            // 清掃は実行担当が対象を確認してPrunletで行う。
        }
    }

    private static byte[] CreatePdf()
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", string.Empty };
        var pages = new List<int>();
        for (var index = 0; index < 6; index++)
        {
            var page = objects.Count + 1;
            pages.Add(page);
            var rotation = index < 4 ? index * 90 : 0;
            var crop = index == 4 ? "/CropBox [20 20 300 260]" : string.Empty;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 320 280] {crop} /Rotate {rotation} /Resources << /Font << /F1 15 0 R /F2 16 0 R >> /XObject << /Im1 20 0 R >> >> /Contents {page + 1} 0 R >>");
            var content = index == 5
                ? "q 100 0 0 100 40 60 cm /Im1 Do Q"
                : "BT /F1 16 Tf 1 0 0 1 40 220 Tm (Alpha beta) Tj 1 0 0 1 40 185 Tm (Gamma delta) Tj /F2 16 Tf 1 0 0 1 40 150 Tm <65E5672C8A9E> Tj ET";
            objects.Add(Stream(content));
        }
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', pages.Select(p => p + " 0 R"))}] /Count 6 >>";
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        objects.Add("<< /Type /Font /Subtype /Type0 /BaseFont /HeiseiKakuGo-W5 /Encoding /UniJIS-UTF16-H /DescendantFonts [17 0 R] /ToUnicode 18 0 R >>");
        objects.Add("<< /Type /Font /Subtype /CIDFontType0 /BaseFont /HeiseiKakuGo-W5 /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 6 >> /FontDescriptor 19 0 R /DW 1000 >>");
        objects.Add(Stream("/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def /CMapName /Japanese def /CMapType 2 def 1 begincodespacerange <0000> <FFFF> endcodespacerange 3 beginbfchar <65E5> <65E5> <672C> <672C> <8A9E> <8A9E> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end"));
        objects.Add("<< /Type /FontDescriptor /FontName /HeiseiKakuGo-W5 /Flags 4 /FontBBox [0 -200 1000 900] /ItalicAngle 0 /Ascent 880 /Descent -120 /CapHeight 700 /StemV 80 >>");
        objects.Add("<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /ASCIIHexDecode /Length 25 >>\nstream\nFF000000FF000000FFFFFFFF>\nendstream");
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) builder.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static string Stream(string contents) => $"<< /Length {Encoding.ASCII.GetByteCount(contents)} >>\nstream\n{contents}\nendstream";
}
