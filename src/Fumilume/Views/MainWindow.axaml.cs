using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Search;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly TextEditor _editor;
    private readonly SearchPanel _searchPanel;
    private DocumentViewModel? _boundDocument;
    private bool _closeConfirmed;
    private bool _closeCheckInProgress;
    private bool _syncingCaret;

    public MainWindow()
    {
        InitializeComponent();
        var dialogs = new EditorDialogService(this);
        _viewModel = new MainWindowViewModel(new DocumentFileService(), dialogs);
        DataContext = _viewModel;

        _editor = this.FindControl<TextEditor>("Editor")
            ?? throw new InvalidOperationException("エディタを初期化できませんでした。");
        ConfigureEditor();
        _searchPanel = SearchPanel.Install(_editor);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _editor.TextArea.Caret.PositionChanged += OnEditorCaretPositionChanged;
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        BindSelectedDocument();

        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs args)
    {
        ApplyTransparencyFallback();
        UpdateMaximizeRestoreGlyph();
        _ = _viewModel.InitializeAsync(Program.StartupArgs);
        _ = UpdateService.CheckAsync(this, manually: false);
        _editor.Focus();
    }

    private void ConfigureEditor()
    {
        _editor.Options.AcceptsTab = true;
        _editor.Options.AllowScrollBelowDocument = true;
        _editor.Options.EnableRectangularSelection = true;
        _editor.Options.EnableTextDragDrop = true;
        _editor.Options.HighlightCurrentLine = true;
        _editor.Options.IndentationSize = 4;
        ApplyEditorOptions();
    }

    private void ApplyEditorOptions()
    {
        _editor.ShowLineNumbers = _viewModel.ShowLineNumbers;
        _editor.WordWrap = _viewModel.WordWrap;
        _editor.Options.ShowSpaces = _viewModel.ShowWhitespace;
        _editor.Options.ShowTabs = _viewModel.ShowWhitespace;
        _editor.Options.ShowEndOfLine = _viewModel.ShowWhitespace;
    }

    private void BindSelectedDocument()
    {
        if (_boundDocument is not null)
        {
            _boundDocument.PropertyChanged -= OnBoundDocumentPropertyChanged;
        }

        _boundDocument = _viewModel.SelectedDocument;
        if (_boundDocument is null)
        {
            _editor.Document = new TextDocument();
            return;
        }

        _boundDocument.PropertyChanged += OnBoundDocumentPropertyChanged;
        _editor.Document = _boundDocument.EditorDocument;
        RestoreCaretFromDocument(scrollIntoView: false);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.SelectedDocument))
        {
            BindSelectedDocument();
            _editor.Focus();
            return;
        }

        if (args.PropertyName is nameof(MainWindowViewModel.ShowLineNumbers)
            or nameof(MainWindowViewModel.WordWrap)
            or nameof(MainWindowViewModel.ShowWhitespace))
        {
            ApplyEditorOptions();
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
        }
        finally
        {
            _syncingCaret = false;
        }
    }

    private void OnBoundDocumentPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_syncingCaret && args.PropertyName == nameof(DocumentViewModel.CaretIndex))
        {
            RestoreCaretFromDocument(scrollIntoView: true);
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

    private void OnGlobalKeyDown(object? sender, KeyEventArgs args)
    {
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
        if (!string.IsNullOrEmpty(_editor.SelectedText))
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

    private void ApplyTransparencyFallback()
    {
        var acrylicAvailable = ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur;
        var acrylic = this.FindControl<ExperimentalAcrylicBorder>("AcrylicLayer");
        var scrim = this.FindControl<Border>("AcrylicScrim");
        var fallback = this.FindControl<Border>("FallbackLayer");
        if (acrylic is not null) acrylic.IsVisible = acrylicAvailable;
        if (scrim is not null) scrim.IsVisible = acrylicAvailable;
        if (fallback is not null) fallback.IsVisible = !acrylicAvailable;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            UpdateMaximizeRestoreGlyph();
        }
    }

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
            if (await _viewModel.CanCloseAsync())
            {
                _closeConfirmed = true;
                Close();
            }
        }
        finally
        {
            _closeCheckInProgress = false;
        }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs args)
    {
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
            var isMaximized = WindowState == WindowState.Maximized;
            maximizeIcon.IsVisible = !isMaximized;
            restoreIcon.IsVisible = isMaximized;
        }
    }
}
