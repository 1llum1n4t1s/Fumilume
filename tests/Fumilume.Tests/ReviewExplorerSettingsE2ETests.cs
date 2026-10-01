using System.Diagnostics;
using System.Text.Json;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

/// <summary>実ウィンドウでフォルダの更新・選択と設定のAutomation名を確認する。</summary>
[Collection(HeadlessAppCollection.Name)]
public sealed class ReviewExplorerSettingsE2ETests(HeadlessAppFixture fixture)
{
    [Fact]
    public void RefreshPreservesSelectionAndSettingsExposeDistinctNames() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var folder = Directory.CreateDirectory(Path.Combine(storage.Path, "workspace")).FullName;
        for (var index = 0; index < 512; index++)
            File.WriteAllText(Path.Combine(folder, $"file-{index:D4}.txt"), $"本文{index}");
        var window = new MainWindow(new AppSettings { CheckUpdatesOnStartup = false, RestoreSession = true });
        var viewModel = (MainWindowViewModel)window.DataContext!;
        window.Show();
        try
        {
            Complete(viewModel.OpenFolderPathAsync(folder));
            var root = viewModel.FolderRoots.Single();
            var original = root.Children.ToArray();
            var selected = original[256];
            Complete(viewModel.OpenFolderNodeAsync(selected, false));
            var tree = window.FindControl<TreeView>("FolderTree")!;
            Assert.Same(selected, tree.SelectedItem);
            var notifications = 0;
            root.Children.CollectionChanged += (_, _) => notifications++;
            var elapsed = Stopwatch.StartNew();
            Complete(viewModel.RefreshFolderCommand.ExecuteAsync(null));
            elapsed.Stop();
            Assert.Equal(0, notifications);
            Assert.Equal(original.Length, root.Children.Count);
            for (var index = 0; index < original.Length; index++)
                Assert.Same(original[index], root.Children[index]);
            Assert.Same(selected, tree.SelectedItem);

            // 一覧の先頭に追加・削除があっても、既存の選択とノードを維持する。
            var inserted = Path.Combine(folder, "aaa.txt");
            File.WriteAllText(inserted, "追加");
            Complete(viewModel.RefreshFolderCommand.ExecuteAsync(null));
            Assert.Equal("aaa.txt", root.Children[0].Name);
            Assert.Same(selected, tree.SelectedItem);
            Assert.All(original, node => Assert.Contains(node, root.Children));
            File.Delete(inserted);
            Complete(viewModel.RefreshFolderCommand.ExecuteAsync(null));
            Assert.DoesNotContain(root.Children, node => node.FullPath == inserted);
            Assert.Same(selected, tree.SelectedItem);

            viewModel.OpenSettingsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var settings = window.GetVisualDescendants().OfType<SettingsView>().Single();
            var tabs = settings.FindControl<TabControl>("SettingsTabs")!;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tab in tabs.Items.OfType<TabItem>())
            {
                tabs.SelectedItem = tab;
                Dispatcher.UIThread.RunJobs();
                foreach (var slider in settings.GetVisualDescendants().OfType<Slider>())
                {
                    var peer = ControlAutomationPeer.CreatePeerForElement(slider);
                    Assert.NotNull(peer);
                    var name = peer.GetName();
                    Assert.False(string.IsNullOrWhiteSpace(name));
                    names.Add(name);
                }
            }
            Assert.Equal(new[] { "UIフォントサイズ", "エディタフォントサイズ", "大容量ファイルの警告閾値", "行の高さ", "縦線の桁位置" }
                .Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));

            if (Environment.GetEnvironmentVariable("FUMILUME_REVIEW_ARTIFACTS") is { Length: > 0 } artifacts)
            {
                Directory.CreateDirectory(artifacts);
                File.WriteAllText(Path.Combine(artifacts, "explorer-settings.json"), JsonSerializer.Serialize(new
                {
                    Environment = "Avalonia headless control tree / actual MainWindow",
                    FileCount = original.Length,
                    UnchangedRefreshMilliseconds = elapsed.Elapsed.TotalMilliseconds,
                    UnchangedCollectionNotifications = 0,
                    SelectionPreservedAfterInsertAndDelete = true,
                    AutomationNames = names.Order(StringComparer.Ordinal).ToArray(),
                    Passed = true,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            window.Close();
            Until(() => !window.IsVisible);
        }
    });

    private static void Complete(Task task)
    {
        Until(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Until(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20), "UI操作が20秒以内に完了しませんでした。");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
