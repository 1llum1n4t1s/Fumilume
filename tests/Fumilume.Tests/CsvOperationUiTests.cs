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
public sealed class CsvOperationUiTests(HeadlessAppFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PasteStartsAtTopLeftOfForwardOrReverseSelection(bool reverse) => fixture.Run(() =>
    {
        const string source = "a,b,c\n1,2,3\n4,5,6";
        const string pasted = "A\tB\tC\r\nD\tE\tF\r\nG\tH\tI";
        var clipboard = new ControlledClipboard(pasted);
        var (window, preview, document) = CreatePreview(source, clipboard);
        try
        {
            ClickCell(window, preview, reverse ? "C3" : "A1");
            ClickCell(window, preview, reverse ? "A1" : "C3", RawInputModifiers.Shift);
            Assert.Equal(new CsvCellRange(0, 0, 3, 3), preview.SelectedCellRange);
            Assert.Equal(reverse ? (0, 0) : (2, 2), preview.ActiveCell);

            SendKey(window, Key.V, RawInputModifiers.Control);
            DrainTableOperation(preview);

            Assert.Equal("A,B,C\nD,E,F\nG,H,I", document.Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void LargeColumnCopyYieldsToUiAndSelectionChangeCancelsItsResult() => fixture.Run(() =>
    {
        var source = string.Join('\n', Enumerable.Repeat("a,b", 100_000));
        var clipboard = new ControlledClipboard("original", delayFirstWrite: true);
        var (window, preview, document) = CreatePreview(source, clipboard);
        try
        {
            ClickHeader(window, preview, "CsvColumnHeader:A");
            var stopwatch = Stopwatch.StartNew();
            SendKey(window, Key.C, RawInputModifiers.Control);
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"大きな列コピーの開始に {stopwatch.Elapsed} かかりました。");
            Assert.True(preview.IsTableOperationInProgress);
            WaitUntil(() => clipboard.FirstWriteStarted, "列コピーがクリップボード書き込みへ到達しませんでした。");

            ClickHeader(window, preview, "CsvColumnHeader:B");
            Assert.Equal((false, 1, 1), preview.SelectedHeaders);
            Assert.Equal(1, clipboard.SetCallCount);
            Assert.Equal(source, document.Text);

            clipboard.ReleaseFirstWrite();
            DrainTableOperation(preview);
            Assert.False(preview.IsTableOperationInProgress);
        }
        finally
        {
            clipboard.ReleaseFirstWrite();
            window.Close();
        }
    });

    [Fact]
    public void ArrowRoundTripCannotRevivePasteWaitingForClipboardRead() => fixture.Run(() =>
    {
        const string source = "a,b\n1,2";
        var clipboard = new ControlledClipboard("changed", delayFirstRead: true);
        var (window, preview, document) = CreatePreview(source, clipboard);
        try
        {
            ClickCell(window, preview, "A1");
            SendKey(window, Key.V, RawInputModifiers.Control);
            Assert.True(clipboard.FirstReadStarted);
            Assert.True(preview.IsTableOperationInProgress);

            SendKey(window, Key.Right, RawInputModifiers.None);
            SendKey(window, Key.Left, RawInputModifiers.None);
            Assert.Equal((0, 0), preview.ActiveCell);

            clipboard.ReleaseFirstRead();
            DrainTableOperation(preview);
            Assert.Equal(source, document.Text);
        }
        finally
        {
            clipboard.ReleaseFirstRead();
            window.Close();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingLargeColumnInsertCannotApplyAfterSourceOrDocumentChanges(bool switchDocument) => fixture.Run(() =>
    {
        var source = string.Join('\n', Enumerable.Repeat("a,b", 200_000));
        var clipboard = new ControlledClipboard(null);
        var (window, preview, document) = CreatePreview(source, clipboard);
        try
        {
            ClickHeader(window, preview, "CsvColumnHeader:A");
            var insert = GetHeaderMenuItem(preview, "CsvColumnHeader:A", "CsvInsertBeforeMenuItem");
            insert.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(preview.IsTableOperationInProgress);

            if (switchDocument)
            {
                var other = CreateDocument("other,value", "other.csv");
                preview.Document = other;
                preview.Csv = other.Text;
            }
            else
            {
                document.Text = "changed,value";
                preview.Csv = document.Text;
            }

            DrainTableOperation(preview);
            Assert.Equal(switchDocument ? source : "changed,value", document.Text);
            Assert.Equal(switchDocument ? "other,value" : "changed,value", preview.Document!.Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void PendingLargeColumnInsertSuppressesDuplicateOperation() => fixture.Run(() =>
    {
        var source = string.Join('\n', Enumerable.Repeat("a,b", 200_000));
        var clipboard = new ControlledClipboard(null);
        var (window, preview, document) = CreatePreview(source, clipboard);
        try
        {
            ClickHeader(window, preview, "CsvColumnHeader:A");
            var insert = GetHeaderMenuItem(preview, "CsvColumnHeader:A", "CsvInsertBeforeMenuItem");
            insert.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(preview.IsTableOperationInProgress);
            insert.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

            DrainTableOperation(preview);
            var parsed = CsvDocumentParser.Parse(document.Text, TestContext.Current.CancellationToken);
            Assert.Equal(3, parsed.TotalColumnCount);
        }
        finally
        {
            window.Close();
        }
    });

    private static (Window Window, CsvPreview Preview, DocumentViewModel Document) CreatePreview(
        string source,
        ControlledClipboard clipboard)
    {
        var document = CreateDocument(source, "operation.csv");
        var preview = new CsvPreview(clipboard.SetTextAsync, clipboard.GetTextAsync)
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

    private static void ClickCell(
        Window window,
        CsvPreview preview,
        string address,
        RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.UpdateLayout();
        var cell = preview.GetVisualDescendants()
            .OfType<Border>()
            .Single(control => AutomationProperties.GetName(control) == address);
        ClickControl(window, cell, modifiers);
    }

    private static void ClickHeader(Window window, CsvPreview preview, string name)
    {
        window.UpdateLayout();
        var header = FindHeader(preview, name);
        ClickControl(window, header, RawInputModifiers.None);
    }

    private static void ClickControl(Window window, Control control, RawInputModifiers modifiers)
    {
        var point = control.TranslatePoint(
            new Point(control.Bounds.Width / 2, control.Bounds.Height / 2),
            window)!.Value;
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static Border FindHeader(CsvPreview preview, string name)
        => preview.GetVisualDescendants()
            .OfType<Border>()
            .Single(control => AutomationProperties.GetName(control) == name);

    private static MenuItem GetHeaderMenuItem(CsvPreview preview, string headerName, string itemName)
    {
        var header = FindHeader(preview, headerName);
        var menu = Assert.IsType<ContextMenu>(header.ContextMenu);
        menu.Open(header);
        Dispatcher.UIThread.RunJobs();
        var item = menu.Items.OfType<MenuItem>().Single(item => item.Name == itemName);
        menu.Close();
        return item;
    }

    private static void SendKey(Window window, Key key, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, default, null);
        window.KeyRelease(key, modifiers, default, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void DrainRendering(CsvPreview preview)
    {
        WaitUntil(() => !preview.IsRenderPending, "CSVの非同期解析が完了しませんでした。");
    }

    private static void DrainTableOperation(CsvPreview preview)
    {
        WaitUntil(() => preview.PendingTableOperation.IsCompleted, "CSVの表操作が完了しませんでした。");
        Dispatcher.UIThread.RunJobs();
    }

    private static void WaitUntil(Func<bool> condition, string failureMessage)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            Thread.Sleep(1);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition(), failureMessage);
    }

    private sealed class ControlledClipboard(
        string? initialText,
        bool delayFirstWrite = false,
        bool delayFirstRead = false)
    {
        private readonly TaskCompletionSource _firstWriteRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstReadRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string? Text { get; private set; } = initialText;
        public int SetCallCount { get; private set; }
        public bool FirstWriteStarted { get; private set; }
        public bool FirstReadStarted { get; private set; }

        public async Task SetTextAsync(string text)
        {
            SetCallCount++;
            if (delayFirstWrite && SetCallCount == 1)
            {
                FirstWriteStarted = true;
                await _firstWriteRelease.Task;
            }
            Text = text;
        }

        public async Task<string?> GetTextAsync()
        {
            if (delayFirstRead && !FirstReadStarted)
            {
                FirstReadStarted = true;
                await _firstReadRelease.Task;
            }
            return Text;
        }

        public void ReleaseFirstWrite() => _firstWriteRelease.TrySetResult();
        public void ReleaseFirstRead() => _firstReadRelease.TrySetResult();
    }
}
