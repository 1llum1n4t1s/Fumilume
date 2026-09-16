using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Views;

/// <summary>CSV を表として描画し、セルと表構造を編集するプレビュー。</summary>
public sealed class CsvPreview : UserControl
{
    public static readonly StyledProperty<string?> CsvProperty =
        AvaloniaProperty.Register<CsvPreview, string?>(nameof(Csv));
    public static readonly StyledProperty<DocumentViewModel?> DocumentProperty =
        AvaloniaProperty.Register<CsvPreview, DocumentViewModel?>(nameof(Document));

    private const string EditHint =
        "セルを選択して入力・コピー・貼り付けできます。ダブルクリック、Enter、F2 で値を編集します。";
    private const string OperationBusyMessage = "CSVの操作を処理しています。完了するまで表の変更はできません。";
    private const int BackgroundParseThreshold = 128 * 1024;
    private readonly ConditionalWeakTable<DocumentViewModel, CsvViewState> _viewStates = new();
    private readonly TextBlock _editHint = new() { Text = EditHint, Margin = new Thickness(12, 6), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _editPanel = new() { IsVisible = false, Margin = new Thickness(12, 4, 12, 8), Spacing = 6 };
    private readonly TextBlock _cellLabel = new();
    private readonly TextBox _cellEditor = new()
    {
        Name = "CsvCellEditor", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        MinHeight = 60, MaxHeight = 140,
    };
    private readonly TextBlock _limitMessage = new()
    {
        Margin = new Thickness(12, 8), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false,
    };
    private readonly StackPanel _emptyPanel = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Spacing = 10,
        IsVisible = true,
    };
    private readonly ScrollViewer _scrollViewer;
    private readonly CsvGridSurface _surface;
    private readonly Func<string, Task>? _writeClipboardText;
    private readonly Func<Task<string?>>? _readClipboardText;
    private string? _editSource;
    private DocumentViewModel? _editDocument;
    private int _editRow;
    private int _editColumn;
    private bool _preserveOffsetForEdit;
    private bool _preserveSelectionForEdit;
    private DocumentViewModel? _renderedDocument;
    private DocumentViewModel? _preserveStateDocument;
    private bool _renderPending;
    private bool _needsRender = true;
    private bool _clipboardOperationInProgress;
    private CsvTableOperationState? _tableOperation;
    private Task? _tableOperationTask;
    private CancellationTokenSource? _renderCancellation;
    private Task? _renderTask;
    private (DocumentViewModel Document, string Source, string Message)? _postRenderHint;
    private bool _isAttached;

    public CsvPreview()
    {
        Focusable = true;
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Z, KeyModifiers.Control),
            Command = new RelayCommand(() => Document?.EditorDocument.UndoStack.Undo(), () => !_editPanel.IsVisible && Document?.CanUndo == true),
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Y, KeyModifiers.Control),
            Command = new RelayCommand(() => Document?.EditorDocument.UndoStack.Redo(), () => !_editPanel.IsVisible && Document?.CanRedo == true),
        });
        // ウィンドウ側のエディタ用ショートカットより先に、表の操作として処理する。
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        _surface = new CsvGridSurface { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _surface.CellEditRequested = BeginCellEdit;
        _surface.SelectionChanged = OnSelectionChanged;
        _surface.SelectionActionRequested = PerformSelectionAction;
        _scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _surface,
            IsVisible = false,
        };
        _scrollViewer.ScrollChanged += (_, _) =>
        {
            _surface.RefreshVisibleCells();
            _surface.InvalidateGrid();
        };
        _surface.ScrollOwner = _scrollViewer;

        _emptyPanel.Children.Add(new TextBlock
        {
            Text = "CSV データがありません",
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var emptyButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 8,
        };
        var addFirstRow = new Button { Name = "CsvAddFirstRowButton", Content = "最初の行を追加" };
        var addFirstColumn = new Button { Name = "CsvAddFirstColumnButton", Content = "最初の列を追加" };
        addFirstRow.Click += (_, _) => PerformEmptyInsert(CsvTableOperation.InsertRows);
        addFirstColumn.Click += (_, _) => PerformEmptyInsert(CsvTableOperation.InsertColumns);
        emptyButtons.Children.Add(addFirstRow);
        emptyButtons.Children.Add(addFirstColumn);
        _emptyPanel.Children.Add(emptyButtons);

        var content = new Grid();
        content.Children.Add(_scrollViewer);
        content.Children.Add(_emptyPanel);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        root.Children.Add(_limitMessage);
        Grid.SetRow(_editHint, 1);
        root.Children.Add(_editHint);
        _editPanel.Children.Add(_cellLabel);
        _editPanel.Children.Add(_cellEditor);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var apply = new Button { Name = "ApplyCsvCellEdit", Content = "適用 (Ctrl+Enter)" };
        var cancel = new Button { Name = "CancelCsvCellEdit", Content = "キャンセル (Esc)" };
        apply.Click += (_, _) => ApplyCellEdit();
        cancel.Click += (_, _) => { CancelCellEdit(); Focus(); };
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        _editPanel.Children.Add(buttons);
        _editPanel.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                CancelCellEdit();
                Focus();
                args.Handled = true;
            }
            else if (args.Key == Key.Enter && args.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                ApplyCellEdit(0, 0);
                args.Handled = true;
            }
            else if (args.Key == Key.Tab)
            {
                ApplyCellEdit(0, args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                args.Handled = true;
            }
            else if (args.Key == Key.Enter && !args.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                ApplyCellEdit(1, 0);
                args.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        Grid.SetRow(_editPanel, 2);
        root.Children.Add(_editPanel);
        Grid.SetRow(content, 3);
        root.Children.Add(content);
        Content = root;
        DetachedFromVisualTree += (_, _) =>
        {
            _isAttached = false;
            CancelTableOperation();
            CancelBackgroundRender(markDirty: true);
        };
        AttachedToVisualTree += (_, _) =>
        {
            _isAttached = true;
            if (_needsRender)
            {
                ScheduleRender();
            }
        };
        ScheduleRender();
    }

    internal CsvPreview(Func<string, Task> writeClipboardText, Func<Task<string?>> readClipboardText)
        : this()
    {
        ArgumentNullException.ThrowIfNull(writeClipboardText);
        ArgumentNullException.ThrowIfNull(readClipboardText);
        _writeClipboardText = writeClipboardText;
        _readClipboardText = readClipboardText;
    }

    public string? Csv
    {
        get => GetValue(CsvProperty);
        set => SetValue(CsvProperty, value);
    }

    public DocumentViewModel? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    private void BeginCellEdit(int row, int column)
    {
        if (_tableOperation is not null)
        {
            _editHint.Text = "CSVの操作を処理しています。完了後にセルを編集してください。";
            return;
        }
        if (_editPanel.IsVisible)
        {
            _cellEditor.Focus();
            return;
        }
        if (Document is not { IsCsv: true } document || Csv != document.Text || _needsRender)
        {
            ExplainUnavailableOperation();
            return;
        }

        _editDocument = document;
        _editSource = document.Text;
        _editRow = row;
        _editColumn = column;
        _cellLabel.Text = $"{CsvGridSurface.GetColumnName(column)}{row + 1} の値（Enter で適用して下へ、Alt+Enter で改行）";
        var sourceRow = _surface.Document.Rows[row];
        _cellEditor.Text = column < sourceRow.Count ? sourceRow[column] : string.Empty;
        _editHint.Text = "編集中の値は「適用」で反映します。表示やタブを切り替えると未適用の入力は取り消されます。";
        _editPanel.IsVisible = true;
        _cellEditor.Focus();
        _cellEditor.SelectAll();
    }

    private void ApplyCellEdit(int rowDelta = 0, int columnDelta = 0)
    {
        var document = _editDocument;
        var source = _editSource;
        var value = _cellEditor.Text ?? string.Empty;
        var editedRow = _editRow;
        var editedColumn = _editColumn;
        CancelCellEdit();
        if (document is null || source is null || !ReferenceEquals(document, Document))
        {
            return;
        }

        _preserveOffsetForEdit = true;
        _preserveSelectionForEdit = true;
        _preserveStateDocument = document;
        var state = GetViewState(document);
        var nextRow = Math.Clamp(editedRow + rowDelta, 0, Math.Max(0, _surface.Document.Rows.Count - 1));
        var nextColumn = Math.Clamp(editedColumn + columnDelta, 0, Math.Max(0, _surface.Document.DisplayedColumnCount - 1));
        state.SelectCell(nextRow, nextColumn, false);
        if (!document.TryUpdateCsvCell(source, _editRow, _editColumn, value))
        {
            _preserveOffsetForEdit = false;
            _preserveSelectionForEdit = false;
            _preserveStateDocument = null;
            _editHint.Text = GetOperationFailureMessage(
                document,
                source,
                "セルを更新できませんでした。セルを選び直してください。");
        }
        else
        {
            _surface.ScrollCellIntoView(nextRow, nextColumn);
            if (!_needsRender)
            {
                _preserveOffsetForEdit = false;
                _preserveSelectionForEdit = false;
                _preserveStateDocument = null;
                _surface.InvalidateGrid();
                UpdateSelectionHint();
            }
        }
        Focus();
    }

    private void CancelCellEdit()
    {
        _editPanel.IsVisible = false;
        _editDocument = null;
        _editSource = null;
        _cellEditor.Text = string.Empty;
        _editHint.Text = EditHint;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs args)
    {
        if (_cellEditor.IsKeyboardFocusWithin || _editPanel.IsVisible)
        {
            return;
        }

        var state = _surface.ViewState;
        var control = args.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = args.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (control && args.Key == Key.C
            && args.Source is SelectableTextBlock selectedText
            && selectedText.SelectionStart != selectedText.SelectionEnd)
        {
            return;
        }
        if (control && args.Key == Key.C)
        {
            StartCopySelection(false);
            args.Handled = state.SelectionKind != CsvSelectionKind.None;
        }
        else if (control && args.Key == Key.X)
        {
            StartCopySelection(true);
            args.Handled = state.SelectionKind != CsvSelectionKind.None;
        }
        else if (control && args.Key == Key.V)
        {
            StartPasteSelection();
            args.Handled = state.SelectionKind == CsvSelectionKind.Cells;
        }
        else if (control && args.Key == Key.D)
        {
            args.Handled = StartCellAction(CsvSelectionAction.FillDown);
        }
        else if (control && args.Key == Key.R)
        {
            args.Handled = StartCellAction(CsvSelectionAction.FillRight);
        }
        else if (args.Key is Key.Delete or Key.Back)
        {
            args.Handled = StartCellAction(CsvSelectionAction.Clear);
        }
        else if (args.Key is Key.Enter or Key.F2)
        {
            if (state.TryGetActiveCell(out var row, out var column))
            {
                BeginCellEdit(row, column);
                args.Handled = true;
            }
        }
        else if (args.Key == Key.Tab)
        {
            args.Handled = MoveActiveCell(0, shift ? -1 : 1, false);
        }
        else if (!control && args.Key is (Key.Left or Key.Right or Key.Up or Key.Down))
        {
            var (rowDelta, columnDelta) = args.Key switch
            {
                Key.Left => (0, -1),
                Key.Right => (0, 1),
                Key.Up => (-1, 0),
                _ => (1, 0),
            };
            args.Handled = MoveActiveCell(rowDelta, columnDelta, shift);
        }
    }

    private void PerformEmptyInsert(CsvTableOperation operation)
    {
        if (_tableOperation is not null || Document is not { IsCsv: true } document)
        {
            return;
        }

        GetViewState(document).SetSelection(
            operation == CsvTableOperation.InsertRows ? CsvSelectionKind.Rows : CsvSelectionKind.Columns,
            0,
            0);
        StartStructureEdit(operation, 0, 1);
    }

    private void PerformSelectionAction(CsvSelectionAction action)
    {
        if (_needsRender || !ReferenceEquals(Document, _renderedDocument))
        {
            ExplainUnavailableOperation();
            return;
        }
        var state = _surface.ViewState;
        if (state.SelectionKind == CsvSelectionKind.None)
        {
            return;
        }
        if (_editPanel.IsVisible)
        {
            _editHint.Text = "セルの編集を適用またはキャンセルしてから表を操作してください。";
            return;
        }
        if (_tableOperation is not null)
        {
            _editHint.Text = "CSVの操作を処理しています。完了後にもう一度お試しください。";
            return;
        }
        if (action == CsvSelectionAction.Copy)
        {
            StartCopySelection(false);
            return;
        }
        if (action == CsvSelectionAction.Cut)
        {
            StartCopySelection(true);
            return;
        }
        if (action == CsvSelectionAction.Paste)
        {
            StartPasteSelection();
            return;
        }
        if (action is CsvSelectionAction.Clear or CsvSelectionAction.FillDown or CsvSelectionAction.FillRight)
        {
            StartCellAction(action);
            return;
        }

        if (state.SelectionKind == CsvSelectionKind.Cells)
        {
            return;
        }

        var rows = state.SelectionKind == CsvSelectionKind.Rows;
        var operation = action switch
        {
            CsvSelectionAction.InsertBefore => rows ? CsvTableOperation.InsertRows : CsvTableOperation.InsertColumns,
            CsvSelectionAction.InsertAfter => rows ? CsvTableOperation.InsertRows : CsvTableOperation.InsertColumns,
            CsvSelectionAction.Delete => rows ? CsvTableOperation.DeleteRows : CsvTableOperation.DeleteColumns,
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        var index = action == CsvSelectionAction.InsertAfter ? state.SelectionEnd + 1 : state.SelectionStart;
        var count = state.SelectionCount;

        if (action == CsvSelectionAction.Delete)
        {
            var remaining = (rows ? _surface.Document.TotalRowCount : _surface.Document.TotalColumnCount) - count;
            if (remaining > 0)
            {
                var next = Math.Min(state.SelectionStart, remaining - 1);
                state.SetSelection(state.SelectionKind, next, next);
            }
            else
            {
                state.ClearSelection();
            }
        }
        else
        {
            state.SetSelection(state.SelectionKind, index, index + count - 1);
        }
        StartStructureEdit(operation, index, count);
    }

    private void StartStructureEdit(CsvTableOperation operation, int index, int count)
    {
        if (_needsRender || Document is not { IsCsv: true } document || !ReferenceEquals(document, _renderedDocument))
        {
            ExplainUnavailableOperation();
            return;
        }

        var snapshot = _surface.ViewState.SourceSnapshot;
        var state = _surface.ViewState;
        var selection = state.GetSelectionIdentity();
        StartTableOperation(
            operationState => ApplyStructureEditAsync(
                operationState,
                document,
                snapshot,
                state,
                selection,
                operation,
                index,
                count),
            clipboardOperation: false,
            "行または列の変更中にエラーが発生しました。もう一度お試しください。");
    }

    private async Task ApplyStructureEditAsync(
        CsvTableOperationState operationState,
        DocumentViewModel document,
        string snapshot,
        CsvViewState state,
        CsvViewState.SelectionIdentity selection,
        CsvTableOperation operation,
        int index,
        int count)
    {
        var edit = await document.PrepareCsvStructureEditAsync(
            snapshot,
            operation,
            index,
            count,
            operationState.Cancellation.Token);
        if (edit is null)
        {
            if (CanUseTableOperationResult(operationState, document, snapshot, state, selection))
            {
                state.ClearSelection();
                _surface.InvalidateGrid();
                _editHint.Text = GetOperationFailureMessage(
                    document,
                    snapshot,
                    GetCsvLimitFailureMessage("行または列を変更"));
            }
            return;
        }

        if (!TryApplyPreparedEdit(
                operationState,
                document,
                snapshot,
                state,
                selection,
                edit,
                GetCsvLimitFailureMessage("行または列を変更")))
        {
            return;
        }

        if (operation is CsvTableOperation.InsertRows or CsvTableOperation.InsertColumns)
        {
            var limit = operation == CsvTableOperation.InsertRows
                ? CsvDocumentParser.MaxPreviewRows
                : CsvDocumentParser.MaxPreviewColumns;
            if (index + count > limit)
            {
                var target = operation == CsvTableOperation.InsertRows ? "行" : "列";
                _postRenderHint = (document, document.Text,
                    $"追加した{target}の一部または全部はプレビューの表示上限外です。「編集へ戻る」で確認・修正してください。");
                if (index >= limit)
                {
                    GetViewState(document).ClearSelection();
                }
            }
        }
    }

    private void StartCopySelection(bool cut)
    {
        var state = _surface.ViewState;
        if (state.SelectionKind == CsvSelectionKind.None)
        {
            return;
        }
        if (_needsRender || Document is not { IsCsv: true } document || !ReferenceEquals(document, _renderedDocument))
        {
            ExplainUnavailableOperation();
            return;
        }

        var snapshot = state.SourceSnapshot;
        var selection = state.GetSelectionIdentity();
        var range = GetSelectedRange(state);
        if (range is not { } selectedRange)
        {
            return;
        }
        var writeText = _writeClipboardText;
        if (writeText is null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            writeText = clipboard.SetTextAsync;
        }
        if (writeText is null)
        {
            return;
        }

        StartTableOperation(
            operationState => CopySelectionAsync(
                operationState,
                document,
                snapshot,
                state,
                selection,
                selectedRange,
                writeText,
                cut),
            clipboardOperation: true,
            "クリップボードへコピーできませんでした。もう一度お試しください。");
    }

    private async Task CopySelectionAsync(
        CsvTableOperationState operationState,
        DocumentViewModel document,
        string snapshot,
        CsvViewState state,
        CsvViewState.SelectionIdentity selection,
        CsvCellRange selectedRange,
        Func<string, Task> writeText,
        bool cut)
    {
        var text = await document.GetCsvCellRangeTextAsync(
            snapshot,
            selectedRange,
            operationState.Cancellation.Token);
        if (text is null)
        {
            if (CanUseTableOperationResult(operationState, document, snapshot, state, selection))
            {
                _editHint.Text = GetOperationFailureMessage(
                    document,
                    snapshot,
                    GetCsvLimitFailureMessage("選択範囲をコピー"));
            }
            return;
        }
        if (!CanUseTableOperationResult(operationState, document, snapshot, state, selection))
        {
            return;
        }

        await writeText(text);
        if (!CanUseTableOperationResult(operationState, document, snapshot, state, selection))
        {
            return;
        }
        if (!cut)
        {
            _editHint.Text = state.SelectionKind switch
            {
                CsvSelectionKind.Rows => $"{selectedRange.RowCount:N0} 行をコピーしました。",
                CsvSelectionKind.Columns => $"{selectedRange.ColumnCount:N0} 列をコピーしました。",
                _ => $"{selectedRange.RowCount:N0} 行 × {selectedRange.ColumnCount:N0} 列をコピーしました。",
            };
            return;
        }

        var edit = await document.PrepareCsvClearAsync(
            snapshot,
            selectedRange,
            operationState.Cancellation.Token);
        if (edit is null)
        {
            if (CanUseTableOperationResult(operationState, document, snapshot, state, selection))
            {
                _editHint.Text = GetOperationFailureMessage(
                    document,
                    snapshot,
                    GetCsvLimitFailureMessage("切り取り元を消去"));
            }
            return;
        }
        TryApplyPreparedEdit(
            operationState,
            document,
            snapshot,
            state,
            selection,
            edit,
            GetCsvLimitFailureMessage("切り取り元を消去"));
    }

    private void StartPasteSelection()
    {
        var state = _surface.ViewState;
        if (state.SelectionKind != CsvSelectionKind.Cells)
        {
            return;
        }
        var selectedRange = state.CellRange;
        if (_needsRender || Document is not { IsCsv: true } document || !ReferenceEquals(document, _renderedDocument))
        {
            ExplainUnavailableOperation();
            return;
        }

        var readText = _readClipboardText;
        if (readText is null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            readText = clipboard.TryGetTextAsync;
        }
        if (readText is null)
        {
            return;
        }
        var snapshot = state.SourceSnapshot;
        var selection = state.GetSelectionIdentity();
        StartTableOperation(
            operationState => PasteSelectionAsync(
                operationState,
                document,
                snapshot,
                state,
                selection,
                selectedRange.Row,
                selectedRange.Column,
                readText),
            clipboardOperation: true,
            "クリップボードから読み取れませんでした。もう一度お試しください。");
    }

    private async Task PasteSelectionAsync(
        CsvTableOperationState operationState,
        DocumentViewModel document,
        string snapshot,
        CsvViewState state,
        CsvViewState.SelectionIdentity selection,
        int row,
        int column,
        Func<Task<string?>> readText)
    {
        var text = await readText();
        if (text is null || !CanUseTableOperationResult(operationState, document, snapshot, state, selection))
        {
            return;
        }

        var edit = await document.PrepareCsvPasteAsync(
            snapshot,
            row,
            column,
            text,
            operationState.Cancellation.Token);
        if (edit is null)
        {
            if (CanUseTableOperationResult(operationState, document, snapshot, state, selection))
            {
                _editHint.Text = GetOperationFailureMessage(
                    document,
                    snapshot,
                    GetCsvLimitFailureMessage("貼り付け", includesClipboard: true));
            }
            return;
        }

        TryApplyPreparedEdit(
            operationState,
            document,
            snapshot,
            state,
            selection,
            edit,
            GetCsvLimitFailureMessage("貼り付け", includesClipboard: true));
    }

    private bool StartCellAction(CsvSelectionAction action)
    {
        if (_needsRender || Document is not { IsCsv: true } document || !ReferenceEquals(document, _renderedDocument)
            || GetSelectedRange(_surface.ViewState) is not { } range)
        {
            ExplainUnavailableOperation();
            return false;
        }

        var state = _surface.ViewState;
        var snapshot = state.SourceSnapshot;
        var selection = state.GetSelectionIdentity();
        StartTableOperation(
            operationState => PerformCellActionAsync(
                operationState,
                document,
                snapshot,
                state,
                selection,
                range,
                action),
            clipboardOperation: false,
            "CSVの選択範囲を変更できませんでした。もう一度お試しください。");
        return true;
    }

    private async Task PerformCellActionAsync(
        CsvTableOperationState operationState,
        DocumentViewModel document,
        string snapshot,
        CsvViewState state,
        CsvViewState.SelectionIdentity selection,
        CsvCellRange range,
        CsvSelectionAction action)
    {
        var edit = action switch
        {
            CsvSelectionAction.Clear => await document.PrepareCsvClearAsync(
                snapshot,
                range,
                operationState.Cancellation.Token),
            CsvSelectionAction.FillDown => await document.PrepareCsvFillAsync(
                snapshot,
                range,
                true,
                operationState.Cancellation.Token),
            CsvSelectionAction.FillRight => await document.PrepareCsvFillAsync(
                snapshot,
                range,
                false,
                operationState.Cancellation.Token),
            _ => null,
        };
        var failureMessage = action switch
        {
            CsvSelectionAction.Clear => GetCsvLimitFailureMessage("選択範囲を消去"),
            CsvSelectionAction.FillDown => GetCsvLimitFailureMessage("下方向へフィル", "2行以上の範囲を選択してください。"),
            CsvSelectionAction.FillRight => GetCsvLimitFailureMessage("右方向へフィル", "2列以上の範囲を選択してください。"),
            _ => "CSVの選択範囲を変更できませんでした。",
        };

        if (edit is null)
        {
            if (CanUseTableOperationResult(operationState, document, snapshot, state, selection))
            {
                _editHint.Text = GetOperationFailureMessage(document, snapshot, failureMessage);
            }
            return;
        }

        TryApplyPreparedEdit(operationState, document, snapshot, state, selection, edit, failureMessage);
    }

    private bool StartTableOperation(
        Func<CsvTableOperationState, Task> operation,
        bool clipboardOperation,
        string unexpectedFailureMessage)
    {
        if (_editPanel.IsVisible)
        {
            _editHint.Text = "セルの編集を適用またはキャンセルしてから表を操作してください。";
            return false;
        }
        if (_tableOperation is not null)
        {
            _editHint.Text = "CSVの操作を処理しています。完了後にもう一度お試しください。";
            return false;
        }

        var operationState = new CsvTableOperationState(Document, clipboardOperation);
        _tableOperation = operationState;
        _clipboardOperationInProgress = clipboardOperation;
        _editHint.Text = OperationBusyMessage;
        _tableOperationTask = RunTableOperationAsync(operationState, operation, unexpectedFailureMessage);
        return true;
    }

    private async Task RunTableOperationAsync(
        CsvTableOperationState operationState,
        Func<CsvTableOperationState, Task> operation,
        string unexpectedFailureMessage)
    {
        try
        {
            await operation(operationState);
        }
        catch (OperationCanceledException) when (operationState.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLogger.For<CsvPreview>().Error("CSVの表操作に失敗しました。", ex);
            if (!operationState.Cancellation.IsCancellationRequested
                && ReferenceEquals(Document, operationState.Document))
            {
                _editHint.Text = unexpectedFailureMessage;
            }
        }
        finally
        {
            if (ReferenceEquals(_tableOperation, operationState))
            {
                _tableOperation = null;
            }
            if (operationState.IsClipboardOperation)
            {
                _clipboardOperationInProgress = false;
            }
            if (!_needsRender
                && ReferenceEquals(Document, operationState.Document)
                && string.Equals(_editHint.Text, OperationBusyMessage, StringComparison.Ordinal))
            {
                UpdateSelectionHint();
            }
            operationState.Dispose();
        }
    }

    private bool CanUseTableOperationResult(
        CsvTableOperationState operationState,
        DocumentViewModel document,
        string snapshot,
        CsvViewState state,
        CsvViewState.SelectionIdentity selection)
        => ReferenceEquals(_tableOperation, operationState)
           && !operationState.Cancellation.IsCancellationRequested
           && _isAttached
           && IsVisible
           && !_needsRender
           && ReferenceEquals(Document, document)
           && ReferenceEquals(_renderedDocument, document)
           && ReferenceEquals(_surface.ViewState, state)
           && string.Equals(Csv, snapshot, StringComparison.Ordinal)
           && string.Equals(document.Text, snapshot, StringComparison.Ordinal)
           && state.MatchesSelection(selection);

    private bool TryApplyPreparedEdit(
        CsvTableOperationState operationState,
        DocumentViewModel document,
        string snapshot,
        CsvViewState state,
        CsvViewState.SelectionIdentity selection,
        CsvPreparedEdit edit,
        string failureMessage)
    {
        if (!CanUseTableOperationResult(operationState, document, snapshot, state, selection))
        {
            return false;
        }

        // 本文更新が CsvProperty へ同期されたとき、それを外部変更として処理中止しない。
        _tableOperation = null;
        _preserveOffsetForEdit = true;
        _preserveSelectionForEdit = true;
        _preserveStateDocument = document;
        if (document.TryApplyPreparedCsvEdit(edit))
        {
            if (!_needsRender)
            {
                ResetPreservedEditState();
            }
            return true;
        }

        ResetPreservedEditState();
        _editHint.Text = GetOperationFailureMessage(document, snapshot, failureMessage);
        return false;
    }

    private void ResetPreservedEditState()
    {
        _preserveOffsetForEdit = false;
        _preserveSelectionForEdit = false;
        _preserveStateDocument = null;
    }

    private void CancelTableOperation()
    {
        if (_tableOperation is { } operationState)
        {
            operationState.Cancellation.Cancel();
        }
    }

    private void OnSelectionChanged()
    {
        CancelTableOperation();
        UpdateSelectionHint();
    }

    private static string GetCsvLimitFailureMessage(
        string operation,
        string? guidance = null,
        bool includesClipboard = false)
    {
        var prefix = string.IsNullOrEmpty(guidance) ? string.Empty : guidance + " ";
        var clipboardLimit = includesClipboard
            ? $"クリップボードは最大 {CsvTableEditingService.MaximumClipboardLength:N0} 文字、"
            : string.Empty;
        return $"{operation}できませんでした。{prefix}" +
               $"CSV本文は最大 {CsvTableEditingService.MaximumSourceLength:N0} 文字、" +
               clipboardLimit +
               $"操作結果は最大 {CsvTableEditingService.MaximumResultLength:N0} 文字、対象は最大 " +
               $"{CsvTableEditingService.MaximumCellCount:N0} セルです。引用符が閉じているかも確認してください。";
    }

    private string GetOperationFailureMessage(
        DocumentViewModel document,
        string sourceSnapshot,
        string fallback)
    {
        if (!string.Equals(document.Text, sourceSnapshot, StringComparison.Ordinal))
        {
            return "CSVの本文が変わったため操作できませんでした。再描画後に選び直してください。";
        }
        if (_surface.Document.HasUnterminatedQuotedField)
        {
            return "CSVの引用符が閉じていないため、表からは操作できません。「編集へ戻る」で引用符を修正してください。";
        }
        return fallback;
    }

    private void ExplainUnavailableOperation()
    {
        if (_needsRender)
        {
            _editHint.Text = "CSVの解析・再描画中です。完了後に選び直してください。";
        }
        else if (!ReferenceEquals(Document, _renderedDocument))
        {
            _editHint.Text = "表示する文書が変わったため操作できませんでした。新しい表の表示後に選び直してください。";
        }
        else if (Document is { } document
                 && !string.Equals(document.Text, _surface.ViewState.SourceSnapshot, StringComparison.Ordinal))
        {
            _editHint.Text = "CSVの本文が変わったため操作できませんでした。再描画後に選び直してください。";
        }
    }

    private CsvCellRange? GetSelectedRange(CsvViewState state)
        => state.SelectionKind switch
        {
            CsvSelectionKind.Rows when _surface.Document.TotalColumnCount > 0 =>
                new CsvCellRange(state.SelectionStart, 0, state.SelectionCount, _surface.Document.TotalColumnCount),
            CsvSelectionKind.Columns when _surface.Document.TotalRowCount > 0 =>
                new CsvCellRange(0, state.SelectionStart, _surface.Document.TotalRowCount, state.SelectionCount),
            CsvSelectionKind.Cells => state.CellRange,
            _ => null,
        };

    private bool MoveActiveCell(int rowDelta, int columnDelta, bool extend)
    {
        var state = _surface.ViewState;
        if (!state.TryGetActiveCell(out var row, out var column)
            || _surface.Document.Rows.Count == 0 || _surface.Document.DisplayedColumnCount == 0)
        {
            return false;
        }

        row = Math.Clamp(row + rowDelta, 0, _surface.Document.Rows.Count - 1);
        column = Math.Clamp(column + columnDelta, 0, _surface.Document.DisplayedColumnCount - 1);
        state.SelectCell(row, column, extend);
        _surface.ScrollCellIntoView(row, column);
        _surface.RefreshVisibleCells();
        _surface.InvalidateGrid();
        OnSelectionChanged();
        return true;
    }

    private void UpdateSelectionHint()
    {
        var state = _surface.ViewState;
        _editHint.Text = state.SelectionKind switch
        {
            CsvSelectionKind.Rows => $"{state.SelectionStart + 1:N0}～{state.SelectionEnd + 1:N0} 行を選択中。Ctrl+C または右クリックで操作できます。",
            CsvSelectionKind.Columns => $"{CsvGridSurface.GetColumnName(state.SelectionStart)}～{CsvGridSurface.GetColumnName(state.SelectionEnd)} 列を選択中。Ctrl+C・右クリックで操作（表示外を含む全行が対象）。",
            CsvSelectionKind.Cells => $"{CsvGridSurface.GetColumnName(state.CellStartColumn)}{state.CellStartRow + 1}～{CsvGridSurface.GetColumnName(state.CellEndColumn)}{state.CellEndRow + 1} を選択中。",
            _ => EditHint,
        };
    }

    internal int RenderedRowCount => _surface.Document.Rows.Count;
    internal int RenderedColumnCount => _surface.Document.DisplayedColumnCount;
    internal string? TruncationMessage => _limitMessage.IsVisible ? _limitMessage.Text : null;
    internal string EditStatus => _editHint.Text ?? string.Empty;
    internal (bool Rows, int Index, int Count)? SelectedHeaders => _surface.ViewState.SelectionKind switch
    {
        CsvSelectionKind.Rows => (true, _surface.ViewState.SelectionStart, _surface.ViewState.SelectionCount),
        CsvSelectionKind.Columns => (false, _surface.ViewState.SelectionStart, _surface.ViewState.SelectionCount),
        _ => null,
    };
    internal CsvCellRange? SelectedCellRange => _surface.ViewState.SelectionKind == CsvSelectionKind.Cells
        ? _surface.ViewState.CellRange
        : null;
    internal (int Row, int Column)? ActiveCell => _surface.ViewState.TryGetActiveCell(out var row, out var column)
        ? (row, column)
        : null;
    internal bool IsClipboardOperationInProgress => _clipboardOperationInProgress;
    internal bool IsTableOperationInProgress => _tableOperation is not null;
    internal Task PendingTableOperation => _tableOperationTask ?? Task.CompletedTask;
    internal bool IsRenderPending => _needsRender;
    internal Task PendingRender => _renderTask ?? Task.CompletedTask;
    internal double GetCsvColumnWidth(int index) => _surface.ViewState.GetColumnWidth(index);
    internal double GetCsvRowHeight(int index) => _surface.ViewState.GetRowHeight(index);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CsvProperty || change.Property == DocumentProperty)
        {
            CancelCellEdit();
            CancelTableOperation();
            CancelBackgroundRender(markDirty: false);
            _needsRender = true;
            ScheduleRender();
        }
        else if (change.Property == IsVisibleProperty && IsVisible && _needsRender)
        {
            ScheduleRender();
        }
        if (change.Property == IsVisibleProperty && !IsVisible)
        {
            CancelCellEdit();
            CancelTableOperation();
            CancelBackgroundRender(markDirty: true);
        }
    }

    private void ScheduleRender()
    {
        if (_renderPending)
        {
            return;
        }
        _renderPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _renderPending = false;
            if (_isAttached && IsVisible && _needsRender)
            {
                RenderCsv();
            }
        }, DispatcherPriority.Background);
    }

    private void RenderCsv()
    {
        var source = Csv ?? string.Empty;
        var viewModel = Document;
        CancelBackgroundRender(markDirty: false);

        if (viewModel is not null && viewModel.TryGetCsvPreview(source, out var cached))
        {
            ApplyParsedDocument(source, viewModel, cached);
            return;
        }

        if (source.Length < BackgroundParseThreshold)
        {
            var parsed = CsvDocumentParser.Parse(source);
            viewModel?.CacheCsvPreview(source, parsed);
            ApplyParsedDocument(source, viewModel, parsed);
            return;
        }

        _needsRender = true;
        _limitMessage.IsVisible = true;
        _limitMessage.Text = "CSVを解析しています。完了するまで表の操作はできません。";
        var cancellation = new CancellationTokenSource();
        _renderCancellation = cancellation;
        _renderTask = ParseInBackgroundAsync(source, viewModel, cancellation);
    }

    private async Task ParseInBackgroundAsync(
        string source,
        DocumentViewModel? viewModel,
        CancellationTokenSource cancellation)
    {
        try
        {
            var parsed = await Task.Run(
                () => CsvDocumentParser.Parse(source, cancellation.Token),
                cancellation.Token);
            if (cancellation.IsCancellationRequested
                || !_isAttached
                || !IsVisible
                || !ReferenceEquals(Document, viewModel)
                || !string.Equals(Csv, source, StringComparison.Ordinal)
                || viewModel is not null && !string.Equals(viewModel.Text, source, StringComparison.Ordinal))
            {
                return;
            }

            viewModel?.CacheCsvPreview(source, parsed);
            ApplyParsedDocument(source, viewModel, parsed);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLogger.For<CsvPreview>().Error("CSVプレビューの解析に失敗しました。", ex);
            if (!cancellation.IsCancellationRequested
                && IsVisible
                && ReferenceEquals(Document, viewModel)
                && string.Equals(Csv, source, StringComparison.Ordinal))
            {
                _limitMessage.IsVisible = true;
                _limitMessage.Text = "CSVを解析できませんでした。「編集へ戻る」で内容を確認してください。";
            }
        }
        finally
        {
            if (ReferenceEquals(_renderCancellation, cancellation))
            {
                _renderCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void ApplyParsedDocument(
        string source,
        DocumentViewModel? viewModel,
        CsvDocument document)
    {
        if (!_isAttached || !IsVisible
            || !ReferenceEquals(Document, viewModel)
            || !string.Equals(Csv ?? string.Empty, source, StringComparison.Ordinal))
        {
            return;
        }

        _needsRender = false;
        _renderedDocument = viewModel;
        var state = viewModel is not null ? GetViewState(viewModel) : new CsvViewState();
        var preserveEditState = ReferenceEquals(viewModel, _preserveStateDocument);
        var offset = _preserveOffsetForEdit && preserveEditState ? _scrollViewer.Offset : default;
        var preserveSelection = _preserveSelectionForEdit && preserveEditState;
        if (!preserveSelection && !string.Equals(state.SourceSnapshot, source, StringComparison.Ordinal))
        {
            state.ClearSelection();
        }

        state.SourceSnapshot = source;
        state.ClampSelection(document.TotalRowCount, document.TotalColumnCount);
        _preserveOffsetForEdit = false;
        _preserveSelectionForEdit = false;
        _preserveStateDocument = null;
        _surface.SetDocument(document, state);
        _scrollViewer.Offset = offset;
        _scrollViewer.IsVisible = document.Rows.Count > 0;
        _emptyPanel.IsVisible = document.Rows.Count == 0;
        UpdateSelectionHint();
        if (_postRenderHint is { } hint)
        {
            if (ReferenceEquals(hint.Document, viewModel)
                && string.Equals(hint.Source, source, StringComparison.Ordinal))
            {
                _editHint.Text = hint.Message;
            }
            _postRenderHint = null;
        }
        UpdateLimitMessage(document);
    }

    private void UpdateLimitMessage(CsvDocument document)
    {
        var messages = new List<string>(2);
        if (document.HasUnterminatedQuotedField)
        {
            messages.Add("CSVの引用符が閉じていません。安全のため表からの編集・コピーはできません。「編集へ戻る」で引用符を修正してください。");
        }
        if (document.IsTruncated)
        {
            messages.Add($"プレビューは先頭 {document.Rows.Count:N0} 行 × {document.DisplayedColumnCount:N0} 列まで表示しています" +
                         $"（全 {document.TotalRowCount:N0} 行 × {document.TotalColumnCount:N0} 列）。表示上限外を確認・修正するには「編集へ戻る」を使ってください。");
        }
        _limitMessage.IsVisible = messages.Count > 0;
        _limitMessage.Text = messages.Count > 0 ? string.Join(Environment.NewLine, messages) : null;
    }

    private void CancelBackgroundRender(bool markDirty)
    {
        if (_renderCancellation is not { } cancellation)
        {
            return;
        }
        _renderCancellation = null;
        cancellation.Cancel();
        if (markDirty)
        {
            _needsRender = true;
        }
    }

    private CsvViewState GetViewState(DocumentViewModel document)
        => _viewStates.GetValue(document, static _ => new CsvViewState());

    private enum CsvSelectionKind { None, Rows, Columns, Cells }
    private enum CsvSelectionAction
    {
        InsertBefore,
        InsertAfter,
        Delete,
        Copy,
        Cut,
        Paste,
        Clear,
        FillDown,
        FillRight,
    }

    private sealed class CsvTableOperationState(
        DocumentViewModel? document,
        bool isClipboardOperation) : IDisposable
    {
        public DocumentViewModel? Document { get; } = document;
        public bool IsClipboardOperation { get; } = isClipboardOperation;
        public CancellationTokenSource Cancellation { get; } = new();

        public void Dispose() => Cancellation.Dispose();
    }

    private sealed class CsvViewState
    {
        private const double DefaultColumnWidth = 140;
        private const double DefaultRowHeight = 28;
        private readonly Dictionary<int, double> _columnWidths = [];
        private readonly Dictionary<int, double> _rowHeights = [];

        public string SourceSnapshot { get; set; } = string.Empty;
        public CsvSelectionKind SelectionKind { get; private set; }
        public int SelectionAnchor { get; private set; }
        public int SelectionStart { get; private set; }
        public int SelectionEnd { get; private set; }
        public int SelectionCount => SelectionEnd - SelectionStart + 1;
        public int CellAnchorRow { get; private set; }
        public int CellAnchorColumn { get; private set; }
        public int ActiveRow { get; private set; }
        public int ActiveColumn { get; private set; }
        public int CellStartRow => Math.Min(CellAnchorRow, ActiveRow);
        public int CellEndRow => Math.Max(CellAnchorRow, ActiveRow);
        public int CellStartColumn => Math.Min(CellAnchorColumn, ActiveColumn);
        public int CellEndColumn => Math.Max(CellAnchorColumn, ActiveColumn);
        public CsvCellRange CellRange => new(
            CellStartRow,
            CellStartColumn,
            CellEndRow - CellStartRow + 1,
            CellEndColumn - CellStartColumn + 1);
        public double GetColumnWidth(int index) => _columnWidths.TryGetValue(index, out var width) ? width : DefaultColumnWidth;
        public double GetRowHeight(int index) => _rowHeights.TryGetValue(index, out var height) ? height : DefaultRowHeight;
        public void SetColumnWidth(int index, double width) => _columnWidths[index] = width;
        public void SetRowHeight(int index, double height) => _rowHeights[index] = height;

        public void Select(CsvSelectionKind kind, int index, bool extend)
        {
            if (!extend || SelectionKind != kind)
            {
                SelectionKind = kind;
                SelectionAnchor = index;
                SelectionStart = index;
                SelectionEnd = index;
                return;
            }
            SelectionStart = Math.Min(SelectionAnchor, index);
            SelectionEnd = Math.Max(SelectionAnchor, index);
        }

        public void SetSelection(CsvSelectionKind kind, int start, int end)
        {
            SelectionKind = kind;
            SelectionAnchor = start;
            SelectionStart = Math.Min(start, end);
            SelectionEnd = Math.Max(start, end);
        }

        public void SelectCell(int row, int column, bool extend)
        {
            if (!extend || SelectionKind != CsvSelectionKind.Cells)
            {
                SelectionKind = CsvSelectionKind.Cells;
                CellAnchorRow = row;
                CellAnchorColumn = column;
            }
            ActiveRow = row;
            ActiveColumn = column;
        }

        public bool TryGetActiveCell(out int row, out int column)
        {
            row = ActiveRow;
            column = ActiveColumn;
            return SelectionKind == CsvSelectionKind.Cells;
        }

        public SelectionIdentity GetSelectionIdentity()
            => new(SelectionKind, SelectionAnchor, SelectionStart, SelectionEnd,
                CellAnchorRow, CellAnchorColumn, ActiveRow, ActiveColumn);

        public bool MatchesSelection(SelectionIdentity identity)
            => GetSelectionIdentity() == identity;

        public void ClearSelection()
        {
            SelectionKind = CsvSelectionKind.None;
            SelectionAnchor = 0;
            SelectionStart = 0;
            SelectionEnd = 0;
            CellAnchorRow = 0;
            CellAnchorColumn = 0;
            ActiveRow = 0;
            ActiveColumn = 0;
        }

        public void ClampSelection(int totalRows, int totalColumns)
        {
            if (SelectionKind == CsvSelectionKind.Cells)
            {
                if (totalRows <= 0 || totalColumns <= 0)
                {
                    ClearSelection();
                    return;
                }
                CellAnchorRow = Math.Clamp(CellAnchorRow, 0, totalRows - 1);
                ActiveRow = Math.Clamp(ActiveRow, 0, totalRows - 1);
                CellAnchorColumn = Math.Clamp(CellAnchorColumn, 0, totalColumns - 1);
                ActiveColumn = Math.Clamp(ActiveColumn, 0, totalColumns - 1);
                return;
            }

            var total = SelectionKind == CsvSelectionKind.Rows ? totalRows : totalColumns;
            if (SelectionKind == CsvSelectionKind.None || total <= 0 || SelectionStart >= total)
            {
                ClearSelection();
                return;
            }
            SelectionAnchor = Math.Clamp(SelectionAnchor, 0, total - 1);
            SelectionEnd = Math.Min(SelectionEnd, total - 1);
        }

        public readonly record struct SelectionIdentity(
            CsvSelectionKind Kind,
            int HeaderAnchor,
            int HeaderStart,
            int HeaderEnd,
            int CellAnchorRow,
            int CellAnchorColumn,
            int ActiveRow,
            int ActiveColumn);
    }

    /// <summary>罫線は直接描画し、セル文字と操作用見出しだけを画面内へ生成して巨大 CSV でも負荷を一定に保つ。</summary>
    private sealed class CsvGridSurface : UserControl
    {
        private const double RowHeaderWidth = 54;
        private const double HeaderHeight = 32;
        private const double MinimumColumnWidth = 60;
        private const double MaximumColumnWidth = 600;
        private const double MinimumRowHeight = 22;
        private const double MaximumRowHeight = 200;
        private const double ResizeGrip = 5;
        private static readonly Typeface CellTypeface = new("Segoe UI");
        private static readonly Cursor ColumnResizeCursor = new(StandardCursorType.SizeWestEast);
        private static readonly Cursor RowResizeCursor = new(StandardCursorType.SizeNorthSouth);
        private readonly Canvas _cells = new();
        private readonly Canvas _headers = new();
        private readonly DrawingSurface _drawingSurface;
        private CsvDocument _document = new([], 0, 0);
        private double[] _columnOffsets = [0];
        private double[] _rowOffsets = [0];
        private ResizeState? _resize;
        private bool _selectingCells;

        public CsvGridSurface()
        {
            Focusable = true;
            AddHandler(PointerMovedEvent, (_, args) => SurfacePointerMoved(args), RoutingStrategies.Tunnel);
            AddHandler(PointerReleasedEvent, (_, args) => SurfacePointerReleased(args), RoutingStrategies.Tunnel);
            PointerCaptureLost += (_, _) =>
            {
                _resize = null;
                _selectingCells = false;
            };
            _drawingSurface = new DrawingSurface(this);
            var layers = new Grid();
            layers.Children.Add(_drawingSurface);
            layers.Children.Add(_cells);
            layers.Children.Add(_headers);
            Content = layers;
        }

        public ScrollViewer? ScrollOwner { get; set; }
        public Action<int, int>? CellEditRequested { get; set; }
        public Action? SelectionChanged { get; set; }
        public Action<CsvSelectionAction>? SelectionActionRequested { get; set; }
        public CsvDocument Document => _document;
        public CsvViewState ViewState { get; private set; } = new();

        public void SetDocument(CsvDocument document, CsvViewState viewState)
        {
            _document = document;
            ViewState = viewState;
            RebuildOffsets();
            RefreshVisibleCells();
            InvalidateGrid();
        }

        public void InvalidateGrid() => _drawingSurface.InvalidateVisual();

        public void ScrollCellIntoView(int row, int column)
        {
            if (ScrollOwner is not { } scrollOwner
                || row < 0 || row >= _document.Rows.Count
                || column < 0 || column >= _document.DisplayedColumnCount)
            {
                return;
            }

            var offset = scrollOwner.Offset;
            var viewport = GetViewport();
            var left = _columnOffsets[column];
            var right = _columnOffsets[column + 1];
            var top = _rowOffsets[row];
            var bottom = _rowOffsets[row + 1];
            var x = offset.X;
            var y = offset.Y;
            if (left < x) x = left;
            else if (right > x + Math.Max(1, viewport.Width - RowHeaderWidth))
                x = right - Math.Max(1, viewport.Width - RowHeaderWidth);
            if (top < y) y = top;
            else if (bottom > y + Math.Max(1, viewport.Height - HeaderHeight))
                y = bottom - Math.Max(1, viewport.Height - HeaderHeight);
            scrollOwner.Offset = new Vector(Math.Max(0, x), Math.Max(0, y));
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    RefreshVisibleCells();
                    InvalidateGrid();
                }, DispatcherPriority.Background);
            }
        }

        public void RefreshVisibleCells()
        {
            _cells.Children.Clear();
            _headers.Children.Clear();
            if (_document.Rows.Count == 0 || _document.DisplayedColumnCount == 0)
            {
                return;
            }

            var (firstColumn, lastColumn, firstRow, lastRow) = GetVisibleRange();
            var offset = ScrollOwner?.Offset ?? default;
            var viewport = GetViewport();
            _cells.Clip = new RectangleGeometry(new Rect(
                offset.X + RowHeaderWidth,
                offset.Y + HeaderHeight,
                Math.Max(0, viewport.Width - RowHeaderWidth),
                Math.Max(0, viewport.Height - HeaderHeight)));
            _headers.Clip = new RectangleGeometry(new Rect(offset.X, offset.Y, viewport.Width, viewport.Height));
            var foreground = ResourceBrush("TextPrimary", Brushes.Black);
            for (var rowIndex = firstRow; rowIndex < lastRow; rowIndex++)
            {
                var row = _document.Rows[rowIndex];
                for (var columnIndex = firstColumn; columnIndex < lastColumn; columnIndex++)
                {
                    var value = columnIndex < row.Count ? row[columnIndex] : string.Empty;
                    var text = new SelectableTextBlock
                    {
                        Text = NormalizeCellText(PreviewText(value, 512)),
                        FontFamily = CellTypeface.FontFamily,
                        FontSize = 13,
                        TextWrapping = TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = foreground,
                        Width = Math.Max(1, GetColumnWidth(columnIndex) - 12),
                        Height = Math.Max(1, GetRowHeight(rowIndex) - 4),
                    };
                    ToolTip.SetTip(text, PreviewText(value, 4096));
                    var cell = new Border
                    {
                        Background = Brushes.Transparent, Focusable = true,
                        Width = GetColumnWidth(columnIndex), Height = GetRowHeight(rowIndex),
                        Padding = new Thickness(6, 4, 6, 0), Child = text,
                        ContextMenu = CreateCellMenu(),
                    };
                    var selectedRow = rowIndex;
                    var selectedColumn = columnIndex;
                    AutomationProperties.SetName(cell, $"{GetColumnName(columnIndex)}{rowIndex + 1}");
                    cell.AddHandler(PointerPressedEvent,
                        (_, args) => CellPointerPressed(cell, selectedRow, selectedColumn, args),
                        RoutingStrategies.Tunnel);
                    cell.KeyDown += (_, args) =>
                    {
                        if (args.Key is not (Key.Enter or Key.F2))
                        {
                            return;
                        }
                        ViewState.SelectCell(selectedRow, selectedColumn, false);
                        SelectionChanged?.Invoke();
                        InvalidateGrid();
                        CellEditRequested?.Invoke(selectedRow, selectedColumn);
                        args.Handled = true;
                    };
                    Canvas.SetLeft(cell, RowHeaderWidth + _columnOffsets[columnIndex]);
                    Canvas.SetTop(cell, HeaderHeight + _rowOffsets[rowIndex]);
                    _cells.Children.Add(cell);
                }
            }

            for (var columnIndex = firstColumn; columnIndex < lastColumn; columnIndex++)
            {
                AddHeader(CsvSelectionKind.Columns, columnIndex, new Rect(
                    RowHeaderWidth + _columnOffsets[columnIndex], offset.Y,
                    GetColumnWidth(columnIndex), HeaderHeight));
            }
            for (var rowIndex = firstRow; rowIndex < lastRow; rowIndex++)
            {
                AddHeader(CsvSelectionKind.Rows, rowIndex, new Rect(
                    offset.X, HeaderHeight + _rowOffsets[rowIndex],
                    RowHeaderWidth, GetRowHeight(rowIndex)));
            }
        }

        private void AddHeader(CsvSelectionKind kind, int index, Rect bounds)
        {
            var header = new Border
            {
                Background = Brushes.Transparent,
                Focusable = true,
                Width = bounds.Width,
                Height = bounds.Height,
                ContextMenu = CreateHeaderMenu(),
            };
            AutomationProperties.SetName(header, kind == CsvSelectionKind.Columns
                ? $"CsvColumnHeader:{GetColumnName(index)}"
                : $"CsvRowHeader:{index + 1}");
            header.AddHandler(PointerPressedEvent, (_, args) => HeaderPointerPressed(header, kind, index, args), RoutingStrategies.Tunnel);
            header.AddHandler(PointerMovedEvent, (_, args) => HeaderPointerMoved(header, kind, args), RoutingStrategies.Tunnel);
            header.AddHandler(PointerReleasedEvent, (_, args) => HeaderPointerReleased(args), RoutingStrategies.Tunnel);
            Canvas.SetLeft(header, bounds.X);
            Canvas.SetTop(header, bounds.Y);
            _headers.Children.Add(header);
        }

        private ContextMenu CreateHeaderMenu()
        {
            var insertBefore = CreateMenuItem("CsvInsertBeforeMenuItem", "前に追加", CsvSelectionAction.InsertBefore);
            var insertAfter = CreateMenuItem("CsvInsertAfterMenuItem", "後に追加", CsvSelectionAction.InsertAfter);
            var delete = CreateMenuItem("CsvDeleteSelectionMenuItem", "選択した行・列を削除", CsvSelectionAction.Delete);
            var copy = CreateMenuItem("CsvCopySelectionMenuItem", "選択した行・列をコピー", CsvSelectionAction.Copy);
            var cut = CreateMenuItem("CsvCutSelectionMenuItem", "選択した行・列を切り取り", CsvSelectionAction.Cut);
            var clear = CreateMenuItem("CsvClearSelectionContentsMenuItem", "選択範囲の値を消去", CsvSelectionAction.Clear);
            var fillDown = CreateMenuItem("CsvFillSelectionDownMenuItem", "下方向へフィル", CsvSelectionAction.FillDown);
            var fillRight = CreateMenuItem("CsvFillSelectionRightMenuItem", "右方向へフィル", CsvSelectionAction.FillRight);
            return new ContextMenu
            {
                Items =
                {
                    insertBefore, insertAfter, new Separator(), delete,
                    new Separator(), copy, cut, clear,
                    new Separator(), fillDown, fillRight,
                },
            };
        }

        private ContextMenu CreateCellMenu()
        {
            var copy = CreateMenuItem("CsvCopyCellsMenuItem", "コピー", CsvSelectionAction.Copy);
            var cut = CreateMenuItem("CsvCutCellsMenuItem", "切り取り", CsvSelectionAction.Cut);
            var paste = CreateMenuItem("CsvPasteCellsMenuItem", "貼り付け", CsvSelectionAction.Paste);
            var clear = CreateMenuItem("CsvClearCellsMenuItem", "値を消去", CsvSelectionAction.Clear);
            var fillDown = CreateMenuItem("CsvFillDownMenuItem", "下方向へフィル", CsvSelectionAction.FillDown);
            var fillRight = CreateMenuItem("CsvFillRightMenuItem", "右方向へフィル", CsvSelectionAction.FillRight);
            return new ContextMenu
            {
                Items = { copy, cut, paste, new Separator(), clear, new Separator(), fillDown, fillRight },
            };
        }

        private void CellPointerPressed(Border cell, int row, int column, PointerPressedEventArgs args)
        {
            var point = args.GetCurrentPoint(cell);
            if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
            {
                return;
            }

            var isAlreadySelected = ViewState.SelectionKind == CsvSelectionKind.Cells
                && row >= ViewState.CellStartRow && row <= ViewState.CellEndRow
                && column >= ViewState.CellStartColumn && column <= ViewState.CellEndColumn;
            if (!point.Properties.IsRightButtonPressed || !isAlreadySelected)
            {
                ViewState.SelectCell(row, column, args.KeyModifiers.HasFlag(KeyModifiers.Shift));
            }
            Focus();
            SelectionChanged?.Invoke();
            InvalidateGrid();

            if (point.Properties.IsLeftButtonPressed)
            {
                if (args.ClickCount == 2)
                {
                    CellEditRequested?.Invoke(row, column);
                    args.Handled = true;
                    return;
                }
                // Alt+ドラッグはセル内文字の選択へ譲り、通常のドラッグは矩形選択に使う。
                if (args.KeyModifiers.HasFlag(KeyModifiers.Alt))
                {
                    return;
                }
                _selectingCells = true;
                args.Pointer.Capture(this);
                args.Handled = true;
            }
        }

        private MenuItem CreateMenuItem(string name, string header, CsvSelectionAction action)
        {
            var item = new MenuItem { Name = name, Header = header };
            item.Click += (_, _) => SelectionActionRequested?.Invoke(action);
            return item;
        }

        private void HeaderPointerPressed(Border header, CsvSelectionKind kind, int index, PointerPressedEventArgs args)
        {
            var point = args.GetCurrentPoint(header);
            if (point.Properties.IsLeftButtonPressed && IsResizeGrip(kind, header, point.Position))
            {
                _resize = new ResizeState(kind, index, args.GetPosition(this),
                    kind == CsvSelectionKind.Columns ? GetColumnWidth(index) : GetRowHeight(index));
                args.Pointer.Capture(this);
                args.Handled = true;
                return;
            }
            if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
            {
                return;
            }

            // 選択範囲内の右クリックは複数選択を維持して、その範囲のメニューを開く。
            if (!point.Properties.IsRightButtonPressed || !IsSelected(kind, index))
            {
                ViewState.Select(kind, index, args.KeyModifiers.HasFlag(KeyModifiers.Shift));
            }
            Focus();
            SelectionChanged?.Invoke();
            InvalidateGrid();
        }

        private void HeaderPointerMoved(Border header, CsvSelectionKind kind, PointerEventArgs args)
        {
            if (_resize is null)
            {
                header.Cursor = IsResizeGrip(kind, header, args.GetPosition(header))
                    ? kind == CsvSelectionKind.Columns ? ColumnResizeCursor : RowResizeCursor
                    : Cursor.Default;
            }
        }

        private void SurfacePointerMoved(PointerEventArgs args)
        {
            if (_resize is not null)
            {
                ResizePointerMoved(args);
                return;
            }
            if (!_selectingCells || _document.Rows.Count == 0 || _document.DisplayedColumnCount == 0)
            {
                return;
            }

            var position = args.GetPosition(this);
            var row = FindIndex(_rowOffsets, Math.Max(0, position.Y - HeaderHeight));
            var column = FindIndex(_columnOffsets, Math.Max(0, position.X - RowHeaderWidth));
            row = Math.Clamp(row, 0, _document.Rows.Count - 1);
            column = Math.Clamp(column, 0, _document.DisplayedColumnCount - 1);
            if (ViewState.ActiveRow == row && ViewState.ActiveColumn == column)
            {
                return;
            }
            ViewState.SelectCell(row, column, true);
            SelectionChanged?.Invoke();
            InvalidateGrid();
            args.Handled = true;
        }

        private void SurfacePointerReleased(PointerReleasedEventArgs args)
        {
            if (_resize is not null)
            {
                HeaderPointerReleased(args);
                return;
            }
            if (!_selectingCells)
            {
                return;
            }
            _selectingCells = false;
            args.Pointer.Capture(null);
            args.Handled = true;
        }

        private void ResizePointerMoved(PointerEventArgs args)
        {
            if (_resize is not { } resize)
            {
                return;
            }

            var position = args.GetPosition(this);
            var delta = resize.Kind == CsvSelectionKind.Columns ? position.X - resize.Start.X : position.Y - resize.Start.Y;
            if (resize.Kind == CsvSelectionKind.Columns)
            {
                ViewState.SetColumnWidth(resize.Index, Math.Clamp(resize.InitialSize + delta, MinimumColumnWidth, MaximumColumnWidth));
            }
            else
            {
                ViewState.SetRowHeight(resize.Index, Math.Clamp(resize.InitialSize + delta, MinimumRowHeight, MaximumRowHeight));
            }
            RebuildOffsets();
            RefreshVisibleCells();
            InvalidateGrid();
            args.Handled = true;
        }

        private void HeaderPointerReleased(PointerReleasedEventArgs args)
        {
            if (_resize is null)
            {
                return;
            }
            _resize = null;
            args.Pointer.Capture(null);
            args.Handled = true;
        }

        private static bool IsResizeGrip(CsvSelectionKind kind, Border header, Point position)
            => kind == CsvSelectionKind.Columns
                ? position.X >= header.Bounds.Width - ResizeGrip
                : position.Y >= header.Bounds.Height - ResizeGrip;

        private void RebuildOffsets()
        {
            _columnOffsets = new double[_document.DisplayedColumnCount + 1];
            for (var index = 0; index < _document.DisplayedColumnCount; index++)
            {
                _columnOffsets[index + 1] = _columnOffsets[index] + GetColumnWidth(index);
            }
            _rowOffsets = new double[_document.Rows.Count + 1];
            for (var index = 0; index < _document.Rows.Count; index++)
            {
                _rowOffsets[index + 1] = _rowOffsets[index] + GetRowHeight(index);
            }
            Width = RowHeaderWidth + Math.Max(1, _columnOffsets[^1]);
            Height = HeaderHeight + Math.Max(1, _rowOffsets[^1]);
            InvalidateMeasure();
        }

        private double GetColumnWidth(int index) => ViewState.GetColumnWidth(index);
        private double GetRowHeight(int index) => ViewState.GetRowHeight(index);

        private void RenderGrid(DrawingContext context)
        {
            if (_document.Rows.Count == 0 || _document.DisplayedColumnCount == 0)
            {
                return;
            }
            var offset = ScrollOwner?.Offset ?? default;
            var viewport = GetViewport();
            var secondary = ResourceBrush("TextSecondary", Brushes.DimGray);
            var background = ResourceBrush("EditorBg", Brushes.White);
            var headerBackground = ResourceBrush("SettingsPageBg", Brushes.LightGray);
            var divider = ResourceBrush("Divider", Brushes.Gray);
            var selection = ResourceColorBrush("TextControlSelectionHighlightColor", Color.FromArgb(72, 0, 120, 215));
            var gridPen = new Pen(divider, 1);
            context.FillRectangle(background, new Rect(new Point(offset.X, offset.Y), viewport));
            var (firstColumn, lastColumn, firstRow, lastRow) = GetVisibleRange();

            for (var rowIndex = firstRow; rowIndex < lastRow; rowIndex++)
            {
                var y = HeaderHeight + _rowOffsets[rowIndex];
                for (var columnIndex = firstColumn; columnIndex < lastColumn; columnIndex++)
                {
                    var bounds = new Rect(RowHeaderWidth + _columnOffsets[columnIndex], y,
                        GetColumnWidth(columnIndex), GetRowHeight(rowIndex));
                    if (IsSelected(CsvSelectionKind.Rows, rowIndex)
                        || IsSelected(CsvSelectionKind.Columns, columnIndex)
                        || IsCellSelected(rowIndex, columnIndex))
                    {
                        context.FillRectangle(selection, bounds);
                    }
                    context.DrawRectangle(null, gridPen, bounds);
                    if (ViewState.SelectionKind == CsvSelectionKind.Cells
                        && ViewState.ActiveRow == rowIndex && ViewState.ActiveColumn == columnIndex)
                    {
                        context.DrawRectangle(new Pen(ResourceBrush("AccentBrush", Brushes.DodgerBlue), 2), bounds.Deflate(1));
                    }
                }
            }

            for (var columnIndex = firstColumn; columnIndex < lastColumn; columnIndex++)
            {
                var bounds = new Rect(RowHeaderWidth + _columnOffsets[columnIndex], offset.Y,
                    GetColumnWidth(columnIndex), HeaderHeight);
                context.FillRectangle(IsSelected(CsvSelectionKind.Columns, columnIndex) ? selection : headerBackground, bounds);
                context.DrawRectangle(null, gridPen, bounds);
                DrawText(context, GetColumnName(columnIndex), secondary, bounds, TextAlignment.Center);
            }
            for (var rowIndex = firstRow; rowIndex < lastRow; rowIndex++)
            {
                var bounds = new Rect(offset.X, HeaderHeight + _rowOffsets[rowIndex],
                    RowHeaderWidth, GetRowHeight(rowIndex));
                context.FillRectangle(IsSelected(CsvSelectionKind.Rows, rowIndex) ? selection : headerBackground, bounds);
                context.DrawRectangle(null, gridPen, bounds);
                DrawText(context, (rowIndex + 1).ToString(), secondary, bounds, TextAlignment.Center);
            }
            var corner = new Rect(offset.X, offset.Y, RowHeaderWidth, HeaderHeight);
            context.FillRectangle(headerBackground, corner);
            context.DrawRectangle(null, gridPen, corner);
        }

        private bool IsSelected(CsvSelectionKind kind, int index)
            => ViewState.SelectionKind == kind && index >= ViewState.SelectionStart && index <= ViewState.SelectionEnd;
        private bool IsCellSelected(int row, int column)
            => ViewState.SelectionKind == CsvSelectionKind.Cells
                && row >= ViewState.CellStartRow && row <= ViewState.CellEndRow
                && column >= ViewState.CellStartColumn && column <= ViewState.CellEndColumn;
        private IBrush ResourceBrush(string key, IBrush fallback)
            => _drawingSurface.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

        private IBrush ResourceColorBrush(string key, Color fallback)
        {
            if (_drawingSurface.TryFindResource(key, ActualThemeVariant, out var value))
            {
                if (value is IBrush brush) return brush;
                if (value is Color color) return new SolidColorBrush(color, 0.35);
            }
            return new SolidColorBrush(fallback);
        }

        private (int FirstColumn, int LastColumn, int FirstRow, int LastRow) GetVisibleRange()
        {
            var offset = ScrollOwner?.Offset ?? default;
            var viewport = GetViewport();
            var firstColumn = FindIndex(_columnOffsets, Math.Max(0, offset.X - RowHeaderWidth));
            var lastColumn = Math.Min(_document.DisplayedColumnCount,
                FindIndex(_columnOffsets, Math.Max(0, offset.X + viewport.Width - RowHeaderWidth)) + 1);
            var firstRow = FindIndex(_rowOffsets, Math.Max(0, offset.Y - HeaderHeight));
            var lastRow = Math.Min(_document.Rows.Count,
                FindIndex(_rowOffsets, Math.Max(0, offset.Y + viewport.Height - HeaderHeight)) + 1);
            return (firstColumn, Math.Max(firstColumn + 1, lastColumn), firstRow, Math.Max(firstRow + 1, lastRow));
        }

        private static int FindIndex(double[] offsets, double position)
        {
            if (offsets.Length <= 1) return 0;
            var result = Array.BinarySearch(offsets, position);
            if (result >= 0) return Math.Min(result, offsets.Length - 2);
            return Math.Clamp(~result - 1, 0, offsets.Length - 2);
        }

        private Size GetViewport()
        {
            var viewport = ScrollOwner?.Viewport ?? default;
            return viewport.Width > 0 && viewport.Height > 0 ? viewport : new Size(1_200, 800);
        }

        private static void DrawText(DrawingContext context, string text, IBrush brush, Rect bounds, TextAlignment alignment)
        {
            using var layout = new TextLayout(text, CellTypeface, 13, brush,
                textAlignment: alignment, textWrapping: TextWrapping.NoWrap,
                textTrimming: TextTrimming.CharacterEllipsis,
                maxWidth: Math.Max(1, bounds.Width - 12), maxHeight: bounds.Height);
            layout.Draw(context, new Point(bounds.X + 6, bounds.Y + Math.Max(0, (bounds.Height - layout.Height) / 2)));
        }

        private static string NormalizeCellText(string text)
            => text.Replace("\r\n", " ↵ ", StringComparison.Ordinal)
                .Replace("\r", " ↵ ", StringComparison.Ordinal)
                .Replace("\n", " ↵ ", StringComparison.Ordinal);

        private static string PreviewText(string text, int limit)
        {
            if (text.Length <= limit) return text;
            if (char.IsHighSurrogate(text[limit - 1])) limit--;
            return text[..limit] + "…（続きは編集画面で確認）";
        }

        internal static string GetColumnName(int zeroBasedIndex)
        {
            var name = string.Empty;
            for (var value = zeroBasedIndex + 1; value > 0; value = (value - 1) / 26)
            {
                name = (char)('A' + (value - 1) % 26) + name;
            }
            return name;
        }

        private sealed class DrawingSurface(CsvGridSurface owner) : Control
        {
            public override void Render(DrawingContext context)
            {
                base.Render(context);
                owner.RenderGrid(context);
            }
        }

        private sealed record ResizeState(CsvSelectionKind Kind, int Index, Point Start, double InitialSize);
    }
}
