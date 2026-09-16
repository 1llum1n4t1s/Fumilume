using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class CsvPreviewAuditTests(HeadlessAppFixture fixture)
{
    [Fact]
    public void UnterminatedQuotedFieldShowsRepairGuidanceAlongsidePreviewLimits() => fixture.Run(() =>
    {
        var rows = Enumerable.Repeat("value", CsvDocumentParser.MaxPreviewRows + 2).ToArray();
        rows[^1] = "one,\"two";
        var (window, preview, _) = CreatePreview(string.Join('\n', rows));
        try
        {
            var message = Assert.IsType<string>(preview.TruncationMessage);
            Assert.Contains("引用符が閉じていません", message);
            Assert.Contains("編集へ戻る", message);
            Assert.Contains("表示しています", message);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void InsertingPastPreviewColumnLimitClearsInvisibleSelectionAndGuidesToEditor() => fixture.Run(() =>
    {
        var source = string.Join(',', Enumerable.Range(1, CsvDocumentParser.MaxPreviewColumns));
        var (window, preview, document) = CreatePreview(source);
        try
        {
            InsertAfterLastColumn(window, preview);
            preview.Csv = document.Text;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(CsvDocumentParser.MaxPreviewColumns + 1,
                CsvDocumentParser.Parse(document.Text, TestContext.Current.CancellationToken).TotalColumnCount);
            Assert.Null(preview.SelectedHeaders);
            Assert.Contains("表示上限外", preview.EditStatus);
            Assert.Contains("編集へ戻る", preview.EditStatus);
            Assert.Contains("表示上限外を確認・修正", Assert.IsType<string>(preview.TruncationMessage));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void LargeRenderReturnsPromptlyAndOnlyAppliesLatestDocumentSource() => fixture.Run(() =>
    {
        var (window, preview, firstDocument) = CreatePreview("initial,value");
        try
        {
            var slowSource = "old," + new string('a', 8 * 1024 * 1024);
            var stopwatch = Stopwatch.StartNew();
            firstDocument.Text = slowSource;
            preview.Csv = slowSource;
            Dispatcher.UIThread.RunJobs();
            stopwatch.Stop();

            Assert.True(preview.IsRenderPending);
            Assert.False(preview.PendingRender.IsCompleted);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"大きなCSVの描画予約に {stopwatch.Elapsed} かかりました。");

            var latestDocument = CreateDocument("latest," + new string('b', 256 * 1024), "latest.csv");
            preview.Document = latestDocument;
            preview.Csv = latestDocument.Text;
            DrainRendering(preview);

            Assert.False(preview.IsRenderPending);
            Assert.Same(latestDocument, preview.Document);
            Assert.Equal(1, preview.RenderedRowCount);
            Assert.Contains(
                preview.GetVisualDescendants().OfType<SelectableTextBlock>(),
                cell => cell.Text == "latest");
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingInsertionHintDoesNotLeakAcrossUndoOrDocumentSwitch(bool switchDocument) => fixture.Run(() =>
    {
        var source = string.Join(',', Enumerable.Range(1, CsvDocumentParser.MaxPreviewColumns));
        var (window, preview, document) = CreatePreview(source);
        try
        {
            InsertAfterLastColumn(window, preview);
            preview.Csv = document.Text;
            if (switchDocument)
            {
                var other = CreateDocument("other,value", "other.csv");
                preview.Document = other;
                preview.Csv = other.Text;
            }
            else
            {
                document.EditorDocument.UndoStack.Undo();
                preview.Csv = document.Text;
            }
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("追加した", preview.EditStatus);
            Assert.Null(preview.TruncationMessage);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void DetachedPreviewDoesNotStartQueuedParsingAndResumesOnAttach() => fixture.Run(() =>
    {
        var source = string.Join('\n', Enumerable.Repeat("a,b", 40_000));
        var (window, preview, document) = CreatePreview(source);
        try
        {
            var previousTask = preview.PendingRender;
            document.Text = source + "\nnew,value";
            preview.Csv = document.Text;
            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(previousTask, preview.PendingRender);
            Assert.False(document.TryGetCsvPreview(document.Text, out _));
            window.Content = preview;
            DrainRendering(preview);
            Assert.True(document.TryGetCsvPreview(document.Text, out var parsed));
            Assert.Equal(40_001, parsed.TotalRowCount);
        }
        finally
        {
            window.Close();
        }
    });

    private static void InsertAfterLastColumn(Window window, CsvPreview preview)
    {
        var scroll = preview.GetVisualDescendants().OfType<ScrollViewer>().Single();
        scroll.Offset = new Vector(double.MaxValue, 0);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var header = preview.GetVisualDescendants()
            .OfType<Border>()
            .Single(control => AutomationProperties.GetName(control) == "CsvColumnHeader:CV");
        var point = header.TranslatePoint(
            new Point(header.Bounds.Width / 2, header.Bounds.Height / 2),
            window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        header = preview.GetVisualDescendants().OfType<Border>()
            .Single(control => AutomationProperties.GetName(control) == "CsvColumnHeader:CV");
        var menu = Assert.IsType<ContextMenu>(header.ContextMenu);
        menu.Open(header);
        Dispatcher.UIThread.RunJobs();
        var insertAfter = menu.Items.OfType<MenuItem>()
            .Single(item => item.Name == "CsvInsertAfterMenuItem");
        insertAfter.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
    }

    private static (Window Window, CsvPreview Preview, DocumentViewModel Document) CreatePreview(string source)
    {
        var document = CreateDocument(source, "audit.csv");
        var preview = new CsvPreview
        {
            Document = document,
            Csv = document.Text,
        };
        var window = new Window
        {
            Width = 640,
            Height = 360,
            Content = preview,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        DrainRendering(preview);
        window.UpdateLayout();
        return (window, preview, document);
    }

    private static DocumentViewModel CreateDocument(string source, string name)
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Load(
            $@"C:\tmp\{name}",
            new TextDocumentContent(source, DocumentEncoding.Utf8, "\n"));
        return document;
    }

    private static void DrainRendering(CsvPreview preview)
    {
        var timeout = Stopwatch.StartNew();
        while (preview.IsRenderPending && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(1);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.False(preview.IsRenderPending, "CSVの非同期解析が完了しませんでした。");
    }
}
