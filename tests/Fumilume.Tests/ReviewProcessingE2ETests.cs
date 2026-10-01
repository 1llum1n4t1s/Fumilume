using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class ReviewProcessingE2ETests(HeadlessAppFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public void MixedNewLinesAreHandledByEditorCommandsWithUndo() => fixture.Run(() =>
    {
        using var scope = new ProcessingWindow();
        var editor = scope.Window.FindControl<TextEditor>("Editor")!;
        foreach (var (command, source, expected) in new[]
        {
            (EditorCommandId.TrimLineEnds, "b  \r\na \nc\t\r", "b\r\na\nc\r"),
            (EditorCommandId.TrimLineStarts, " b\r\n\ta\n　c\r", "b\r\na\nc\r"),
            (EditorCommandId.SortLinesAscending, "b\r\na\nc\r", "a\r\nb\r\nc\r"),
            (EditorCommandId.SortLinesDescending, "b\r\na\nc\r", "c\r\nb\r\na\r"),
            (EditorCommandId.MergeLines, "a\r\na\na\rb\r", "a\r\nb\r"),
        })
        {
            editor.Text = source;
            editor.SelectAll();
            Dispatcher.UIThread.RunJobs();
            scope.ViewModel.RunEditorCommandCommand.Execute(command);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(expected, editor.Text);
            editor.Undo();
            Assert.Equal(source, editor.Text);
        }
    });

    [Fact]
    public void MarkdownPreviewHandlesMalformedLinksAndKeepsValidLabels() => fixture.Run(() =>
    {
        using var scope = new ProcessingWindow();
        var document = scope.ViewModel.Documents.Single();
        document.MarkSaved(System.IO.Path.Combine(scope.StoragePath, "links.md"));
        document.Text = "[link](https://example.test) ![alt](image) **bold**";
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var preview = scope.Window.GetVisualDescendants().OfType<MarkdownPreview>().Single();
        var text = preview.GetVisualDescendants().OfType<SelectableTextBlock>().Select(block => block.Text).ToArray();
        Assert.Contains("link 画像: alt bold", text);
        Assert.Equal(1, preview.RenderedBlockCount);
        var originalLargeInput = document.Text + "\n\n" + new string('[', 600_000);
        var parseElapsed = Stopwatch.StartNew();
        _ = MarkdownDocumentParser.Parse(originalLargeInput);
        parseElapsed.Stop();
        output.WriteLine($"Original attached-input Parse only: {parseElapsed.Elapsed.TotalMilliseconds:F2} ms");
        // 解析・コントロール構築と実画面の組版を分けて計測する。
        var malformed = new string('[', 600_000) + "\n\n" + string.Concat(Enumerable.Repeat("[x](", 150_000));
        var elapsed = Stopwatch.StartNew();
        var largePreview = new MarkdownPreview { Markdown = malformed };
        Dispatcher.UIThread.RunJobs();
        elapsed.Stop();
        Assert.Equal(2, largePreview.RenderedBlockCount);
        Assert.Equal(malformed.Replace("\n\n", string.Empty, StringComparison.Ordinal),
            string.Concat(largePreview.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(PreviewText)));
        var unicode = new string('あ', 4095) + "👩‍👩‍👧‍👦e\u0301" + new string('い', 4096);
        var unicodePreview = new MarkdownPreview { Markdown = unicode };
        Dispatcher.UIThread.RunJobs();
        var unicodeBlock = unicodePreview.GetLogicalDescendants().OfType<SelectableTextBlock>().Single();
        Assert.Equal(unicode, PreviewText(unicodeBlock));
        Assert.Contains(unicodeBlock.Inlines!.OfType<Avalonia.Controls.Documents.Run>(),
            run => run.Text!.Contains("👩‍👩‍👧‍👦e\u0301", StringComparison.Ordinal));
        unicodeBlock.SelectAll();
        Assert.Equal(unicode, unicodeBlock.SelectedText);
        var joining = new string('あ', 4095) + "لا";
        unicodePreview.Markdown = joining;
        Dispatcher.UIThread.RunJobs();
        var joiningBlock = unicodePreview.GetLogicalDescendants().OfType<SelectableTextBlock>().Single();
        Assert.Equal(joining, PreviewText(joiningBlock));
        Assert.Contains(joiningBlock.Inlines!.OfType<Avalonia.Controls.Documents.Run>(),
            run => run.Text!.Contains("لا", StringComparison.Ordinal));
        output.WriteLine($"Malformed Markdown preview control construction: {elapsed.Elapsed.TotalMilliseconds:F2} ms");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"プレビュー構築（組版を除外）: {elapsed.Elapsed}");
        var previewControlBuildMilliseconds = elapsed.Elapsed.TotalMilliseconds;
        double? attachedRenderMilliseconds = null;
        if (HeadlessAppFixture.UsesSkia)
        {
            elapsed.Restart();
            document.Text = originalLargeInput;
            Dispatcher.UIThread.RunJobs();
            scope.Window.UpdateLayout();
            elapsed.Stop();
            attachedRenderMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            Assert.True(preview.IsEffectivelyVisible);
            Assert.Equal(2, preview.RenderedBlockCount);
            Assert.Equal("link 画像: alt bold" + new string('[', 600_000),
                string.Concat(preview.GetVisualDescendants().OfType<SelectableTextBlock>().Select(PreviewText)));
            var longParagraph = preview.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Single(block => PreviewText(block)?.Length == 600_000);
            longParagraph.SelectAll();
            Assert.Equal(new string('[', 600_000), longParagraph.SelectedText);
            longParagraph.SelectionStart = 4090;
            longParagraph.SelectionEnd = 4110;
            Assert.Equal(new string('[', 20), longParagraph.SelectedText);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"実描画プレビュー更新: {elapsed.Elapsed}");
            output.WriteLine($"Skia attached MainWindow preview update and layout: {attachedRenderMilliseconds:F2} ms");
        }
        var artifactDirectory = Environment.GetEnvironmentVariable("FUMILUME_REVIEW_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(artifactDirectory))
        {
            Directory.CreateDirectory(artifactDirectory);
            File.WriteAllText(System.IO.Path.Combine(artifactDirectory, "review-processing-markdown-metrics.json"),
                JsonSerializer.Serialize(new
                {
                    UsesSkia = HeadlessAppFixture.UsesSkia,
                    SourceBracketCount = 600_000,
                    SourceMissingUrlCount = 150_000,
                    OriginalInputParseMilliseconds = parseElapsed.Elapsed.TotalMilliseconds,
                    PreviewControlBuildMilliseconds = previewControlBuildMilliseconds,
                    AttachedRenderMilliseconds = attachedRenderMilliseconds,
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
    });

    [Fact]
    public async Task SearchTabHonorsCancellationAfterFinalFileRead()
    {
        ProcessingWindow scope = null!;
        fixture.Run(() => scope = new ProcessingWindow());
        try
        {
            var path = System.IO.Path.Combine(scope.StoragePath, "last.txt");
            await File.WriteAllTextAsync(path, "needle\r\nneedle\nneedle\r", TestContext.Current.CancellationToken);
            var files = new PausedReadService();
            using var tab = new GrepResultTabViewModel(new GrepQuery("needle", scope.StoragePath, "*.txt", false, false, false),
                new GrepService(files), _ => Task.CompletedTask, _ => Task.CompletedTask);
            Task run = null!;
            fixture.Run(() =>
            {
                scope.ViewModel.Tabs.Add(tab);
                scope.ViewModel.SelectedTab = tab;
                run = tab.RunAsync();
            });
            await files.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            fixture.Run(() => tab.CancelSearchCommand.Execute(null));
            files.Release.SetResult();
            await PumpAsync(run);
            Assert.Empty(tab.Matches);
            Assert.Contains("中止", tab.Status);
            Assert.True(tab.HasCompleted);
            fixture.Run(() => Assert.True(scope.ViewModel.IsGrepSelected));
        }
        finally { fixture.Run(scope.Dispose); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledOrClosedSearchTabCannotPublishLateResults(bool close)
    {
        var grep = new PausedGrepService();
        ProcessingWindow scope = null!;
        fixture.Run(() => scope = new ProcessingWindow());
        try
        {
            using var tab = new GrepResultTabViewModel(new GrepQuery("needle", "folder", "*", false, false, false),
                grep, _ => Task.CompletedTask, _ => Task.CompletedTask);
            Task run = null!;
            fixture.Run(() =>
            {
                scope.ViewModel.Tabs.Add(tab);
                scope.ViewModel.SelectedTab = tab;
                run = tab.RunAsync();
            });
            await grep.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            fixture.Run(() =>
            {
                if (close)
                {
                    scope.ViewModel.Tabs.Remove(tab);
                    scope.ViewModel.SelectedTab = scope.ViewModel.Documents.Single();
                    tab.Dispose();
                }
                else tab.CancelSearchCommand.Execute(null);
            });
            grep.Release.SetResult();
            await PumpAsync(run);
            Assert.Empty(tab.Matches);
            Assert.Null(tab.SelectedMatch);
            if (!close) Assert.Contains("中止", tab.Status);
        }
        finally { fixture.Run(scope.Dispose); }
    }

    private static string? PreviewText(SelectableTextBlock block)
        => block.Inlines is { Count: > 0 } inlines ? inlines.Text : block.Text;

    private async Task PumpAsync(Task task)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            fixture.Run(() => Dispatcher.UIThread.RunJobs());
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        await task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    }

    private sealed class PausedReadService : IDocumentFileService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TextDocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            var result = await new DocumentFileService().ReadAsync(path, cancellationToken);
            Started.SetResult();
            // 中止直前に読み込みが完了する競合を再現する。
            await Release.Task;
            return result;
        }
        public Task WriteAsync(string path, TextDocumentContent content, bool createBackup = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PausedGrepService : IGrepService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GrepResult> SearchAsync(GrepQuery query, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await Release.Task;
            return new GrepResult([new GrepMatch("late.txt", 1, 1, "needle")], 1, 0, false);
        }
    }

    private sealed class ProcessingWindow : IDisposable
    {
        private readonly TemporaryStorage _storage = new();
        public ProcessingWindow()
        {
            Window = new MainWindow(new AppSettings { CheckUpdatesOnStartup = false });
            ViewModel = (MainWindowViewModel)Window.DataContext!;
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }
        public MainWindow Window { get; }
        public MainWindowViewModel ViewModel { get; }
        public string StoragePath => _storage.Path;
        public void Dispose()
        {
            foreach (var document in ViewModel.Documents)
                document.MarkSaved(System.IO.Path.Combine(StoragePath, "closed.txt"));
            ViewModel.Options.RestoreSession = false;
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            _storage.Dispose();
        }
    }
}
