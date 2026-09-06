using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;

namespace Fumilume.Views;

/// <summary>AvaloniaEdit の IME 接続を保ち、本文を変更せず未確定文字を表示する。</summary>
internal sealed class EditorInputMethod : TextInputMethodClient
{
    private readonly TextEditor _editor;
    private readonly PreeditLayer _layer;
    private TextInputMethodClient? _inner;
    private string _text = "";
    private int _cursor;

    public EditorInputMethod(TextEditor editor)
    {
        _editor = editor;
        _layer = new PreeditLayer(this) { Name = "EditorPreeditLayer", IsHitTestVisible = false, ClipToBounds = true };
        editor.TextArea.TextView.InsertLayer(_layer, KnownLayer.Caret, LayerInsertionPosition.Above);
        editor.TextArea.AddHandler(InputElement.TextInputMethodClientRequestedEvent, OnClientRequested);
        editor.TextArea.LostFocus += (_, _) => Clear();
        editor.DocumentChanged += (_, _) => Clear();
        editor.TextArea.TextEntering += (_, _) => Clear();
        editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Refresh();
        editor.ActualThemeVariantChanged += (_, _) => Refresh();
    }

    private void OnClientRequested(object? sender, TextInputMethodClientRequestedEventArgs args)
    {
        if (args.Client is not { } client || ReferenceEquals(client, this))
            return;

        if (!ReferenceEquals(_inner, client))
        {
            if (_inner is not null)
            {
                _inner.CursorRectangleChanged -= OnCursorChanged;
                _inner.SurroundingTextChanged -= OnSurroundingChanged;
                _inner.SelectionChanged -= OnSelectionChanged;
                _inner.TextViewVisualChanged -= OnVisualChanged;
            }
            _inner = client;
            client.CursorRectangleChanged += OnCursorChanged;
            client.SurroundingTextChanged += OnSurroundingChanged;
            client.SelectionChanged += OnSelectionChanged;
            client.TextViewVisualChanged += OnVisualChanged;
        }
        args.Client = this;
    }

    public override Visual TextViewVisual => _inner?.TextViewVisual ?? _editor.TextArea;
    public override bool SupportsPreedit => true;
    public override bool SupportsSurroundingText => _inner?.SupportsSurroundingText ?? false;
    public override string SurroundingText => _inner?.SurroundingText ?? "";
    public override TextSelection Selection
    {
        get => _inner?.Selection ?? default;
        set { if (_inner is not null) _inner.Selection = value; }
    }
    public override void ExecuteContextMenuAction(ContextMenuAction action) => _inner?.ExecuteContextMenuAction(action);

    public override Rect CursorRectangle
    {
        get
        {
            if (_text.Length == 0 || _layer.Bounds.Width <= 0)
                return _inner?.CursorRectangle ?? default;
            var origin = GetOrigin();
            using var layout = CreateLayout(origin);
            var caret = layout.HitTestTextPosition(_cursor);
            var point = _layer.TranslatePoint(origin + caret.Position, TextViewVisual);
            return point is { } position ? new Rect(position, new Size(1, caret.Height)) : default;
        }
    }

    public override void SetPreeditText(string? text) => SetPreeditText(text, null);

    public override void SetPreeditText(string? text, int? cursorPos)
    {
        _text = text ?? "";
        _cursor = Math.Clamp(cursorPos ?? _text.Length, 0, _text.Length);
        Refresh();
    }

    private void Clear() => SetPreeditText(null, null);
    private void Refresh()
    {
        _layer.InvalidateVisual();
        RaiseCursorRectangleChanged();
    }
    private void OnCursorChanged(object? sender, EventArgs args) => Refresh();
    private void OnSurroundingChanged(object? sender, EventArgs args) => RaiseSurroundingTextChanged();
    private void OnSelectionChanged(object? sender, EventArgs args) => RaiseSelectionChanged();
    private void OnVisualChanged(object? sender, EventArgs args) => RaiseTextViewVisualChanged();

    private Point GetOrigin()
    {
        var caret = _inner?.CursorRectangle ?? default;
        var point = TextViewVisual.TranslatePoint(caret.Position, _layer) ?? default;
        // 行末・画面下端でも変換中の文字が切れないよう、表示領域内へ寄せる。
        using var layout = CreateLayout(default);
        return new Point(
            Math.Clamp(point.X, 0, Math.Max(0, _layer.Bounds.Width - layout.WidthIncludingTrailingWhitespace)),
            Math.Clamp(point.Y, 0, Math.Max(0, _layer.Bounds.Height - layout.Height)));
    }

    private TextLayout CreateLayout(Point origin) => new(
        _text, new Typeface(_editor.FontFamily, _editor.FontStyle, _editor.FontWeight),
        _editor.FontSize, _editor.Foreground,
        textWrapping: TextWrapping.Wrap, textDecorations: TextDecorations.Underline,
        maxWidth: Math.Max(1, _layer.Bounds.Width - origin.X));

    private sealed class PreeditLayer(EditorInputMethod owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            if (owner._text.Length == 0 || Bounds.Width <= 0)
                return;
            var origin = owner.GetOrigin();
            using var layout = owner.CreateLayout(origin);
            var background = owner._editor.FindResource("EditorBg") as IBrush ?? Brushes.White;
            context.FillRectangle(background, new Rect(origin, new Size(layout.WidthIncludingTrailingWhitespace, layout.Height)));
            layout.Draw(context, origin);
            var caret = layout.HitTestTextPosition(owner._cursor);
            var position = origin + caret.Position;
            context.DrawLine(new Pen(owner._editor.Foreground, 1), position, position.WithY(position.Y + caret.Height));
        }
    }
}
