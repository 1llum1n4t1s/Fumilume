using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fumilume.Models;
using Fumilume.Services;

namespace Fumilume.ViewModels;

/// <summary>View へ渡すスクロール位置の要求。適用後に <see cref="PdfDocumentViewModel.CompleteScrollRequest"/> で返す。</summary>
public sealed record PdfScrollRequest(int Id, double OffsetX, double OffsetY);

/// <summary>
/// 読み取り専用 PDF。全ページを縦につなげた連続表示を管理する。
/// 全ページの配置は寸法だけで計算し、画像と文字情報は表示範囲の近くのページだけを保持する。
/// </summary>
public sealed partial class PdfDocumentViewModel : WorkspaceTabViewModel, IDisposable
{
    internal const double MinimumManualZoom = 0.25;
    internal const double MaximumManualZoom = 4.0;

    /// <summary>View に載せるページ数の上限。極端に小さい倍率でも要素数を抑える。</summary>
    internal const int MaximumVisiblePages = 100;

    /// <summary>表示範囲の外側で、画像を保持するページ数。</summary>
    private const int RetainedPageMargin = 3;

    /// <summary>保持する画像の画素数の合計の上限。超えたら表示から遠いページの画像を破棄する。</summary>
    internal const long MaximumRetainedPixels = 64L * 1024 * 1024;

    /// <summary>画像の画素数の合計の上限。テストで小さくするために差し替えられる。</summary>
    internal long RetainedPixelLimit { get; set; } = MaximumRetainedPixels;

    private const int MaximumTextPages = 32;
    private const double Epsilon = 0.000000001;

    private readonly IPdfRenderer _renderer;
    private readonly PdfTextService? _textService;
    private readonly Size[] _pageSizes;
    private readonly double _widestPage;
    private readonly double _tallestPage;
    private readonly Dictionary<int, PdfPageViewModel> _pages = [];
    private readonly HashSet<int> _visibleIndexes = [];
    private readonly HashSet<int> _failedPages = [];
    private readonly Dictionary<int, PdfTextPage> _textCache = [];
    private readonly List<int> _textUsage = [];
    private readonly HashSet<int> _failedTextPages = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _generationCancellation = new();
    private PdfPageLayout _layout;
    private Task? _renderWorker;
    private int _renderWorkerGeneration = -1;
    private Task? _textWorker;
    private int _generation;
    private int _requestId;
    private bool _disposed;
    private bool _textDenied;
    private string? _textDeniedMessage;
    private Size _available;
    // 1 DIP あたりのデバイス画素数。View が表示領域と一緒に渡すまでは 1 とする。
    private double _renderScaling = 1;
    private double _viewportWidth;
    private double _viewportHeight;
    private double _offsetX;
    private double _offsetY;
    private bool _scrollPlaced;
    private PdfScrollAnchor? _anchor;
    private int? _pinnedPage;
    private double? _pinnedOffsetY;
    private int _viewFirst;
    private int _viewLast = -1;

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
        _pageSizes = new Size[renderer.PageCount];
        for (var index = 0; index < _pageSizes.Length; index++)
        {
            _pageSizes[index] = renderer.GetPageSize(index);
            _widestPage = Math.Max(_widestPage, _pageSizes[index].Width);
            _tallestPage = Math.Max(_tallestPage, _pageSizes[index].Height);
        }

        _layout = new PdfPageLayout(_pageSizes, _zoom, 0, _renderScaling);
        _contentWidth = _layout.ContentWidth;
        _contentHeight = _layout.ContentHeight;
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
        await viewModel.RefreshAsync();
        return viewModel;
    }

    public string FilePath { get; }

    public int PageCount => _renderer.PageCount;

    public override string TabTitle => Path.GetFileName(FilePath);

    public override string TabGlyph => "";

    public override string TabTooltip => FilePath;

    public override bool IsPdfTab => true;

    /// <summary>View に載せているページ（表示範囲とその前後 1 ページ）。</summary>
    public ObservableCollection<PdfPageViewModel> VisiblePages { get; } = [];

    /// <summary>コピーの実行先。選択中のタブにしたときに <see cref="MainWindowViewModel"/> が設定する。</summary>
    internal ICommand? CopyCommand { get; set; }

    /// <summary>最後に文字選択の操作を始めたページ（0 始まり）。全選択の対象にする。</summary>
    internal int LastPressedPageIndex { get; set; } = -1;

    private int _currentPage = 1;

    /// <summary>表示範囲の中心にあるページ（1 始まり）。</summary>
    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (!SetProperty(ref _currentPage, value)) return;
            OnPropertyChanged(nameof(PageStatus));
            ResetPageInput();
            NotifyCommandStates();
            UpdateTextStatus();
        }
    }

    private double _zoom = 1.0;

    public double Zoom
    {
        get => _zoom;
        private set
        {
            if (!SetProperty(ref _zoom, value)) return;
            OnPropertyChanged(nameof(ZoomText));
            NotifyCommandStates();
        }
    }

    [ObservableProperty]
    private PdfZoomMode _zoomMode = PdfZoomMode.FitWidth;

    [ObservableProperty]
    private double _contentWidth;

    [ObservableProperty]
    private double _contentHeight;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private PdfScrollRequest? _scrollRequest;

    [ObservableProperty]
    private string _pageInputText = "1";

    [ObservableProperty]
    private string? _textSelectionStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedText))]
    [NotifyPropertyChangedFor(nameof(HasTextSelection))]
    private int _selectionPageIndex = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedText))]
    [NotifyPropertyChangedFor(nameof(HasTextSelection))]
    private int _selectionStart = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedText))]
    [NotifyPropertyChangedFor(nameof(HasTextSelection))]
    private int _selectionEnd = -1;

    public string PageStatus => $"{CurrentPage:N0} / {PageCount:N0}";

    public string PageCountText => $"/ {PageCount:N0}";

    public string ZoomText => $"{Zoom:P0}";

    private bool HasViewport => _available.Width > 0 && _available.Height > 0;

    /// <summary>ページ番号（1 始まり）の表示用 ViewModel。表示範囲の近くにないページは null。</summary>
    public PdfPageViewModel? GetPage(int pageNumber)
        => _pages.GetValueOrDefault(pageNumber - 1);

    /// <summary>現在のページの文字情報。まだ読み込めていなければ null。</summary>
    internal PdfTextPage? CurrentTextPage => _textCache.GetValueOrDefault(CurrentPage - 1);

    internal PdfTextPage? GetTextPage(int pageIndex) => _textCache.GetValueOrDefault(pageIndex);

    // ===== 文字選択 =====

    public bool HasTextSelection => SelectionPageIndex >= 0
        && _textCache.TryGetValue(SelectionPageIndex, out var page)
        && page.Characters.Count > 0
        && SelectionStart >= 0 && SelectionEnd >= 0;

    public string SelectedText
    {
        get
        {
            if (!HasTextSelection || !_textCache.TryGetValue(SelectionPageIndex, out var page)) return string.Empty;
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

    internal void SelectText(int pageIndex, int start, int end)
    {
        if (!_textCache.TryGetValue(pageIndex, out var page) || page.Characters.Count == 0) return;
        SelectionPageIndex = pageIndex;
        SelectionStart = Math.Clamp(start, 0, page.Characters.Count - 1);
        SelectionEnd = Math.Clamp(end, 0, page.Characters.Count - 1);
    }

    public void ClearSelection()
    {
        SelectionPageIndex = -1;
        SelectionStart = -1;
        SelectionEnd = -1;
    }

    internal void SelectAllOnPage(int pageIndex)
    {
        if (_textCache.TryGetValue(pageIndex, out var page) && page.Characters.Count > 0)
            SelectText(pageIndex, 0, page.Characters.Count - 1);
        else
            ClearSelection();
    }

    [RelayCommand]
    private void SelectAllText()
    {
        // 最後に操作したページが見えていればそのページ、そうでなければ現在のページを対象にする。
        var target = LastPressedPageIndex >= _viewFirst && LastPressedPageIndex <= _viewLast
            ? LastPressedPageIndex
            : CurrentPage - 1;
        SelectAllOnPage(target);
    }

    // ===== ページ移動・ズーム =====

    private bool CanGoPrevious() => CurrentPage > 1;

    private bool CanGoNext() => CurrentPage < PageCount;

    private bool CanZoomOut() => Zoom > MinimumManualZoom;

    private bool CanZoomIn() => Zoom < MaximumManualZoom;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private Task PreviousPageAsync() => NavigateAsync(CurrentPage - 1);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private Task NextPageAsync() => NavigateAsync(CurrentPage + 1);

    [RelayCommand]
    private Task CommitPageInputAsync()
    {
        if (int.TryParse(PageInputText?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var page))
        {
            var task = NavigateAsync(page);
            // 範囲外の入力を丸めた結果が同じページのときも、入力欄を現在のページへ戻す。
            ResetPageInput();
            return task;
        }

        ResetPageInput();
        return Task.CompletedTask;
    }

    /// <summary>ページ番号の入力欄を現在のページへ戻す。</summary>
    public void ResetPageInput() => PageInputText = CurrentPage.ToString(CultureInfo.CurrentCulture);

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
        ApplyZoom(GetFitZoom(), reference: 0.5);
        return WhenIdleAsync();
    }

    private Task ChangeZoomAsync(double zoom)
    {
        ZoomMode = PdfZoomMode.Manual;
        var target = Math.Clamp(zoom, MinimumManualZoom, MaximumManualZoom);
        if (Math.Abs(target - Zoom) >= 0.001) ApplyZoom(target, reference: 0.5);
        return WhenIdleAsync();
    }

    /// <summary>指定ページの先頭へスクロールする。範囲外は端のページへ丸める。</summary>
    internal Task NavigateAsync(int page)
    {
        if (_disposed) return Task.CompletedTask;
        var target = Math.Clamp(page, 1, Math.Max(PageCount, 1));
        _pinnedPage = target - 1;
        _pinnedOffsetY = null;
        SetPosition(_offsetX, _layout.OffsetToPageTop(target - 1, _viewportHeight));
        CurrentPage = target;
        AfterPositionChanged();
        return WhenIdleAsync();
    }

    /// <summary>
    /// 保存した表示位置・倍率へ戻す。ページ（0 始まり）と、そのページ内の上端位置（0～1）で表す。
    /// 範囲外のページは 1 ページ目のままにする。
    /// </summary>
    internal Task RestoreViewAsync(PdfZoomMode mode, double? zoom, int pageIndex, double fraction)
    {
        if (_disposed) return Task.CompletedTask;
        ZoomMode = mode;
        if (zoom is { } saved) SetZoom(Math.Clamp(saved, MinimumManualZoom, MaximumManualZoom));
        if (mode != PdfZoomMode.Manual && HasViewport) SetZoom(GetFitZoom());
        if (pageIndex < 0 || pageIndex >= PageCount) pageIndex = 0;
        if (!double.IsFinite(fraction)) fraction = 0;
        _anchor = pageIndex == 0 && fraction <= 0 ? null : new PdfScrollAnchor(pageIndex, Math.Clamp(fraction, 0, 1), 0);
        _scrollPlaced = false;
        _pinnedPage = pageIndex;
        _pinnedOffsetY = null;
        PlaceFromAnchor(_anchor, 0);
        CurrentPage = pageIndex + 1;
        AfterPositionChanged();
        return WhenIdleAsync();
    }

    /// <summary>表示上端の位置を、セッションへ保存できる形（0 始まりのページ、ページ内の割合）で返す。</summary>
    internal (int PageIndex, double Fraction) CaptureTopPosition()
    {
        var anchor = _scrollPlaced && HasViewport
            ? _layout.AnchorAt(_offsetX, _offsetY, _viewportWidth, _viewportHeight, 0)
            : _anchor ?? default;
        var fraction = double.IsFinite(anchor.FractionY) ? Math.Clamp(anchor.FractionY, 0, 1) : 0;
        return (Math.Clamp(anchor.PageIndex, 0, Math.Max(PageCount - 1, 0)), fraction);
    }

    // ===== View との同期 =====

    /// <summary>
    /// 表示領域（ページを囲む余白を除いた大きさ）を受け取り、フィット倍率と表示範囲を更新する。
    /// <paramref name="renderScaling"/> は表示先の拡大率で、変わったら画像をその画素密度で描き直す。
    /// 省略すると直前の値を保つ。描画が落ち着くまで待つ。
    /// </summary>
    internal Task UpdateViewportAsync(Size available, double? renderScaling = null)
    {
        if (_disposed || !double.IsFinite(available.Width) || !double.IsFinite(available.Height)
            || available.Width <= 0 || available.Height <= 0)
        {
            return _disposed ? Task.CompletedTask : WhenIdleAsync();
        }

        var changed = Math.Abs(_available.Width - available.Width) > Epsilon
            || Math.Abs(_available.Height - available.Height) > Epsilon;
        var scaling = renderScaling is { } value && double.IsFinite(value) && value > 0 ? value : _renderScaling;
        var scalingChanged = Math.Abs(_renderScaling - scaling) > Epsilon;
        if (!changed && !scalingChanged && _scrollPlaced) return WhenIdleAsync();

        if (scalingChanged)
        {
            _renderScaling = scaling;
            BumpGeneration();
        }

        _available = available;
        _viewportWidth = available.Width + 2 * PdfPageLayout.Margin;
        _viewportHeight = available.Height + 2 * PdfPageLayout.Margin;
        // フィット表示では、ウィンドウの大きさが変わっても表示上端のページ位置を保つ。
        var fit = ZoomMode == PdfZoomMode.Manual ? Zoom : GetFitZoom();
        if (Math.Abs(fit - Zoom) > Epsilon)
        {
            ApplyZoom(fit, reference: 0);
        }
        else if (scalingChanged)
        {
            // ページ寸法の画素への丸めが変わるので、表示上端のページ位置を保って置き直す。
            var (anchor, used) = CaptureReference(0);
            RebuildLayout();
            PlaceFromAnchor(anchor, used);
        }
        else
        {
            RebuildLayout();
            if (!_scrollPlaced) PlaceFromAnchor(_anchor, 0);
        }

        AfterPositionChanged();
        return WhenIdleAsync();
    }

    /// <summary>View のスクロール位置を受け取る。適用待ちの要求があるあいだは、古い位置なので無視する。</summary>
    internal void ReportScroll(double offsetX, double offsetY)
    {
        if (_disposed || ScrollRequest is not null || !HasViewport) return;
        if (Math.Abs(_offsetX - offsetX) < 0.01 && Math.Abs(_offsetY - offsetY) < 0.01 && _scrollPlaced) return;
        _offsetX = offsetX;
        _offsetY = offsetY;
        _scrollPlaced = true;
        AfterPositionChanged();
    }

    /// <summary>View が要求を適用した結果の実際のスクロール位置を受け取る。</summary>
    internal void CompleteScrollRequest(int id, double offsetX, double offsetY)
    {
        if (_disposed || ScrollRequest?.Id != id) return;
        ScrollRequest = null;
        _offsetX = offsetX;
        _offsetY = offsetY;
        if (_pinnedPage is not null) _pinnedOffsetY = offsetY;
        AfterPositionChanged();
    }

    /// <summary>
    /// タブが選ばれたとき、ViewModel が持つ位置を共有の ScrollViewer へ当て直す。
    /// 位置の正本は ViewModel で、ScrollViewer の現在値には依存しない。
    /// </summary>
    internal void Activate()
    {
        if (_disposed || !HasViewport || !_scrollPlaced) return;
        IssueScroll(_offsetX, _offsetY);
    }

    private void SetPosition(double x, double y)
    {
        if (HasViewport)
        {
            IssueScroll(x, y);
            return;
        }

        _offsetX = x;
        _offsetY = y;
        _anchor = y <= 0 && x <= 0 ? null : _layout.AnchorAt(x, y, 0, 0, 0);
        _scrollPlaced = false;
    }

    private void IssueScroll(double x, double y)
    {
        _offsetX = Math.Clamp(x, 0, _layout.MaxOffsetX(_viewportWidth));
        _offsetY = Math.Clamp(y, 0, _layout.MaxOffsetY(_viewportHeight));
        _scrollPlaced = true;
        ScrollRequest = new PdfScrollRequest(++_requestId, _offsetX, _offsetY);
    }

    private void PlaceFromAnchor(PdfScrollAnchor? anchor, double reference)
    {
        if (!HasViewport)
        {
            _anchor = anchor;
            _scrollPlaced = false;
            return;
        }

        var offset = anchor is { } value
            ? _layout.OffsetFor(value, _viewportWidth, _viewportHeight, reference)
            : default;
        IssueScroll(offset.X, offset.Y);
    }

    private (PdfScrollAnchor? Anchor, double Reference) CaptureReference(double reference)
        => _scrollPlaced && HasViewport
            ? (_layout.AnchorAt(_offsetX, _offsetY, _viewportWidth, _viewportHeight, reference), reference)
            : (_anchor, 0);

    // ===== 倍率と配置 =====

    private double GetFitZoom() => ZoomMode switch
    {
        PdfZoomMode.FitWidth when _available.Width > 0 && _widestPage > 0 => _available.Width / _widestPage,
        PdfZoomMode.FitHeight when _available.Height > 0 && _tallestPage > 0 => _available.Height / _tallestPage,
        _ => Zoom,
    };

    /// <summary>倍率だけを設定し、位置は動かさない（表示前の復元用）。</summary>
    private void SetZoom(double zoom)
    {
        if (Math.Abs(zoom - Zoom) <= Epsilon) return;
        Zoom = zoom;
        BumpGeneration();
        RebuildLayout();
    }

    /// <summary>倍率を変えて配置を作り直し、<paramref name="reference"/> の点（0 = 上端、0.5 = 中心）を保つ。</summary>
    private void ApplyZoom(double zoom, double reference)
    {
        if (_disposed || Math.Abs(zoom - Zoom) <= Epsilon) return;
        var (anchor, used) = CaptureReference(reference);
        Zoom = zoom;
        BumpGeneration();
        RebuildLayout();
        PlaceFromAnchor(anchor, used);
        AfterPositionChanged();
    }

    private void RebuildLayout()
    {
        _layout = new PdfPageLayout(_pageSizes, Zoom, _available.Width, _renderScaling);
        ContentWidth = _layout.ContentWidth;
        ContentHeight = _layout.ContentHeight;
        foreach (var page in _pages.Values) ApplyGeometry(page);
    }

    private void ApplyGeometry(PdfPageViewModel page)
    {
        page.Left = _layout.Left(page.Index);
        page.Top = _layout.Top(page.Index);
        page.Width = _layout.Width(page.Index);
        page.Height = _layout.Height(page.Index);
    }

    /// <summary>倍率か拡大率が変わったので、描いた画像を古い世代にして描き直す。進行中の描画は捨てる。</summary>
    private void BumpGeneration()
    {
        _generationCancellation.Cancel();
        _generationCancellation.Dispose();
        _generationCancellation = new CancellationTokenSource();
        _generation++;
        _failedPages.Clear();
    }

    // ===== 現在のページと表示範囲 =====

    private void AfterPositionChanged()
    {
        UpdateCurrentPage();
        RefreshVisible();
        EnsureWorkers();
    }

    private void UpdateCurrentPage()
    {
        if (!HasViewport) return;
        int page;
        if (_pinnedPage is { } pinned && (_pinnedOffsetY is null || Math.Abs(_offsetY - _pinnedOffsetY.Value) <= 1))
        {
            page = pinned;
        }
        else
        {
            _pinnedPage = null;
            _pinnedOffsetY = null;
            page = CenterPage();
        }

        CurrentPage = Math.Clamp(page, 0, Math.Max(PageCount - 1, 0)) + 1;
    }

    private int CenterPage()
    {
        if (_layout.PageCount == 0) return 0;
        if (_offsetY <= 0.5) return 0;
        if (_offsetY >= _layout.MaxOffsetY(_viewportHeight) - 0.5) return _layout.PageCount - 1;
        return _layout.IndexAt(_offsetY + _viewportHeight / 2);
    }

    private int CenterIndex()
        => HasViewport ? CenterPage() : Math.Clamp(_anchor?.PageIndex ?? 0, 0, Math.Max(PageCount - 1, 0));

    /// <summary>表示範囲に合わせて、ページの ViewModel・画像・View へ載せる集合を更新する。</summary>
    private void RefreshVisible()
    {
        if (_disposed || PageCount == 0) return;
        int viewFirst, viewLast, extra;
        if (HasViewport)
        {
            (viewFirst, viewLast) = _layout.VisibleRange(_offsetY, _viewportHeight);
            extra = 1;
        }
        else
        {
            // 表示領域が未確定のあいだは、位置のページだけを描く。
            viewFirst = viewLast = CenterIndex();
            extra = 0;
        }

        _viewFirst = viewFirst;
        _viewLast = viewLast;
        var center = CenterIndex();
        var first = Math.Max(0, viewFirst - extra);
        var last = Math.Min(PageCount - 1, viewLast + extra);
        if (last - first + 1 > MaximumVisiblePages)
        {
            first = Math.Clamp(center - MaximumVisiblePages / 2, first, last - MaximumVisiblePages + 1);
            last = first + MaximumVisiblePages - 1;
        }

        var retainFirst = Math.Max(0, first - (RetainedPageMargin - extra));
        var retainLast = Math.Min(PageCount - 1, last + (RetainedPageMargin - extra));
        for (var index = retainFirst; index <= retainLast; index++)
        {
            if (_pages.ContainsKey(index)) continue;
            var page = new PdfPageViewModel(this, index) { TextPage = _textCache.GetValueOrDefault(index) };
            ApplyGeometry(page);
            _pages[index] = page;
        }

        foreach (var index in _pages.Keys.Where(index => index < retainFirst || index > retainLast).ToArray())
        {
            var page = _pages[index];
            // 画面から外してから画像を破棄する。
            if (_visibleIndexes.Remove(index)) VisiblePages.Remove(page);
            _pages.Remove(index);
            page.ReleaseImage();
        }

        foreach (var index in _visibleIndexes.Where(index => index < first || index > last).ToArray())
        {
            _visibleIndexes.Remove(index);
            VisiblePages.Remove(_pages[index]);
        }

        for (var index = first; index <= last; index++)
        {
            if (_visibleIndexes.Add(index)) VisiblePages.Add(_pages[index]);
        }

        EnforcePixelBudget(first, last, center);
    }

    /// <summary>保持する画像の画素数が上限を超えたら、表示から遠いページの画像から破棄する。</summary>
    private void EnforcePixelBudget(int visibleFirst, int visibleLast, int center)
    {
        long total = 0;
        foreach (var page in _pages.Values)
        {
            if (page.Image is { } image) total += (long)image.PixelSize.Width * image.PixelSize.Height;
        }

        if (total <= RetainedPixelLimit) return;
        foreach (var page in _pages.Values
                     .Where(page => page.Image is not null && (page.Index < visibleFirst || page.Index > visibleLast))
                     .OrderByDescending(page => Math.Abs(page.Index - center))
                     .ToArray())
        {
            var image = page.Image!;
            total -= (long)image.PixelSize.Width * image.PixelSize.Height;
            page.ReleaseImage();
            if (total <= RetainedPixelLimit) return;
        }
    }

    // ===== 画像の描画 =====

    private void EnsureWorkers()
    {
        EnsureRenderWorker();
        EnsureTextWorker();
    }

    private void EnsureRenderWorker()
    {
        if (_disposed) return;
        if (_renderWorker is { IsCompleted: false } && _renderWorkerGeneration == _generation) return;
        if (NextPageToRender(_generation) is null) return;
        _renderWorkerGeneration = _generation;
        IsLoading = true;
        _renderWorker = RunRenderWorkerAsync(_generation, _generationCancellation.Token);
    }

    private PdfPageViewModel? NextPageToRender(int generation)
    {
        var center = CenterIndex();
        PdfPageViewModel? best = null;
        foreach (var page in VisiblePages)
        {
            if (page.RenderedGeneration == generation || _failedPages.Contains(page.Index)) continue;
            if (best is null
                || Math.Abs(page.Index - center) < Math.Abs(best.Index - center)) best = page;
        }

        return best;
    }

    private async Task RunRenderWorkerAsync(int generation, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && !_disposed && generation == _generation)
            {
                var page = NextPageToRender(generation);
                if (page is null) break;
                await RenderPageAsync(page, generation, token);
            }
        }
        finally
        {
            if (generation == _generation && !_disposed) IsLoading = false;
        }
    }

    private bool IsCurrent(PdfPageViewModel page, int generation, CancellationToken token)
        => !_disposed && generation == _generation && !token.IsCancellationRequested
            && _pages.TryGetValue(page.Index, out var current) && ReferenceEquals(current, page);

    private async Task RenderPageAsync(PdfPageViewModel page, int generation, CancellationToken token)
    {
        page.IsLoading = true;
        page.ErrorMessage = null;
        Avalonia.Media.Imaging.Bitmap? bitmap = null;
        try
        {
            // 表示は DIP、画像はデバイス画素。拡大率を掛けないと表示側で引き伸ばされてぼやける。
            bitmap = await _renderer.RenderAsync(page.Index, Zoom * _renderScaling, token);
            if (!IsCurrent(page, generation, token)) return;
            var previous = page.Image;
            page.Image = bitmap;
            bitmap = null;
            page.RenderedGeneration = generation;
            previous?.Dispose();
            EnforcePixelBudget(_visibleIndexes.Count == 0 ? 0 : _visibleIndexes.Min(),
                _visibleIndexes.Count == 0 ? -1 : _visibleIndexes.Max(), CenterIndex());
        }
        catch (OperationCanceledException)
        {
            // 倍率変更や閉鎖で、古い描画は捨てる。
        }
        catch (Exception ex)
        {
            AppLogger.For<PdfDocumentViewModel>().Error($"PDF ページを描画できませんでした: {FilePath} ({page.Number})", ex);
            if (IsCurrent(page, generation, token))
            {
                // 失敗したページに古い画像を残すと、エラー文と重なって別の内容に見える。
                page.ReleaseImage();
                page.ErrorMessage = ex.Message;
                _failedPages.Add(page.Index);
            }
        }
        finally
        {
            bitmap?.Dispose();
            if (generation == _generation && !_disposed) page.IsLoading = false;
        }
    }

    // ===== 文字情報の抽出 =====

    private void EnsureTextWorker()
    {
        if (_disposed || _textService is null || _textDenied) return;
        if (_textWorker is { IsCompleted: false }) return;
        if (NextPageToRead() < 0)
        {
            UpdateTextStatus();
            return;
        }

        _textWorker = RunTextWorkerAsync(_lifetime.Token);
    }

    private int NextPageToRead()
    {
        var center = CenterIndex();
        var best = -1;
        foreach (var page in VisiblePages)
        {
            if (_textCache.ContainsKey(page.Index) || _failedTextPages.Contains(page.Index)) continue;
            if (best < 0 || Math.Abs(page.Index - center) < Math.Abs(best - center)) best = page.Index;
        }

        return best;
    }

    /// <summary>画像とは別の、優先度の低い直列ループ。見えているページを中心に近い順に読む。</summary>
    private async Task RunTextWorkerAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && !_disposed)
            {
                var index = NextPageToRead();
                if (index < 0) break;
                UpdateTextStatus();
                try
                {
                    var page = await _textService!.ReadPageAsync(index, token);
                    if (token.IsCancellationRequested || _disposed) return;
                    StoreText(index, page);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (UnauthorizedAccessException ex)
                {
                    // コピー禁止は文書全体の設定なので、1 回だけ伝えて以後は読まない。
                    if (token.IsCancellationRequested || _disposed) return;
                    _textDenied = true;
                    _textDeniedMessage = ex.Message;
                    return;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested || _disposed) return;
                    _failedTextPages.Add(index);
                    AppLogger.For<PdfDocumentViewModel>().Error($"PDF の文字情報を読み込めませんでした: {FilePath} ({index + 1})", ex);
                }
            }
        }
        finally
        {
            if (!_disposed) UpdateTextStatus();
        }
    }

    private void StoreText(int index, PdfTextPage page)
    {
        _textCache[index] = page;
        _textUsage.Remove(index);
        _textUsage.Add(index);
        if (_pages.TryGetValue(index, out var view)) view.TextPage = page;

        // 古いものから捨てる。選択中のページと表示中のページは残す。
        for (var position = 0; _textCache.Count > MaximumTextPages && position < _textUsage.Count;)
        {
            var candidate = _textUsage[position];
            if (candidate == SelectionPageIndex || _visibleIndexes.Contains(candidate))
            {
                position++;
                continue;
            }

            _textUsage.RemoveAt(position);
            _textCache.Remove(candidate);
            if (_pages.TryGetValue(candidate, out var evicted)) evicted.TextPage = null;
        }

        OnPropertyChanged(nameof(CurrentTextPage));
        UpdateTextStatus();
    }

    private void UpdateTextStatus()
    {
        if (_textService is null)
        {
            TextSelectionStatus = null;
            return;
        }

        var index = CurrentPage - 1;
        TextSelectionStatus = _textDenied
            ? _textDeniedMessage
            : _textCache.TryGetValue(index, out var page)
                ? page.Characters.Count == 0 ? "このページに選択できる文字はありません" : null
                : _failedTextPages.Contains(index)
                    ? "文字情報を読み込めないため、このページでは選択できません"
                    : "文字情報を読み込んでいます…";
    }

    // ===== 待機と終了 =====

    /// <summary>画像と文字情報の読み込みが落ち着くまで待つ。</summary>
    internal Task WhenIdleAsync()
        => _renderWorker is { IsCompleted: false } || _textWorker is { IsCompleted: false }
            ? WaitForWorkersAsync()
            : Task.CompletedTask;

    private async Task WaitForWorkersAsync()
    {
        while (!_disposed)
        {
            var render = _renderWorker is { IsCompleted: false } ? _renderWorker : null;
            var text = _textWorker is { IsCompleted: false } ? _textWorker : null;
            if (render is null && text is null) return;
            await Task.WhenAll(render ?? Task.CompletedTask, text ?? Task.CompletedTask);
        }
    }

    /// <summary>配置を計算し直し、位置のページを描く。開いた直後に呼ぶ。</summary>
    internal Task RefreshAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RebuildLayout();
        AfterPositionChanged();
        return WhenIdleAsync();
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
        _generationCancellation.Cancel();
        _lifetime.Cancel();
        ScrollRequest = null;
        // 画面から外してから画像を破棄する。後から届く結果は IsCurrent が捨てる。
        VisiblePages.Clear();
        _visibleIndexes.Clear();
        foreach (var page in _pages.Values) page.ReleaseImage();
        _pages.Clear();
        _textCache.Clear();
        _textUsage.Clear();
        ClearSelection();
        _generationCancellation.Dispose();
        _lifetime.Dispose();
        _textService?.Dispose();
        _renderer.Dispose();
    }
}
