using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Fumilume.ViewModels;

namespace Fumilume.Views;

public sealed partial class MainWindow
{
    private ColumnDefinition? _folderColumn;
    private ColumnDefinition? _folderSplitterColumn;
    private ColumnDefinition? _contentColumn;
    private bool _openingFolderNode;
    private bool _applyingExplorerLayout;

    private void InitializeFolderExplorer(Grid workspaceGrid)
    {
        _folderColumn = workspaceGrid.ColumnDefinitions[0];
        _folderSplitterColumn = workspaceGrid.ColumnDefinitions[1];
        _contentColumn = workspaceGrid.ColumnDefinitions[4];
        ApplyFolderExplorerWidth();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == BoundsProperty) ApplyFolderExplorerWidth();
        };
        _sidePanelColumn.PropertyChanged += (_, args) =>
        {
            if (args.Property == ColumnDefinition.WidthProperty) ApplyFolderExplorerWidth();
        };
        this.FindControl<TreeView>("FolderTree")?.AddHandler(KeyDownEvent,
            FolderTree_KeyDown, RoutingStrategies.Tunnel);
        if (this.FindControl<Grid>("FolderExplorerRoot") is { } panel)
        {
            panel.PropertyChanged += (_, args) =>
            {
                if (args.Property == BoundsProperty && _viewModel.IsFolderTreeVisible
                    && panel.Bounds.Width >= 140)
                {
                    _viewModel.FolderTreeWidth = Math.Clamp(panel.Bounds.Width + 6, 140, 400);
                }
            };
        }
    }

    private void ApplyFolderExplorerWidth()
    {
        if (_folderColumn is null || _folderSplitterColumn is null || _applyingExplorerLayout) return;
        _applyingExplorerLayout = true;
        try
        {
            var visible = _viewModel.IsFolderTreeVisible;
            var windowWidth = Bounds.Width > 0 ? Bounds.Width : Width;
            var editorMinimum = visible ? 320 : 480;
            var reserved = editorMinimum + (visible ? 148 : 4);
            _sidePanelColumn.MaxWidth = Math.Clamp(windowWidth - reserved, 120, 480);
            var tabWidth = Math.Clamp(_sidePanelColumn.Width.Value, 120, _sidePanelColumn.MaxWidth);
            var folderMaximum = Math.Clamp(windowWidth - tabWidth - editorMinimum - 8, 140, 400);
            _folderColumn.MinWidth = visible ? 140 : 0;
            _folderColumn.MaxWidth = visible ? folderMaximum : 0;
            _folderColumn.Width = new GridLength(visible
                ? Math.Clamp(_viewModel.FolderTreeWidth, 140, folderMaximum) : 0);
            _folderSplitterColumn.Width = new GridLength(visible ? 4 : 0);
            if (_contentColumn is not null) _contentColumn.MinWidth = editorMinimum;
        }
        finally { _applyingExplorerLayout = false; }
    }

    private async void FolderNode_PointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (sender is not Control { DataContext: FolderTreeNode node } control
            || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || node.IsPlaceholder) return;
        args.Handled = true;
        _viewModel.SelectedFolderNode = node;
        control.FindAncestorOfType<TreeViewItem>()?.Focus();
        if (node.IsDirectory && args.ClickCount > 1) return;
        await OpenFolderNodeFromViewAsync(node, args.ClickCount > 1);
    }

    private async void FolderTree_KeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || _viewModel.SelectedFolderNode is not { } node) return;
        args.Handled = true;
        await OpenFolderNodeFromViewAsync(node, permanent: true);
    }

    private async Task OpenFolderNodeFromViewAsync(FolderTreeNode node, bool permanent)
    {
        _openingFolderNode = true;
        try
        {
            await _viewModel.OpenFolderNodeAsync(node, permanent);
        }
        finally
        {
            _openingFolderNode = false;
        }
        if (permanent && !node.IsDirectory && _viewModel.IsDocumentSelected) _editor.TextArea.Focus();
    }
}
