using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Search;
using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

/// <summary>実ファイルと実ウィンドウで、プレビューからの検索入力と関連付けラベルを検証する。</summary>
[Collection(HeadlessAppCollection.Name)]
public sealed class GogoUiE2ETests(HeadlessAppFixture fixture)
{
    [Theory]
    [InlineData("md", "find")]
    [InlineData("md", "replace")]
    [InlineData("md", "f3")]
    [InlineData("md", "toolbar")]
    [InlineData("md", "open-panel-f3")]
    [InlineData("csv", "find")]
    [InlineData("csv", "replace")]
    [InlineData("csv", "f3")]
    [InlineData("csv", "toolbar")]
    [InlineData("csv", "open-panel-f3")]
    public void PreviewSearchAllowsTypingAndClosing(string extension, string action) => fixture.Run(() =>
    {
        using var scope = new Scope();
        var text = extension == "md" ? "# needle\nneedle body\n" : "needle,value\nneedle,second\n";
        var path = Path.Combine(scope.StoragePath, $"search.{extension}");
        File.WriteAllText(path, text);
        Pump(scope.ViewModel.OpenPathsAsync([path]));
        var document = scope.ViewModel.SelectedDocument!;

        if (action == "open-panel-f3")
        {
            scope.Window.KeyPress(Key.F, RawInputModifiers.Control, default, null);
            Layout(scope.Window);
            var previous = Assert.Single(scope.Editor.GetVisualDescendants().OfType<SearchPanel>());
            previous.SearchPattern = "needle";
        }

        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Layout(scope.Window);
        Assert.False(scope.Editor.IsEffectivelyVisible);
        Assert.Equal(extension == "md", document.IsMarkdownPreview);
        Assert.Equal(extension == "csv", document.IsCsvPreview);

        if (action == "toolbar")
        {
            Click(scope.Window, scope.Window.FindControl<Button>("FindToolbarButton")!);
        }
        else
        {
            var key = action == "replace" ? Key.H : action is "f3" or "open-panel-f3" ? Key.F3 : Key.F;
            scope.Window.KeyPress(key, key == Key.F3 ? RawInputModifiers.None : RawInputModifiers.Control, default, null);
        }
        Layout(scope.Window);
        var panel = Assert.Single(scope.Editor.GetVisualDescendants().OfType<SearchPanel>());
        Artifact($"search-{extension}-{action}-opened", new
        {
            extension, action, document.IsMarkdownPreview, document.IsCsvPreview,
            EditorVisible = scope.Editor.IsEffectivelyVisible,
            panel.IsOpened, panel.IsReplaceMode,
            SearchHasFocus = panel.IsKeyboardFocusWithin,
        }, scope.Window);
        Assert.True(scope.Editor.IsEffectivelyVisible);
        Assert.True(panel.IsOpened);
        Assert.Equal(action == "replace", panel.IsReplaceMode);
        if (action == "open-panel-f3")
        {
            Assert.Equal("needle", panel.SearchPattern);
            // F3は次の一致へ移動する操作なので、検索入力へ自動でフォーカスを奪わない。
            panel.GetVisualDescendants().OfType<TextBox>().First().Focus();
        }

        // 検索欄へ実入力を送り、本文へ誤入力されないことも確かめる。
        var searchInput = Assert.IsType<TextBox>(scope.Window.FocusManager!.GetFocusedElement());
        Assert.Contains(searchInput, panel.GetVisualDescendants());
        scope.Window.KeyPress(Key.A, RawInputModifiers.Control, default, null);
        scope.Window.KeyTextInput("needle");
        Layout(scope.Window);
        Assert.Equal("needle", panel.SearchPattern);
        Assert.Equal(text, document.Text);
        Assert.False(document.IsModified);
        var close = Assert.Single(panel.GetVisualDescendants().OfType<Button>(),
            button => AutomationProperties.GetAutomationId(button) == "CloseSearchPanelButton");
        Click(scope.Window, close);
        Layout(scope.Window);
        Artifact($"search-{extension}-{action}-closed", new
        {
            panel.IsClosed, EditorHasFocus = scope.Editor.TextArea.IsKeyboardFocusWithin,
            document.Text, document.IsModified, DiskText = File.ReadAllText(path),
        }, scope.Window);
        Assert.True(panel.IsClosed);
        Assert.DoesNotContain(panel, scope.Editor.GetVisualDescendants());
        Assert.True(scope.Editor.TextArea.IsKeyboardFocusWithin);
        Assert.Equal(text, File.ReadAllText(path));
    });

    [Fact]
    public void FileAssociationControlsExposeEveryExtensionName() => fixture.Run(() =>
    {
        using var scope = new Scope();
        scope.ViewModel.OpenSettingsCommand.Execute(null);
        Layout(scope.Window);
        var settings = Assert.Single(scope.Window.GetVisualDescendants().OfType<SettingsView>());
        settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 4;
        Layout(scope.Window);
        var controls = settings.GetVisualDescendants().OfType<ToggleSwitch>().ToArray();
        var expected = scope.ViewModel.SettingsTab!.Associations;
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Count, controls.Length);
        // 状態を変更せずpeerを読むだけにし、OSの関連付けへ書き込まない。
        var observed = controls.Select(control => new
        {
            Item = Assert.IsType<FileAssociationItemViewModel>(control.DataContext),
            Name = ControlAutomationPeer.CreatePeerForElement(control).GetName(),
        }).ToArray();
        Artifact("association-names", observed.Select(item => new
        {
            item.Item.Extension, item.Item.Description, item.Name,
        }), scope.Window);
        foreach (var item in observed)
        {
            Assert.Equal(item.Item.Description, item.Name);
            Assert.Contains(item.Item.Extension, item.Name!);
        }
    });

    private static void Click(MainWindow window, Control control)
    {
        Assert.True(control.IsEffectivelyVisible);
        Assert.True(control.IsEffectivelyEnabled);
        // 通常のHeadlessDrawingは実描画とフォント寸法・ヒット判定が異なるため、実ボタンの操作経路を通す。
        // SkiaでのE2Eは座標から実ポインタ入力を送り、レイアウトとヒット判定も検証する。
        if (!HeadlessAppFixture.UsesSkia)
        {
            var button = Assert.IsType<Button>(control);
            if (AutomationProperties.GetAutomationId(button) == "CloseSearchPanelButton")
            {
                Assert.NotNull(button.Command);
                Assert.True(button.Command.CanExecute(button.CommandParameter));
                button.Command.Execute(button.CommandParameter);
            }
            else
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            return;
        }
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static void Layout(MainWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Pump(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Yield();
        }
        Assert.True(task.IsCompleted, "UI処理が15秒以内に完了しませんでした");
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Artifact(string name, object result, MainWindow window)
    {
        var directory = Environment.GetEnvironmentVariable("FUMILUME_E2E_ARTIFACTS");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"gogo-ui-{name}.json"), JsonSerializer.Serialize(result));
        if (!HeadlessAppFixture.UsesSkia) return;
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, $"gogo-ui-{name}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private sealed class Scope : IDisposable
    {
        private readonly TemporaryStorage _storage = new();
        public MainWindow Window { get; }
        public MainWindowViewModel ViewModel { get; }
        public TextEditor Editor { get; }
        public string StoragePath => _storage.Path;

        public Scope()
        {
            Window = new MainWindow(new AppSettings { RestoreSession = false, CheckUpdatesOnStartup = false });
            ViewModel = (MainWindowViewModel)Window.DataContext!;
            Window.Show();
            Layout(Window);
            Editor = Window.FindControl<TextEditor>("Editor")!;
        }

        public void Dispose()
        {
            ViewModel.Options.RestoreSession = true;
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            _storage.Dispose();
        }
    }
}
