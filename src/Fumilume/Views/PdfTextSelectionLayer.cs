using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Fumilume.ViewModels;

namespace Fumilume.Views;

/// <summary>1 ページ分の画像と同じ表示寸法で、PDF の文字位置を選択範囲へ対応させる。</summary>
public sealed class PdfTextSelectionLayer : Control
{
    public static readonly StyledProperty<PdfPageViewModel?> PageProperty =
        AvaloniaProperty.Register<PdfTextSelectionLayer, PdfPageViewModel?>(nameof(Page));

    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(90, 50, 120, 230));
    private readonly MenuItem _copyItem;
    private readonly MenuItem _selectAllItem;
    private PdfPageViewModel? _page;
    private IPointer? _capturedPointer;
    private int _anchor = -1;

    public PdfTextSelectionLayer()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        _copyItem = new MenuItem { Name = "PdfCopyMenuItem", Header = "コピー" };
        _selectAllItem = new MenuItem { Name = "PdfSelectAllMenuItem", Header = "ページ内の文字をすべて選択" };
        _copyItem.Click += (_, _) => _page?.Owner.CopyCommand?.Execute(null);
        _selectAllItem.Click += (_, _) =>
        {
            if (_page is not { } page) return;
            page.Owner.LastPressedPageIndex = page.Index;
            page.Owner.SelectAllOnPage(page.Index);
        };
        ContextMenu = new ContextMenu { ItemsSource = new[] { _copyItem, _selectAllItem } };
        ContextMenu.Opening += (_, _) =>
        {
            _copyItem.IsEnabled = _page?.Owner.HasTextSelection == true;
            _selectAllItem.IsEnabled = _page?.TextPage is { Characters.Count: > 0 };
        };
    }

    public PdfPageViewModel? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != PageProperty) return;
        Unsubscribe();
        ReleasePointer();
        _page = Page;
        if (VisualRoot is not null) Subscribe();
        InvalidateVisual();
    }

    private void Subscribe()
    {
        if (_page is null) return;
        _page.PropertyChanged -= OnPagePropertyChanged;
        _page.PropertyChanged += OnPagePropertyChanged;
        _page.Owner.PropertyChanged -= OnDocumentPropertyChanged;
        _page.Owner.PropertyChanged += OnDocumentPropertyChanged;
    }

    private void Unsubscribe()
    {
        if (_page is null) return;
        _page.PropertyChanged -= OnPagePropertyChanged;
        _page.Owner.PropertyChanged -= OnDocumentPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // ビジュアルツリーから外れた（ページが表示範囲を離れた）ときは捕捉を手放す。
        ReleasePointer();
        Unsubscribe();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe();
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        // 捕捉を手放すのは、このページの文字情報が差し替わったときだけ。
        // 隣のページの描画や画像の更新で、ドラッグを途切れさせない。
        if (args.PropertyName == nameof(PdfPageViewModel.TextPage)) ReleasePointer();
        if (args.PropertyName is nameof(PdfPageViewModel.TextPage)) InvalidateVisual();
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(PdfDocumentViewModel.SelectionPageIndex)
            or nameof(PdfDocumentViewModel.SelectionStart)
            or nameof(PdfDocumentViewModel.SelectionEnd))
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_page is not { TextPage: { } page } || _page.Owner is not { HasTextSelection: true } document
            || document.SelectionPageIndex != _page.Index) return;
        var start = Math.Min(document.SelectionStart, document.SelectionEnd);
        var end = Math.Min(Math.Max(document.SelectionStart, document.SelectionEnd), page.Characters.Count - 1);
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
        if (_page?.TextPage is not { Characters.Count: > 0 } page
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
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _page is not { } page) return;
        var document = page.Owner;
        Focus();
        document.LastPressedPageIndex = page.Index;
        _anchor = HitCharacter(e.GetPosition(this), nearest: false);
        if (_anchor < 0)
        {
            // 文字以外をクリックしたら、どのページの選択も解除する。
            document.ClearSelection();
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && document.HasTextSelection
            && document.SelectionPageIndex == page.Index)
            _anchor = document.SelectionStart;
        document.SelectText(page.Index, _anchor, HitCharacter(e.GetPosition(this), nearest: true));
        _capturedPointer = e.Pointer;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_anchor < 0 || e.Pointer.Captured != this || _page is not { } page) return;
        var index = HitCharacter(e.GetPosition(this), nearest: true);
        if (index >= 0) page.Owner.SelectText(page.Index, _anchor, index);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left || _capturedPointer != e.Pointer) return;
        var index = HitCharacter(e.GetPosition(this), nearest: true);
        if (_anchor >= 0 && index >= 0 && _page is { } page) page.Owner.SelectText(page.Index, _anchor, index);
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
        if (_page is not { } page) return;
        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.KeyModifiers == primary && e.Key == Key.C)
        {
            page.Owner.CopyCommand?.Execute(null);
            e.Handled = true;
        }
        else if (e.KeyModifiers == primary && e.Key == Key.A)
        {
            page.Owner.LastPressedPageIndex = page.Index;
            page.Owner.SelectAllOnPage(page.Index);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            page.Owner.ClearSelection();
            e.Handled = true;
        }
    }
}
