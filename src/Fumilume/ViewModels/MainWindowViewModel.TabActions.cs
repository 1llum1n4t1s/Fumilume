using Fumilume.Services;

namespace Fumilume.ViewModels;

/// <summary>
/// タブの右クリックメニューが使うタブ操作（閉じた履歴・複数閉じ・ピン一括・並べ替え・パスのコピー）。
/// ファイルそのものへの操作（改名・削除・クリップボード・表示・ターミナル）は含めない。
/// </summary>
public sealed partial class MainWindowViewModel
{
    private const int ClosedTabHistoryLimit = 25;
    private const int ClosedTabMenuLimit = 10;

    // 新しい順。セッションには保存しない。
    private readonly List<string> _closedTabPaths = [];

    // ===== 閉じた履歴 =====

    /// <summary>メニューに出す最近閉じたタブのパス（新しい順、最大 10 件）。</summary>
    public IReadOnlyList<string> RecentClosedTabPaths => _closedTabPaths.Take(ClosedTabMenuLimit).ToArray();

    /// <summary>履歴の先頭を開き直す。履歴が空なら何もしない。</summary>
    public Task ReopenClosedTabAsync()
        => _closedTabPaths.Count == 0 ? Task.CompletedTask : ReopenClosedTabAsync(_closedTabPaths[0]);

    /// <summary>履歴の指定パスを開き直す。開けたら履歴から外し、ディスク上に無いパスは外して通知する。</summary>
    public async Task ReopenClosedTabAsync(string path)
    {
        var entry = _closedTabPaths.FirstOrDefault(item => FileNameRules.ArePathsEqual(item, path));
        if (entry is null)
        {
            return;
        }

        if (!File.Exists(entry))
        {
            _closedTabPaths.Remove(entry);
            StatusMessage = $"{Path.GetFileName(entry)} が見つからないため開けません";
            return;
        }

        await OpenPathsAsync([entry]);
        if (FindOpenFileTab(Path.GetFullPath(entry)) is not null)
        {
            _closedTabPaths.RemoveAll(item => FileNameRules.ArePathsEqual(item, entry));
        }
    }

    private void RecordClosedTab(string path)
    {
        _closedTabPaths.RemoveAll(item => FileNameRules.ArePathsEqual(item, path));
        _closedTabPaths.Insert(0, path);
        if (_closedTabPaths.Count > ClosedTabHistoryLimit)
        {
            _closedTabPaths.RemoveRange(ClosedTabHistoryLimit, _closedTabPaths.Count - ClosedTabHistoryLimit);
        }
    }

    /// <summary>
    /// 改名・移動に合わせて履歴のパスを付け替える。フォルダの改名では配下も付け替え、
    /// 付け替えで重複したパスは新しい方（履歴の前方）だけ残す。
    /// </summary>
    internal void RemapClosedTabPaths(string oldPath, string newPath)
    {
        var remapped = new List<string>(_closedTabPaths.Count);
        foreach (var entry in _closedTabPaths)
        {
            var mapped = FileNameRules.IsSameOrDescendant(entry, oldPath)
                ? FileNameRules.ArePathsEqual(entry, oldPath)
                    ? newPath
                    : Path.Combine(newPath, Path.GetRelativePath(oldPath, entry))
                : entry;
            if (!remapped.Any(item => FileNameRules.ArePathsEqual(item, mapped)))
            {
                remapped.Add(mapped);
            }
        }

        _closedTabPaths.Clear();
        _closedTabPaths.AddRange(remapped);
    }

    /// <summary>パスを持つ文書・PDF タブのパス。それ以外（空文書・設定・検索結果）は null。</summary>
    internal static string? GetTabFilePath(WorkspaceTabViewModel tab) => tab switch
    {
        DocumentViewModel document => document.FilePath,
        PdfDocumentViewModel pdf => pdf.FilePath,
        _ => null,
    };

    // ===== 新しいタブ・ピン留め =====

    /// <summary>新しい空文書を <paramref name="tab"/> の直後へ開く（ピン留めの群と設定タブの間に収める）。</summary>
    public void NewTabBelow(WorkspaceTabViewModel tab)
    {
        var pinnedCount = PinnedCount();
        var anchor = Tabs.IndexOf(tab);
        NewDocument();
        var created = SelectedTab!;
        var target = Math.Min(Math.Max(anchor + 1, pinnedCount), Tabs.IndexOf(created));
        MoveTab(created, target);
    }

    public void TogglePinTab(WorkspaceTabViewModel tab)
    {
        if (tab.CanPin)
        {
            tab.IsPinned = !tab.IsPinned;
        }
    }

    public bool CanPinAllTabs() => Tabs.Any(CanBePinned);

    public bool CanPinTabsAbove(WorkspaceTabViewModel tab) => TabsAbove(tab).Any(CanBePinned);

    public bool CanPinTabsBelow(WorkspaceTabViewModel tab) => TabsBelow(tab).Any(CanBePinned);

    public bool CanUnpinAllTabs() => Tabs.Any(tab => tab.IsPinned && !tab.IsSettingsTab);

    public void PinAllTabs() => PinTabs(Tabs.ToArray());

    public void PinTabsAbove(WorkspaceTabViewModel tab) => PinTabs(TabsAbove(tab));

    public void PinTabsBelow(WorkspaceTabViewModel tab) => PinTabs(TabsBelow(tab));

    /// <summary>後ろのピン留めから順に解除する（解除されたタブは通常群の先頭へ入るので、元の順序が保たれる）。</summary>
    public void UnpinAllTabs()
    {
        var pinned = Tabs.Where(tab => tab.IsPinned && !tab.IsSettingsTab).Reverse().ToArray();
        foreach (var tab in pinned)
        {
            tab.IsPinned = false;
        }

        StatusMessage = $"{pinned.Length:N0} 件のピン留めを解除しました";
    }

    private static bool CanBePinned(WorkspaceTabViewModel tab) => tab.CanPin && !tab.IsPinned;

    private void PinTabs(IReadOnlyList<WorkspaceTabViewModel> candidates)
    {
        var targets = candidates.Where(CanBePinned).ToArray();
        foreach (var tab in targets)
        {
            tab.IsPinned = true;
        }

        StatusMessage = $"{targets.Length:N0} 件のタブをピン留めしました";
    }

    // ===== 複数のタブを閉じる =====

    public bool CanCloseTabsBelow(WorkspaceTabViewModel tab) => TabsBelow(tab).Any(IsClosableInBulk);

    public bool CanCloseTabsAbove(WorkspaceTabViewModel tab) => TabsAbove(tab).Any(IsClosableInBulk);

    public bool CanCloseOtherTabs(WorkspaceTabViewModel tab)
        => Tabs.Any(item => !ReferenceEquals(item, tab) && IsClosableInBulk(item));

    public bool CanCloseUnpinnedTabs() => Tabs.Any(tab => !tab.IsSettingsTab && IsClosableInBulk(tab));

    /// <summary>下側のタブを閉じる（ピン留めは残す）。</summary>
    public Task CloseTabsBelowAsync(WorkspaceTabViewModel tab) => CloseTabsInOrderAsync(TabsBelow(tab));

    /// <summary>上側のタブを閉じる（ピン留めは残す）。</summary>
    public Task CloseTabsAboveAsync(WorkspaceTabViewModel tab) => CloseTabsInOrderAsync(TabsAbove(tab));

    /// <summary>他のタブを閉じる（ピン留めは残す。設定タブは閉じる対象）。</summary>
    public Task CloseOtherTabsAsync(WorkspaceTabViewModel tab)
        => CloseTabsInOrderAsync(Tabs.Where(item => !ReferenceEquals(item, tab)).ToArray());

    /// <summary>ピン留めされていないタブをすべて閉じる（設定タブは除く）。</summary>
    public Task CloseUnpinnedTabsAsync()
        => CloseTabsInOrderAsync(Tabs.Where(tab => !tab.IsSettingsTab).ToArray());

    private static bool IsClosableInBulk(WorkspaceTabViewModel tab) => !tab.IsPinned;

    /// <summary>
    /// ピン留めを除いて順に閉じる。<c>_openPathsGate</c> は保持しない（未保存の保存がゲートを取るため）。
    /// 未保存確認で取り消されたら残りも閉じずに止める。
    /// </summary>
    private async Task CloseTabsInOrderAsync(IReadOnlyList<WorkspaceTabViewModel> candidates)
    {
        var closed = 0;
        foreach (var tab in candidates.Where(IsClosableInBulk))
        {
            if (!Tabs.Contains(tab))
            {
                continue;
            }

            await CloseTabCoreAsync(tab, recordClosed: true);
            if (Tabs.Contains(tab))
            {
                StatusMessage = $"{closed:N0} 件のタブを閉じ、残りを中止しました";
                return;
            }

            closed++;
        }

        StatusMessage = $"{closed:N0} 件のタブを閉じました";
    }

    // ===== 開き直す =====

    public bool CanReloadAllTabs() => Tabs.Any(CanReloadTab);

    public bool CanReloadTabsAbove(WorkspaceTabViewModel tab) => TabsAbove(tab).Any(CanReloadTab);

    public bool CanReloadTabsBelow(WorkspaceTabViewModel tab) => TabsBelow(tab).Any(CanReloadTab);

    public Task ReloadAllTabsAsync() => ReloadTabsAsync(Tabs.ToArray());

    public Task ReloadTabsAboveAsync(WorkspaceTabViewModel tab) => ReloadTabsAsync(TabsAbove(tab));

    public Task ReloadTabsBelowAsync(WorkspaceTabViewModel tab) => ReloadTabsAsync(TabsBelow(tab));

    /// <summary>パス付きのテキスト文書だけが対象（PDF・未保存の新規文書・設定・検索結果は対象外）。</summary>
    private static bool CanReloadTab(WorkspaceTabViewModel tab) => tab is DocumentViewModel { FilePath: not null };

    private async Task ReloadTabsAsync(IReadOnlyList<WorkspaceTabViewModel> candidates)
    {
        var reloaded = 0;
        foreach (var document in candidates.Where(CanReloadTab).Cast<DocumentViewModel>())
        {
            if (!await ReloadDocumentAsync(document))
            {
                StatusMessage = $"{reloaded:N0} 件を開き直し、残りを中止しました";
                return;
            }

            reloaded++;
        }

        StatusMessage = $"{reloaded:N0} 件の文書を開き直しました";
    }

    // ===== 並べ替え =====

    public bool CanMoveTabToStart(WorkspaceTabViewModel tab)
        => !tab.IsSettingsTab && Tabs.IndexOf(tab) > GroupStart(tab);

    public bool CanMoveTabToEnd(WorkspaceTabViewModel tab)
        => !tab.IsSettingsTab && Tabs.IndexOf(tab) is var index and >= 0 && index < GroupEnd(tab);

    /// <summary>同じ群（ピン留め／通常）の先頭へ移す。</summary>
    public void MoveTabToStart(WorkspaceTabViewModel tab)
    {
        if (CanMoveTabToStart(tab))
        {
            MoveTab(tab, GroupStart(tab));
        }
    }

    /// <summary>同じ群（ピン留め／通常）の末尾へ移す。</summary>
    public void MoveTabToEnd(WorkspaceTabViewModel tab)
    {
        if (CanMoveTabToEnd(tab))
        {
            MoveTab(tab, GroupEnd(tab));
        }
    }

    /// <summary>名前順・パス順・反転は、2 つ以上の内容タブがあるときだけ意味がある。</summary>
    public bool CanSortTabs() => Tabs.Count(tab => !tab.IsSettingsTab) > 1;

    /// <summary>タブ名（大文字小文字を区別しない）の昇順。ピン留め群と通常群で別々に並べ、設定タブは末尾。</summary>
    public void SortTabsByName()
        => ApplyTabOrder(group => group.OrderBy(TabNameOf, StringComparer.OrdinalIgnoreCase));

    /// <summary>パスの昇順。パスの無いタブは各群の末尾へ、名前順で置く。</summary>
    public void SortTabsByPath()
        => ApplyTabOrder(group => group
            .OrderBy(tab => GetTabFilePath(tab) is null)
            .ThenBy(tab => GetTabFilePath(tab) ?? TabNameOf(tab), StringComparer.OrdinalIgnoreCase));

    /// <summary>各群の並び順を反転する。</summary>
    public void ReverseTabOrder() => ApplyTabOrder(group => group.Reverse());

    private static string TabNameOf(WorkspaceTabViewModel tab)
        => tab is DocumentViewModel document ? document.DisplayName : tab.TabTitle;

    private void ApplyTabOrder(Func<IEnumerable<WorkspaceTabViewModel>, IEnumerable<WorkspaceTabViewModel>> arrange)
    {
        var selected = SelectedTab;
        var pinned = arrange(Tabs.Where(tab => tab.IsPinned && !tab.IsSettingsTab).ToArray());
        var normal = arrange(Tabs.Where(tab => !tab.IsPinned && !tab.IsSettingsTab).ToArray());
        var desired = pinned.Concat(normal).Concat(Tabs.Where(tab => tab.IsSettingsTab)).ToArray();
        for (var index = 0; index < desired.Length; index++)
        {
            var current = Tabs.IndexOf(desired[index]);
            if (current != index)
            {
                Tabs.Move(current, index);
            }
        }

        RestoreSelection(selected);
    }

    private void MoveTab(WorkspaceTabViewModel tab, int targetIndex)
    {
        var selected = SelectedTab;
        var current = Tabs.IndexOf(tab);
        if (current >= 0 && current != targetIndex)
        {
            Tabs.Move(current, targetIndex);
        }

        RestoreSelection(selected);
    }

    private void RestoreSelection(WorkspaceTabViewModel? selected)
    {
        if (selected is not null && !ReferenceEquals(SelectedTab, selected) && Tabs.Contains(selected))
        {
            SelectedTab = selected;
        }
    }

    private int PinnedCount() => Tabs.TakeWhile(tab => tab.IsPinned && !tab.IsSettingsTab).Count();

    private int GroupStart(WorkspaceTabViewModel tab) => tab.IsPinned ? 0 : PinnedCount();

    private int GroupEnd(WorkspaceTabViewModel tab)
        => tab.IsPinned ? PinnedCount() - 1 : Tabs.Count(item => !item.IsSettingsTab) - 1;

    private IReadOnlyList<WorkspaceTabViewModel> TabsAbove(WorkspaceTabViewModel tab)
        => Tabs.Take(Math.Max(Tabs.IndexOf(tab), 0)).ToArray();

    private IReadOnlyList<WorkspaceTabViewModel> TabsBelow(WorkspaceTabViewModel tab)
        => Tabs.IndexOf(tab) is var index and >= 0 ? Tabs.Skip(index + 1).ToArray() : [];

    // ===== フォルダ・パスのコピー =====

    public bool CanOpenParentFolder(WorkspaceTabViewModel tab) => GetTabFilePath(tab) is not null;

    /// <summary>タブのファイルがあるフォルダを、既存のフォルダを開く経路でフォルダツリーへ開く。</summary>
    public async Task OpenParentFolderAsync(WorkspaceTabViewModel tab)
    {
        if (GetTabFilePath(tab) is { } path && Path.GetDirectoryName(path) is { Length: > 0 } folder)
        {
            await OpenFolderPathAsync(folder);
        }
    }

    /// <summary>file URI・Markdown リンクのコピーが使えるか（パスがあり URI にできる）。</summary>
    public bool CanCopyTabPathText(WorkspaceTabViewModel tab) => GetTabFilePath(tab) is not null;

    public bool CanCopyAllTabPaths() => Tabs.Any(tab => GetTabFilePath(tab) is not null);

    /// <summary>タブ名（変更印を含まないファイル名・表示名）をコピーする。</summary>
    public Task CopyTabNameAsync(WorkspaceTabViewModel tab) => CopyTabTextAsync(TabNameOf(tab));

    public Task CopyTabFileUriAsync(WorkspaceTabViewModel tab)
        => GetTabFilePath(tab) is { } path && FileNameRules.ToFileUri(path) is { } uri
            ? CopyTabTextAsync(uri)
            : Task.CompletedTask;

    public Task CopyTabMarkdownLinkAsync(WorkspaceTabViewModel tab)
        => GetTabFilePath(tab) is { } path && FileNameRules.ToMarkdownLink(Path.GetFileName(path), path) is { } link
            ? CopyTabTextAsync(link)
            : Task.CompletedTask;

    /// <summary>パスを持つすべてのタブのパスを、タブの並び順に 1 行ずつコピーする。</summary>
    public Task CopyAllTabPathsAsync()
    {
        var paths = Tabs.Select(GetTabFilePath).OfType<string>().ToArray();
        return paths.Length == 0 ? Task.CompletedTask : CopyTabTextAsync(string.Join(Environment.NewLine, paths));
    }

    private async Task CopyTabTextAsync(string text)
    {
        try
        {
            await _dialogs.CopyTextAsync(text);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("クリップボードへコピーできませんでした", ex.Message);
        }
    }
}
