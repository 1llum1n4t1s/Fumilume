using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fumilume.Models;
using Fumilume.Services;

namespace Fumilume.ViewModels;

public sealed partial class PdfDocumentViewModel : WorkspaceTabViewModel, IDisposable
{
    private readonly IPdfRenderer _renderer;
    private CancellationTokenSource? _renderCancellation;
    private bool _disposed;
    private Size _viewport;
    private Size _pageSize;
    private readonly PdfTextService? _textService;
    private int _textPageIndex = -1;

    internal PdfDocumentViewModel(
        string filePath,
        IPdfRenderer renderer,
        Func<WorkspaceTabViewModel, Task> closeAsync,
        PdfTextService? textService = null)
        : base(closeAsync)
    {
        FilePath = Path.GetFullPath(filePath);
        _renderer = renderer;
        _textService = textService;
    }

    public static async Task<PdfDocumentViewModel> OpenAsync(
        string filePath,
        Func<WorkspaceTabViewModel, Task> closeAsync)
    {
#if WINDOWS
        var renderer = await WindowsPdfRenderer.OpenAsync(filePath);
#else
        var renderer = await MacPdfRenderer.OpenAsync(filePath);
#endif
        var viewModel = new PdfDocumentViewModel(filePath, renderer, closeAsync, new PdfTextService(filePath));
        await viewModel.RenderCurrentPageAsync();
        return viewModel;
    }

    public string FilePath { get; }

    public int PageCount => _renderer.PageCount;

    public override string TabTitle => Path.GetFileName(FilePath);

    public override string TabGlyph => "";

    public override string TabTooltip => FilePath;

    public override bool IsPdfTab => true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageStatus))]
    private int _currentPage = 1;

    [ObservableProperty]
    private Bitmap? _pageImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    [NotifyPropertyChangedFor(nameof(PageWidth))]
    [NotifyPropertyChangedFor(nameof(PageHeight))]
    private double _zoom = 1.0;

    [ObservableProperty]
    private PdfZoomMode _zoomMode = PdfZoomMode.FitWidth;

    public double PageWidth => _pageSize.Width * Zoom;

    public double PageHeight => _pageSize.Height * Zoom;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private PdfTextPage? _textPage;

    [ObservableProperty]
    private string? _textSelectionStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedText))]
    [NotifyPropertyChangedFor(nameof(HasTextSelection))]
    private int _selectionStart = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedText))]
    [NotifyPropertyChangedFor(nameof(HasTextSelection))]
    private int _selectionEnd = -1;

    public bool HasTextSelection => TextPage is { Characters.Count: > 0 }
        && SelectionStart >= 0 && SelectionEnd >= 0;

    public string SelectedText
    {
        get
        {
            if (!HasTextSelection || TextPage is not { } page) return string.Empty;
            var start = Math.Min(SelectionStart, SelectionEnd);
            var end = Math.Min(Math.Max(SelectionStart, SelectionEnd), page.Characters.Count - 1);
            var text = new System.Text.StringBuilder();
            for (var index = start; index <= end; index++)
            {
                var character = page.Characters[index];
                if (index > start) text.Append(character.SeparatorBefore);
                text.Append(character.Text);
            }
            return text.ToString();
        }
    }

    internal void SelectText(int start, int end)
    {
        if (TextPage is not { Characters.Count: > 0 } page) return;
        SelectionStart = Math.Clamp(start, 0, page.Characters.Count - 1);
        SelectionEnd = Math.Clamp(end, 0, page.Characters.Count - 1);
    }

    public void ClearSelection()
    {
        SelectionStart = -1;
        SelectionEnd = -1;
    }

    [RelayCommand]
    private void SelectAllText()
    {
        if (TextPage is { Characters.Count: > 0 } page) SelectText(0, page.Characters.Count - 1);
    }

    public string PageStatus => $"{CurrentPage:N0} / {PageCount:N0}";

    public string ZoomText => $"{Zoom:P0}";

    private bool CanGoPrevious() => !IsLoading && CurrentPage > 1;

    private bool CanGoNext() => !IsLoading && CurrentPage < PageCount;

    private bool CanZoomOut() => !IsLoading && Zoom > 0.25;

    private bool CanZoomIn() => !IsLoading && Zoom < 4.0;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private Task PreviousPageAsync() => NavigateAsync(CurrentPage - 1);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private Task NextPageAsync() => NavigateAsync(CurrentPage + 1);

    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private Task ZoomOutAsync() => ChangeZoomAsync(Zoom / 1.25);

    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    private Task ZoomInAsync() => ChangeZoomAsync(Zoom * 1.25);

    [RelayCommand]
    private Task ActualSizeAsync() => ChangeZoomAsync(1.0);

    [RelayCommand]
    private Task FitWidthAsync() => FitAsync(PdfZoomMode.FitWidth);

    [RelayCommand]
    private Task FitHeightAsync() => FitAsync(PdfZoomMode.FitHeight);

    private Task FitAsync(PdfZoomMode mode)
    {
        ZoomMode = mode;
        return RenderCurrentPageAsync();
    }

    internal Task UpdateViewportAsync(Size viewport)
    {
        if (_disposed || !double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height)
            || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return Task.CompletedTask;
        }

        _viewport = viewport;
        return ZoomMode != PdfZoomMode.Manual
            && ((PageImage is null && !IsLoading) || Math.Abs(GetFitZoom() - Zoom) > 0.000000001)
            ? RenderCurrentPageAsync()
            : Task.CompletedTask;
    }

    private double GetFitZoom() => ZoomMode switch
    {
        PdfZoomMode.FitWidth when _viewport.Width > 0 && _pageSize.Width > 0
            => _viewport.Width / _pageSize.Width,
        PdfZoomMode.FitHeight when _viewport.Height > 0 && _pageSize.Height > 0
            => _viewport.Height / _pageSize.Height,
        _ => Zoom,
    };

    internal async Task NavigateAsync(int page)
    {
        var target = Math.Clamp(page, 1, PageCount);
        if (target == CurrentPage)
        {
            return;
        }

        CurrentPage = target;
        ClearSelection();
        TextPage = null;
        await RenderCurrentPageAsync();
    }

    private async Task ChangeZoomAsync(double zoom)
    {
        ZoomMode = PdfZoomMode.Manual;
        var target = Math.Clamp(zoom, 0.25, 4.0);
        if (Math.Abs(target - Zoom) < 0.001)
        {
            return;
        }

        Zoom = target;
        await RenderCurrentPageAsync();
    }

    internal async Task RenderCurrentPageAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _renderCancellation?.Cancel();
        _renderCancellation?.Dispose();
        _renderCancellation = new CancellationTokenSource();
        var token = _renderCancellation.Token;
        var pageIndex = CurrentPage - 1;
        IsLoading = true;
        ErrorMessage = null;
        NotifyCommandStates();
        try
        {
            _pageSize = _renderer.GetPageSize(pageIndex);
            Zoom = GetFitZoom();
            OnPropertyChanged(nameof(PageWidth));
            OnPropertyChanged(nameof(PageHeight));
            var bitmap = await _renderer.RenderAsync(pageIndex, Zoom, token);
            if (token.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            PageImage?.Dispose();
            PageImage = bitmap;
            if (_textService is not null && (_textPageIndex != pageIndex || TextPage is null))
            {
                TextSelectionStatus = "文字情報を読み込んでいます…";
                try
                {
                    var textPage = await _textService.ReadPageAsync(pageIndex, token);
                    token.ThrowIfCancellationRequested();
                    ClearSelection();
                    TextPage = textPage;
                    _textPageIndex = pageIndex;
                    TextSelectionStatus = textPage.Characters.Count == 0
                        ? "このページに選択できる文字はありません"
                        : null;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        TextSelectionStatus = ex is UnauthorizedAccessException
                            ? ex.Message
                            : "文字情報を読み込めないため、このページでは選択できません";
                        AppLogger.For<PdfDocumentViewModel>().Error($"PDF の文字情報を読み込めませんでした: {FilePath}", ex);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ページ移動やズームが連続したときは古い描画を捨てる。
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                ErrorMessage = ex.Message;
            }
            AppLogger.For<PdfDocumentViewModel>().Error($"PDF ページを描画できませんでした: {FilePath}", ex);
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
                NotifyCommandStates();
            }
        }
    }

    private void NotifyCommandStates()
    {
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
        ZoomInCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _renderCancellation?.Cancel();
        _renderCancellation?.Dispose();
        PageImage?.Dispose();
        PageImage = null;
        TextPage = null;
        ClearSelection();
        _textService?.Dispose();
        _renderer.Dispose();
    }
}
