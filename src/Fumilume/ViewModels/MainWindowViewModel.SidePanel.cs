using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fumilume.Services;

namespace Fumilume.ViewModels;

/// <summary>左サイドに出す面。1 枚に 1 つの用途だけを持たせ、切り替えで出し分ける。</summary>
public enum SidePanelKind
{
    /// <summary>開いているタブの一覧。</summary>
    Tabs,

    /// <summary>本文から拾った見出し。</summary>
    Outline,

    /// <summary>印を付けた行。</summary>
    Bookmarks,
}

/// <summary>ブックマーク一覧の 1 行。</summary>
/// <param name="LineNumber">印の付いている行番号（1 始まり）。</param>
/// <param name="Preview">その行の中身。空行のときは空文字。</param>
public sealed record BookmarkEntry(int LineNumber, string Preview)
{
    public string Location => $"{LineNumber:N0}";

    public string Display => Preview.Length == 0 ? "（空行）" : Preview;
}

/// <summary>
/// 左サイドのパネル。
///
/// 機能を 1 か所へ積むと優先度が消えるため、タブ一覧・アウトライン・ブックマークを
/// 同じ場所で切り替える。フォルダ横断検索の結果だけは横幅が要るので、ここではなく
/// 中央のタブに出す（縦 190px では 1 行 1 一致の一覧が読めない）。
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>印の増減を受け取っている文書のブックマーク。タブを切り替えるたびに張り替える。</summary>
    private DocumentBookmarks? _watchedBookmarks;

    /// <summary>引き直す必要があるか。閉じている間は本文が変わっても解析しない。</summary>
    private bool _outlineStale = true;

    private bool _bookmarksStale = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTabsPanel))]
    [NotifyPropertyChangedFor(nameof(IsOutlinePanel))]
    [NotifyPropertyChangedFor(nameof(IsBookmarksPanel))]
    private SidePanelKind _sidePanel;

    public bool IsTabsPanel => SidePanel is SidePanelKind.Tabs;

    public bool IsOutlinePanel => SidePanel is SidePanelKind.Outline;

    public bool IsBookmarksPanel => SidePanel is SidePanelKind.Bookmarks;

    /// <summary>本文から拾った見出し。</summary>
    public ObservableCollection<OutlineItem> Outline { get; } = [];

    /// <summary>印の付いている行。</summary>
    public ObservableCollection<BookmarkEntry> Bookmarks { get; } = [];

    /// <summary>一覧で選ばれている見出し。選び直すたびにその行へ飛ぶ。</summary>
    [ObservableProperty]
    private OutlineItem? _selectedOutlineItem;

    /// <summary>一覧で選ばれている印。</summary>
    [ObservableProperty]
    private BookmarkEntry? _selectedBookmark;

    public bool HasOutline => Outline.Count > 0;

    public bool HasBookmarks => Bookmarks.Count > 0;

    /// <summary>見出しが 1 つも無いときに、その理由を出す。</summary>
    public string OutlineEmptyMessage => SelectedDocument is not { } document
        ? "文書のタブを選ぶと見出しが出ます"
        : OutlineService.IsSupported(document.FilePath)
            ? "見出しが見つかりません"
            : "この形式のアウトラインには対応していません";

    public string BookmarkEmptyMessage => IsDocumentSelected
        ? "Ctrl+F2 で今の行に印を付けます"
        : "文書のタブを選ぶと印の一覧が出ます";

    [RelayCommand]
    private void SelectSidePanel(SidePanelKind kind) => SidePanel = kind;

    /// <summary>
    /// 一覧で選んだ行へ飛ぶ。フォーカスは一覧に残す（矢印キーで見出しを辿ると本文が追う）。
    /// カーソルを動かすだけで編集領域の追随はコードビハインドの既存の同期に任せる。
    /// </summary>
    partial void OnSelectedOutlineItemChanged(OutlineItem? value) => GoToLine(value?.LineNumber);

    partial void OnSelectedBookmarkChanged(BookmarkEntry? value) => GoToLine(value?.LineNumber);

    private void GoToLine(int? lineNumber)
    {
        if (lineNumber is { } line && SelectedDocument is { } document)
        {
            document.GoToLine(line);
            StatusMessage = $"行 {line:N0} へ移動しました";
        }
    }

    partial void OnSidePanelChanged(SidePanelKind value)
    {
        Options.RememberSidePanel(value);
        RefreshOutline();
        RefreshBookmarks();
    }

    /// <summary>タブが変わったときの張り替え。印の購読先も選択中の文書へ移す。</summary>
    private void OnSelectedTabChangedForSidePanel()
    {
        var bookmarks = SelectedDocument is { } document ? document.Bookmarks : null;
        if (!ReferenceEquals(_watchedBookmarks, bookmarks))
        {
            if (_watchedBookmarks is not null)
            {
                _watchedBookmarks.Changed -= OnBookmarksChanged;
            }

            _watchedBookmarks = bookmarks;
            if (_watchedBookmarks is not null)
            {
                _watchedBookmarks.Changed += OnBookmarksChanged;
            }
        }

        OnPropertyChanged(nameof(OutlineEmptyMessage));
        OnPropertyChanged(nameof(BookmarkEmptyMessage));
        MarkSidePanelStale();
    }

    private void OnBookmarksChanged(object? sender, EventArgs args)
    {
        _bookmarksStale = true;
        RefreshBookmarks();
    }

    /// <summary>本文が変わったときの追随。開いている面だけを引き直す。</summary>
    private void OnSelectedTextChangedForSidePanel() => MarkSidePanelStale();

    private void MarkSidePanelStale()
    {
        _outlineStale = true;
        _bookmarksStale = true;
        RefreshOutline();
        RefreshBookmarks();
    }

    private void RefreshOutline()
    {
        if (!IsOutlinePanel || !_outlineStale)
        {
            return;
        }

        _outlineStale = false;
        var parsed = SelectedDocument is { } document
            ? OutlineService.Parse(document.FilePath, document.Text)
            : [];

        // 打鍵のたびに作り直すと選択と表示位置が飛ぶので、中身が変わったときだけ入れ替える。
        if (!Outline.SequenceEqual(parsed))
        {
            Outline.Clear();
            foreach (var item in parsed)
            {
                Outline.Add(item);
            }
        }

        OnPropertyChanged(nameof(HasOutline));
        OnPropertyChanged(nameof(OutlineEmptyMessage));
    }

    private void RefreshBookmarks()
    {
        if (!IsBookmarksPanel || !_bookmarksStale)
        {
            return;
        }

        _bookmarksStale = false;
        var entries = SelectedDocument is { } document && _watchedBookmarks is { } bookmarks
            ? bookmarks.Lines.Select(lineNumber => ReadBookmark(document, lineNumber)).ToList()
            : [];

        if (!Bookmarks.SequenceEqual(entries))
        {
            Bookmarks.Clear();
            foreach (var entry in entries)
            {
                Bookmarks.Add(entry);
            }
        }

        OnPropertyChanged(nameof(HasBookmarks));
        OnPropertyChanged(nameof(BookmarkEmptyMessage));
    }

    private static BookmarkEntry ReadBookmark(DocumentViewModel document, int lineNumber)
    {
        var line = document.EditorDocument.GetLineByNumber(lineNumber);
        return new BookmarkEntry(lineNumber, document.EditorDocument.GetText(line.Offset, line.Length).Trim());
    }
}
