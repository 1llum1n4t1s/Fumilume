using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fumilume.Services;

namespace Fumilume.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly FolderTreeService _folderTreeService = new();
    private CancellationTokenSource _folderLifetime = new();
    private IDisposable? _folderWatcher;
    private long _folderGeneration;
    private long _folderOpenGeneration;
    private long _folderRevealGeneration;
    private bool _folderDisposed;
    private bool _folderRefreshRunning;
    private bool _folderRefreshPending;
    private bool _folderRevealStale;

    public ObservableCollection<FolderTreeNode> FolderRoots { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolder))]
    private string? _folderPath;
    public bool HasFolder => FolderPath is not null;
    [ObservableProperty] private FolderTreeNode? _selectedFolderNode;
    [ObservableProperty] private bool _isFolderTreeVisible = true;

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var path = await _dialogs.PickFolderPathAsync();
        if (path is not null) await OpenFolderPathAsync(path);
    }

    public async Task OpenFolderPathAsync(string path)
    {
        await WaitForInitializationAsync();
        await OpenFolderPathCoreAsync(path);
    }

    private async Task OpenFolderPathCoreAsync(string path)
    {
        if (_folderDisposed) return;
        var request = ++_folderOpenGeneration;
        var generation = _folderGeneration;
        try
        {
            var fullPath = await _folderTreeService.ValidateFolderAsync(path, _folderLifetime.Token);
            if (request != _folderOpenGeneration || _folderDisposed) return;
            generation = ++_folderGeneration;
            ResetFolderTree();
            FolderPath = fullPath;
            IsFolderTreeVisible = true;
            var name = Path.GetFileName(fullPath);
            var root = CreateFolderNode(new(fullPath, string.IsNullOrEmpty(name) ? fullPath : name, true, false));
            FolderRoots.Add(root);
            try
            {
                var token = _folderLifetime.Token;
                var watcher = await Task.Run(() => _folderTreeService.Watch(fullPath,
                    () => Dispatcher.UIThread.Post(() =>
                    {
                        if (generation == _folderGeneration && !_folderDisposed)
                            _ = RefreshFolderAsync();
                    })), token);
                if (generation != _folderGeneration || _folderDisposed || token.IsCancellationRequested)
                {
                    watcher.Dispose();
                    return;
                }
                _folderWatcher = watcher;
            }
            catch (Exception ex) when (FolderTreeService.IsFileSystemError(ex))
            {
                StatusMessage = $"フォルダーの変更を監視できません: {ex.Message}";
            }
            if (generation != _folderGeneration || _folderDisposed) return;
            root.IsExpanded = true;
            await LoadFolderNodeAsync(root, generation);
            await RevealSelectedFileAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (FolderTreeService.IsFileSystemError(ex))
        {
            if (request == _folderOpenGeneration && !_folderDisposed)
                StatusMessage = $"フォルダーを開けません: {ex.Message}";
        }
    }

    private FolderTreeNode CreateFolderNode(FolderTreeEntry entry)
        => new(entry, node => _ = LoadFolderNodeAsync(node, _folderGeneration));

    [RelayCommand]
    private void CloseFolder()
    {
        ++_folderOpenGeneration;
        ++_folderGeneration;
        ResetFolderTree();
        FolderPath = null;
        IsFolderTreeVisible = true;
    }

    private void ResetFolderTree()
    {
        _folderWatcher?.Dispose();
        _folderWatcher = null;
        _folderLifetime.Cancel();
        _folderLifetime.Dispose();
        _folderLifetime = new();
        FolderRoots.Clear();
        SelectedFolderNode = null;
    }

    private async Task LoadFolderNodeAsync(FolderTreeNode node, long generation, bool refresh = false)
    {
        if (node.LoadingTask is { IsCompleted: false } loading)
        {
            await loading;
            if (!refresh) return;
        }
        if (!node.CanExpand || (node.IsLoaded && !refresh) || _folderDisposed
            || generation != _folderGeneration) return;
        var task = LoadFolderNodeCoreAsync(node, generation, refresh);
        node.LoadingTask = task;
        await task;
    }

    private async Task LoadFolderNodeCoreAsync(FolderTreeNode node, long generation, bool refresh)
    {
        node.IsLoading = true;
        var version = ++node.LoadVersion;
        try
        {
            var listing = await _folderTreeService.ListAsync(node.FullPath, _folderLifetime.Token);
            if (generation != _folderGeneration || version != node.LoadVersion || _folderDisposed) return;
            var previous = new Dictionary<string, FolderTreeNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in node.Children.Where(child => !child.IsPlaceholder))
                previous.TryAdd(child.FullPath, child);
            var next = listing.Entries.Select(entry => previous.TryGetValue(entry.FullPath, out var old)
                && old.Name == entry.Name && old.IsDirectory == entry.IsDirectory
                && old.IsReparsePoint == entry.IsReparsePoint ? old : CreateFolderNode(entry)).ToArray();
            // 同一ノードを外さず更新し、TreeView の選択・キーボードフォーカスを維持する。
            for (var index = 0; index < next.Length; index++)
            {
                var child = next[index];
                if (index < node.Children.Count && ReferenceEquals(node.Children[index], child))
                    continue;
                var oldIndex = node.Children.IndexOf(child);
                if (oldIndex < 0) node.Children.Insert(index, child);
                else if (oldIndex != index) node.Children.Move(oldIndex, index);
            }
            while (node.Children.Count > next.Length) node.Children.RemoveAt(node.Children.Count - 1);
            node.ErrorMessage = listing.ErrorMessage;
            node.IsLoaded = true;
            if (listing.ErrorMessage is not null) StatusMessage = listing.ErrorMessage;
            if (refresh)
            {
                foreach (var child in next.Where(child => child.IsLoaded || child.IsExpanded))
                    await LoadFolderNodeAsync(child, generation, refresh: true);
            }
        }
        catch (OperationCanceledException) { }
        finally { if (version == node.LoadVersion) node.IsLoading = false; }
    }

    [RelayCommand]
    private async Task RefreshFolderAsync()
    {
        if (_folderDisposed || !HasFolder) return;
        if (_folderRefreshRunning) { _folderRefreshPending = true; return; }
        _folderRefreshRunning = true;
        try
        {
            do
            {
                _folderRefreshPending = false;
                var generation = _folderGeneration;
                var selectedPath = SelectedFolderNode?.FullPath;
                foreach (var root in FolderRoots.ToArray())
                    await LoadFolderNodeAsync(root, generation, refresh: true);
                if (generation != _folderGeneration) return;
                // 再読込ではユーザーの折りたたみ状態を維持し、残っている選択だけを引き継ぐ。
                if (selectedPath is not null)
                    SelectedFolderNode = FindLoadedFolderNode(selectedPath);
            } while (_folderRefreshPending && !_folderDisposed);
        }
        finally { _folderRefreshRunning = false; }
    }

    private FolderTreeNode? FindLoadedFolderNode(string path)
    {
        foreach (var root in FolderRoots)
        {
            var found = Find(root);
            if (found is not null) return found;
        }
        return null;

        FolderTreeNode? Find(FolderTreeNode node)
        {
            if (string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase)) return node;
            foreach (var child in node.Children)
            {
                var found = Find(child);
                if (found is not null) return found;
            }
            return null;
        }
    }

    [RelayCommand]
    private void CollapseFolders()
    {
        foreach (var root in FolderRoots) Collapse(root);
        static void Collapse(FolderTreeNode node)
        {
            node.IsExpanded = false;
            foreach (var child in node.Children) Collapse(child);
        }
    }

    [RelayCommand]
    private void ToggleFolderTree() => IsFolderTreeVisible = !IsFolderTreeVisible;

    public async Task OpenFolderNodeAsync(FolderTreeNode node, bool permanent)
    {
        if (node.IsPlaceholder) return;
        if (node.IsDirectory)
        {
            if (node.CanExpand) node.IsExpanded = !node.IsExpanded;
            return;
        }
        await OpenFolderFileAsync(node.FullPath, permanent);
    }

    [RelayCommand]
    private async Task RevealFileInFolderAsync()
    {
        IsFolderTreeVisible = true;
        await RevealSelectedFileAsync();
    }

    private void OnFolderSelectedTabChanged()
    {
        _folderRevealStale = true;
        if (IsFolderTreeVisible) _ = RevealSelectedFileAsync();
    }

    partial void OnIsFolderTreeVisibleChanged(bool value)
    {
        if (value && _folderRevealStale) _ = RevealSelectedFileAsync();
        else if (!value)
        {
            ++_folderRevealGeneration;
            _folderRevealStale = true;
        }
    }

    private async Task RevealSelectedFileAsync()
    {
        _folderRevealStale = false;
        var revealGeneration = ++_folderRevealGeneration;
        var generation = _folderGeneration;
        var filePath = SelectedDocument?.FilePath ?? SelectedPdf?.FilePath;
        if (FolderPath is null || filePath is null || FolderRoots.Count == 0) return;
        var relative = Path.GetRelativePath(FolderPath, filePath);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var node = FolderRoots[0];
        foreach (var segment in segments)
        {
            await LoadFolderNodeAsync(node, generation);
            if (generation != _folderGeneration || revealGeneration != _folderRevealGeneration || _folderDisposed) return;
            var child = node.Children.FirstOrDefault(item => string.Equals(item.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (child is null) return;
            node.IsExpanded = true;
            node = child;
        }
        SelectedFolderNode = node;
    }

    public void DisposeFolderTree()
    {
        if (_folderDisposed) return;
        _folderDisposed = true;
        ++_folderOpenGeneration;
        ++_folderGeneration;
        ResetFolderTree();
        _folderLifetime.Dispose();
    }
}
