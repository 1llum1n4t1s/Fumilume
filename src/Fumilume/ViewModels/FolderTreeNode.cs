using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Fumilume.Services;

namespace Fumilume.ViewModels;

public sealed partial class FolderTreeNode : ObservableObject
{
    private readonly Action<FolderTreeNode>? _expanded;

    internal FolderTreeNode(FolderTreeEntry entry, Action<FolderTreeNode> expanded)
    {
        FullPath = entry.FullPath;
        Name = entry.Name;
        IsDirectory = entry.IsDirectory;
        IsReparsePoint = entry.IsReparsePoint;
        _expanded = expanded;
        // 未読込のフォルダーにも TreeView の展開操作を表示する。
        if (CanExpand) Children.Add(new FolderTreeNode());
    }

    private FolderTreeNode() { Name = "読み込み中…"; FullPath = string.Empty; IsPlaceholder = true; }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public bool IsReparsePoint { get; }
    public bool IsPlaceholder { get; }
    public bool CanExpand => IsDirectory && !IsReparsePoint;
    public string Glyph => IsDirectory ? "\uF42E" : "\uF378";
    public string Tooltip => IsReparsePoint && IsDirectory
        ? $"{FullPath}\nリンク先の循環を避けるため展開しません。" : FullPath;
    public ObservableCollection<FolderTreeNode> Children { get; } = [];
    internal bool IsLoaded { get; set; }
    internal int LoadVersion { get; set; }
    internal Task? LoadingTask { get; set; }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;
    public bool HasError => ErrorMessage is not null;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && CanExpand) _expanded?.Invoke(this);
    }
}
