using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fumilume.Models;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class CsvClipboardIntegrationTests(HeadlessAppFixture fixture)
{
    [Fact]
    public void PendingCopySuppressesCutAndPasteUntilClipboardWriteCompletes() => fixture.Run(() =>
    {
        var clipboard = new DelayedClipboard("paste-value");
        var (window, preview, document) = CreatePreview(clipboard);
        try
        {
            ClickCell(window, preview, "A1");
            SendKey(window, Key.C, RawInputModifiers.Control);

            Assert.True(clipboard.FirstSetStarted);
            Assert.True(preview.IsClipboardOperationInProgress);
            Assert.Equal(1, clipboard.SetCallCount);

            ClickCell(window, preview, "B1");
            SendKey(window, Key.X, RawInputModifiers.Control);
            SendKey(window, Key.V, RawInputModifiers.Control);
            Assert.Equal(1, clipboard.SetCallCount);
            Assert.Equal(0, clipboard.GetCallCount);
            Assert.Equal("A,B", document.Text);

            clipboard.ReleaseFirstSet();
            Dispatcher.UIThread.RunJobs();
            Assert.False(preview.IsClipboardOperationInProgress);
            Assert.Equal("A", clipboard.Text);
            Assert.Equal("A,B", document.Text);

            SendKey(window, Key.X, RawInputModifiers.Control);
            Assert.Equal(2, clipboard.SetCallCount);
            Assert.Equal("B", clipboard.Text);
            Assert.Equal("A,", document.Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ClipboardFailureReleasesGateForTheNextCut() => fixture.Run(() =>
    {
        var clipboard = new DelayedClipboard("original", failFirstSet: true);
        var (window, preview, document) = CreatePreview(clipboard);
        try
        {
            ClickCell(window, preview, "A1");
            SendKey(window, Key.C, RawInputModifiers.Control);
            Assert.True(preview.IsClipboardOperationInProgress);

            clipboard.ReleaseFirstSet();
            Dispatcher.UIThread.RunJobs();
            Assert.False(preview.IsClipboardOperationInProgress);
            Assert.Equal("original", clipboard.Text);
            Assert.Equal("A,B", document.Text);

            ClickCell(window, preview, "B1");
            SendKey(window, Key.X, RawInputModifiers.Control);
            Assert.Equal(2, clipboard.SetCallCount);
            Assert.Equal("B", clipboard.Text);
            Assert.Equal("A,", document.Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingCutDoesNotClearChangedOrReplacedDocument(bool switchDocument) => fixture.Run(() =>
    {
        var clipboard = new DelayedClipboard("original");
        var (window, preview, document) = CreatePreview(clipboard);
        try
        {
            ClickCell(window, preview, "A1");
            SendKey(window, Key.X, RawInputModifiers.Control);
            Assert.True(preview.IsClipboardOperationInProgress);
            if (switchDocument)
            {
                var other = new DocumentViewModel("別の文書", _ => Task.CompletedTask);
                other.Load(@"C:\tmp\other.csv", new TextDocumentContent("other,value", DocumentEncoding.Utf8, "\n"));
                preview.Document = other;
                preview.Csv = other.Text;
            }
            else
            {
                document.Text = "changed,B";
                preview.Csv = document.Text;
            }
            Dispatcher.UIThread.RunJobs();
            clipboard.ReleaseFirstSet();
            Dispatcher.UIThread.RunJobs();
            Assert.False(preview.IsClipboardOperationInProgress);
            Assert.Equal(switchDocument ? "A,B" : "changed,B", document.Text);
            Assert.Equal(switchDocument ? "other,value" : "changed,B", preview.Document!.Text);
        }
        finally
        {
            window.Close();
        }
    });

    private static (Window Window, CsvPreview Preview, DocumentViewModel Document) CreatePreview(
        DelayedClipboard clipboard)
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Load(
            @"C:\tmp\clipboard.csv",
            new TextDocumentContent("A,B", DocumentEncoding.Utf8, "\n"));
        var preview = new CsvPreview(clipboard.SetTextAsync, clipboard.GetTextAsync)
        {
            Document = document,
            Csv = document.Text,
        };
        var window = new Window
        {
            Width = 500,
            Height = 300,
            Content = preview,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, preview, document);
    }

    private static void ClickCell(Window window, CsvPreview preview, string address)
    {
        window.UpdateLayout();
        var cell = preview.GetVisualDescendants()
            .OfType<Border>()
            .Single(control => AutomationProperties.GetName(control) == address);
        var point = cell.TranslatePoint(
            new Avalonia.Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2),
            window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void SendKey(Window window, Key key, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, default, null);
        window.KeyRelease(key, modifiers, default, null);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class DelayedClipboard
    {
        private readonly TaskCompletionSource<bool> _firstSetRelease = new();
        private readonly bool _failFirstSet;

        public DelayedClipboard(string? initialText, bool failFirstSet = false)
        {
            Text = initialText;
            _failFirstSet = failFirstSet;
        }

        public string? Text { get; private set; }
        public int SetCallCount { get; private set; }
        public int GetCallCount { get; private set; }
        public bool FirstSetStarted { get; private set; }

        public async Task SetTextAsync(string text)
        {
            var call = ++SetCallCount;
            if (call == 1)
            {
                FirstSetStarted = true;
                await _firstSetRelease.Task;
                if (_failFirstSet)
                {
                    throw new InvalidOperationException("意図したクリップボード書き込み失敗");
                }
            }
            Text = text;
        }

        public Task<string?> GetTextAsync()
        {
            GetCallCount++;
            return Task.FromResult(Text);
        }

        public void ReleaseFirstSet() => _firstSetRelease.TrySetResult(true);

    }
}
