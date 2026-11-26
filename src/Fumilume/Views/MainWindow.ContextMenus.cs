using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Views;

/// <summary>
/// 縦タブとフォルダツリーの右クリックメニュー。項目は <see cref="MainWindowViewModel"/> が組み立て、
/// ここは「どの行で起きたか」の特定と <see cref="MenuFlyout"/> への変換・表示だけを受け持つ。
///
/// <c>MenuFlyout.ItemsSource</c> のデータバインドは階層メニューでリフレクション束縛になり、
/// <c>PublishAot</c> のトリミングで落ちるので、エディタのメニューと同じく C# で組み立てる。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>直近に表示した右クリックメニュー（テストが項目を検査する口）。</summary>
    internal MenuFlyout? ShownContextFlyout { get; private set; }

    private IShellFileService CreatePlatformShellFiles()
    {
#if WINDOWS
        return new WindowsShellFileService(() => TryGetPlatformHandle()?.Handle ?? 0);
#else
        return new MacShellFileService(() => Clipboard, () => StorageProvider);
#endif
    }

    private void InitializeContextMenus()
    {
        if (this.FindControl<ListBox>("TabList") is { } tabList)
        {
            tabList.ContextRequested += TabList_ContextRequested;
        }

        if (this.FindControl<TreeView>("FolderTree") is { } folderTree)
        {
            folderTree.ContextRequested += FolderTree_ContextRequested;
        }
    }

    private async void TabList_ContextRequested(object? sender, ContextRequestedEventArgs args)
    {
        if (sender is not ListBox tabList)
        {
            return;
        }

        args.Handled = true;
        var keyboard = !args.TryGetPosition(tabList, out _);
        var tab = keyboard
            ? _viewModel.SelectedTab
            : (args.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as WorkspaceTabViewModel;
        var anchor = keyboard && tab is not null ? tabList.ContainerFromItem(tab) as Control ?? tabList : tabList;
        await ShowContextMenuAsync(
            anchor,
            keyboard,
            _ => tab is null ? _viewModel.BuildTabBarContextMenu() : _viewModel.BuildTabContextMenu(tab, canPaste: false),
            needsClipboard: false);
    }

    private async void FolderTree_ContextRequested(object? sender, ContextRequestedEventArgs args)
    {
        if (sender is not TreeView folderTree)
        {
            return;
        }

        args.Handled = true;
        var keyboard = !args.TryGetPosition(folderTree, out _);
        var node = keyboard
            ? _viewModel.SelectedFolderNode
            : (args.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as FolderTreeNode;
        if (node is { IsPlaceholder: false })
        {
            // 選ぶだけで、ファイルは開かない（開くのは左クリック・Enter だけ）。
            _viewModel.SelectedFolderNode = node;
        }

        var anchor = keyboard && node is not null ? folderTree.ContainerFromItem(node) as Control ?? folderTree : folderTree;
        await ShowContextMenuAsync(
            anchor, keyboard, canPaste => _viewModel.BuildFolderTreeContextMenu(node, canPaste), needsClipboard: true);
    }

    private async Task ShowContextMenuAsync(
        Control anchor, bool keyboard, Func<bool, IReadOnlyList<ContextMenuEntry>> build, bool needsClipboard)
    {
        try
        {
            var canPaste = needsClipboard && await HasFileClipboardSafeAsync();
            var flyout = CreateContextFlyout(build(canPaste));
            ShownContextFlyout = flyout;
            flyout.ShowAt(anchor, showAtPointer: !keyboard);
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"メニューを表示できません: {ex.Message}";
        }
    }

    private async Task<bool> HasFileClipboardSafeAsync()
    {
        try
        {
            return await _viewModel.ShellFiles.HasFileClipboardAsync();
        }
        catch (Exception)
        {
            // クリップボードを読めなくても、貼り付けが無効になるだけでメニューは出す。
            return false;
        }
    }

    private MenuFlyout CreateContextFlyout(IReadOnlyList<ContextMenuEntry> entries)
    {
        var flyout = new MenuFlyout();
        AddMenuItems(flyout.Items, entries);
        return flyout;
    }

    private void AddMenuItems(ItemCollection items, IReadOnlyList<ContextMenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                items.Add(new Separator());
                continue;
            }

            var item = new MenuItem
            {
                Header = entry.Header,
                IsEnabled = entry.IsEnabled,
                InputGesture = ParseGesture(PlatformGesture(entry.Gesture)),
            };
            ShowEnterKeyName(item);
            if (entry.ToolTip is not null)
            {
                ToolTip.SetTip(item, entry.ToolTip);
            }

            if (entry.HasChildren)
            {
                AddMenuItems(item.Items, entry.Children!);
            }
            else
            {
                var captured = entry;
                item.Click += async (_, _) => await _viewModel.RunMenuEntryAsync(captured);
            }

            items.Add(item);
        }
    }

    /// <summary>
    /// Enter と Return は同じキー値で、Avalonia の Windows 向け表示は Return になるため、表示だけを Enter にする。
    /// 割り当て（InputGesture）は変えず、macOS は既定の記号表示（↩）のままにする。
    /// </summary>
    private static void ShowEnterKeyName(MenuItem item)
    {
        if (UsesMacPlatformConventions || item.InputGesture is not { Key: Key.Enter } gesture)
        {
            return;
        }

        var text = gesture.ToString("p", null).Replace(nameof(Key.Return), nameof(Key.Enter), StringComparison.Ordinal);
        item.TemplateApplied += (_, args) =>
        {
            if (args.NameScope.Find<TextBlock>("PART_InputGestureText") is { } label)
            {
                label.Text = text;
            }
        };
    }

    /// <summary>ViewModel の Windows 表記（Ctrl+W）を、macOS では Command 表記へ読み替える。</summary>
    private static string? PlatformGesture(string? gesture)
        => UsesMacPlatformConventions ? gesture?.Replace("Ctrl+", "Cmd+", StringComparison.Ordinal) : gesture;
}
