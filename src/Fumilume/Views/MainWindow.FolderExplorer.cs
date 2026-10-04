using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fumilume.ViewModels;

namespace Fumilume.Views;

public sealed partial class MainWindow
{
    private RowDefinition? _tabsRow;
    private RowDefinition? _folderSplitterRow;
    private RowDefinition? _folderRow;
    private bool _openingFolderNode;
    private bool _applyingExplorerLayout;
    private bool _sidebarSplitCapturePending;

    private void InitializeFolderExplorer()
    {
        var sidebar = this.FindControl<Grid>("SidebarRoot")
            ?? throw new InvalidOperationException("サイドバーを初期化できませんでした。");
        _tabsRow = sidebar.RowDefinitions[0];
        _folderSplitterRow = sidebar.RowDefinitions[1];
        _folderRow = sidebar.RowDefinitions[2];
        ApplySidebarLayout();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == BoundsProperty) ApplySidebarLayout();
        };
        _tabsRow.PropertyChanged += OnSidebarRowChanged;
        _folderRow.PropertyChanged += OnSidebarRowChanged;
        this.FindControl<TreeView>("FolderTree")?.AddHandler(KeyDownEvent,
            FolderTree_KeyDown, RoutingStrategies.Tunnel);
    }

    private void OnSidebarRowChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property != RowDefinition.HeightProperty || _applyingExplorerLayout
            || !_viewModel.IsFolderTreeVisible || _sidebarSplitCapturePending) return;
        _sidebarSplitCapturePending = true;
        // Splitter が上下両方の行を更新し終えてから比率を控える。
        Dispatcher.UIThread.Post(() =>
        {
            _sidebarSplitCapturePending = false;
            CaptureSidebarSplitRatio();
        }, DispatcherPriority.Background);
    }

    private void CaptureSidebarSplitRatio()
    {
        if (!_viewModel.IsFolderTreeVisible || _tabsRow is null || _folderRow is null) return;
        var upper = _tabsRow.Height;
        var lower = _folderRow.Height;
        if (!upper.IsStar || !lower.IsStar || upper.Value + lower.Value <= 0) return;
        _viewModel.SidebarSplitRatio = Math.Clamp(upper.Value / (upper.Value + lower.Value), 0.1, 0.9);
    }

    private void ApplySidebarLayout()
    {
        if (_tabsRow is null || _folderSplitterRow is null || _folderRow is null
            || _applyingExplorerLayout) return;
        _applyingExplorerLayout = true;
        try
        {
            var visible = _viewModel.IsFolderTreeVisible;
            var windowWidth = Bounds.Width > 0 ? Bounds.Width : Width;
            // 本文列480px、幅分割線4px、右端余白6pxを残す。
            _sidePanelColumn.MaxWidth = Math.Clamp(windowWidth - 490, 120, 480);
            var ratio = double.IsFinite(_viewModel.SidebarSplitRatio)
                ? Math.Clamp(_viewModel.SidebarSplitRatio, 0.1, 0.9) : 0.5;
            _tabsRow.Height = new GridLength(visible ? ratio : 1, GridUnitType.Star);
            _folderSplitterRow.Height = new GridLength(visible ? 6 : 0);
            _folderRow.MinHeight = visible ? 140 : 0;
            _folderRow.Height = visible ? new GridLength(1 - ratio, GridUnitType.Star) : new GridLength(0);
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
