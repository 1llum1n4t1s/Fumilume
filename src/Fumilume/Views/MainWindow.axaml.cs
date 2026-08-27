using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Search;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly AppOptionsViewModel _options;
    private readonly TextEditor _editor;
    private readonly SearchPanel _searchPanel;
    private DocumentViewModel? _boundDocument;
    private bool _closeConfirmed;
    private bool _closeCheckInProgress;
    private bool _syncingCaret;

    /// <summary>XAML ローダー用（プレビューア）。設定は自分で読み込む。</summary>
    public MainWindow()
        : this(null)
    {
    }

    /// <param name="settings">
    /// <see cref="App"/> が起動時に読み込んだ設定。null のときは自分で読む。
    /// </param>
    public MainWindow(AppSettings? settings)
    {
        InitializeComponent();

        settings ??= SettingsService.Load();
        var dialogs = new EditorDialogService(this);
        _viewModel = new MainWindowViewModel(new DocumentFileService(), dialogs, settings);
        _options = _viewModel.Options;
        DataContext = _viewModel;

        _editor = this.FindControl<TextEditor>("Editor")
            ?? throw new InvalidOperationException("エディタを初期化できませんでした。");
        _searchPanel = SearchPanel.Install(_editor);
        _editor.TextArea.TextView.BackgroundRenderers.Add(new BookmarkRenderer(() => _boundDocument));
        ApplyEditorOptions();
        ApplyTabHeight();

        BuildCommandMenu();
        RoundedClip.Attach(this.FindControl<Border>("ContentIsland"));
        ApplyWindowDecorations(WindowState);
        ApplyBackdrop();
        RestoreWindowBounds(settings);

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _options.PropertyChanged += OnOptionsPropertyChanged;
        _editor.TextArea.Caret.PositionChanged += OnEditorCaretPositionChanged;
        _editor.TextArea.SelectionChanged += OnEditorSelectionChanged;
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        BindSelectedDocument();

        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs args)
    {
        UpdateMaximizeRestoreGlyph();
        _ = _viewModel.InitializeAsync(Program.StartupArgs);
        if (_options.CheckUpdatesOnStartup)
        {
            _ = UpdateService.CheckAsync(this, manually: false);
        }

        _editor.Focus();
    }

    // ===== エディタのオプション =====

    private void ApplyEditorOptions()
    {
        // 表示
        _editor.ShowLineNumbers = _options.ShowLineNumbers;
        _editor.WordWrap = _options.WordWrap;
        _editor.Options.InheritWordWrapIndentation = _options.InheritWordWrapIndentation;
        _editor.Options.HighlightCurrentLine = _options.HighlightCurrentLine;
        _editor.Options.ShowColumnRulers = _options.ShowColumnRuler;
        _editor.Options.ColumnRulerPositions = [_options.ColumnRulerPosition];
        _editor.Options.LineHeightFactor = _options.LineHeightFactor;

        // 記号の表示（sakura と同じく種類ごとに切れる）
        _editor.Options.ShowSpaces = _options.ShowSpaces;
        _editor.Options.ShowTabs = _options.ShowTabs;
        _editor.Options.ShowEndOfLine = _options.ShowEndOfLine;
        _editor.Options.ShowBoxForControlCharacters = _options.ShowControlCharacters;

        // 編集
        _editor.Options.IndentationSize = _options.IndentationSize;
        _editor.Options.ConvertTabsToSpaces = _options.ConvertTabsToSpaces;
        _editor.Options.AcceptsTab = _options.AcceptsTab;
        _editor.Options.EnableRectangularSelection = _options.EnableRectangularSelection;
        _editor.Options.EnableVirtualSpace = _options.EnableVirtualSpace;
        _editor.Options.EnableTextDragDrop = _options.EnableTextDragDrop;
        _editor.Options.CutCopyWholeLine = _options.CutCopyWholeLine;
        _editor.Options.AllowScrollBelowDocument = _options.AllowScrollBelowDocument;
        _editor.Options.AllowToggleOverstrikeMode = _options.AllowToggleOverstrikeMode;
        _editor.Options.HideCursorWhileTyping = _options.HideCursorWhileTyping;
        _editor.Options.EnableHyperlinks = _options.EnableHyperlinks;
        _editor.Options.EnableEmailHyperlinks = _options.EnableHyperlinks;

        // 指定フォントが入っていない環境でも等幅で読めるようにフォールバックを連ねる。
        _editor.FontFamily = new FontFamily($"{_options.FontFamily}, Cascadia Mono, Consolas, monospace");
        _editor.FontSize = _options.FontSize;
    }

    /// <summary>タブ 1 行の厚みを反映する。項目テンプレートの Grid と ListBoxItem の
    /// ControlTheme が同じ <c>TabItemHeight</c> を DynamicResource で見ているので、
    /// ここを 1 箇所書き換えれば両方が追従する（DataTemplate の DataContext はタブ側の
    /// ビューモデルなので、テンプレートから設定を辿らせずに済ませたい）。</summary>
    private void ApplyTabHeight() => Resources["TabItemHeight"] = _options.TabHeight;

    private void OnOptionsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppOptionsViewModel.UseAcrylic))
        {
            ApplyBackdrop();
            return;
        }

        if (args.PropertyName == nameof(AppOptionsViewModel.TabHeight))
        {
            ApplyTabHeight();
            return;
        }

        ApplyEditorOptions();
    }

    // ===== ウィンドウの地（アクリル / 単色） =====

    /// <summary>設定のアクリル可否をウィンドウへ反映する。実際に効いたかどうかは
    /// <see cref="TopLevel.ActualTransparencyLevel"/> で確認し、効かない環境では単色へ落とす。</summary>
    private void ApplyBackdrop()
    {
        TransparencyLevelHint = _options.UseAcrylic
            ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None]
            : [WindowTransparencyLevel.None];
        UpdateBackdropLayers();
    }

    private void UpdateBackdropLayers()
    {
        var acrylicActive = _options.UseAcrylic
            && ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur;
        var acrylic = this.FindControl<ExperimentalAcrylicBorder>("AcrylicLayer");
        var scrim = this.FindControl<Border>("AcrylicScrim");
        var fallback = this.FindControl<Border>("FallbackLayer");
        if (acrylic is not null) acrylic.IsVisible = acrylicActive;
        if (scrim is not null) scrim.IsVisible = acrylicActive;
        if (fallback is not null) fallback.IsVisible = !acrylicActive;
    }

    // ===== ウィンドウ位置とサイズ =====

    private void RestoreWindowBounds(AppSettings settings)
    {
        if (!settings.RememberWindowBounds)
        {
            return;
        }

        if (settings.WindowWidth > 0 && settings.WindowHeight > 0)
        {
            Width = Math.Max(settings.WindowWidth, MinWidth);
            Height = Math.Max(settings.WindowHeight, MinHeight);
        }

        if (settings.WindowX != int.MinValue && settings.WindowY != int.MinValue)
        {
            // 位置は画面構成が変わっている可能性があるため、Screens が読める Opened 後に検証して適用する。
            WindowStartupLocation = WindowStartupLocation.Manual;
            Opened += RestorePositionOnce;
        }

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        void RestorePositionOnce(object? sender, EventArgs args)
        {
            Opened -= RestorePositionOnce;
            var target = new PixelPoint(settings.WindowX, settings.WindowY);
            // 前回のモニターが外れている場合にウィンドウが画面外へ出るのを防ぐ。
            if (Screens.All.Any(screen => screen.Bounds.Contains(target)))
            {
                Position = target;
            }
        }
    }

    private void SaveWindowBounds()
    {
        var settings = _options.Settings;
        if (!settings.RememberWindowBounds)
        {
            return;
        }

        settings.WindowMaximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
        if (WindowState == WindowState.Normal)
        {
            // 最大化・最小化中のサイズを保存すると次回に復元できないので、通常状態のときだけ更新する。
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
            settings.WindowX = Position.X;
            settings.WindowY = Position.Y;
        }
    }

    // ===== 文書の切り替え =====

    private void BindSelectedDocument()
    {
        if (_boundDocument is not null)
        {
            _boundDocument.PropertyChanged -= OnBoundDocumentPropertyChanged;
            _boundDocument.Bookmarks.Changed -= OnBookmarksChanged;
        }

        _boundDocument = _viewModel.SelectedDocument;
        if (_boundDocument is null)
        {
            // 設定タブを選んでいる状態。エディタは隠れているので空の文書を差しておく。
            _editor.Document = new TextDocument();
            return;
        }

        _boundDocument.PropertyChanged += OnBoundDocumentPropertyChanged;
        _boundDocument.Bookmarks.Changed += OnBookmarksChanged;
        _editor.Document = _boundDocument.EditorDocument;
        RestoreCaretFromDocument(scrollIntoView: false);
        InvalidateBookmarks();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.IsCommandPaletteOpen))
        {
            if (_viewModel.IsCommandPaletteOpen)
            {
                OnCommandPaletteOpened();
            }

            return;
        }

        if (args.PropertyName != nameof(MainWindowViewModel.SelectedTab))
        {
            return;
        }

        BindSelectedDocument();
        if (_viewModel.IsDocumentSelected)
        {
            _editor.Focus();
        }
    }

    private void OnEditorCaretPositionChanged(object? sender, EventArgs args)
    {
        if (_syncingCaret || _boundDocument is null)
        {
            return;
        }

        _syncingCaret = true;
        try
        {
            _boundDocument.CaretIndex = _editor.CaretOffset;
            PushSelectionToDocument();
        }
        finally
        {
            _syncingCaret = false;
        }
    }

    private void OnBookmarksChanged(object? sender, EventArgs args) => InvalidateBookmarks();

    /// <summary>印の増減を背景レイヤーへ反映する（印は文字ではないので文書の変更通知では引き直されない）。</summary>
    private void InvalidateBookmarks()
        => _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

    /// <summary>選択範囲の変化を文書側へ渡す。変換系・行編集系のコマンドはこれを対象にする。</summary>
    private void OnEditorSelectionChanged(object? sender, EventArgs args)
    {
        if (_syncingCaret || _boundDocument is null)
        {
            return;
        }

        _syncingCaret = true;
        try
        {
            PushSelectionToDocument();
        }
        finally
        {
            _syncingCaret = false;
        }
    }

    private void PushSelectionToDocument()
    {
        if (_boundDocument is null)
        {
            return;
        }

        _boundDocument.SelectionStart = _editor.SelectionStart;
        _boundDocument.SelectionLength = _editor.SelectionLength;
    }

    private void OnBoundDocumentPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_syncingCaret)
        {
            return;
        }

        if (args.PropertyName == nameof(DocumentViewModel.CaretIndex))
        {
            RestoreCaretFromDocument(scrollIntoView: true);
        }
        else if (args.PropertyName is nameof(DocumentViewModel.SelectionStart)
                 or nameof(DocumentViewModel.SelectionLength))
        {
            RestoreSelectionFromDocument();
        }
    }

    /// <summary>コマンドが動かした選択範囲をエディタへ映す（続けて別の変換を掛けられるように）。</summary>
    private void RestoreSelectionFromDocument()
    {
        if (_boundDocument is null)
        {
            return;
        }

        _syncingCaret = true;
        try
        {
            var length = _boundDocument.EditorDocument.TextLength;
            var start = Math.Clamp(_boundDocument.SelectionStart, 0, length);
            _editor.Select(start, Math.Clamp(_boundDocument.SelectionLength, 0, length - start));
        }
        finally
        {
            _syncingCaret = false;
        }
    }

    private void RestoreCaretFromDocument(bool scrollIntoView)
    {
        if (_boundDocument is null)
        {
            return;
        }

        _syncingCaret = true;
        try
        {
            _editor.CaretOffset = Math.Clamp(
                _boundDocument.CaretIndex,
                0,
                _boundDocument.EditorDocument.TextLength);
            if (scrollIntoView)
            {
                _editor.ScrollToLine(_boundDocument.CurrentLine);
            }
        }
        finally
        {
            _syncingCaret = false;
        }
    }

    // ===== コマンドメニューとコマンドパレット =====

    /// <summary>
    /// ツールバーの「コマンド」メニューを <see cref="EditorCommandCatalog"/> から組む。
    ///
    /// <c>MenuFlyout.ItemsSource</c> でデータバインドすると、階層メニューの Header と
    /// ItemsSource をリフレクション束縛（<c>ReflectionBinding</c>）で引くことになり、
    /// <c>PublishAot</c> のトリミングで落ちる。項目数は起動時に確定するので、ここで組み立てる。
    /// </summary>
    private void BuildCommandMenu()
    {
        foreach (var icon in EditorCommandCatalog.CategoryIcons)
        {
            var group = _viewModel.EditorMenu.FirstOrDefault(node => node.Title == icon.Category);
            var button = this.FindControl<Button>(icon.ButtonName);
            if (group is null || button is null)
            {
                continue;
            }

            var flyout = new MenuFlyout();
            foreach (var leaf in group.Children)
            {
                flyout.Items.Add(new MenuItem
                {
                    Header = leaf.Title,
                    Command = leaf.Command,
                    CommandParameter = leaf.CommandParameter,
                    InputGesture = ParseGesture(leaf.Gesture),
                });
            }

            button.Flyout = flyout;
        }
    }

    /// <summary>メニュー右端へ出すキー表示。読めない書式は表示しないだけで、機能は動く。</summary>
    private static KeyGesture? ParseGesture(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return null;
        }

        try
        {
            return KeyGesture.Parse(gesture);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return null;
        }
    }

    private void OnCommandPaletteOpened()
    {
        var input = this.FindControl<TextBox>("CommandPaletteInput");
        // 開いた直後はレイアウトが済んでいないので、次のフレームで入力欄へ移す。
        Dispatcher.UIThread.Post(() =>
        {
            input?.Focus();
            input?.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void CommandPaletteScrim_PointerPressed(object? sender, PointerPressedEventArgs args)
    {
        // 外側の暗幕を押したときだけ閉じる（パレット本体のクリックは素通しする）。
        if (ReferenceEquals(args.Source, sender))
        {
            _viewModel.CloseCommandPaletteCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void CommandPaletteList_DoubleTapped(object? sender, TappedEventArgs args)
    {
        _viewModel.RunSelectedPaletteCommandCommand.Execute(null);
        args.Handled = true;
    }

    private void CommandPalette_KeyDown(object? sender, KeyEventArgs args)
    {
        switch (args.Key)
        {
            case Key.Escape:
                _viewModel.CloseCommandPaletteCommand.Execute(null);
                _editor.Focus();
                args.Handled = true;
                break;
            case Key.Enter:
                _viewModel.RunSelectedPaletteCommandCommand.Execute(null);
                _editor.Focus();
                args.Handled = true;
                break;
            case Key.Down:
                MovePaletteSelection(1);
                args.Handled = true;
                break;
            case Key.Up:
                MovePaletteSelection(-1);
                args.Handled = true;
                break;
        }
    }

    /// <summary>入力欄に居たままでも候補を選び替えられるようにする。</summary>
    private void MovePaletteSelection(int delta)
    {
        var results = _viewModel.CommandPaletteResults;
        if (results.Count == 0)
        {
            return;
        }

        var current = _viewModel.SelectedPaletteCommand is { } selected
            ? results.IndexOf(selected)
            : -1;
        var next = Math.Clamp(current + delta, 0, results.Count - 1);
        _viewModel.SelectedPaletteCommand = results[next];
        this.FindControl<ListBox>("CommandPaletteList")?.ScrollIntoView(next);
    }

    // ===== 検索 =====

    private void OnGlobalKeyDown(object? sender, KeyEventArgs args)
    {
        if (!_viewModel.IsDocumentSelected)
        {
            return;
        }

        if (args.KeyModifiers.HasFlag(KeyModifiers.Control) && args.Key == Key.F)
        {
            OpenSearch(replace: false);
            args.Handled = true;
        }
        else if (args.KeyModifiers.HasFlag(KeyModifiers.Control) && args.Key == Key.H)
        {
            OpenSearch(replace: true);
            args.Handled = true;
        }
        else if (args.Key == Key.F3)
        {
            if (!_searchPanel.IsOpened)
            {
                OpenSearch(replace: false);
            }
            else if (args.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                _searchPanel.FindPrevious();
            }
            else
            {
                _searchPanel.FindNext(_editor.CaretOffset);
            }

            args.Handled = true;
        }
    }

    private void OpenSearch(bool replace)
    {
        // 検索条件の初期値は設定に従う（sakura の共通設定『検索』相当）。
        _searchPanel.MatchCase = _options.SearchMatchCase;
        _searchPanel.UseRegex = _options.SearchUseRegex;

        if (_options.SearchUseCaretWord && !string.IsNullOrEmpty(_editor.SelectedText))
        {
            _searchPanel.SearchPattern = _editor.SelectedText;
        }

        _searchPanel.IsReplaceMode = replace;
        _searchPanel.Open();
        Dispatcher.UIThread.Post(_searchPanel.Reactivate, DispatcherPriority.Loaded);
    }

    private void Find_Click(object? sender, RoutedEventArgs args)
        => OpenSearch(replace: false);

    private void Replace_Click(object? sender, RoutedEventArgs args)
        => OpenSearch(replace: true);

    // ===== ウィンドウ状態 =====

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty && change.NewValue is WindowState state)
        {
            ApplyWindowDecorations(state);
            UpdateMaximizeRestoreGlyph();
        }
        else if (change.Property == ActualTransparencyLevelProperty)
        {
            UpdateBackdropLayers();
        }
    }

    /// <summary>
    /// 最大化・全画面のときはウィンドウ枠を消す。
    ///
    /// 通常時の BorderOnly は、キャプションを自前で描いているぶん OS 側の枠（リサイズ用の掴みしろ）を
    /// 残すためのもの。最大化・全画面ではリサイズできないので枠に用は無く、画面の端に線が 1 本残るだけになる。
    /// </summary>
    private void ApplyWindowDecorations(WindowState state)
        => WindowDecorations = state is WindowState.Maximized or WindowState.FullScreen
            ? WindowDecorations.None
            : WindowDecorations.BorderOnly;

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_closeConfirmed)
        {
            return;
        }

        args.Cancel = true;
        if (_closeCheckInProgress)
        {
            return;
        }

        _closeCheckInProgress = true;
        try
        {
            // 未保存の確認より先に「終了しますか？」を出す（sakura の m_bExitConfirm）。
            // 未保存があるときは続く CanCloseAsync が保存の要否を尋ねるので、確認は二段になる。
            if (_options.ConfirmOnExit &&
                !await _viewModel.ConfirmExitAsync())
            {
                return;
            }

            if (await _viewModel.CanCloseAsync())
            {
                SaveWindowBounds();
                _viewModel.PersistSessionState();
                _closeConfirmed = true;
                Close();
            }
        }
        finally
        {
            _closeCheckInProgress = false;
        }
    }

    // ===== タイトルバー =====

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs args)
    {
        // キャプションボタンの上はボタン自身の操作に任せる。ここで掴むとクリックがドラッグに化ける。
        if (args.Source is Visual visual && visual.FindAncestorOfType<Button>() is not null)
        {
            return;
        }

        if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(args);
        }
    }

    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs args)
        => ToggleMaximize();

    private void Minimize_Click(object? sender, RoutedEventArgs args)
        => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object? sender, RoutedEventArgs args)
        => ToggleMaximize();

    private void Close_Click(object? sender, RoutedEventArgs args)
        => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void UpdateMaximizeRestoreGlyph()
    {
        var maximizeIcon = this.FindControl<PathIcon>("MaximizeIcon");
        var restoreIcon = this.FindControl<PathIcon>("RestoreIcon");
        if (maximizeIcon is not null && restoreIcon is not null)
        {
            var isMaximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
            maximizeIcon.IsVisible = !isMaximized;
            restoreIcon.IsVisible = isMaximized;
        }
    }
}
