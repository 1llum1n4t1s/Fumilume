using Fumilume.Services;

namespace Fumilume.Tests;

/// <summary>ManagedFileTransfer.Transfer（macOS のコピー・移動）。一時フォルダで実際のファイルを使う。</summary>
public sealed class ManagedFileTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fumilume-transfer-" + Guid.NewGuid().ToString("N"));

    public ManagedFileTransferTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Make(string relative, string? content = "x")
    {
        var path = Path.Combine(_root, relative);
        if (content is null)
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return path;
    }

    [Fact]
    public void CopyingIntoTheSameFolderNumbersTheCopies()
    {
        var source = Make("a.txt");

        var first = ManagedFileTransfer.Transfer([source], _root, move: false, renameOnCollision: true);
        var second = ManagedFileTransfer.Transfer([source], _root, move: false, renameOnCollision: true);

        Assert.True(first.IsSuccess);
        Assert.Equal(Path.Combine(_root, "a のコピー.txt"), Assert.Single(first.Completed).Destination);
        Assert.Equal(Path.Combine(_root, "a のコピー 2.txt"), Assert.Single(second.Completed).Destination);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void CollisionsInAnotherFolderAreSkippedAndCounted()
    {
        var a = Make("src/a.txt", "new");
        var b = Make("src/b.txt");
        var c = Make("src/c.txt");
        Make("dst/a.txt", "old");
        Make("dst/c.txt", "old");

        var result = ManagedFileTransfer.Transfer([a, b, c], Path.Combine(_root, "dst"), move: false, renameOnCollision: false);

        Assert.True(result.IsFailed);
        Assert.Contains("2 件", result.ErrorMessage);
        Assert.Equal(Path.Combine(_root, "dst", "b.txt"), Assert.Single(result.Completed).Destination);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, "dst", "a.txt")));
    }

    [Fact]
    public void MovingAFolderMovesItsContents()
    {
        var folder = Make(Path.Combine("src", "dir"), content: null);
        Make("src/dir/inner/file.txt", "body");
        Directory.CreateDirectory(Path.Combine(_root, "dst"));

        var result = ManagedFileTransfer.Transfer([folder], Path.Combine(_root, "dst"), move: true, renameOnCollision: false);

        Assert.True(result.IsSuccess);
        Assert.Equal(new ShellTransfer(folder, Path.Combine(_root, "dst", "dir")), Assert.Single(result.Completed));
        Assert.False(Directory.Exists(folder));
        Assert.Equal("body", File.ReadAllText(Path.Combine(_root, "dst", "dir", "inner", "file.txt")));
    }

    [Fact]
    public void CopyingAFolderCopiesItsContentsAndKeepsTheSource()
    {
        var folder = Make("src/dir", content: null);
        Make("src/dir/inner/file.txt", "body");

        var result = ManagedFileTransfer.Transfer([folder], Path.Combine(_root, "src"), move: false, renameOnCollision: true);

        Assert.True(result.IsSuccess);
        Assert.Equal(Path.Combine(_root, "src", "dir のコピー"), Assert.Single(result.Completed).Destination);
        Assert.Equal("body", File.ReadAllText(Path.Combine(_root, "src", "dir のコピー", "inner", "file.txt")));
        Assert.True(File.Exists(Path.Combine(folder, "inner", "file.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransferringAFolderIntoItselfOrItsDescendantFails(bool move)
    {
        var folder = Make("dir/inner/file.txt");
        var parent = Path.Combine(_root, "dir");

        var intoItself = ManagedFileTransfer.Transfer([parent], parent, move, renameOnCollision: true);
        var intoChild = ManagedFileTransfer.Transfer([parent], Path.Combine(parent, "inner"), move, renameOnCollision: true);

        Assert.True(intoItself.IsFailed);
        Assert.True(intoChild.IsFailed);
        Assert.Empty(intoItself.Completed);
        Assert.Empty(intoChild.Completed);
        Assert.True(File.Exists(folder));
    }

    [Fact]
    public void CrossVolumeMove_WhenSourceRemovalFails_ReportsFailureButKeepsTheCompletedCopy()
    {
        var folder = Make(Path.Combine("src", "dir"), content: null);
        Make("src/dir/inner/file.txt", "body");
        var destination = Directory.CreateDirectory(Path.Combine(_root, "dst")).FullName;
        var hooks = new ManagedFileTransfer.Hooks(
            ForceCopyThenDelete: true,
            DeleteSource: _ => throw new IOException("locked"));

        var result = ManagedFileTransfer.Transfer([folder], destination, move: true, renameOnCollision: false, hooks);

        Assert.True(result.IsFailed);
        Assert.Contains("元のフォルダを削除できませんでした", result.ErrorMessage);
        Assert.Equal(new ShellTransfer(folder, Path.Combine(destination, "dir")), Assert.Single(result.Completed));
        Assert.Equal("body", File.ReadAllText(Path.Combine(destination, "dir", "inner", "file.txt")));
        Assert.True(File.Exists(Path.Combine(folder, "inner", "file.txt")));
    }

    [Fact]
    public void CrossVolumeMove_DeletesTheSourceAfterCopying()
    {
        var folder = Make(Path.Combine("src", "dir"), content: null);
        Make("src/dir/file.txt", "body");
        var destination = Directory.CreateDirectory(Path.Combine(_root, "dst")).FullName;
        var hooks = new ManagedFileTransfer.Hooks(true, path => Directory.Delete(path, recursive: true));

        var result = ManagedFileTransfer.Transfer([folder], destination, move: true, renameOnCollision: false, hooks);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.False(Directory.Exists(folder));
        Assert.Equal("body", File.ReadAllText(Path.Combine(destination, "dir", "file.txt")));
    }
}
