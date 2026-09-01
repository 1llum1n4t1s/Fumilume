using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
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
    private readonly ColumnDefinition _sidePanelColumn;
    private DocumentViewModel? _boundDocument;
    private bool _closeConfirmed;
    private bool _closeCheckInProgress;
    private bool _syncingCaret;
    private bool _opened;
    private bool _formatDocumentChordPending;
    private readonly Queue<IReadOnlyList<string>> _pendingForwardedArguments = [];
    private Task _forwardedOpen = Task.CompletedTask;

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

        var workspaceGrid = this.FindControl<Grid>("WorkspaceGrid")
            ?? throw new InvalidOperationException("ワークスペースを初期化できませんでした。");
        _sidePanelColumn = workspaceGrid.ColumnDefinitions[0];
        _sidePanelColumn.Width = new GridLength(Math.Clamp(
            settings.SidePanelWidth,
            AppSettingsDefaults.MinimumSidePanelWidth,
            AppSettingsDefaults.MaximumSidePanelWidth));

        _editor = this.FindControl<TextEditor>("Editor")
            ?? throw new InvalidOperationException("エディタを初期化できませんでした。");
        _searchPanel = SearchPanel.Install(_editor);
        _editor.TextArea.TextView.BackgroundRenderers.Add(new BookmarkRenderer(() => _boundDocument));
        ApplyUiFontOptions();
        ApplyEditorOptions();
        ApplyTabHeight();

        BuildEditorContextMenu();
        RoundedClip.Attach(this.FindControl<Border>("ContentIsland"));
        ApplyWindowDecorations(WindowState);
        ApplyBackdrop();
        RestoreWindowBounds(settings);

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _options.PropertyChanged += OnOptionsPropertyChanged;
        _editor.TextArea.Caret.PositionChanged += OnEditorCaretPositionChanged;
        _editor.TextArea.SelectionChanged += OnEditorSelectionChanged;

        // キーボードマクロの記録。移動と削除は AvaloniaEdit が処理する前に見たいので Tunnel で受ける
        // （読むだけで Handled は立てない）。打った文字そのものは TextEntered から拾う。
        _editor.TextArea.AddHandler(KeyDownEvent, OnEditorKeyDownForMacro, RoutingStrategies.Tunnel);
        _editor.TextArea.TextEntered += OnEditorTextEnteredForMacro;
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        // システムのライト・ダーク切り替えにも強調表示の配色を追従させる。
        ActualThemeVariantChanged += (_, _) => ApplySyntaxHighlighting();
        BindSelectedDocument();

        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs args)
    {
        _opened = true;
        UpdateMaximizeRestoreGlyph();
        // テーマ辞書はウィンドウが VisualRoot へ接続されたあとに確定する。
        ApplyUiFontOptions();
        ApplyEditorOptions();
        _ = _viewModel.InitializeAsync(Program.StartupArgs);
        while (_pendingForwardedArguments.TryDequeue(out var arguments))
        {
            StartForwardedOpen(arguments);
        }

        if (_options.CheckUpdatesOnStartup)
        {
            _ = UpdateService.CheckAsync(this, manually: false);
        }

        _editor.Focus();
    }

    /// <summary>別プロセスから転送されたファイルを開き、既存ウィンドウを前面へ戻す。</summary>
    internal void OpenForwardedArguments(IReadOnlyList<string> arguments)
    {
        if (!_opened)
        {
            _pendingForwardedArguments.Enqueue(arguments);
            return;
        }

        StartForwardedOpen(arguments);
    }

    private void StartForwardedOpen(IReadOnlyList<string> arguments)
        => _forwardedOpen = OpenForwardedArgumentsAfterAsync(_forwardedOpen, arguments);

    private async Task OpenForwardedArgumentsAfterAsync(
        Task previous,
        IReadOnlyList<string> arguments)
    {
        await previous;
        await _viewModel.OpenForwardedPathsAsync(arguments);
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
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

        // VS Code 形式の引用符付きリストを解釈し、最後は等幅フォントへ落とす。
        _editor.FontFamily = new FontFamily(EditorFontFamily.ToAvalonia(_options.EditorFontFamily));
        _editor.FontSize = _options.EditorFontSize;

        ApplySyntaxHighlighting();

        // AvaloniaEdit の既定色（緑系）を使わず、テーマに合う青系へ固定する。
        var textView = _editor.TextArea.TextView;
        if (textView.TryFindResource("EditorCurrentLineBg", textView.ActualThemeVariant, out var currentLineBackground))
        {
            textView.CurrentLineBackground = currentLineBackground as IBrush;
        }

        if (textView.TryFindResource("EditorCurrentLineBorder", textView.ActualThemeVariant, out var currentLineBorder))
        {
            textView.CurrentLineBorder = currentLineBorder is IBrush brush
                ? new Pen(brush, 1)
                : null;
        }
    }

    /// <summary>タブ 1 行の厚みを反映する。項目テンプレートの Grid と ListBoxItem の
    /// ControlTheme が同じ <c>TabItemHeight</c> を DynamicResource で見ているので、
    /// ここを 1 箇所書き換えれば両方が追従する（DataTemplate の DataContext はタブ側の
    /// ビューモデルなので、テンプレートから設定を辿らせずに済ませたい）。</summary>
    private void ApplyTabHeight() => Resources["TabItemHeight"] = _options.TabHeight;

    /// <summary>
    /// 今の文書に合う強調表示を当てる。同梱定義の役割名をOne Dark／One Lightの
    /// 共通パレットへ割り当ててから、現在のテーマへ適用する。
    /// </summary>
    private void ApplySyntaxHighlighting()
        => _editor.SyntaxHighlighting = _options.EnableSyntaxHighlighting
            ? SyntaxHighlightingService.Resolve(
                _boundDocument?.FilePath,
                ActualThemeVariant == ThemeVariant.Dark)
            : null;

    /// <summary>ウィンドウの継承フォントを変え、明示指定されたアイコンとエディタは各自の設定を保つ。</summary>
    private void ApplyUiFontOptions()
    {
        FontFamily = AppFontFamilies.ResolveUiFont(_options.UiFontFamily);
        FontSize = _options.UiFontSize;
    }

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

        if (args.PropertyName is nameof(AppOptionsViewModel.UiFontFamily) or nameof(AppOptionsViewModel.UiFontSize))
        {
            ApplyUiFontOptions();
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
        settings.SidePanelWidth = Math.Clamp(
            _sidePanelColumn.ActualWidth,
            AppSettingsDefaults.MinimumSidePanelWidth,
            AppSettingsDefaults.MaximumSidePanelWidth);
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
        ApplySyntaxHighlighting();
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
        else if (args.PropertyName == nameof(DocumentViewModel.FilePath))
        {
            // 名前を付けて保存で拡張子が決まったら、その場で強調表示を切り替える。
            ApplySyntaxHighlighting();
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
    /// エディタの右クリックメニューを <see cref="EditorCommandCatalog"/> から組む。
    ///
    /// 変換・整形の類は「今どこを選んでいるか」が決まって初めて意味を持つので、常時見えるツールバーではなく
    /// 選択したその場に出す。ツールバーへ並べていた区分ボタン 4 つの置き換えでもある。
    ///
    /// <c>MenuFlyout.ItemsSource</c> でデータバインドすると、階層メニューの Header と ItemsSource を
    /// リフレクション束縛（<c>ReflectionBinding</c>）で引くことになり、<c>PublishAot</c> のトリミングで
    /// 落ちる。項目数は起動時に確定するので、ここで組み立てる。
    /// </summary>
    private void BuildEditorContextMenu()
    {
        var cut = CreateEditorAction("切り取り", new KeyGesture(Key.X, KeyModifiers.Control), () => _editor.Cut());
        var copy = CreateEditorAction("コピー", new KeyGesture(Key.C, KeyModifiers.Control), () => _editor.Copy());
        var paste = CreateEditorAction("貼り付け", new KeyGesture(Key.V, KeyModifiers.Control), () => _editor.Paste());
        var selectAll = CreateEditorAction("すべて選択", new KeyGesture(Key.A, KeyModifiers.Control), () => _editor.SelectAll());

        var flyout = new MenuFlyout();
        flyout.Items.Add(cut);
        flyout.Items.Add(copy);
        flyout.Items.Add(paste);
        flyout.Items.Add(selectAll);
        flyout.Items.Add(new Separator());

        foreach (var group in _viewModel.EditorMenu)
        {
            var parent = new MenuItem
            {
                Header = group.Title,
                Icon = CreateCategoryIcon(group.Title),
            };
            foreach (var leaf in group.Children)
            {
                parent.Items.Add(new MenuItem
                {
                    Header = leaf.Title,
                    Command = leaf.Command,
                    CommandParameter = leaf.CommandParameter,
                    InputGesture = ParseGesture(leaf.Gesture),
                });
            }

            flyout.Items.Add(parent);
        }

        // 選択が要る項目は、開くたびに今の状態で判断する。
        flyout.Opening += (_, _) =>
        {
            var hasSelection = _editor.SelectionLength > 0;
            cut.IsEnabled = hasSelection;
            copy.IsEnabled = hasSelection;
        };

        _editor.ContextFlyout = flyout;
    }

    private static MenuItem CreateEditorAction(string header, KeyGesture gesture, Action run)
    {
        var item = new MenuItem { Header = header, InputGesture = gesture };
        item.Click += (_, _) => run();
        return item;
    }

    /// <summary>区分の見出しに添えるアイコン。対応が無ければ何も付けない。</summary>
    private static Control? CreateCategoryIcon(string category)
    {
        var glyph = EditorCommandCatalog.CategoryIcons
            .FirstOrDefault(icon => icon.Category == category)?.Glyph;
        return glyph is null
            ? null
            : new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14 };
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
        var controlOnly = args.KeyModifiers == KeyModifiers.Control;
        if (_formatDocumentChordPending)
        {
            _formatDocumentChordPending = false;
            if (controlOnly && args.Key == Key.D && _viewModel.IsDocumentSelected)
            {
                _viewModel.RunEditorCommandCommand.Execute(EditorCommandId.FormatDocument);
                args.Handled = true;
                return;
            }
        }

        // Avalonia の KeyBinding は複数ストロークを表現できないため、Visual Studio の
        // Edit.FormatDocument と同じ Ctrl+K, Ctrl+D だけをここで状態として受ける。
        if (controlOnly && args.Key == Key.K && _viewModel.IsDocumentSelected)
        {
            _formatDocumentChordPending = true;
            _viewModel.StatusMessage = "Ctrl+K が押されました。Ctrl+D で文書全体を書式整形します";
            args.Handled = true;
            return;
        }

        // フォルダ横断検索はどのタブを見ていても始められる（結果タブからの再検索を含む）。
        if (args.KeyModifiers.HasFlag(KeyModifiers.Control)
            && args.KeyModifiers.HasFlag(KeyModifiers.Shift)
            && args.Key == Key.F)
        {
            _viewModel.GrepCommand.Execute(null);
            args.Handled = true;
            return;
        }

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
                _viewModel.RecordMacroStep(new MacroStep
                {
                    Kind = MacroStepKind.FindNext,
                    Text = _searchPanel.SearchPattern ?? string.Empty,
                    MatchCase = _searchPanel.MatchCase,
                    UseRegex = _searchPanel.UseRegex,
                });
            }

            args.Handled = true;
        }
    }

    // ===== キーボードマクロの記録 =====

    /// <summary>
    /// カーソル移動・削除・改行・タブを 1 手として記録する。文字入力は
    /// <see cref="OnEditorTextEnteredForMacro"/> が受けるので、ここでは扱わない。
    /// </summary>
    private void OnEditorKeyDownForMacro(object? sender, KeyEventArgs args)
    {
        if (!_viewModel.IsCapturingMacro)
        {
            return;
        }

        var ctrl = args.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = args.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var step = args.Key switch
        {
            Key.Left => Motion(ctrl ? MacroMotion.WordLeft : MacroMotion.CharacterLeft, shift),
            Key.Right => Motion(ctrl ? MacroMotion.WordRight : MacroMotion.CharacterRight, shift),
            Key.Up => Motion(MacroMotion.LineUp, shift),
            Key.Down => Motion(MacroMotion.LineDown, shift),
            Key.Home => Motion(ctrl ? MacroMotion.DocumentStart : MacroMotion.LineStart, shift),
            Key.End => Motion(ctrl ? MacroMotion.DocumentEnd : MacroMotion.LineEnd, shift),
            Key.Back => new MacroStep { Kind = MacroStepKind.DeleteBack },
            Key.Delete => new MacroStep { Kind = MacroStepKind.DeleteForward },
            Key.Enter or Key.Return => new MacroStep { Kind = MacroStepKind.InsertText, Text = "\n" },

            // Tab は、実際に文字が入るときだけ記録する。選択があるときは字下げ、
            // AcceptsTab が切れているときはフォーカス移動になり、文字は入らない。
            Key.Tab when !shift && _editor.Options.AcceptsTab && _editor.SelectionLength == 0
                => new MacroStep { Kind = MacroStepKind.InsertText, Text = "\t" },
            _ => null,
        };

        if (step is not null)
        {
            _viewModel.RecordMacroStep(step);
        }
    }

    private void OnEditorTextEnteredForMacro(object? sender, TextInputEventArgs args)
    {
        if (_viewModel.IsCapturingMacro && !string.IsNullOrEmpty(args.Text))
        {
            _viewModel.RecordMacroStep(new MacroStep { Kind = MacroStepKind.InsertText, Text = args.Text });
        }
    }

    private static MacroStep Motion(MacroMotion motion, bool extendSelection)
        => new() { Kind = MacroStepKind.MoveCaret, Motion = motion, ExtendSelection = extendSelection };

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

    // ===== フォルダ横断検索の結果 =====

    private void GrepResults_DoubleTapped(object? sender, TappedEventArgs args)
    {
        _viewModel.SelectedGrep?.OpenSelectedCommand.Execute(null);
        args.Handled = true;
    }

    private void GrepResults_KeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key is not (Key.Enter or Key.Return))
        {
            return;
        }

        _viewModel.SelectedGrep?.OpenSelectedCommand.Execute(null);
        args.Handled = true;
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

        if (RequiresSynchronousShutdownPersistence(args.CloseReason))
        {
            SaveWindowBounds();
            if (!_viewModel.PersistSessionStateForShutdown())
            {
                AppLogger.For<MainWindow>().Warn("システム終了時にセッションを保存できませんでした。");
            }

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

            if (!await _viewModel.CanCloseAsync())
            {
                return;
            }

            SaveWindowBounds();

            // 未保存の内容をセッションへ預けられなかったときは閉じない（黙って捨てるより止まる）。
            if (!await _viewModel.PersistSessionStateAsync())
            {
                return;
            }

            _closeConfirmed = true;
            Close();
        }
        finally
        {
            _closeCheckInProgress = false;
        }
    }

    internal static bool RequiresSynchronousShutdownPersistence(WindowCloseReason reason)
        => reason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown;

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
