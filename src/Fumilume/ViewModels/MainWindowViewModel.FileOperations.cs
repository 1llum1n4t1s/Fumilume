using Fumilume.Models;
using Fumilume.Services;

namespace Fumilume.ViewModels;

/// <summary>
/// ファイル・フォルダへのシェル操作（改名・削除・切り取り/コピー/貼り付け・新規作成・表示・ターミナル・プロパティ）と、
/// 開いているタブとの整合。タブの右クリックメニューとフォルダツリーのメニューの両方が、パスを受ける中核を呼ぶ。
///
/// 整合の原則: シェル操作の間は <c>_openPathsGate</c> を保持し、外部変更の監視・保存・オープンと競合させない。
/// ゲートを保持したまま確認付きの閉じ方（<c>CloseTabCoreAsync</c>）は呼ばない（保存がゲートを取り直して詰まる）。
/// ゲート内では確認を出さない <see cref="RemoveTabCore"/> だけを使う。
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>操作前に閉じた PDF の表示状態。操作後に同じ見え方で開き直す。</summary>
    private sealed record ClosedPdfState(
        string Path,
        int Index,
        bool IsPinned,
        bool IsPreview,
        PdfZoomMode ZoomMode,
        double Zoom,
        int PageIndex,
        double Fraction);

    // ===== 有効判定 =====

    /// <summary>ディスク上にあるファイル・フォルダか。</summary>
    private static bool ExistsOnDisk(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>タブのファイルに対する切り取り・コピー・名前の変更・削除・表示などが使えるか（パスがありディスクに存在する）。</summary>
    public bool CanOperateOnTabFile(WorkspaceTabViewModel tab)
        => GetTabFilePath(tab) is { } path && File.Exists(path);

    /// <summary>タブのプロパティを出せるか（Windows のみ。ディスクに無いファイルは不可）。</summary>
    public bool CanShowTabProperties(WorkspaceTabViewModel tab)
        => ShellFiles.CanShowProperties && CanOperateOnTabFile(tab);

    public bool CanRenameNode(FolderTreeNode node) => CanModifyNode(node);

    public bool CanCutNode(FolderTreeNode node) => CanModifyNode(node);

    public bool CanDeleteNode(FolderTreeNode node) => CanModifyNode(node);

    /// <summary>コピーできるか（プレースホルダーとドライブ直下は不可。フォルダツリーの根はコピーできる）。</summary>
    public bool CanCopyNode(FolderTreeNode node)
        => !node.IsPlaceholder && !FileNameRules.IsFileSystemRoot(node.FullPath);

    /// <summary>パスのコピー・ターミナル・表示が使えるか。</summary>
    public bool CanUseNodePath(FolderTreeNode node) => !node.IsPlaceholder;

    public bool CanShowNodeProperties(FolderTreeNode node)
        => ShellFiles.CanShowProperties && !node.IsPlaceholder && ExistsOnDisk(node.FullPath);

    /// <summary>貼り付けできるか（フォルダのノードで、クリップボードにファイルがあるとき）。</summary>
    public bool CanPasteIntoNode(FolderTreeNode node, bool clipboardHasFiles)
        => clipboardHasFiles && !node.IsPlaceholder && node.IsDirectory;

    /// <summary>名前の変更・切り取り・削除の対象にできるか（フォルダツリーの根とドライブ直下は不可）。</summary>
    private bool CanModifyNode(FolderTreeNode node)
        => !node.IsPlaceholder
            && !FolderRoots.Contains(node)
            && !FileNameRules.IsFileSystemRoot(node.FullPath);

    // ===== タブ・ノード向けの薄い窓口 =====

    public Task RenameTabFileAsync(WorkspaceTabViewModel tab)
        => OnTabFileAsync(tab, RenamePathAsync);

    public Task DeleteTabFileAsync(WorkspaceTabViewModel tab)
        => OnTabFileAsync(tab, DeletePathAsync);

    public Task CutTabFileAsync(WorkspaceTabViewModel tab)
        => OnTabFileAsync(tab, path => SetClipboardAsync(path, isCut: true));

    public Task CopyTabFileAsync(WorkspaceTabViewModel tab)
        => OnTabFileAsync(tab, path => SetClipboardAsync(path, isCut: false));

    public Task CopyTabFilePathAsync(WorkspaceTabViewModel tab)
        => GetTabFilePath(tab) is { } path ? CopyPathTextAsync(path) : Task.CompletedTask;

    public Task RevealTabFileAsync(WorkspaceTabViewModel tab)
        => OnTabFileAsync(tab, RevealPathAsync);

    public Task OpenTerminalForTabAsync(WorkspaceTabViewModel tab)
        => GetTabFilePath(tab) is { } path ? OpenTerminalAtAsync(path) : Task.CompletedTask;

    public Task ShowTabPropertiesAsync(WorkspaceTabViewModel tab)
        => CanShowTabProperties(tab) ? ShowPropertiesOfAsync(GetTabFilePath(tab)!) : Task.CompletedTask;

    public Task RenameNodeAsync(FolderTreeNode node)
        => CanRenameNode(node) ? RenamePathAsync(node.FullPath) : Task.CompletedTask;

    public Task DeleteNodeAsync(FolderTreeNode node)
        => CanDeleteNode(node) ? DeletePathAsync(node.FullPath) : Task.CompletedTask;

    public Task CutNodeAsync(FolderTreeNode node)
        => CanCutNode(node) ? SetClipboardAsync(node.FullPath, isCut: true) : Task.CompletedTask;

    public Task CopyNodeAsync(FolderTreeNode node)
        => CanCopyNode(node) ? SetClipboardAsync(node.FullPath, isCut: false) : Task.CompletedTask;

    public Task CopyNodePathAsync(FolderTreeNode node)
        => CanUseNodePath(node) ? CopyPathTextAsync(node.FullPath) : Task.CompletedTask;

    public Task PasteIntoNodeAsync(FolderTreeNode node)
        => !node.IsPlaceholder && node.IsDirectory ? PasteIntoFolderAsync(node.FullPath) : Task.CompletedTask;

    /// <summary>ノードがフォルダならその中、ファイルならその親に新しいファイルを作る。</summary>
    public Task CreateFileForNodeAsync(FolderTreeNode node)
        => node.IsPlaceholder ? Task.CompletedTask : CreateItemAsync(node.FullPath, isFolder: false);

    public Task CreateFolderForNodeAsync(FolderTreeNode node)
        => node.IsPlaceholder ? Task.CompletedTask : CreateItemAsync(node.FullPath, isFolder: true);

    /// <summary>ファイルは選択状態で表示、フォルダは中を開く（Windows は「エクスプローラーで開く」、Mac は Finder で開く）。</summary>
    public Task RevealNodeAsync(FolderTreeNode node)
        => CanUseNodePath(node)
            ? node.IsDirectory ? OpenFolderInShellAsync(node.FullPath) : RevealPathAsync(node.FullPath)
            : Task.CompletedTask;

    public Task OpenTerminalForNodeAsync(FolderTreeNode node)
        => CanUseNodePath(node) ? OpenTerminalAtAsync(node.FullPath) : Task.CompletedTask;

    public Task ShowNodePropertiesAsync(FolderTreeNode node)
        => CanShowNodeProperties(node) ? ShowPropertiesOfAsync(node.FullPath) : Task.CompletedTask;

    private Task OnTabFileAsync(WorkspaceTabViewModel tab, Func<string, Task> action)
        => CanOperateOnTabFile(tab) ? action(GetTabFilePath(tab)!) : Task.CompletedTask;

    // ===== パスを受ける中核 =====

    /// <summary>入力ダイアログで新しい名前を尋ねて改名する。</summary>
    internal async Task RenamePathAsync(string path)
    {
        path = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(path);
        if (parent is null || FileNameRules.IsFileSystemRoot(path) || !ExistsOnDisk(path))
        {
            return;
        }

        var current = Path.GetFileName(path);
        var input = await _dialogs.PromptTextAsync("名前の変更", "新しい名前を入力してください。", current);
        if (input is null)
        {
            return;
        }

        var name = FileNameRules.Normalize(input);
        if (string.Equals(name, current, StringComparison.Ordinal))
        {
            return;
        }

        var target = Path.Combine(parent, name);
        var error = FileNameRules.Validate(name) ?? FindNameConflict(target, name, path);
        if (error is not null)
        {
            await _dialogs.ShowErrorAsync("名前を変更できません", error);
            return;
        }

        var result = await RunShellOperationAsync([path], () => ShellFiles.RenameAsync(path, name), detachPdfs: true);
        await ReportAsync("名前を変更できません", result, $"{name} に名前を変更しました");
    }

    /// <summary>ごみ箱へ移す。確認は出さない（Windows はシェルが必要に応じて確認し、ごみ箱から戻せる）。</summary>
    internal async Task DeletePathAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (FileNameRules.IsFileSystemRoot(path) || !ExistsOnDisk(path))
        {
            return;
        }

        var result = await RunShellOperationAsync([path], () => ShellFiles.MoveToTrashAsync([path]), detachPdfs: true);
        // 未保存の編集を残した文書があるときは、その通知（ディスク上から削除）を上書きしない。
        var keptEdits = Documents.Any(document => document.IsModified && document.FilePath is { } tabPath
            && FileNameRules.IsSameOrDescendant(tabPath, path));
        await ReportAsync(
            "削除できません", result, keptEdits ? null : $"{Path.GetFileName(path)} を削除しました");
    }

    /// <summary>クリップボードへ切り取り・コピーとして入れる。</summary>
    internal async Task SetClipboardAsync(string path, bool isCut)
    {
        path = Path.GetFullPath(path);
        if (!ExistsOnDisk(path) || FileNameRules.IsFileSystemRoot(path) && isCut)
        {
            return;
        }

        if (await ShellFiles.SetFileClipboardAsync([path], isCut))
        {
            StatusMessage = isCut
                ? $"{Path.GetFileName(path)} を切り取りました"
                : $"{Path.GetFileName(path)} をコピーしました";
        }
        else
        {
            await _dialogs.ShowErrorAsync("クリップボードへ入れられません", "ファイルをクリップボードへ入れられませんでした。");
        }
    }

    /// <summary>クリップボードのファイルを <paramref name="folder"/> へ貼り付ける（切り取りは移動）。</summary>
    internal async Task PasteIntoFolderAsync(string folder)
    {
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)
            || await ShellFiles.GetFileClipboardAsync() is not { Paths.Count: > 0 } clipboard)
        {
            return;
        }

        var move = clipboard.IsCut;
        var sources = clipboard.Paths.Select(Path.GetFullPath).ToList();
        var sameFolder = sources.Where(source => IsDirectChild(source, folder)).ToList();
        if (move)
        {
            // 切り取ったフォルダへ貼り付けても動かすものは無い。
            sources.RemoveAll(sameFolder.Contains);
            if (sources.Count == 0)
            {
                StatusMessage = "貼り付け先は移動元と同じフォルダです";
                return;
            }
        }

        var renameOnCollision = !move && sameFolder.Count > 0;
        var error = FindPasteConflict(sources, folder, renameOnCollision);
        if (error is not null)
        {
            await _dialogs.ShowErrorAsync("貼り付けできません", error);
            return;
        }

        var result = await RunShellOperationAsync(
            sources,
            () => ShellFiles.TransferAsync(sources, folder, move, renameOnCollision),
            detachPdfs: move,
            remapTabs: move);
        if (move && result.IsSuccess)
        {
            await ShellFiles.ClearFileClipboardIfUnchangedAsync(clipboard.Token);
        }

        await ReportAsync(
            move ? "移動できません" : "コピーできません",
            result,
            move ? $"{sources.Count:N0} 件を移動しました" : $"{sources.Count:N0} 件を貼り付けました");
    }

    /// <summary>
    /// 新しいファイル・フォルダを作る。作成先は <paramref name="basePath"/> がフォルダならその中、ファイルならその親。
    /// 作ったファイルは開き、フォルダはツリーで選択する。
    /// </summary>
    internal async Task CreateItemAsync(string basePath, bool isFolder)
    {
        basePath = Path.GetFullPath(basePath);
        var parent = Directory.Exists(basePath) ? basePath : Path.GetDirectoryName(basePath);
        if (parent is null || !Directory.Exists(parent))
        {
            return;
        }

        var title = isFolder ? "新しいフォルダ" : "新しいファイル";
        var input = await _dialogs.PromptTextAsync(
            title, isFolder ? "フォルダの名前を入力してください。" : "ファイルの名前を入力してください。",
            isFolder ? "新しいフォルダ" : "新しいファイル.txt");
        if (input is null)
        {
            return;
        }

        var name = FileNameRules.Normalize(input);
        var target = Path.Combine(parent, name);
        var error = FileNameRules.Validate(name) ?? FindNameConflict(target, name, null);
        if (error is not null)
        {
            await _dialogs.ShowErrorAsync($"{title}を作れません", error);
            return;
        }

        var result = await RunShellOperationAsync(
            [],
            () => isFolder ? ShellFiles.CreateFolderAsync(target) : ShellFiles.CreateFileAsync(target),
            detachPdfs: false);
        await ReportAsync($"{title}を作れません", result, $"{name} を作成しました");
        if (!result.IsSuccess)
        {
            return;
        }

        if (FindLoadedFolderNode(parent) is { } parentNode)
        {
            parentNode.IsExpanded = true;
            await RefreshFolderAsync();
            if (FindLoadedFolderNode(target) is { } created)
            {
                SelectedFolderNode = created;
            }
        }

        if (!isFolder)
        {
            await OpenFolderFileAsync(target, permanent: true);
        }
    }

    /// <summary>パスを <c>"パス"</c> の形でテキストクリップボードへコピーする。</summary>
    internal Task CopyPathTextAsync(string path)
        => CopyTabTextAsync(FileNameRules.ToQuotedPath(Path.GetFullPath(path)));

    internal Task RevealPathAsync(string path)
        => ReportShellAsync("エクスプローラーで表示できません", () => ShellFiles.Reveal(Path.GetFullPath(path)));

    /// <summary>フォルダの中身をエクスプローラー・Finder で開く（<c>Reveal</c> は親の中で選択表示）。</summary>
    internal Task OpenFolderInShellAsync(string folder)
        => ReportShellAsync("フォルダを開けません", () => ShellFiles.OpenFolder(Path.GetFullPath(folder)));

    /// <summary>ターミナルを開く。ファイルのときはその親フォルダで開く。</summary>
    internal Task OpenTerminalAtAsync(string path)
    {
        path = Path.GetFullPath(path);
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        return folder is null
            ? Task.CompletedTask
            : ReportShellAsync("ターミナルを開けません", () => ShellFiles.OpenTerminal(folder));
    }

    internal Task ShowPropertiesOfAsync(string path)
        => ShellFiles.CanShowProperties
            ? ReportShellAsync("プロパティを表示できません", () => ShellFiles.ShowProperties(Path.GetFullPath(path)))
            : Task.CompletedTask;

    private async Task ReportShellAsync(string title, Func<ShellOperationResult> action)
    {
        var result = action();
        if (result.IsFailed)
        {
            await _dialogs.ShowErrorAsync(title, result.ErrorMessage ?? "操作に失敗しました。");
        }
    }

    /// <summary>失敗はエラーダイアログ、成功はステータス、取り消しは何も出さない。</summary>
    private async Task ReportAsync(string failureTitle, ShellOperationResult result, string? successMessage)
    {
        if (result.IsFailed)
        {
            await _dialogs.ShowErrorAsync(failureTitle, result.ErrorMessage ?? "操作に失敗しました。");
        }
        else if (result.IsSuccess && successMessage is not null)
        {
            StatusMessage = successMessage;
        }
    }

    // ===== 事前の検査 =====

    private static bool IsDirectChild(string path, string folder)
        => Path.GetDirectoryName(path) is { } parent && FileNameRules.ArePathsEqual(parent, folder);

    /// <summary>同名の項目がある、またはその位置にタブが開いているときの文言。大文字小文字だけの改名は許す。</summary>
    private string? FindNameConflict(string target, string name, string? renaming)
    {
        var caseOnly = renaming is not null && FileNameRules.ArePathsEqual(target, renaming);
        if (caseOnly)
        {
            return null;
        }

        return ExistsOnDisk(target) || IsAnyTabOpenUnder(target)
            ? FileNameRules.ExistsMessage(name)
            : null;
    }

    private string? FindPasteConflict(IReadOnlyList<string> sources, string folder, bool renameOnCollision)
    {
        foreach (var source in sources)
        {
            if (Directory.Exists(source) && FileNameRules.IsSameOrDescendant(folder, source))
            {
                return $"フォルダ「{Path.GetFileName(source)}」は、自分自身またはその中へは貼り付けできません。";
            }

            var target = Path.Combine(folder, Path.GetFileName(source));
            if (!renameOnCollision && IsAnyTabOpenUnder(target))
            {
                return $"「{Path.GetFileName(source)}」と同じ名前のタブが開いています。タブを閉じてからもう一度貼り付けてください。";
            }
        }

        return null;
    }

    private bool IsAnyTabOpenUnder(string path)
        => Tabs.Any(tab => GetTabFilePath(tab) is { } tabPath && FileNameRules.IsSameOrDescendant(tabPath, path));

    // ===== 整合の中核 =====

    /// <summary>
    /// 影響する PDF を閉じ、<paramref name="operation"/> を実行し、結果（<see cref="ShellOperationResult.Completed"/>）に合わせて
    /// 開いている文書・PDF・カーソル位置・閉じた履歴を付け替える。途中で失敗・取り消しになっても済んだ分は付け替える。
    /// すべて <c>_openPathsGate</c> を保持したまま行い、確認付きの閉じ方は呼ばない。ゲートを放してからフォルダツリーを更新する。
    /// </summary>
    internal async Task<ShellOperationResult> RunShellOperationAsync(
        IReadOnlyList<string> affectedPaths,
        Func<Task<ShellOperationResult>> operation,
        bool detachPdfs,
        bool remapTabs = true)
    {
        await WaitForInitializationAsync();
        ShellOperationResult result;
        await _openPathsGate.WaitAsync();
        try
        {
            var selectedBefore = SelectedTab;
            var closedPdfs = detachPdfs ? ClosePdfsUnder(affectedPaths) : [];
            try
            {
                result = await operation();
            }
            catch (Exception ex)
            {
                AppLogger.For<MainWindowViewModel>().Error("シェル操作に失敗しました。", ex);
                result = ShellOperationResult.Failed(ex.Message);
            }

            // コピーは元のファイルが残るので、タブ・控え・履歴は付け替えない（ツリー更新だけ）。
            var changes = remapTabs
                ? result.Completed
                    .Where(item => !string.Equals(item.Source, item.Destination, StringComparison.Ordinal))
                    .ToArray()
                : [];
            ApplyChangesToTabs(changes);
            await ReopenPdfsAsync(closedPdfs, changes, selectedBefore);
        }
        finally
        {
            _openPathsGate.Release();
        }

        await RefreshFolderAsync();
        return result;
    }

    /// <summary>パスが <paramref name="changes"/> の対象（または配下）なら、操作後のパスを返す。削除は空文字列。</summary>
    private static bool TryRebase(string path, IReadOnlyList<ShellTransfer> changes, out string rebased)
    {
        ShellTransfer? best = null;
        foreach (var change in changes)
        {
            if (FileNameRules.IsSameOrDescendant(path, change.Source)
                && (best is null || change.Source.Length > best.Value.Source.Length))
            {
                best = change;
            }
        }

        if (best is not { } match)
        {
            rebased = string.Empty;
            return false;
        }

        if (match.Destination.Length == 0)
        {
            rebased = string.Empty;
            return true;
        }

        var source = match.Source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relative = path.Length > source.Length
            ? path[source.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : string.Empty;
        rebased = relative.Length == 0 ? match.Destination : Path.Combine(match.Destination, relative);
        return true;
    }

    /// <summary>操作の対象（配下を含む）の PDF を、表示状態を控えてから閉じる（ファイルの保持を外す）。確認は出さない。</summary>
    private List<ClosedPdfState> ClosePdfsUnder(IReadOnlyList<string> paths)
    {
        var states = new List<ClosedPdfState>();
        var targets = Tabs.OfType<PdfDocumentViewModel>()
            .Where(pdf => paths.Any(path => FileNameRules.IsSameOrDescendant(pdf.FilePath, path)))
            .ToArray();
        // 閉じると後ろの位置が詰まるので、先に全部の元の位置を取る。
        var indexes = targets.Select(pdf => Tabs.IndexOf(pdf)).ToArray();
        for (var i = 0; i < targets.Length; i++)
        {
            var pdf = targets[i];
            var (pageIndex, fraction) = pdf.CaptureTopPosition();
            states.Add(new ClosedPdfState(
                pdf.FilePath,
                indexes[i],
                pdf.IsPinned,
                pdf.IsPreview,
                pdf.ZoomMode,
                pdf.Zoom,
                pageIndex,
                fraction));
            RemoveTabCore(pdf);
        }

        return states;
    }

    /// <summary>済んだ変更に合わせて文書を付け替え・外し、カーソル位置と閉じた履歴を付け替える。</summary>
    private void ApplyChangesToTabs(IReadOnlyList<ShellTransfer> changes)
    {
        if (changes.Count == 0)
        {
            return;
        }

        var relocated = new List<DocumentViewModel>();
        var plan = new List<(DocumentViewModel Document, string Rebased)>();
        foreach (var document in Documents.ToArray())
        {
            if (document.FilePath is { } path && TryRebase(path, changes, out var rebased))
            {
                plan.Add((document, rebased));
            }
        }

        foreach (var (document, rebased) in plan)
        {
            if (!Tabs.Contains(document))
            {
                continue;
            }

            if (rebased.Length == 0)
            {
                ApplyDeletion(document);
            }
            else if (FindOpenFileTab(rebased, document) is not null)
            {
                // 上書きで、移動先に別のタブが既にある。同じパスのタブを 2 つにしない。
                // 未保存の本文は失わず、ディスクに無い状態で残す。
                ApplyDeletion(document);
            }
            else
            {
                document.Relocate(rebased);
                relocated.Add(document);
            }
        }

        foreach (var change in changes)
        {
            if (change.Destination.Length == 0)
            {
                _closedTabPaths.RemoveAll(entry => FileNameRules.IsSameOrDescendant(entry, change.Source));
            }
            else
            {
                RemapClosedTabPaths(change.Source, change.Destination);
            }
        }

        var persist = RemapCaretPositions(changes);
        foreach (var document in relocated)
        {
            persist |= RecordCaretPosition(document);
        }

        if (persist)
        {
            Options.Persist();
        }
    }

    /// <summary>ディスクから消えた文書。未編集なら外し、未保存の編集があれば本文を残して「ディスク上から削除」の状態にする。</summary>
    private void ApplyDeletion(DocumentViewModel document)
    {
        if (document.IsModified)
        {
            document.MarkMissingOnDisk();
            StatusMessage = $"{document.DisplayName} はディスク上から削除されました（編集中の内容は保持しています）";
        }
        else
        {
            RemoveTabCore(document);
        }
    }

    /// <summary>カーソル位置の控えのキーを付け替える（削除は控えを消す）。変えたら true。</summary>
    private bool RemapCaretPositions(IReadOnlyList<ShellTransfer> changes)
    {
        var positions = Options.Settings.CaretPositions;
        var rebuilt = new Dictionary<string, int>(positions.Comparer);
        var changed = false;
        foreach (var (path, offset) in positions)
        {
            if (!TryRebase(path, changes, out var rebased))
            {
                rebuilt[path] = offset;
                continue;
            }

            changed = true;
            if (rebased.Length > 0)
            {
                rebuilt[rebased] = offset;
            }
        }

        if (changed)
        {
            Options.Settings.CaretPositions = rebuilt;
        }

        return changed;
    }

    /// <summary>閉じていた PDF を新しいパス（無ければ元のパス）で開き直し、位置・倍率・ピン・並び・選択を戻す。削除されたものは開かない。</summary>
    private async Task ReopenPdfsAsync(
        List<ClosedPdfState> closed, IReadOnlyList<ShellTransfer> changes, WorkspaceTabViewModel? selectedBefore)
    {
        WorkspaceTabViewModel? reselect = null;
        var selectedPdfPath = (selectedBefore as PdfDocumentViewModel)?.FilePath;
        foreach (var state in closed.OrderBy(item => item.Index))
        {
            var target = state.Path;
            if (TryRebase(state.Path, changes, out var rebased))
            {
                if (rebased.Length == 0)
                {
                    continue;
                }

                target = rebased;
            }

            var pdf = await OpenPdfTabAsync(target);
            if (pdf is null && !FileNameRules.ArePathsEqual(target, state.Path))
            {
                pdf = await OpenPdfTabAsync(state.Path);
            }

            if (pdf is null)
            {
                continue;
            }

            await pdf.RestoreViewAsync(
                state.ZoomMode, state.Zoom > 0 ? state.Zoom : null, state.PageIndex, state.Fraction);
            pdf.IsPreview = state.IsPreview;
            if (state.IsPinned)
            {
                pdf.IsPinned = true;
            }

            var start = GroupStart(pdf);
            MoveTab(pdf, Math.Clamp(state.Index, start, Math.Max(start, GroupEnd(pdf))));
            if (selectedPdfPath is not null && FileNameRules.ArePathsEqual(state.Path, selectedPdfPath))
            {
                reselect = pdf;
            }
        }

        if (reselect is not null)
        {
            SelectedTab = reselect;
        }
        else if (selectedBefore is not null && Tabs.Contains(selectedBefore))
        {
            SelectedTab = selectedBefore;
        }
    }

    private async Task<PdfDocumentViewModel?> OpenPdfTabAsync(string path)
    {
        // 開き直しなので大容量の確認は出さない（「いいえ」でタブを失わない）。
        await OpenPathsCoreAsync([path], skipLargeFileConfirm: true);
        return FindOpenFileTab(Path.GetFullPath(path)) as PdfDocumentViewModel;
    }
}
