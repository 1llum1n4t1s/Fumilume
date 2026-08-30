using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;

namespace Fumilume.Views;

/// <summary>
/// 文書全体を縮約して右端に描く、VS Code 型のスクロールマップ。
/// 実際の文字を極小描画せず行の形を棒へ縮約するため、大きな文書でも表示行数に比例して重くならない。
/// </summary>
public sealed class EditorMinimap : Control
{
    private TextEditor? _editor;
    private TextDocument? _document;
    private bool _dragging;

    public void Attach(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (ReferenceEquals(_editor, editor))
        {
            RefreshDocument();
            return;
        }

        Detach();
        _editor = editor;
        var textView = editor.TextArea.TextView;
        textView.ScrollOffsetChanged += OnEditorViewChanged;
        textView.VisualLinesChanged += OnEditorViewChanged;
        RefreshDocument();
    }

    public void RefreshDocument()
    {
        var next = _editor?.Document;
        if (ReferenceEquals(_document, next))
        {
            InvalidateVisual();
            return;
        }

        if (_document is not null)
        {
            _document.TextChanged -= OnDocumentTextChanged;
        }

        _document = next;
        if (_document is not null)
        {
            _document.TextChanged += OnDocumentTextChanged;
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var document = _editor?.Document;
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (document is null || width <= 1 || height <= 1)
        {
            return;
        }

        var secondary = FindColor("TextSecondary", Color.FromRgb(128, 128, 128));
        var accent = FindColor("AccentBrush", Color.FromRgb(15, 108, 189));
        var divider = FindColor("Divider", Color.FromRgb(96, 96, 96));
        var lineBrush = new SolidColorBrush(Color.FromArgb(112, secondary.R, secondary.G, secondary.B));
        var viewportBrush = new SolidColorBrush(Color.FromArgb(42, accent.R, accent.G, accent.B));
        var viewportPen = new Pen(new SolidColorBrush(Color.FromArgb(130, accent.R, accent.G, accent.B)), 1);

        context.FillRectangle(
            new SolidColorBrush(Color.FromArgb(36, secondary.R, secondary.G, secondary.B)),
            Bounds);
        context.FillRectangle(new SolidColorBrush(divider), new Rect(0, 0, 1, height));

        var lineCount = Math.Max(1, document.LineCount);
        var rowCount = Math.Min(lineCount, Math.Max(1, (int)Math.Floor(height)));
        var contentWidth = Math.Max(1, width - 8);
        for (var row = 0; row < rowCount; row++)
        {
            var startLine = (int)Math.Floor(row * lineCount / (double)rowCount) + 1;
            var endLine = Math.Max(startLine, (int)Math.Floor((row + 1) * lineCount / (double)rowCount));
            var metrics = MeasureRepresentativeLine(document, startLine, endLine);
            if (metrics.Length == 0)
            {
                continue;
            }

            var x = 4 + Math.Min(contentWidth * 0.32, metrics.Indent / 48d * contentWidth);
            var available = Math.Max(1, width - x - 3);
            var lineWidth = Math.Clamp(metrics.Length / 120d * contentWidth, 2, available);
            var y = row * height / rowCount;
            context.FillRectangle(lineBrush, new Rect(x, y, lineWidth, 1));
        }

        DrawViewport(context, viewportBrush, viewportPen, lineCount, height, width);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = true;
        e.Pointer.Capture(this);
        ScrollTo(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ScrollTo(e.GetPosition(this).Y);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            ScrollTo(e.GetPosition(this).Y);
            _dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    internal static int MapPointToLine(double y, double height, int lineCount)
    {
        if (lineCount <= 1 || height <= 0)
        {
            return 1;
        }

        var ratio = Math.Clamp(y / height, 0, 1);
        return Math.Clamp((int)Math.Round(ratio * (lineCount - 1)) + 1, 1, lineCount);
    }

    internal static MinimapLineMetrics MeasureLine(string text)
    {
        var indent = 0;
        var index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            indent += text[index] == '\t' ? 4 : 1;
            index++;
        }

        var length = Math.Min(160, text.AsSpan(index).TrimEnd().Length);
        return new MinimapLineMetrics(Math.Min(indent, 48), length);
    }

    private static MinimapLineMetrics MeasureRepresentativeLine(
        TextDocument document,
        int startLine,
        int endLine)
    {
        var best = default(MinimapLineMetrics);
        var step = Math.Max(1, (endLine - startLine + 1) / 6);
        for (var lineNumber = startLine; lineNumber <= endLine; lineNumber += step)
        {
            var line = document.GetLineByNumber(lineNumber);
            var metrics = MeasureLine(document.GetText(line.Offset, Math.Min(line.Length, 512)));
            if (metrics.Length > best.Length)
            {
                best = metrics;
            }
        }

        return best;
    }

    private void DrawViewport(
        DrawingContext context,
        IBrush fill,
        IPen pen,
        int lineCount,
        double height,
        double width)
    {
        var textView = _editor?.TextArea.TextView;
        if (textView is null || !textView.VisualLinesValid || textView.VisualLines.Count == 0)
        {
            return;
        }

        var first = textView.VisualLines[0].FirstDocumentLine.LineNumber;
        var last = textView.VisualLines[^1].LastDocumentLine.LineNumber;
        var top = (first - 1d) / lineCount * height;
        var bottom = last / (double)lineCount * height;
        var viewportHeight = Math.Clamp(bottom - top, Math.Min(12, height), height);
        top = Math.Clamp(top, 0, Math.Max(0, height - viewportHeight));
        context.DrawRectangle(fill, pen, new Rect(1, top, Math.Max(0, width - 1), viewportHeight));
    }

    private void ScrollTo(double y)
    {
        if (_editor?.Document is not { } document)
        {
            return;
        }

        var textView = _editor.TextArea.TextView;
        var scrollable = (IScrollable)textView;
        var line = MapPointToLine(y, Bounds.Height, document.LineCount);
        var ratio = document.LineCount <= 1 ? 0 : (line - 1d) / (document.LineCount - 1d);
        var target = ratio * scrollable.Extent.Height - scrollable.Viewport.Height / 2;
        var maximum = Math.Max(0, scrollable.Extent.Height - scrollable.Viewport.Height);
        scrollable.Offset = new Vector(scrollable.Offset.X, Math.Clamp(target, 0, maximum));
    }

    private void OnEditorViewChanged(object? sender, EventArgs args)
    {
        RefreshDocument();
        InvalidateVisual();
    }

    private void OnDocumentTextChanged(object? sender, EventArgs args) => InvalidateVisual();

    private Color FindColor(string key, Color fallback)
        => this.TryFindResource(key, out var value) && value is ISolidColorBrush brush ? brush.Color : fallback;

    private void Detach()
    {
        if (_editor is not null)
        {
            var textView = _editor.TextArea.TextView;
            textView.ScrollOffsetChanged -= OnEditorViewChanged;
            textView.VisualLinesChanged -= OnEditorViewChanged;
        }

        if (_document is not null)
        {
            _document.TextChanged -= OnDocumentTextChanged;
        }

        _editor = null;
        _document = null;
    }
}

internal readonly record struct MinimapLineMetrics(int Indent, int Length);
