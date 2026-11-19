using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Search;
using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

/// <summary>独立検証で見つかった入力経路・表示の不具合の回帰テスト。</summary>
[Collection(HeadlessAppCollection.Name)]
public sealed class ReviewFixesTests(HeadlessAppFixture fixture)
{
    [Fact]
    public void ControlKControlOOpensFolderPickerNotFilePicker() => fixture.Run(() =>
    {
        using var scope = new DialogScope();
        scope.Window.KeyPress(Key.K, RawInputModifiers.Control, default, null);
        scope.Window.KeyPress(Key.O, RawInputModifiers.Control, default, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, scope.Dialogs.FolderPickCount);
        Assert.Equal(0, scope.Dialogs.FilePickCount);
    });

    [Fact]
    public void ControlOAloneStillOpensFilePicker() => fixture.Run(() =>
    {
        using var scope = new DialogScope();
        scope.Window.KeyPress(Key.O, RawInputModifiers.Control, default, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, scope.Dialogs.FolderPickCount);
        Assert.Equal(1, scope.Dialogs.FilePickCount);
    });

    [Fact]
    public void BodyShortcutsAreNotAppliedWhileASearchInputHasFocus() => fixture.Run(() =>
    {
        using var scope = new DialogScope();
        scope.Window.KeyPress(Key.F, RawInputModifiers.Control, default, null);
        Dispatcher.UIThread.RunJobs();
        var editor = scope.Window.FindControl<TextEditor>("Editor")!;
        var panel = Assert.Single(editor.GetVisualDescendants().OfType<SearchPanel>());
        panel.GetVisualDescendants().OfType<TextBox>().First().Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.True(editor.IsKeyboardFocusWithin);
        Assert.False(scope.Window.EditorBodyHasFocus());

        editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(scope.Window.EditorBodyHasFocus());
    });

    [Fact]
    public void ControlKChordMessageNamesTheKeyThatWorksOnEachPlatform()
    {
        Assert.Contains("Ctrl+O", MainWindow.ControlKChordMessage(macOS: false));
        var mac = MainWindow.ControlKChordMessage(macOS: true);
        Assert.Contains("⌘O", mac);
        Assert.DoesNotContain("Ctrl+O", mac);
    }

    [Fact]
    public void PaletteShowsSymbolsInsteadOfInternalKeyNames() => fixture.Run(() =>
    {
        using var scope = new DialogScope();
        scope.ViewModel.OpenCommandPaletteCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var gestures = scope.ViewModel.CommandPaletteResults.Select(entry => entry.Gesture).ToArray();
        Assert.DoesNotContain(gestures, gesture => gesture?.Contains("Oem", StringComparison.Ordinal) == true);
        Assert.Contains("Ctrl+]", gestures);
        Assert.Equal("⌘]", CommandPaletteEntry.ToDisplayGesture("⌘OemCloseBrackets"));
        Assert.Equal("Ctrl+Shift+F2", CommandPaletteEntry.ToDisplayGesture("Ctrl+Shift+F2"));
    });

    [Theory]
    [InlineData("md")]
    [InlineData("csv")]
    public void HiddenPreviewDoesNotReadTheWholeTextOnEveryEdit(string extension) => fixture.Run(() =>
    {
        using var scope = new DialogScope();
        var document = scope.ViewModel.SelectedDocument!;
        var original = extension == "md" ? "# one\n" : "a,b\n1,2\n";
        document.Load($@"C:\tmp\preview.{extension}", new TextDocumentContent(original, DocumentEncoding.Utf8, DocumentNewLines.Lf));
        Dispatcher.UIThread.RunJobs();
        var markdown = scope.Window.GetVisualDescendants().OfType<MarkdownPreview>().Single();
        var csv = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        var previewNotifications = new List<string?>();
        document.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(DocumentViewModel.MarkdownPreviewText) or nameof(DocumentViewModel.CsvPreviewText))
            {
                previewNotifications.Add(args.PropertyName);
            }
        };
        string Shown() => extension == "md" ? markdown.Markdown ?? string.Empty : csv.Csv ?? string.Empty;
        var previewProperty = extension == "md" ? nameof(DocumentViewModel.MarkdownPreviewText) : nameof(DocumentViewModel.CsvPreviewText);

        document.EditorDocument.Insert(0, "x");
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(previewNotifications);
        Assert.Equal(string.Empty, Shown());

        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(document.Text, Shown());

        document.EditorDocument.Insert(0, "y");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(document.Text, Shown());
        Assert.Contains(previewProperty, previewNotifications);

        var lastShown = document.Text;
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        previewNotifications.Clear();
        document.EditorDocument.Insert(0, "z");
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(previewNotifications);
        Assert.Equal(lastShown, Shown());
    });

    [Theory]
    [InlineData(true, "ab\ncd", 0, 5, "    ab\n    cd")]
    [InlineData(false, "ab\ncd", 0, 5, "\tab\n\tcd")]
    [InlineData(true, "abc", 3, 0, "abc ")]
    [InlineData(true, "ab", 0, 0, "    ab")]
    [InlineData(false, "ab", 0, 0, "\tab")]
    [InlineData(true, "xyz", 1, 1, "x  z")]
    public void RealTabKeyAndItsReplayAgree(bool spaces, string text, int start, int length, string expected)
        => fixture.Run(() =>
    {
        using var scope = new DialogScope();
        scope.ViewModel.Options.ConvertTabsToSpaces = spaces;
        scope.ViewModel.Options.IndentationSize = 4;
        var document = scope.ViewModel.SelectedDocument!;
        document.Load(@"C:\tmp\macro.txt", new TextDocumentContent(text, DocumentEncoding.Utf8, DocumentNewLines.Lf));
        var editor = scope.Window.FindControl<TextEditor>("Editor")!;
        editor.TextArea.Focus();
        editor.Select(start, length);
        Dispatcher.UIThread.RunJobs();

        scope.ViewModel.ToggleMacroRecordingCommand.Execute(null);
        scope.Window.KeyPress(Key.Tab, RawInputModifiers.None, default, null);
        Dispatcher.UIThread.RunJobs();
        scope.ViewModel.ToggleMacroRecordingCommand.Execute(null);

        var step = Assert.Single(scope.ViewModel.RecordedSteps);
        Assert.NotEqual(MacroStepKind.InsertText, step.Kind);
        var actual = document.Text;
        Assert.Equal(expected, actual);

        document.Text = text;
        editor.Select(start, length);
        Dispatcher.UIThread.RunJobs();
        MainWindowE2ETestHelper.Pump(scope.ViewModel.RunMacroCommand.ExecuteAsync(null));

        Assert.Equal(actual, document.Text);
    });

    [Fact]
    public void RealShiftTabAndItsReplayAgree() => fixture.Run(() =>
    {
        using var scope = new DialogScope();
        scope.ViewModel.Options.ConvertTabsToSpaces = true;
        scope.ViewModel.Options.IndentationSize = 4;
        const string text = "        a\n\tb\n  c";
        var document = scope.ViewModel.SelectedDocument!;
        document.Load(@"C:\tmp\macro.txt", new TextDocumentContent(text, DocumentEncoding.Utf8, DocumentNewLines.Lf));
        var editor = scope.Window.FindControl<TextEditor>("Editor")!;
        editor.TextArea.Focus();
        editor.Select(0, text.Length);
        Dispatcher.UIThread.RunJobs();

        scope.ViewModel.ToggleMacroRecordingCommand.Execute(null);
        scope.Window.KeyPress(Key.Tab, RawInputModifiers.Shift, default, null);
        Dispatcher.UIThread.RunJobs();
        scope.ViewModel.ToggleMacroRecordingCommand.Execute(null);

        Assert.Equal(MacroStepKind.Unindent, Assert.Single(scope.ViewModel.RecordedSteps).Kind);
        var actual = document.Text;
        Assert.Equal("    a\nb\nc", actual);

        document.Text = text;
        editor.Select(0, text.Length);
        Dispatcher.UIThread.RunJobs();
        MainWindowE2ETestHelper.Pump(scope.ViewModel.RunMacroCommand.ExecuteAsync(null));
        Assert.Equal(actual, document.Text);
    });

    [Fact]
    public void ButtonHoverColorsReachTheRenderedPresenter() => fixture.Run(() =>
    {
        using var scope = new DialogScope();
        scope.Window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        scope.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.True(Avalonia.Application.Current!.TryGetResource("HoverBg", Avalonia.Styling.ThemeVariant.Light, out var hoverResource));
        var hover = (Avalonia.Media.ISolidColorBrush)hoverResource!;
        var checkedClasses = new HashSet<string>();
        foreach (var button in scope.Window.GetVisualDescendants().OfType<Button>()
                     .Where(b => b.IsEffectivelyEnabled && !b.Classes.Contains("close") && !b.Classes.Contains("active")))
        {
            var kind = new[] { "command", "statusitem", "panelswitch", "palette", "caption" }
                .FirstOrDefault(name => button.Classes.Contains(name));
            var presenter = button.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .FirstOrDefault(p => p.Name == "PART_ContentPresenter");
            if (kind is null || presenter is null)
            {
                continue;
            }

            ((IPseudoClasses)button.Classes).Set(":pointerover", true);
            Dispatcher.UIThread.RunJobs();
            var shown = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(presenter.Background).Color;
            ((IPseudoClasses)button.Classes).Set(":pointerover", false);

            Assert.True(hover.Color == shown, $"{kind}: ホバー時の面が {shown}（期待 {hover.Color}）");
            checkedClasses.Add(kind);
        }

        Assert.Contains("command", checkedClasses);
        Assert.Contains("statusitem", checkedClasses);
        Assert.Contains("palette", checkedClasses);
    });

    private sealed class DialogScope : IDisposable
    {
        private readonly TemporaryStorage _storage = new();

        public DialogScope()
        {
            Window = new MainWindow(new AppSettings { RestoreSession = false, CheckUpdatesOnStartup = false });
            ViewModel = (MainWindowViewModel)Window.DataContext!;
            typeof(MainWindowViewModel).GetField("_dialogs", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(ViewModel, Dialogs);
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public MainWindow Window { get; }

        public MainWindowViewModel ViewModel { get; }

        public RecordingDialogs Dialogs { get; } = new();

        public void Dispose()
        {
            ViewModel.Options.RestoreSession = false;
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            _storage.Dispose();
        }
    }

    private sealed class RecordingDialogs : IEditorDialogService
    {
        public int FilePickCount { get; private set; }

        public int FolderPickCount { get; private set; }

        public Task<IReadOnlyList<string>> PickOpenPathsAsync()
        {
            FilePickCount++;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<string?> PickFolderPathAsync()
        {
            FolderPickCount++;
            return Task.FromResult<string?>(null);
        }

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);

        public Task<UnsavedDocumentDecision> ConfirmUnsavedAsync(string documentName)
            => Task.FromResult(UnsavedDocumentDecision.Discard);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<int?> PickLineNumberAsync(int currentLine, int maximumLine) => Task.FromResult<int?>(null);

        public Task<string?> PromptTextAsync(string title, string message, string initialText)
            => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<GrepQuery?> PickGrepQueryAsync(GrepQuery initial) => Task.FromResult<GrepQuery?>(null);

        public Task CheckForUpdatesAsync(bool manually) => Task.CompletedTask;
    }
}
