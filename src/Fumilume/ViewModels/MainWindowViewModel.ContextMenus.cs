using CommunityToolkit.Mvvm.Input;

namespace Fumilume.ViewModels;

/// <summary>
/// 右クリックメニュー（縦タブの行・タブ一覧の余白・フォルダツリーのノード・ツリーの余白）の項目を組み立てる。
/// View 非依存の <see cref="ContextMenuEntry"/> を返し、View が MenuFlyout へ変換する。
/// 項目の有効・無効と表示条件はここで決め、実際の操作は <c>TabActions</c> と <c>FileOperations</c> の窓口へ委ねる。
/// </summary>
public sealed partial class MainWindowViewModel
{
    private const string NoneHeader = "(なし)";

    /// <summary>メニューの文言を macOS 向け（Finder）にするか。テストが両方の文言を確かめられるよう差し替えられる。</summary>
    internal bool UsesMacMenuText { get; set; } = OperatingSystem.IsMacOS();

    private string RevealHeader => UsesMacMenuText ? "Finder で表示" : "エクスプローラーで表示";

    private string OpenFolderHeader => UsesMacMenuText ? "Finder で開く" : "エクスプローラーで開く";

    /// <summary>直近に閉じたタブを開き直す（Ctrl+Shift+T / ⇧⌘T のキー操作の受け口）。</summary>
    [RelayCommand]
    private Task ReopenLastClosedTabAsync() => ReopenClosedTabAsync();

    /// <summary>
    /// メニューの 1 項目を実行する。無効な項目と <c>Run</c> の無い項目は何もしない。
    /// 失敗は利用者へエラーとして示し、メニューの呼び出し元へ例外を戻さない。
    /// </summary>
    internal async Task RunMenuEntryAsync(ContextMenuEntry entry)
    {
        if (entry.Run is null || !entry.IsEnabled)
        {
            return;
        }

        try
        {
            await entry.Run();
        }
        catch (Exception ex)
        {
            StatusMessage = $"{entry.Header} を実行できませんでした";
            await _dialogs.ShowErrorAsync($"{entry.Header} を実行できません", ex.Message);
        }
    }

    // ===== タブ =====

    /// <summary>縦タブの行のメニュー。貼り付けはタブに無いので <paramref name="canPaste"/> は使わない。</summary>
    public IReadOnlyList<ContextMenuEntry> BuildTabContextMenu(WorkspaceTabViewModel tab, bool canPaste)
    {
        var menu = new List<ContextMenuEntry>();
        AddGroup(menu, Item("新しいタブを下に開く", () => NewTabBelow(tab)));
        if (tab.CanPin)
        {
            AddGroup(menu, Item(tab.PinTooltip, () => TogglePinTab(tab)));
        }

        AddGroup(
            menu,
            Item("タブを閉じる", () => CloseTabCoreAsync(tab), gesture: "Ctrl+W"),
            Item("下側のタブを閉じる", () => CloseTabsBelowAsync(tab), CanCloseTabsBelow(tab)),
            Item("他のタブを閉じる", () => CloseOtherTabsAsync(tab), CanCloseOtherTabs(tab)),
            new ContextMenuEntry("タブの詳細操作", Children: BuildTabDetailMenu(tab)));
        AddGroup(
            menu,
            Item("閉じたタブを開く", () => ReopenClosedTabAsync(), gesture: "Ctrl+Shift+T"),
            new ContextMenuEntry("最近閉じたタブ", Children: BuildRecentClosedTabsMenu()));

        if (GetTabFilePath(tab) is not null)
        {
            var onDisk = CanOperateOnTabFile(tab);
            var fileOperations = new List<ContextMenuEntry>
            {
                Item("切り取り", () => CutTabFileAsync(tab), onDisk),
                Item("コピー", () => CopyTabFileAsync(tab), onDisk),
                Item("パスのコピー", () => CopyTabFilePathAsync(tab)),
                Item("名前の変更", () => RenameTabFileAsync(tab), onDisk),
                Item("削除", () => DeleteTabFileAsync(tab), onDisk),
            };
            if (ShellFiles.CanShowProperties)
            {
                fileOperations.Add(Item("プロパティ", () => ShowTabPropertiesAsync(tab), CanShowTabProperties(tab)));
            }

            AddGroup(menu, [.. fileOperations]);
            AddGroup(
                menu,
                Item("ターミナルで開く", () => OpenTerminalForTabAsync(tab), onDisk),
                Item(RevealHeader, () => RevealTabFileAsync(tab), onDisk));
        }

        return menu;
    }

    /// <summary>タブ一覧の余白のメニュー。</summary>
    public IReadOnlyList<ContextMenuEntry> BuildTabBarContextMenu()
        =>
        [
            Item("新しいタブ", () => NewDocument(), gesture: "Ctrl+N"),
            Item("閉じたタブを開く", () => ReopenClosedTabAsync(), gesture: "Ctrl+Shift+T"),
        ];

    private IReadOnlyList<ContextMenuEntry> BuildTabDetailMenu(WorkspaceTabViewModel tab)
        =>
        [
            Item("上側のタブを閉じる", () => CloseTabsAboveAsync(tab), CanCloseTabsAbove(tab)),
            Item("ピン留めされていないタブをすべて閉じる", () => CloseUnpinnedTabsAsync(), CanCloseUnpinnedTabs()),
            Item("すべてのタブをピン留め", () => PinAllTabs(), CanPinAllTabs()),
            Item("すべてのピン留めを解除", () => UnpinAllTabs(), CanUnpinAllTabs()),
            Item("上側のタブをピン留め", () => PinTabsAbove(tab), CanPinTabsAbove(tab)),
            Item("下側のタブをピン留め", () => PinTabsBelow(tab), CanPinTabsBelow(tab)),
            Item("すべてのタブを開き直す", () => ReloadAllTabsAsync(), CanReloadAllTabs()),
            Item("上側のタブを開き直す", () => ReloadTabsAboveAsync(tab), CanReloadTabsAbove(tab)),
            Item("下側のタブを開き直す", () => ReloadTabsBelowAsync(tab), CanReloadTabsBelow(tab)),
            Item("先頭へ移動", () => MoveTabToStart(tab), CanMoveTabToStart(tab)),
            Item("末尾へ移動", () => MoveTabToEnd(tab), CanMoveTabToEnd(tab)),
            Item("名前順に並べ替え", () => SortTabsByName(), CanSortTabs()),
            Item("パス順に並べ替え", () => SortTabsByPath(), CanSortTabs()),
            Item("並び順を反転", () => ReverseTabOrder(), CanSortTabs()),
            Item("親フォルダをフォルダツリーで開く", () => OpenParentFolderAsync(tab), CanOpenParentFolder(tab)),
            Item("タブ名をコピー", () => CopyTabNameAsync(tab)),
            Item("file URI としてコピー", () => CopyTabFileUriAsync(tab), CanCopyTabPathText(tab)),
            Item("Markdown リンクとしてコピー", () => CopyTabMarkdownLinkAsync(tab), CanCopyTabPathText(tab)),
            Item("すべてのタブのパスをコピー", () => CopyAllTabPathsAsync(), CanCopyAllTabPaths()),
        ];

    private IReadOnlyList<ContextMenuEntry> BuildRecentClosedTabsMenu()
    {
        var paths = RecentClosedTabPaths;
        if (paths.Count == 0)
        {
            return [Item(NoneHeader, () => Task.CompletedTask, false)];
        }

        return [.. paths.Select(path => Item(
            Path.GetFileName(path), () => ReopenClosedTabAsync(path), toolTip: path))];
    }

    // ===== フォルダツリー =====

    /// <summary>
    /// フォルダツリーのメニュー。<paramref name="node"/> が無い・読み込み中の行のときは余白のメニュー。
    /// </summary>
    public IReadOnlyList<ContextMenuEntry> BuildFolderTreeContextMenu(FolderTreeNode? node, bool canPaste)
    {
        if (node is null || node.IsPlaceholder)
        {
            return BuildFolderTreeBlankMenu();
        }

        var menu = new List<ContextMenuEntry>();
        var fileOperations = new List<ContextMenuEntry>
        {
            Item("切り取り", () => CutNodeAsync(node), CanCutNode(node)),
            Item("コピー", () => CopyNodeAsync(node), CanCopyNode(node)),
            Item("パスのコピー", () => CopyNodePathAsync(node), CanUseNodePath(node)),
            Item("名前の変更", () => RenameNodeAsync(node), CanRenameNode(node)),
            Item("削除", () => DeleteNodeAsync(node), CanDeleteNode(node)),
        };
        if (ShellFiles.CanShowProperties)
        {
            fileOperations.Add(Item("プロパティ", () => ShowNodePropertiesAsync(node), CanShowNodeProperties(node)));
        }

        AddGroup(menu, [.. fileOperations]);

        var opening = new List<ContextMenuEntry>();
        if (node.IsDirectory)
        {
            opening.Add(Item("貼り付け", () => PasteIntoNodeAsync(node), CanPasteIntoNode(node, canPaste)));
        }
        else
        {
            opening.Add(Item("開く", () => OpenFolderNodeAsync(node, permanent: true), gesture: "Enter"));
            opening.Add(Item("ピン留めして開く", () => OpenFolderNodePinnedAsync(node)));
        }

        opening.Add(Item("ターミナルで開く", () => OpenTerminalForNodeAsync(node), CanUseNodePath(node)));
        opening.Add(Item(node.IsDirectory ? OpenFolderHeader : RevealHeader, () => RevealNodeAsync(node), CanUseNodePath(node)));
        AddGroup(menu, [.. opening]);

        AddGroup(
            menu,
            Item("新しいファイル", () => CreateFileForNodeAsync(node)),
            Item("新しいフォルダ", () => CreateFolderForNodeAsync(node)));
        AddGroup(menu, [.. BuildFolderTreeBlankMenu()]);
        return menu;
    }

    /// <summary>フォルダツリーの余白のメニュー。</summary>
    public IReadOnlyList<ContextMenuEntry> BuildFolderTreeBlankMenu()
        =>
        [
            Item("最新の情報に更新", () => RefreshFolderAsync()),
            Item("すべて折りたたむ", () => CollapseFolders()),
        ];

    /// <summary>ファイルを通常のタブとして開き、ピン留めする（開けなかったときは何もしない）。</summary>
    internal async Task OpenFolderNodePinnedAsync(FolderTreeNode node)
    {
        await OpenFolderNodeAsync(node, permanent: true);
        if (!node.IsPlaceholder && !node.IsDirectory
            && FindOpenFileTab(Path.GetFullPath(node.FullPath)) is { CanPin: true } tab)
        {
            tab.IsPinned = true;
        }
    }

    // ===== 部品 =====

    private static ContextMenuEntry Item(
        string header, Func<Task> run, bool isEnabled = true, string? gesture = null, string? toolTip = null)
        => new(header, run, gesture, isEnabled, null, toolTip);

    private static ContextMenuEntry Item(string header, Action run, bool isEnabled = true, string? gesture = null)
        => Item(header, () =>
        {
            run();
            return Task.CompletedTask;
        }, isEnabled, gesture);

    /// <summary>空でない群を、直前の群との間に区切りを挟んで足す。</summary>
    private static void AddGroup(List<ContextMenuEntry> menu, params ContextMenuEntry[] group)
    {
        if (group.Length == 0)
        {
            return;
        }

        if (menu.Count > 0)
        {
            menu.Add(ContextMenuEntry.Separator);
        }

        menu.AddRange(group);
    }
}
