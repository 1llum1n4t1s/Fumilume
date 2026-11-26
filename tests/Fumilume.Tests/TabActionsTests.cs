using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class TabActionsTests(HeadlessAppFixture fixture)
{
    [Fact]
    public void ClosedHistoryIsNewestFirstDeduplicatedAndCappedAtTwentyFive() => RunWithFiles(30, async (vm, dialogs, paths) =>
    {
        await vm.OpenPathsAsync(paths);
        foreach (var path in paths)
        {
            await TabOf(vm, path).CloseTabCommand.ExecuteAsync(null);
        }

        Assert.Equal(10, vm.RecentClosedTabPaths.Count);
        Assert.Equal(paths[29], vm.RecentClosedTabPaths[0]);
        Assert.Equal(paths[20], vm.RecentClosedTabPaths[9]);

        // 同じパスを開き直して閉じ直しても 1 件だけ（新しい方が先頭）。
        await vm.ReopenClosedTabAsync(paths[25]);
        await TabOf(vm, paths[25]).CloseTabCommand.ExecuteAsync(null);
        Assert.Equal(paths[25], vm.RecentClosedTabPaths[0]);
        Assert.Single(vm.RecentClosedTabPaths, item => item == paths[25]);

        // 上限は 25 件。25 回開き直すと履歴は空になり、26 回目は何もしない。
        for (var i = 0; i < 26; i++)
        {
            await vm.ReopenClosedTabAsync();
        }

        Assert.Equal(25, vm.Documents.Count(document => document.FilePath is not null));
        Assert.Empty(vm.RecentClosedTabPaths);
    });

    [Fact]
    public void UntitledPreviewReplacementAndRemoveTabCoreAreNotRecorded() => RunWithFiles(3, async (vm, dialogs, paths) =>
    {
        // 空文書の除去（ファイルを開いたとき）は記録しない。
        await vm.OpenPathsAsync([paths[0]]);
        Assert.Empty(vm.RecentClosedTabPaths);

        // 一時タブの差し替えは記録しない。
        await vm.OpenFolderFileAsync(paths[1], permanent: false);
        await vm.OpenFolderFileAsync(paths[2], permanent: false);
        Assert.Null(vm.Documents.FirstOrDefault(document => document.FilePath == paths[1]));
        Assert.Empty(vm.RecentClosedTabPaths);

        // RemoveTabCore は確認も記録もしない（未保存でも外す）。
        var modified = TabOf(vm, paths[0]);
        modified.Text = "edited";
        vm.RemoveTabCore(modified);
        Assert.DoesNotContain(modified, vm.Tabs);
        Assert.Equal(0, dialogs.UnsavedPrompts);
        Assert.Empty(vm.RecentClosedTabPaths);

        // 利用者が閉じたものは記録する。
        await TabOf(vm, paths[2]).CloseTabCommand.ExecuteAsync(null);
        Assert.Equal([paths[2]], vm.RecentClosedTabPaths);
    });

    [Fact]
    public void RemapAndMissingPathsKeepTheHistoryConsistent() => RunWithFiles(2, async (vm, dialogs, paths) =>
    {
        await vm.OpenPathsAsync(paths);
        await TabOf(vm, paths[0]).CloseTabCommand.ExecuteAsync(null);
        await TabOf(vm, paths[1]).CloseTabCommand.ExecuteAsync(null);

        var folder = Path.GetDirectoryName(paths[0])!;
        var renamed = folder + "-renamed";
        vm.RemapClosedTabPaths(folder, renamed);
        Assert.Equal(
            [Path.Combine(renamed, Path.GetFileName(paths[1])), Path.Combine(renamed, Path.GetFileName(paths[0]))],
            vm.RecentClosedTabPaths);

        // 消えたパスは開けず、履歴から外れる。
        await vm.ReopenClosedTabAsync();
        Assert.Single(vm.RecentClosedTabPaths);
        Assert.DoesNotContain(vm.Documents, document => document.FilePath is not null);
    });

    [Fact]
    public void BulkCloseStopsWhenTheUnsavedPromptIsCancelled() => RunWithFiles(4, async (vm, dialogs, paths) =>
    {
        await vm.OpenPathsAsync(paths);
        TabOf(vm, paths[2]).Text = "edited";
        dialogs.Decision = UnsavedDocumentDecision.Cancel;

        await vm.CloseTabsBelowAsync(TabOf(vm, paths[0]));

        Assert.Equal([paths[0], paths[2], paths[3]], vm.Documents.Select(document => document.FilePath));
        Assert.Equal(1, dialogs.UnsavedPrompts);
    });

    [Fact]
    public void BulkCloseKeepsPinnedTabsAndRecordsTheClosedOnes() => RunWithFiles(4, async (vm, dialogs, paths) =>
    {
        await vm.OpenPathsAsync(paths);
        TabOf(vm, paths[3]).IsPinned = true;

        await vm.CloseOtherTabsAsync(TabOf(vm, paths[0]));

        Assert.Equal([paths[3], paths[0]], vm.Documents.Select(document => document.FilePath));
        Assert.Equal([paths[2], paths[1]], vm.RecentClosedTabPaths);
    });

    [Fact]
    public void UnpinAllKeepsTheOriginalOrder() => RunWithFiles(4, async (vm, dialogs, paths) =>
    {
        await vm.OpenPathsAsync(paths);
        TabOf(vm, paths[2]).IsPinned = true;
        TabOf(vm, paths[0]).IsPinned = true;
        Assert.Equal([paths[2], paths[0], paths[1], paths[3]], vm.Documents.Select(document => document.FilePath));

        vm.UnpinAllTabs();

        Assert.All(vm.Tabs, tab => Assert.False(tab.IsPinned));
        Assert.Equal([paths[2], paths[0], paths[1], paths[3]], vm.Documents.Select(document => document.FilePath));
    });

    [Fact]
    public void SortAndReverseWorkPerGroupAndKeepTheSelectedTab() => RunWithFiles(["z.txt", "a.txt", "m.txt", "b.txt"], async (vm, dialogs, paths) =>
    {
        await vm.OpenPathsAsync(paths);
        TabOf(vm, paths[2]).IsPinned = true;
        vm.OpenSettingsCommand.Execute(null);
        vm.SelectedTab = TabOf(vm, paths[0]);

        vm.SortTabsByName();
        Assert.Equal(["m.txt", "a.txt", "b.txt", "z.txt"], vm.Documents.Select(document => document.DisplayName));
        Assert.True(vm.Tabs[^1].IsSettingsTab);
        Assert.Same(TabOf(vm, paths[0]), vm.SelectedTab);

        vm.ReverseTabOrder();
        Assert.Equal(["m.txt", "z.txt", "b.txt", "a.txt"], vm.Documents.Select(document => document.DisplayName));
        Assert.True(vm.Tabs[^1].IsSettingsTab);
        Assert.Same(TabOf(vm, paths[0]), vm.SelectedTab);

        vm.MoveTabToStart(TabOf(vm, paths[1]));
        Assert.Equal("a.txt", vm.Tabs[1].TabTitle);
        Assert.False(vm.CanMoveTabToStart(TabOf(vm, paths[2])));
        Assert.Same(TabOf(vm, paths[0]), vm.SelectedTab);
    });

    [Fact]
    public void NewTabBelowOpensRightAfterTheTabButNotAmongPinnedOrAfterSettings() => RunWithFiles(3, async (vm, dialogs, paths) =>
    {
        await vm.OpenPathsAsync(paths);
        TabOf(vm, paths[2]).IsPinned = true;
        vm.OpenSettingsCommand.Execute(null);

        // [pinned, 0, 1, settings]: 通常群の先頭タブの直後。
        vm.NewTabBelow(TabOf(vm, paths[0]));
        Assert.Equal(2, vm.Tabs.IndexOf(vm.SelectedTab!));

        // ピン留めタブの下に作っても、ピン留めの群には入らない。
        vm.NewTabBelow(TabOf(vm, paths[2]));
        Assert.Equal(1, vm.Tabs.IndexOf(vm.SelectedTab!));
        Assert.False(vm.SelectedTab!.IsPinned);

        // 設定タブの下に作っても設定タブより前。
        vm.NewTabBelow(vm.Tabs[^1]);
        Assert.Equal(vm.Tabs.Count - 2, vm.Tabs.IndexOf(vm.SelectedTab!));
        Assert.True(vm.Tabs[^1].IsSettingsTab);
    });

    [Fact]
    public void ReloadStopsWhenTheDiscardPromptIsDeclined() => RunWithFiles(3, async (vm, dialogs, paths) =>
    {
        await vm.OpenPathsAsync(paths);
        File.WriteAllText(paths[0], "changed on disk");
        TabOf(vm, paths[1]).Text = "edited";
        dialogs.ConfirmResult = false;

        await vm.ReloadAllTabsAsync();

        Assert.Equal("changed on disk", TabOf(vm, paths[0]).Text);
        Assert.Equal("edited", TabOf(vm, paths[1]).Text);
        Assert.Equal("x", TabOf(vm, paths[2]).Text);
        Assert.Equal(1, dialogs.Confirmations);
    });

    [Fact]
    public void CopyActionsUseTheTabPaths() => RunWithFiles(2, async (vm, dialogs, paths) =>
    {
        await vm.OpenPathsAsync(paths);
        var tab = TabOf(vm, paths[0]);
        tab.Text = "edited";

        await vm.CopyTabNameAsync(tab);
        await vm.CopyTabFileUriAsync(tab);
        await vm.CopyTabMarkdownLinkAsync(tab);
        await vm.CopyAllTabPathsAsync();

        Assert.Equal(Path.GetFileName(paths[0]), dialogs.Copied[0]);
        Assert.Equal(FileNameRules.ToFileUri(paths[0]), dialogs.Copied[1]);
        Assert.Equal(FileNameRules.ToMarkdownLink(Path.GetFileName(paths[0]), paths[0]), dialogs.Copied[2]);
        Assert.Equal(string.Join(Environment.NewLine, paths), dialogs.Copied[3]);
    });

    private static DocumentViewModel TabOf(MainWindowViewModel vm, string path)
        => vm.Documents.First(document => document.FilePath == path);

    private void RunWithFiles(int count, Func<MainWindowViewModel, StubDialogs, string[], Task> body)
        => RunWithFiles(Enumerable.Range(0, count).Select(index => $"file{index:D2}.txt").ToArray(), body);

    private void RunWithFiles(string[] names, Func<MainWindowViewModel, StubDialogs, string[], Task> body) => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var folder = Path.Combine(storage.Path, "docs");
        Directory.CreateDirectory(folder);
        var paths = names.Select(name => Path.Combine(folder, name)).ToArray();
        foreach (var path in paths)
        {
            File.WriteAllText(path, "x");
        }

        var dialogs = new StubDialogs();
        var vm = new MainWindowViewModel(new DocumentFileService(), dialogs, new AppSettings());
        SingleThreadedAsync.Run(() => body(vm, dialogs, paths));
    });

    private sealed class StubDialogs : IEditorDialogService
    {
        public UnsavedDocumentDecision Decision { get; set; } = UnsavedDocumentDecision.Discard;

        public bool ConfirmResult { get; set; } = true;

        public int UnsavedPrompts { get; private set; }

        public int Confirmations { get; private set; }

        public List<string> Copied { get; } = [];

        public Task<IReadOnlyList<string>> PickOpenPathsAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);

        public Task<UnsavedDocumentDecision> ConfirmUnsavedAsync(string documentName)
        {
            UnsavedPrompts++;
            return Task.FromResult(Decision);
        }

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<int?> PickLineNumberAsync(int currentLine, int maximumLine) => Task.FromResult<int?>(null);

        public Task<string?> PromptTextAsync(string title, string message, string initialText)
            => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message)
        {
            Confirmations++;
            return Task.FromResult(ConfirmResult);
        }

        public Task<GrepQuery?> PickGrepQueryAsync(GrepQuery initial) => Task.FromResult<GrepQuery?>(null);

        public Task CheckForUpdatesAsync(bool manually) => Task.CompletedTask;

        public Task CopyTextAsync(string text)
        {
            Copied.Add(text);
            return Task.CompletedTask;
        }
    }
}
