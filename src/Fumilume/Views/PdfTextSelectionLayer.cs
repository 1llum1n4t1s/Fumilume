using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Fumilume.ViewModels;

namespace Fumilume.Views;

/// <summary>ページ画像と同じ表示寸法で、PDF の文字位置を選択範囲へ対応させる。</summary>
public sealed class PdfTextSelectionLayer : Control
{
    public static readonly StyledProperty<ICommand?> CopyCommandProperty =
        AvaloniaProperty.Register<PdfTextSelectionLayer, ICommand?>(nameof(CopyCommand));
    public static readonly StyledProperty<PdfDocumentViewModel?> DocumentProperty =
        AvaloniaProperty.Register<PdfTextSelectionLayer, PdfDocumentViewModel?>(nameof(Document));

    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(90, 50, 120, 230));
    private readonly MenuItem _copyItem;
    private readonly MenuItem _selectAllItem;
    private PdfDocumentViewModel? _document;
    private IPointer? _capturedPointer;
    private int _anchor = -1;

    public PdfTextSelectionLayer()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        _copyItem = new MenuItem { Name = "PdfCopyMenuItem", Header = "コピー" };
        _selectAllItem = new MenuItem { Name = "PdfSelectAllMenuItem", Header = "ページ内の文字をすべて選択" };
        _copyItem.Click += (_, _) => CopyCommand?.Execute(null);
        _selectAllItem.Click += (_, _) => _document?.SelectAllTextCommand.Execute(null);
        ContextMenu = new ContextMenu { ItemsSource = new[] { _copyItem, _selectAllItem } };
        ContextMenu.Opening += (_, _) =>
        {
            _copyItem.IsEnabled = _document?.HasTextSelection == true;
            _selectAllItem.IsEnabled = _document?.TextPage is { Characters.Count: > 0 };
        };
    }

    public ICommand? CopyCommand
    {
        get => GetValue(CopyCommandProperty);
        set => SetValue(CopyCommandProperty, value);
    }

    public PdfDocumentViewModel? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != DocumentProperty) return;
        if (_document is not null) _document.PropertyChanged -= OnDocumentPropertyChanged;
        ReleasePointer();
        _document = Document;
        if (_document is not null) _document.PropertyChanged += OnDocumentPropertyChanged;
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleasePointer();
        if (_document is not null) _document.PropertyChanged -= OnDocumentPropertyChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_document is not null)
        {
            _document.PropertyChanged -= OnDocumentPropertyChanged;
            _document.PropertyChanged += OnDocumentPropertyChanged;
        }
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(PdfDocumentViewModel.TextPage) or nameof(PdfDocumentViewModel.IsLoading))
            ReleasePointer();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_document is not { HasTextSelection: true, TextPage: { } page }) return;
        var start = Math.Min(_document.SelectionStart, _document.SelectionEnd);
        var end = Math.Min(Math.Max(_document.SelectionStart, _document.SelectionEnd), page.Characters.Count - 1);
        for (var index = start; index <= end; index++)
            context.FillRectangle(SelectionBrush, ToDisplayBounds(page.Characters[index].Bounds, page.Size));
    }

    private Rect ToDisplayBounds(Rect rectangle, Size pageSize)
        => new(rectangle.X * Bounds.Width / pageSize.Width,
            rectangle.Y * Bounds.Height / pageSize.Height,
            rectangle.Width * Bounds.Width / pageSize.Width,
            rectangle.Height * Bounds.Height / pageSize.Height);

    private int HitCharacter(Point displayPoint, bool nearest)
    {
        if (_document?.TextPage is not { Characters.Count: > 0 } page
            || page.Size.Width <= 0 || page.Size.Height <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return -1;
        var point = new Point(displayPoint.X * page.Size.Width / Bounds.Width,
            displayPoint.Y * page.Size.Height / Bounds.Height);
        var closest = -1;
        var closestDistance = double.MaxValue;
        for (var index = 0; index < page.Characters.Count; index++)
        {
            var rectangle = page.Characters[index].Bounds;
            if (rectangle.Contains(point)) return index;
            if (!nearest) continue;
            var dx = Math.Max(Math.Max(rectangle.Left - point.X, 0), point.X - rectangle.Right);
            var dy = Math.Max(Math.Max(rectangle.Top - point.Y, 0), point.Y - rectangle.Bottom);
            var distance = dx * dx + dy * dy;
            if (distance >= closestDistance) continue;
            closestDistance = distance;
            closest = index;
        }
        return closest;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _document is null) return;
        Focus();
        _anchor = HitCharacter(e.GetPosition(this), nearest: false);
        if (_anchor < 0)
        {
            _document.ClearSelection();
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _document.HasTextSelection)
            _anchor = _document.SelectionStart;
        _document.SelectText(_anchor, HitCharacter(e.GetPosition(this), nearest: true));
        _capturedPointer = e.Pointer;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_anchor < 0 || e.Pointer.Captured != this || _document is null) return;
        var index = HitCharacter(e.GetPosition(this), nearest: true);
        if (index >= 0) _document.SelectText(_anchor, index);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left || _capturedPointer != e.Pointer) return;
        var index = HitCharacter(e.GetPosition(this), nearest: true);
        if (_anchor >= 0 && index >= 0) _document?.SelectText(_anchor, index);
        ReleasePointer();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _capturedPointer = null;
        _anchor = -1;
    }

    private void ReleasePointer()
    {
        var pointer = _capturedPointer;
        _capturedPointer = null;
        _anchor = -1;
        pointer?.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.KeyModifiers == primary && e.Key == Key.C)
        {
            CopyCommand?.Execute(null);
            e.Handled = true;
        }
        else if (e.KeyModifiers == primary && e.Key == Key.A)
        {
            _document?.SelectAllTextCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _document?.ClearSelection();
            e.Handled = true;
        }
    }
}
