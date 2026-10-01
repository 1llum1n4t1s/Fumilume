using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class OpopIntegrationTests(HeadlessAppFixture fixture)
{
    [Fact]
    public void BracketHighlightingHandlesLongRunsInEditor() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var window = new MainWindow(new AppSettings { CheckUpdatesOnStartup = false, RestoreSession = false });
        var vm = (MainWindowViewModel)window.DataContext!;
        var document = vm.Documents.Single();
        var path = Path.Combine(storage.Path, "brackets.cs");
        File.WriteAllText(path, string.Empty);
        document.MarkSaved(path);
        window.Show();
        var editor = window.FindControl<TextEditor>("Editor")!;
        var timings = new List<object>();
        try
        {
            foreach (var (name, source) in new[]
            {
                ("dollars", new string('$', 16_000) + "\n()"),
                ("raw-quotes", new string('"', 16_000) + "x" + new string('"', 15_999) + "x\n[]"),
                ("interpolated-raw-quotes", "$$" + new string('"', 16_000) + "x" + new string('"', 15_999) + "x\n{}"),
            })
            {
                var timer = Stopwatch.StartNew();
                editor.Text = source;
                editor.CaretOffset = source.Length - 1;
                editor.ScrollToLine(editor.Document.LineCount);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
                timer.Stop();
                Assert.Equal(source, document.Text);
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"括弧描画 {name}: {timer.Elapsed}");
                timings.Add(new { Name = name, InputLength = source.Length, UpdateAndRenderMilliseconds = timer.Elapsed.TotalMilliseconds });
            }
            if (HeadlessAppFixture.UsesSkia && Environment.GetEnvironmentVariable("FUMILUME_OPOP_ARTIFACTS") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(directory, "brackets.png"), PngBitmapEncoderOptions.Default);
            }
            WriteArtifact("bracket-editor.json", new { HeadlessAppFixture.UsesSkia, Timings = timings });
            editor.Text = string.Empty;
            document.MarkSaved(Path.Combine(storage.Path, "brackets.cs"));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Fact]
    public void SettingsRetainSearchAndNavigationAfterClosingAndReopening() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var timer = Stopwatch.StartNew();
        var window = new MainWindow(new AppSettings { CheckUpdatesOnStartup = false, RestoreSession = false });
        var vm = (MainWindowViewModel)window.DataContext!;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        timer.Stop();
        var startupMilliseconds = timer.Elapsed.TotalMilliseconds;
        var initialViews = window.GetVisualDescendants().OfType<SettingsView>().Count();
        try
        {
            Assert.Equal(0, initialViews);
            var editor = window.FindControl<TextEditor>("Editor")!;
            editor.Text = "設定を開く前に入力";
            window.UpdateLayout();
            timer.Restart();
            vm.OpenSettingsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            timer.Stop();
            var firstOpenMilliseconds = timer.Elapsed.TotalMilliseconds;
            var settings = window.GetVisualDescendants().OfType<SettingsView>().Single();
            Assert.True(settings.IsEffectivelyVisible);
            Assert.NotEmpty(settings.EditorFontFamilies);
            var tabs = settings.FindControl<TabControl>("SettingsTabs")!;
            tabs.SelectedIndex = 1;
            var search = settings.FindControl<TextBox>("SettingsSearchBox")!;
            search.Text = "フォント";
            Dispatcher.UIThread.RunJobs();
            Assert.True(settings.FindControl<TextBlock>("SettingsSearchSummary")!.IsVisible);
            var selected = tabs.SelectedIndex;
            var oldTab = vm.SettingsTab!;
            Complete(oldTab.CloseTabCommand.ExecuteAsync(null));
            Assert.Null(vm.SettingsTab);
            vm.OpenSettingsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(settings, window.GetVisualDescendants().OfType<SettingsView>().Single());
            Assert.NotSame(oldTab, settings.DataContext);
            Assert.Same(vm.SettingsTab, settings.DataContext);
            Assert.Equal("フォント", search.Text);
            Assert.Equal(selected, tabs.SelectedIndex);
            vm.SelectedTab = vm.Documents.Single();
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.IsEffectivelyVisible);
            Assert.Equal("設定を開く前に入力", editor.Text);
            // 編集文書を汚れたまま閉じると確認ダイアログが発生するため保存済みに戻す。
            editor.Text = string.Empty;
            vm.Documents.Single().MarkSaved(Path.Combine(storage.Path, "edited.txt"));
            WriteArtifact("settings-startup.json", new
            {
                HeadlessAppFixture.UsesSkia, StartupMilliseconds = startupMilliseconds,
                InitialSettingsViewCount = initialViews, FirstSettingsOpenMilliseconds = firstOpenMilliseconds,
                SearchAndSelectedCategoryPreserved = true, EditorTextPreserved = true,
            });
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Fact]
    public void PdfCommandsIgnoreSupersededFailuresAndStillShowCurrentFailures() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var window = new MainWindow(new AppSettings { CheckUpdatesOnStartup = false, RestoreSession = false });
        var vm = (MainWindowViewModel)window.DataContext!;
        window.Show();
        var renderer = new PausedRenderer();
        using var pdf = new PdfDocumentViewModel(Path.Combine(storage.Path, "pages.pdf"), renderer, _ => Task.CompletedTask)
        {
            ZoomMode = PdfZoomMode.Manual,
        };
        vm.Tabs.Add(pdf);
        vm.SelectedTab = pdf;
        Dispatcher.UIThread.RunJobs();
        try
        {
            // ネイティブ描画が取消に応答せず、古い処理が後から失敗する順序を再現する。
            for (var order = 0; order < 2; order++)
            {
                var old = pdf.FitWidthCommand.ExecuteAsync(null);
                var oldRequest = renderer.Requests[^1];
                var current = pdf.FitHeightCommand.ExecuteAsync(null);
                var currentRequest = renderer.Requests[^1];
                Assert.True(oldRequest.Token.IsCancellationRequested);
                if (order == 0)
                {
                    oldRequest.Completion.SetException(new IOException("古い描画"));
                    Complete(old);
                    Assert.Null(pdf.ErrorMessage);
                    Assert.True(pdf.IsLoading);
                }
                currentRequest.Completion.SetResult(NewBitmap());
                Complete(current);
                if (order == 1)
                {
                    oldRequest.Completion.SetException(new IOException("古い描画"));
                    Complete(old);
                }
                Assert.Null(pdf.ErrorMessage);
                Assert.False(pdf.IsLoading);
                Assert.Same(pdf.PageImage, window.FindControl<Image>("PdfPageImage")!.Source);
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(),
                    block => block.IsEffectivelyVisible && block.Text == "古い描画");
            }
            var failed = pdf.FitWidthCommand.ExecuteAsync(null);
            renderer.Requests[^1].Completion.SetException(new IOException("現在の描画"));
            Complete(failed);
            Assert.Equal("現在の描画", pdf.ErrorMessage);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == "現在の描画");
            var closing = pdf.FitWidthCommand.ExecuteAsync(null);
            var closingRequest = renderer.Requests[^1];
            pdf.Dispose();
            closingRequest.Completion.SetException(new IOException("閉じた描画"));
            Complete(closing);
            Assert.Null(pdf.ErrorMessage);
            Assert.Null(pdf.PageImage);
            WriteArtifact("pdf-render-order.json", new
            {
                OldFailureBeforeCurrentSuccess = true, OldFailureAfterCurrentSuccess = true,
                CurrentFailureVisible = true, FailureAfterDisposeIgnored = true,
            });
        }
        finally
        {
            vm.SelectedTab = vm.Documents.Single();
            vm.Tabs.Remove(pdf);
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Fact]
    public void FormattingFromEditorPreservesChosenNewLineAndUndo() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var window = new MainWindow(new AppSettings { CheckUpdatesOnStartup = false, RestoreSession = false });
        var vm = (MainWindowViewModel)window.DataContext!;
        window.Show();
        var editor = window.FindControl<TextEditor>("Editor")!;
        try
        {
            foreach (var newLine in new[] { "\r\n", "\n", "\r" })
            foreach (var finalNewLine in new[] { false, true })
            foreach (var extension in new[] { ".json", ".xml" })
            {
                var document = vm.Documents.Single();
                document.MarkSaved(Path.Combine(storage.Path, "format" + extension));
                document.NewLine = newLine;
                var source = (extension == ".json" ? "{\r\n\"b\":1,\n\"a\":[2,3]\r}" : "<root>\r\n<a/>\n<b/>\r</root>")
                    + (finalNewLine ? "\r" : string.Empty);
                editor.Text = source;
                vm.RunEditorCommandCommand.Execute(EditorCommandId.FormatDocument);
                Dispatcher.UIThread.RunJobs();
                Assert.NotEqual(source, editor.Text);
                Assert.Equal(editor.Text, DocumentFileService.NormalizeNewLines(editor.Text, newLine));
                Assert.Equal(finalNewLine, editor.Text.EndsWith(newLine, StringComparison.Ordinal));
                editor.Undo();
                Assert.Equal(source, editor.Text);
            }
            editor.Text = string.Empty;
            vm.Documents.Single().MarkSaved(Path.Combine(storage.Path, "format.xml"));
            WriteArtifact("formatting.json", new { Cases = 12, NewLineAndFinalSeparatorPreserved = true, UndoRestoresInput = true });
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    private static Bitmap NewBitmap() => new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    private sealed class PausedRenderer : IPdfRenderer
    {
        public int PageCount => 2;
        public List<(CancellationToken Token, TaskCompletionSource<Bitmap> Completion)> Requests { get; } = [];
        public Size GetPageSize(int pageIndex) => new(612, 792);
        public Task<Bitmap> RenderAsync(int pageIndex, double scale, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add((cancellationToken, completion));
            return completion.Task;
        }
        public void Dispose() { }
    }

    private static void Complete(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private static void WriteArtifact(string name, object value)
    {
        if (Environment.GetEnvironmentVariable("FUMILUME_OPOP_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
