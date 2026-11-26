using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

/// <summary>
/// 右クリックメニューの組み立て（項目・順序・文言・有効/無効・表示条件）。
/// 実際の画面での表示は <see cref="MainWindowIntegrationTests"/> が確かめる。
/// </summary>
[Collection(HeadlessAppCollection.Name)]
public sealed class ContextMenuTests(HeadlessAppFixture fixture)
{
    private const string Separator = "-";

    private static readonly string[] WindowsTabLayout =
    [
        "新しいタブを下に開く", Separator, "タブをピン留め", Separator,
        "タブを閉じる", "下側のタブを閉じる", "他のタブを閉じる", "タブの詳細操作", Separator,
        "閉じたタブを開く", "最近閉じたタブ", Separator,
        "切り取り", "コピー", "パスのコピー", "名前の変更", "削除", Separator,
        "ターミナルで開く", "エクスプローラーで表示",
    ];

    private static readonly string[] DetailLayout =
    [
        "上側のタブを閉じる", "ピン留めされていないタブをすべて閉じる", "すべてのタブをピン留め", "すべてのピン留めを解除",
        "上側のタブをピン留め", "下側のタブをピン留め", "すべてのタブを開き直す", "上側のタブを開き直す", "下側のタブを開き直す",
        "先頭へ移動", "末尾へ移動", "名前順に並べ替え", "パス順に並べ替え", "並び順を反転",
        "親フォルダをフォルダツリーで開く", "タブ名をコピー", "file URI としてコピー", "Markdown リンクとしてコピー",
        "すべてのタブのパスをコピー",
    ];

    [Fact]
    public void TabMenuOfADocumentOnDiskHasTheDesignedLayout() => RunWithFiles(3, async (vm, dialogs, shell, paths) =>
    {
        await vm.OpenPathsAsync(paths);
        var menu = vm.BuildTabContextMenu(TabOf(vm, paths[1]), canPaste: false);

        Assert.Equal(WindowsTabLayout, Layout(menu));
        Assert.Equal("Ctrl+W", Entry(menu, "タブを閉じる").Gesture);
        Assert.Equal("Ctrl+Shift+T", Entry(menu, "閉じたタブを開く").Gesture);
        Assert.Equal(DetailLayout, Layout(Entry(menu, "タブの詳細操作").Children!));
        Assert.Empty(Disabled(menu));
        Assert.Equal("(なし)", Assert.Single(Entry(menu, "最近閉じたタブ").Children!).Header);
        Assert.False(Entry(menu, "最近閉じたタブ").Children![0].IsEnabled);
    });

    [Fact]
    public void BulkClosesAndMovesAreDisabledWhereTheyHaveNothingToDo() => RunWithFiles(3, async (vm, dialogs, shell, paths) =>
    {
        await vm.OpenPathsAsync(paths);

        var first = vm.BuildTabContextMenu(TabOf(vm, paths[0]), false);
        Assert.Contains("先頭へ移動", Disabled(Entry(first, "タブの詳細操作").Children!));
        Assert.Contains("上側のタブを閉じる", Disabled(Entry(first, "タブの詳細操作").Children!));
        Assert.DoesNotContain("下側のタブを閉じる", Disabled(first));

        var last = vm.BuildTabContextMenu(TabOf(vm, paths[2]), false);
        Assert.Contains("下側のタブを閉じる", Disabled(last));
        Assert.Contains("末尾へ移動", Disabled(Entry(last, "タブの詳細操作").Children!));
        Assert.DoesNotContain("他のタブを閉じる", Disabled(last));
    });

    [Fact]
    public void PinnedTabShowsUnpinAndPinnedTabsAreKeptOutOfBulkCloses() => RunWithFiles(2, async (vm, dialogs, shell, paths) =>
    {
        await vm.OpenPathsAsync(paths);
        var pinned = TabOf(vm, paths[0]);
        pinned.IsPinned = true;

        var menu = vm.BuildTabContextMenu(pinned, false);
        Assert.Contains("ピン留めを解除", Layout(menu));
        Assert.DoesNotContain("タブをピン留め", Layout(menu));

        // ピン留めだけのとき、ほかのタブを閉じる対象は無い。
        TabOf(vm, paths[1]).IsPinned = true;
        Assert.Contains("他のタブを閉じる", Disabled(vm.BuildTabContextMenu(pinned, false)));
    });

    [Fact]
    public void MenuTextFollowsThePlatformAndPropertiesAreShownOnlyWhereAvailable() => RunWithFiles(1, async (vm, dialogs, shell, paths) =>
    {
        await vm.OpenPathsAsync(paths);
        var tab = TabOf(vm, paths[0]);

        shell.CanShowProperties = true;
        vm.UsesMacMenuText = false;
        var windows = vm.BuildTabContextMenu(tab, false);
        Assert.Equal(["切り取り", "コピー", "パスのコピー", "名前の変更", "削除", "プロパティ"],
            Layout(windows).SkipWhile(item => item != "切り取り").TakeWhile(item => item != Separator));
        Assert.Equal("エクスプローラーで表示", Layout(windows)[^1]);

        shell.CanShowProperties = false;
        vm.UsesMacMenuText = true;
        var mac = vm.BuildTabContextMenu(tab, false);
        Assert.DoesNotContain("プロパティ", Layout(mac));
        Assert.Equal("Finder で表示", Layout(mac)[^1]);
        Assert.DoesNotContain("エクスプローラーで表示", Layout(mac));
    });

    [Fact]
    public void DocumentMissingFromDiskKeepsOnlyCopyPathEnabled() => RunWithFiles(1, async (vm, dialogs, shell, paths) =>
    {
        await vm.OpenPathsAsync(paths);
        shell.CanShowProperties = true;
        File.Delete(paths[0]);

        var menu = vm.BuildTabContextMenu(TabOf(vm, paths[0]), false);
        Assert.Equal(
            ["下側のタブを閉じる", "他のタブを閉じる", "切り取り", "コピー", "名前の変更", "削除", "プロパティ",
                "ターミナルで開く", "エクスプローラーで表示"],
            Disabled(menu));
        Assert.True(Entry(menu, "パスのコピー").IsEnabled);
    });

    [Fact]
    public void UntitledSettingsAndSearchTabsHaveNoFileOperations() => RunWithFiles(1, (vm, dialogs, shell, paths) =>
    {
        var untitled = Assert.Single(vm.Tabs);
        var menu = vm.BuildTabContextMenu(untitled, false);
        Assert.Equal(
            ["新しいタブを下に開く", Separator, "タブをピン留め", Separator,
                "タブを閉じる", "下側のタブを閉じる", "他のタブを閉じる", "タブの詳細操作", Separator,
                "閉じたタブを開く", "最近閉じたタブ"],
            Layout(menu));

        vm.OpenSettingsCommand.Execute(null);
        var settings = vm.Tabs.Single(tab => tab.IsSettingsTab);
        var settingsMenu = Layout(vm.BuildTabContextMenu(settings, false));
        Assert.DoesNotContain("タブをピン留め", settingsMenu);
        Assert.DoesNotContain("切り取り", settingsMenu);
        // 区切りが連続せず、先頭・末尾にも付かない。
        Assert.NotEqual(Separator, settingsMenu[0]);
        Assert.NotEqual(Separator, settingsMenu[^1]);
        Assert.DoesNotContain(Enumerable.Range(1, settingsMenu.Length - 1), i => settingsMenu[i] == Separator && settingsMenu[i - 1] == Separator);

        using var search = new GrepResultTabViewModel(
            new GrepQuery("x", paths[0], "*", false, false, false),
            new GrepService(new DocumentFileService()), _ => Task.CompletedTask, _ => Task.CompletedTask);
        Assert.DoesNotContain("切り取り", Layout(vm.BuildTabContextMenu(search, false)));
        Assert.DoesNotContain("ターミナルで開く", Layout(vm.BuildTabContextMenu(search, false)));
        return Task.CompletedTask;
    });

    [Fact]
    public void RecentClosedTabsListFileNamesWithFullPathToolTipsAndReopenOnRun() => RunWithFiles(12, async (vm, dialogs, shell, paths) =>
    {
        await vm.OpenPathsAsync(paths);
        foreach (var path in paths)
        {
            await TabOf(vm, path).CloseTabCommand.ExecuteAsync(null);
        }

        var menu = vm.BuildTabBarContextMenu();
        Assert.Equal(["新しいタブ", "閉じたタブを開く"], Layout(menu));
        Assert.Equal("Ctrl+N", menu[0].Gesture);
        Assert.Equal("Ctrl+Shift+T", menu[1].Gesture);

        var recent = Entry(vm.BuildTabContextMenu(vm.Tabs[0], false), "最近閉じたタブ").Children!;
        Assert.Equal(10, recent.Count);
        Assert.Equal(Path.GetFileName(paths[11]), recent[0].Header);
        Assert.Equal(paths[11], recent[0].ToolTip);

        await vm.RunMenuEntryAsync(recent[0]);
        Assert.Contains(vm.Documents, document => document.FilePath == paths[11]);
        Assert.DoesNotContain(paths[11], vm.RecentClosedTabPaths);

        // 空でも「閉じたタブを開く」は有効で、何も起こさない。
        while (vm.RecentClosedTabPaths.Count > 0)
        {
            await vm.ReopenClosedTabAsync();
        }

        var tabs = vm.Tabs.Count;
        var emptyEntry = vm.BuildTabBarContextMenu()[1];
        Assert.True(emptyEntry.IsEnabled);
        await vm.RunMenuEntryAsync(emptyEntry);
        Assert.Equal(tabs, vm.Tabs.Count);
    });

    [Fact]
    public void FolderTreeMenuForFilesFoldersRootsDriveRootsAndPlaceholders() => RunWithFiles(["a.txt"], async (vm, dialogs, shell, paths) =>
    {
        var folder = Path.GetDirectoryName(paths[0])!;
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        await vm.OpenFolderPathAsync(folder);
        var root = vm.FolderRoots[0];
        var file = root.Children.Single(node => !node.IsDirectory);
        var sub = root.Children.Single(node => node.IsDirectory);
        var blank = new[] { "最新の情報に更新", "すべて折りたたむ" };

        // ファイル。
        var fileMenu = vm.BuildFolderTreeContextMenu(file, canPaste: true);
        Assert.Equal(
            ["切り取り", "コピー", "パスのコピー", "名前の変更", "削除", Separator,
                "開く", "ピン留めして開く", "ターミナルで開く", "エクスプローラーで表示", Separator,
                "新しいファイル", "新しいフォルダ", Separator, .. blank],
            Layout(fileMenu));
        Assert.Equal("Enter", Entry(fileMenu, "開く").Gesture);
        Assert.Empty(Disabled(fileMenu));

        // フォルダ。貼り付けはクリップボードにファイルがあるときだけ有効。
        var folderMenu = vm.BuildFolderTreeContextMenu(sub, canPaste: false);
        Assert.Equal(
            ["切り取り", "コピー", "パスのコピー", "名前の変更", "削除", Separator,
                "貼り付け", "ターミナルで開く", "エクスプローラーで開く", Separator,
                "新しいファイル", "新しいフォルダ", Separator, .. blank],
            Layout(folderMenu));
        Assert.Equal(["貼り付け"], Disabled(folderMenu));
        Assert.True(Entry(vm.BuildFolderTreeContextMenu(sub, canPaste: true), "貼り付け").IsEnabled);

        // フォルダツリーの根は切り取り・名前の変更・削除が無効（コピーはできる）。
        Assert.Equal(
            ["切り取り", "名前の変更", "削除", "貼り付け"],
            Disabled(vm.BuildFolderTreeContextMenu(root, canPaste: false)));

        // ドライブ直下はコピーも無効。
        var drive = new FolderTreeNode(
            new FolderTreeEntry(Path.GetPathRoot(folder)!, Path.GetPathRoot(folder)!, true, false), _ => { });
        Assert.Equal(
            ["切り取り", "コピー", "名前の変更", "削除", "貼り付け"],
            Disabled(vm.BuildFolderTreeContextMenu(drive, canPaste: false)));

        // 読み込み中の行と対象なしは余白のメニュー。
        var placeholder = sub.Children.Single(node => node.IsPlaceholder);
        Assert.Equal(blank, Layout(vm.BuildFolderTreeContextMenu(placeholder, true)));
        Assert.Equal(blank, Layout(vm.BuildFolderTreeContextMenu(null, true)));
        Assert.Equal(blank, Layout(vm.BuildFolderTreeBlankMenu()));

        // プロパティは対応する OS だけ。Mac は Finder の文言。
        shell.CanShowProperties = true;
        vm.UsesMacMenuText = true;
        Assert.Equal("プロパティ", Layout(vm.BuildFolderTreeContextMenu(file, false)).SkipWhile(item => item != "削除").ElementAt(1));
        Assert.Contains("Finder で表示", Layout(vm.BuildFolderTreeContextMenu(file, false)));
        Assert.Contains("Finder で開く", Layout(vm.BuildFolderTreeContextMenu(sub, false)));
    });

    [Fact]
    public void RunningAnEntryThatThrowsReportsAnErrorInsteadOfEscaping() => RunWithFiles(1, async (vm, dialogs, shell, paths) =>
    {
        var failing = new ContextMenuEntry("壊れた操作", () => throw new InvalidOperationException("理由"));

        await vm.RunMenuEntryAsync(failing);

        Assert.Equal(["理由"], dialogs.Errors);
        // 無効な項目は実行しない。
        var ran = false;
        await vm.RunMenuEntryAsync(new ContextMenuEntry("無効", () =>
        {
            ran = true;
            return Task.CompletedTask;
        }, IsEnabled: false));
        Assert.False(ran);
    });

    [Fact]
    public void MenuEntriesDriveTheTabActions() => RunWithFiles(3, async (vm, dialogs, shell, paths) =>
    {
        await vm.OpenPathsAsync(paths);
        var middle = TabOf(vm, paths[1]);

        await vm.RunMenuEntryAsync(Entry(vm.BuildTabContextMenu(middle, false), "タブをピン留め"));
        Assert.True(middle.IsPinned);

        await vm.RunMenuEntryAsync(Entry(vm.BuildTabContextMenu(middle, false), "パスのコピー"));
        Assert.Equal([FileNameRules.ToQuotedPath(paths[1])], dialogs.Copied);

        await vm.RunMenuEntryAsync(Entry(vm.BuildTabContextMenu(middle, false), "エクスプローラーで表示"));
        Assert.Equal([paths[1]], shell.CallsOf(FakeShellFileService.RevealOperation));

        await vm.RunMenuEntryAsync(Entry(vm.BuildTabContextMenu(middle, false), "タブを閉じる"));
        Assert.DoesNotContain(middle, vm.Tabs);
        Assert.Equal([paths[1]], vm.RecentClosedTabPaths);
    });

    [Fact]
    public void PinAndOpenFromTheTreeOpensAPermanentPinnedTab() => RunWithFiles(["a.txt"], async (vm, dialogs, shell, paths) =>
    {
        await vm.OpenFolderPathAsync(Path.GetDirectoryName(paths[0])!);
        var file = vm.FolderRoots[0].Children.Single(node => !node.IsDirectory);

        await vm.RunMenuEntryAsync(Entry(vm.BuildFolderTreeContextMenu(file, false), "ピン留めして開く"));

        var tab = TabOf(vm, paths[0]);
        Assert.True(tab.IsPinned);
        Assert.False(tab.IsPreview);
    });

    [Fact]
    public void PaletteOffersCopyPathAndRevealOnlyForTabsWithAFile() => RunWithFiles(1, async (vm, dialogs, shell, paths) =>
    {
        vm.CommandPaletteQuery = "パスのコピー";
        Assert.DoesNotContain(vm.CommandPaletteResults, entry => entry.Title == "パスのコピー");
        vm.CommandPaletteQuery = "エクスプローラーで表示";
        Assert.Empty(vm.CommandPaletteResults);

        await vm.OpenPathsAsync(paths);
        vm.CommandPaletteQuery = "パスのコピー";
        await Assert.Single(vm.CommandPaletteResults, entry => entry.Title == "パスのコピー").RunAsync();
        Assert.Equal([FileNameRules.ToQuotedPath(paths[0])], dialogs.Copied);

        vm.CommandPaletteQuery = "エクスプローラーで表示";
        await Assert.Single(vm.CommandPaletteResults).RunAsync();
        Assert.Equal([paths[0]], shell.CallsOf(FakeShellFileService.RevealOperation));
    });

    private static string[] Layout(IReadOnlyList<ContextMenuEntry> entries)
        => [.. entries.Select(entry => entry.IsSeparator ? Separator : entry.Header)];

    private static string[] Disabled(IReadOnlyList<ContextMenuEntry> entries)
        => [.. entries.Where(entry => !entry.IsSeparator && !entry.IsEnabled).Select(entry => entry.Header)];

    private static ContextMenuEntry Entry(IReadOnlyList<ContextMenuEntry> entries, string header)
        => entries.Single(entry => entry.Header == header);

    private static DocumentViewModel TabOf(MainWindowViewModel vm, string path)
        => vm.Documents.First(document => document.FilePath == path);

    private void RunWithFiles(int count, Func<MainWindowViewModel, StubDialogs, FakeShellFileService, string[], Task> body)
        => RunWithFiles(Enumerable.Range(0, count).Select(index => $"file{index:D2}.txt").ToArray(), body);

    private void RunWithFiles(
        string[] names, Func<MainWindowViewModel, StubDialogs, FakeShellFileService, string[], Task> body) => fixture.Run(() =>
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
        var shell = new FakeShellFileService(Path.Combine(storage.Path, "trash"));
        var vm = new MainWindowViewModel(new DocumentFileService(), dialogs, new AppSettings(), shellFiles: shell);
        try
        {
            SingleThreadedAsync.Run(() => body(vm, dialogs, shell, paths));
        }
        finally
        {
            vm.DisposeFolderTree();
        }
    });

    private sealed class StubDialogs : IEditorDialogService
    {
        public List<string> Copied { get; } = [];

        public List<string> Errors { get; } = [];

        public Task<IReadOnlyList<string>> PickOpenPathsAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);

        public Task<UnsavedDocumentDecision> ConfirmUnsavedAsync(string documentName)
            => Task.FromResult(UnsavedDocumentDecision.Discard);

        public Task ShowErrorAsync(string title, string message)
        {
            Errors.Add(message);
            return Task.CompletedTask;
        }

        public Task<int?> PickLineNumberAsync(int currentLine, int maximumLine) => Task.FromResult<int?>(null);

        public Task<string?> PromptTextAsync(string title, string message, string initialText)
            => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<GrepQuery?> PickGrepQueryAsync(GrepQuery initial) => Task.FromResult<GrepQuery?>(null);

        public Task CheckForUpdatesAsync(bool manually) => Task.CompletedTask;

        public Task CopyTextAsync(string text)
        {
            Copied.Add(text);
            return Task.CompletedTask;
        }
    }
}
