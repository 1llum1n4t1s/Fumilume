using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Threading;
using AvaloniaEdit;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

/// <summary>実ファイルと実ウィンドウを通し、フォルダ操作で本文を失わないことを確認する。</summary>
[Collection(HeadlessAppCollection.Name)]
public sealed class FolderExplorerIntegrationTests(HeadlessAppFixture fixture)
{
    [Fact]
    public void StartupWithoutFolderShowsExplorerAndOpenFolderEntry() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        Assert.True(SessionStateService.Save(new SessionState { FolderTreeVisible = false }));
        using var scope = new WindowScope();
        scope.Window.Width = 720;
        scope.Window.UpdateLayout();
        var panel = scope.Window.FindControl<Grid>("FolderExplorerRoot")!;
        Assert.True(panel.IsEffectivelyVisible);
        Assert.True(panel.Bounds.Width >= 140);
        var open = scope.Window.FindControl<Button>("OpenFolderButton")!;
        Assert.NotNull(open);
        Assert.True(open.IsEffectivelyVisible);
        Assert.Same(scope.ViewModel.OpenFolderCommand, open.Command);
        Assert.Empty(scope.ViewModel.FolderRoots);
        var content = scope.Window.FindControl<Border>("ContentIsland")!;
        var rightEdge = content.TranslatePoint(new Point(content.Bounds.Width, 0), scope.Window)!.Value.X;
        Assert.True(rightEdge <= scope.Window.Bounds.Width);
        scope.Window.KeyPress(Key.B, RawInputModifiers.Control, default, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(panel.IsEffectivelyVisible);
        scope.Window.KeyPress(Key.B, RawInputModifiers.Control, default, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(open.IsEffectivelyVisible);
        Complete(scope.ViewModel.OpenFolderPathAsync(storage.Path));
        Assert.False(open.IsEffectivelyVisible);
        Assert.True(scope.Window.FindControl<TreeView>("FolderTree")!.IsEffectivelyVisible);
        scope.ViewModel.CloseFolderCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(panel.IsEffectivelyVisible);
        Assert.True(open.IsEffectivelyVisible);
    });

    [Fact]
    public void TreeLoadsChildrenOnExpansionAndReflectsExternalChanges() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var folder = Directory.CreateDirectory(Path.Combine(storage.Path, "workspace")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(folder, "nested")).FullName;
        File.WriteAllText(Path.Combine(nested, "inside.txt"), "日本語\r\n本文");
        File.WriteAllText(Path.Combine(folder, "root.txt"), "root");
        using var scope = new WindowScope();
        Complete(scope.ViewModel.OpenFolderPathAsync(folder));
        Dispatcher.UIThread.RunJobs();

        var tree = Assert.IsType<TreeView>(scope.Window.FindControl<TreeView>("FolderTree"));
        Assert.True(scope.ViewModel.HasFolder);
        Assert.True(scope.ViewModel.IsFolderTreeVisible);
        Assert.True(tree.IsEffectivelyVisible);
        Assert.Same(scope.ViewModel.FolderRoots, tree.ItemsSource);
        var root = Assert.Single(scope.ViewModel.FolderRoots);
        root.IsExpanded = true;
        Until(() => root.Children.Any(node => node.Name == "nested"));
        var child = root.Children.Single(node => node.Name == "nested");
        Assert.True(child.IsDirectory);
        Assert.DoesNotContain(child.Children, node => node.Name == "inside.txt");
        child.IsExpanded = true;
        Until(() => child.Children.Any(node => node.Name == "inside.txt"));
        Complete(scope.ViewModel.OpenFolderNodeAsync(child.Children.Single(node => node.Name == "inside.txt"), false));
        Assert.Equal("日本語\r\n本文", scope.ViewModel.SelectedDocument!.Text);
        Assert.Same(scope.ViewModel.SelectedDocument.EditorDocument, scope.Window.FindControl<TextEditor>("Editor")!.Document);

        var selectedPath = scope.ViewModel.SelectedDocument!.FilePath;
        var created = Path.Combine(folder, "created.txt");
        File.WriteAllText(created, "created");
        Until(() => scope.ViewModel.FolderRoots.Single().Children.Any(node => node.Name == "created.txt"));
        Assert.Equal(selectedPath, scope.ViewModel.SelectedFolderNode?.FullPath);
        Assert.Same(scope.ViewModel.SelectedFolderNode, tree.SelectedItem);
        File.Move(created, Path.Combine(folder, "renamed.txt"));
        Until(() => scope.ViewModel.FolderRoots.Single().Children.Any(node => node.Name == "renamed.txt")
            && scope.ViewModel.FolderRoots.Single().Children.All(node => node.Name != "created.txt"));
        Assert.Equal(selectedPath, scope.ViewModel.SelectedFolderNode?.FullPath);
        Assert.Same(scope.ViewModel.SelectedFolderNode, tree.SelectedItem);
        File.Delete(Path.Combine(folder, "renamed.txt"));
        Until(() => scope.ViewModel.FolderRoots.Single().Children.All(node => node.Name != "renamed.txt"));
        scope.ViewModel.CollapseFoldersCommand.Execute(null);
        Assert.False(scope.ViewModel.FolderRoots.Single().IsExpanded);
        File.WriteAllText(Path.Combine(folder, "collapsed.txt"), "collapsed");
        Until(() => scope.ViewModel.FolderRoots.Single().Children.Any(node => node.Name == "collapsed.txt"));
        Assert.False(scope.ViewModel.FolderRoots.Single().IsExpanded);
        scope.ViewModel.CloseFolderCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(scope.ViewModel.HasFolder);
        Assert.False(tree.IsEffectivelyVisible);
        Assert.Equal("日本語\r\n本文", scope.ViewModel.SelectedDocument.Text);
    });

    [Fact]
    public void PreviewReplacementPreservesEditedPinnedAndSavedDocuments() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var first = Path.Combine(storage.Path, "first.txt");
        var second = Path.Combine(storage.Path, "second.txt");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        using var scope = new WindowScope();
        Complete(scope.ViewModel.OpenFolderPathAsync(storage.Path));
        Complete(scope.ViewModel.OpenFolderFileAsync(first, false));
        var preview = scope.ViewModel.SelectedDocument!;
        Assert.True(preview.IsPreview);
        Complete(Task.WhenAll(scope.ViewModel.OpenFolderFileAsync(first, false), scope.ViewModel.OpenFolderFileAsync(first, false)));
        Assert.Same(preview, scope.ViewModel.SelectedDocument);
        Assert.Single(scope.ViewModel.Documents, document => document.FilePath == first);
        Complete(scope.ViewModel.OpenFolderFileAsync(second, false));
        Assert.DoesNotContain(preview, scope.ViewModel.Tabs);
        var edited = scope.ViewModel.SelectedDocument!;
        scope.Window.FindControl<TextEditor>("Editor")!.Document.Insert(0, "未保存:");
        Assert.True(edited.IsModified);
        Assert.False(edited.IsPreview);
        Complete(scope.ViewModel.OpenFolderFileAsync(first, false));
        Assert.Contains(edited, scope.ViewModel.Tabs);
        Assert.Equal("未保存:second", edited.Text);
        var pinned = scope.ViewModel.SelectedDocument!;
        pinned.TogglePinCommand.Execute(null);
        Assert.True(pinned.IsPinned);
        Assert.False(pinned.IsPreview);
        Complete(scope.ViewModel.OpenFolderFileAsync(second, true));
        Assert.Same(edited, scope.ViewModel.SelectedDocument);
        Assert.False(edited.IsPreview);
        Complete(scope.ViewModel.SaveCommand.ExecuteAsync(null));
        Assert.Equal("未保存:second", File.ReadAllText(second));
        Assert.False(edited.IsModified);
        Assert.False(edited.IsPreview);
        Assert.Contains(pinned, scope.ViewModel.Tabs);
    });

    [Fact]
    public void FolderAndUnsavedDocumentSurviveRestartAndMissingFolderIsRecoverable() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var folder = Directory.CreateDirectory(Path.Combine(storage.Path, "workspace")).FullName;
        var file = Path.Combine(folder, "restore.txt");
        File.WriteAllText(file, "disk");
        using (var first = new WindowScope())
        {
            Complete(first.ViewModel.OpenFolderPathAsync(folder));
            Complete(first.ViewModel.OpenFolderFileAsync(file, false));
            first.Window.FindControl<TextEditor>("Editor")!.Document.Insert(0, "未保存:");
            Complete(first.ViewModel.PersistSessionStateAsync());
        }
        using var restored = new WindowScope();
        Until(() => restored.ViewModel.HasFolder && restored.ViewModel.Documents.Any(document => document.FilePath == file));
        Assert.Equal(folder, restored.ViewModel.FolderPath);
        var document = restored.ViewModel.Documents.Single(document => document.FilePath == file);
        Assert.Equal("未保存:disk", document.Text);
        Assert.True(document.IsModified);
        Assert.False(document.IsPreview);
        restored.ViewModel.CloseFolderCommand.Execute(null);
        Complete(restored.ViewModel.OpenFolderPathAsync(Path.Combine(folder, "missing")));
        Assert.False(restored.ViewModel.HasFolder);
        Complete(restored.ViewModel.OpenFolderPathAsync(folder));
        Assert.True(restored.ViewModel.HasFolder);
        Assert.Contains(document, restored.ViewModel.Tabs);
    });

    [Fact]
    public void PermanentOpenAndSavePromoteCleanPreviewBeforeAnotherSelection() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var first = Path.Combine(storage.Path, "permanent.txt");
        var second = Path.Combine(storage.Path, "saved.txt");
        var third = Path.Combine(storage.Path, "next.txt");
        File.WriteAllText(first, "permanent");
        File.WriteAllText(second, "saved\r\n");
        File.WriteAllText(third, "next");
        using var scope = new WindowScope();
        Complete(scope.ViewModel.OpenFolderPathAsync(storage.Path));
        Complete(scope.ViewModel.OpenFolderFileAsync(first, false));
        var permanent = scope.ViewModel.SelectedDocument!;
        Assert.True(permanent.IsPreview);
        Complete(scope.ViewModel.OpenFolderFileAsync(first, true));
        Assert.Same(permanent, scope.ViewModel.SelectedDocument);
        Assert.False(permanent.IsPreview);
        Complete(scope.ViewModel.OpenFolderFileAsync(second, false));
        var saved = scope.ViewModel.SelectedDocument!;
        Assert.True(saved.IsPreview);
        Complete(scope.ViewModel.SaveCommand.ExecuteAsync(null));
        Assert.False(saved.IsPreview);
        Assert.Equal("saved\r\n", File.ReadAllText(second));
        Complete(scope.ViewModel.OpenFolderFileAsync(third, false));
        Assert.Contains(permanent, scope.ViewModel.Tabs);
        Assert.Contains(saved, scope.ViewModel.Tabs);
        Assert.True(scope.ViewModel.SelectedDocument!.IsPreview);
        Assert.Single(scope.ViewModel.Tabs, tab => tab.IsPreview);
    });
    [Fact]
    public void TreePointerInputOpensPreviewAndDoubleClickMakesItPermanent() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var folder = Directory.CreateDirectory(Path.Combine(storage.Path, "workspace")).FullName;
        var file = Path.Combine(folder, "click.txt");
        File.WriteAllText(file, "pointer経路");
        using var scope = new WindowScope();
        scope.Window.Width = 720;
        scope.Window.UpdateLayout();
        Complete(scope.ViewModel.OpenFolderPathAsync(folder));
        var tree = scope.Window.FindControl<TreeView>("FolderTree")!;
        Until(() => tree.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "click.txt"));
        var folderPanel = scope.Window.FindControl<Grid>("FolderExplorerRoot")!;
        var tabsPanel = scope.Window.FindControl<Grid>("SidePanelRoot")!;
        var editor = scope.Window.FindControl<TextEditor>("Editor")!;
        Assert.True(folderPanel.Bounds.Width >= 140);
        Assert.True(tabsPanel.Bounds.Width > 0);
        Assert.True(editor.Bounds.Width > 100);
        Assert.True(folderPanel.TranslatePoint(new Point(folderPanel.Bounds.Width, 0), scope.Window)!.Value.X
            <= tabsPanel.TranslatePoint(default, scope.Window)!.Value.X);
        Assert.True(tabsPanel.TranslatePoint(new Point(tabsPanel.Bounds.Width, 0), scope.Window)!.Value.X
            <= editor.TranslatePoint(default, scope.Window)!.Value.X);
        var label = tree.GetVisualDescendants().OfType<TextBlock>().Single(item => item.Text == "click.txt");
        var point = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseDown(point, MouseButton.Left);
        scope.Window.MouseUp(point, MouseButton.Left);
        Until(() => scope.ViewModel.SelectedDocument?.FilePath == file);
        var document = scope.ViewModel.SelectedDocument!;
        Assert.True(document.IsPreview);
        scope.Window.MouseDown(point, MouseButton.Left);
        scope.Window.MouseUp(point, MouseButton.Left);
        Until(() => !document.IsPreview, "入力操作で一時タブが固定されない");
        Assert.Same(document, scope.ViewModel.SelectedDocument);
        Assert.Single(scope.ViewModel.Documents, item => item.FilePath == file);
    });
    [Fact]
    public void EnterPromotesPreviewAndTreeShortcutPreservesTheDocument() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var folder = Directory.CreateDirectory(Path.Combine(storage.Path, "workspace")).FullName;
        var file = Path.Combine(folder, "keyboard.txt");
        File.WriteAllText(file, "keyboard経路");
        using var scope = new WindowScope();
        Complete(scope.ViewModel.OpenFolderPathAsync(folder));
        Complete(scope.ViewModel.OpenFolderFileAsync(file, false));
        var tree = scope.Window.FindControl<TreeView>("FolderTree")!;
        var document = scope.ViewModel.SelectedDocument!;
        Until(() => scope.ViewModel.SelectedFolderNode?.FullPath == file, "開いた文書がツリーへ選択反映されない");
        Assert.True(tree.Focus(), "TreeView.Focus失敗");
        Assert.True(tree.IsKeyboardFocusWithin, "TreeView内にフォーカスなし");
        scope.Window.KeyPress(Key.Enter, RawInputModifiers.None, default, null);
        Until(() => !document.IsPreview, "入力操作で一時タブが固定されない");
        Assert.True(scope.Window.FindControl<TextEditor>("Editor")!.IsKeyboardFocusWithin, "Enter後エディタにフォーカスなし");
        scope.Window.KeyPress(Key.B, RawInputModifiers.Control, default, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(scope.ViewModel.IsFolderTreeVisible);
        Assert.False(tree.IsEffectivelyVisible);
        scope.Window.KeyPress(Key.B, RawInputModifiers.Control, default, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(scope.ViewModel.IsFolderTreeVisible);
        Assert.True(tree.IsEffectivelyVisible);
        Assert.Same(document, scope.ViewModel.SelectedDocument);
        Assert.Equal("keyboard経路", document.Text);
    });
    [Fact]
    public void PointerFocusAllowsArrowSelectionAndFolderExpansion() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var folder = Directory.CreateDirectory(Path.Combine(storage.Path, "workspace")).FullName;
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        File.WriteAllText(Path.Combine(folder, "nested", "child.txt"), "child");
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        File.WriteAllText(Path.Combine(folder, "b.txt"), "b");
        using var scope = new WindowScope();
        Complete(scope.ViewModel.OpenFolderPathAsync(folder));
        var tree = scope.Window.FindControl<TreeView>("FolderTree")!;
        ClickName("a.txt");
        Until(() => scope.ViewModel.SelectedDocument?.DisplayName == "a.txt");
        Assert.True(tree.IsKeyboardFocusWithin);
        scope.Window.KeyPress(Key.Down, RawInputModifiers.None, default, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("b.txt", scope.ViewModel.SelectedFolderNode?.Name);
        scope.Window.KeyPress(Key.Up, RawInputModifiers.None, default, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("a.txt", scope.ViewModel.SelectedFolderNode?.Name);
        ClickName("nested");
        var nested = scope.ViewModel.FolderRoots.Single().Children.Single(node => node.Name == "nested");
        Until(() => nested.IsExpanded && !nested.IsLoading);
        Assert.True(tree.IsKeyboardFocusWithin);
        scope.Window.KeyPress(Key.Left, RawInputModifiers.None, default, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(nested.IsExpanded);
        scope.Window.KeyPress(Key.Right, RawInputModifiers.None, default, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(nested.IsExpanded);

        void ClickName(string name)
        {
            Until(() => tree.GetVisualDescendants().OfType<TextBlock>().Any(item => item.Text == name));
            var label = tree.GetVisualDescendants().OfType<TextBlock>().Single(item => item.Text == name);
            var point = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), scope.Window)!.Value;
            scope.Window.MouseDown(point, MouseButton.Left);
            scope.Window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }
    });
    private static void Complete(Task task)
    {
        Until(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Until(Func<bool> condition, string? reason = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15), reason ?? "フォルダ操作の完了を15秒待っても期待した状態になりませんでした。");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class WindowScope : IDisposable
    {
        public WindowScope()
        {
            Window = new MainWindow(new AppSettings { CheckUpdatesOnStartup = false, RestoreSession = true });
            ViewModel = (MainWindowViewModel)Window.DataContext!;
            Window.Show();
            Complete(ViewModel.PersistSessionStateAsync());
        }

        public MainWindow Window { get; }
        public MainWindowViewModel ViewModel { get; }

        public void Dispose()
        {
            Window.Close();
            Until(() => !Window.IsVisible);
        }
    }
}
