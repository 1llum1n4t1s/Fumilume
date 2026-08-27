using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fumilume.Services;

namespace Fumilume.ViewModels;

public sealed partial class PdfDocumentViewModel : WorkspaceTabViewModel, IDisposable
{
    private readonly IPdfRenderer _renderer;
    private CancellationTokenSource? _renderCancellation;
    private bool _disposed;

    internal PdfDocumentViewModel(
        string filePath,
        IPdfRenderer renderer,
        Func<WorkspaceTabViewModel, Task> closeAsync)
        : base(closeAsync)
    {
        FilePath = Path.GetFullPath(filePath);
        _renderer = renderer;
    }

    public static async Task<PdfDocumentViewModel> OpenAsync(
        string filePath,
        Func<WorkspaceTabViewModel, Task> closeAsync)
    {
        var renderer = await WindowsPdfRenderer.OpenAsync(filePath);
        var viewModel = new PdfDocumentViewModel(filePath, renderer, closeAsync);
        await viewModel.RenderCurrentPageAsync();
        return viewModel;
    }

    public string FilePath { get; }

    public int PageCount => _renderer.PageCount;

    public override string TabTitle => Path.GetFileName(FilePath);

    public override string TabGlyph => "";

    public override string TabTooltip => FilePath;

    public override bool IsPdfTab => true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageStatus))]
    private int _currentPage = 1;

    [ObservableProperty]
    private Bitmap? _pageImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    private double _zoom = 1.0;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

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

    internal async Task NavigateAsync(int page)
    {
        var target = Math.Clamp(page, 1, PageCount);
        if (target == CurrentPage)
        {
            return;
        }

        CurrentPage = target;
        await RenderCurrentPageAsync();
    }

    private async Task ChangeZoomAsync(double zoom)
    {
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
        IsLoading = true;
        ErrorMessage = null;
        NotifyCommandStates();
        try
        {
            var bitmap = await _renderer.RenderAsync(CurrentPage - 1, Zoom, token);
            if (token.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            PageImage?.Dispose();
            PageImage = bitmap;
        }
        catch (OperationCanceledException)
        {
            // ページ移動やズームが連続したときは古い描画を捨てる。
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
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
        _renderer.Dispose();
    }
}
