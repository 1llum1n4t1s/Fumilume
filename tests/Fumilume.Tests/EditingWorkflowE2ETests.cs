using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class EditingWorkflowE2ETests(HeadlessAppFixture fixture)
{
    [Theory]
    [InlineData("a\n😀z", 1, MacroMotion.LineDown, false, "a\nX😀z")]
    [InlineData("😀z\na", 5, MacroMotion.LineUp, false, "X😀z\na")]
    [InlineData("a\n😀z", 1, MacroMotion.LineDown, true, "aX😀z")]
    [InlineData("😀z\nab", 5, MacroMotion.LineUp, true, "Xb")]
    [InlineData("ab\ncd", 1, MacroMotion.LineDown, false, "ab\ncXd")]
    [InlineData("abc\nx", 3, MacroMotion.LineDown, false, "abc\nxX")]
    [InlineData("a\r\n😀z", 1, MacroMotion.LineDown, false, "a\r\nX😀z")]
    [InlineData("a\néz", 1, MacroMotion.LineDown, false, "a\nXéz")]
    public void MacroVerticalEditingKeepsCharacterBoundaries(string text, int caret, MacroMotion motion,
        bool select, string expected) => fixture.Run(() =>
    {
        using var scope = new Scope();
        var file = Path.Combine(scope.Path, "macro.txt");
        File.WriteAllText(file, text);
        Pump(scope.Model.OpenPathsAsync([file]));
        var document = scope.Model.SelectedDocument!;
        document.CaretIndex = caret;
        document.SelectionStart = caret;
        document.SelectionLength = 0;
        var steps = new List<MacroStep>
        {
            new() { Kind = MacroStepKind.MoveCaret, Motion = motion, ExtendSelection = select },
        };
        if (select) steps.Add(new() { Kind = MacroStepKind.DeleteForward });
        steps.Add(new() { Kind = MacroStepKind.InsertText, Text = "X" });
        Pump(scope.Model.RunSavedMacroCommand.ExecuteAsync(new KeyboardMacro { Name = "vertical", Steps = steps }));
        Assert.Equal(expected, document.EditorDocument.Text);
        document.EditorDocument.UndoStack.Undo();
        Assert.Equal(text, document.EditorDocument.Text);
        document.EditorDocument.UndoStack.Redo();
        Assert.Equal(expected, document.EditorDocument.Text);
    });

    [Theory]
    [InlineData("````\n```\n# hidden\n````\n# visible", "visible", 5)]
    [InlineData("````\n~~~\n# hidden\n`````\n# visible", "visible", 5)]
    [InlineData("~~~\n~~~ info\n# hidden\n~~~\n# visible", "visible", 5)]
    [InlineData("```\n```\t \n# visible", "visible", 3)]
    [InlineData("```\n```\u00a0\n# hidden\n```\n# visible", "visible", 5)]
    [InlineData("~~~\n~~~~\n# visible", "visible", 3)]
    [InlineData("~~~\n# hidden", null, 0)]
    [InlineData("---\ntitle: metadata\n---\n# visible\nSetext\n===", "visible", 4)]
    public void MarkdownOutlineRespectsClosingFence(string text, string? title, int line) => fixture.Run(() =>
    {
        using var scope = new Scope();
        var file = Path.Combine(scope.Path, "outline.md");
        File.WriteAllText(file, text);
        Pump(scope.Model.OpenPathsAsync([file]));
        scope.Model.SidePanel.Kind = SidePanelKind.Outline;
        Dispatcher.UIThread.RunJobs();
        var outline = scope.Model.SidePanel.Outline;
        if (title is null) Assert.Empty(outline);
        else
        {
            Assert.Equal(title, outline[0].Title);
            Assert.Equal(line, outline[0].LineNumber);
            Assert.DoesNotContain(outline, item => item.Title == "hidden");
            if (text.Contains("Setext", StringComparison.Ordinal))
                Assert.Contains(outline, item => item.Title == "Setext" && item.LineNumber == 5);
        }
    });

    [Theory]
    [InlineData("````\n```\n# hidden\n````\n# visible", "```\n# hidden", true)]
    [InlineData("````\n~~~\n# hidden\n`````\n# visible", "~~~\n# hidden", true)]
    [InlineData("```\n``` info\n# hidden\n```\n# visible", "``` info\n# hidden", true)]
    [InlineData("```\n# hidden\n````\t \n# visible", "# hidden", true)]
    [InlineData("```\n```\u00a0\n# hidden\n```\n# visible", "```\u00a0\n# hidden", true)]
    [InlineData("````\n```\n# hidden", "```\n# hidden", false)]
    public void MarkdownPreviewKeepsFenceContentsAsCode(string text, string code, bool visible) => fixture.Run(() =>
    {
        using var scope = new Scope();
        var file = Path.Combine(scope.Path, "preview.md");
        File.WriteAllText(file, text);
        Pump(scope.Model.OpenPathsAsync([file]));
        scope.Model.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var preview = scope.Window.GetVisualDescendants().OfType<MarkdownPreview>().Single();
        var block = Assert.Single(preview.GetVisualDescendants().OfType<Border>(),
            border => border.Classes.Contains("markdowncode"));
        Assert.Equal(code.Replace("\n", Environment.NewLine, StringComparison.Ordinal),
            Assert.IsType<SelectableTextBlock>(block.Child).Text);
        Assert.Equal(visible, preview.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Any(element => element.Text == "visible"));
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FolderSearchReportsSkippedFilesWithOrWithoutMatches(bool match, bool binary) => fixture.Run(() =>
    {
        using var scope = new Scope();
        var folder = Path.Combine(scope.Path, "search");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "text.txt"), match ? "needle" : "other");
        if (binary) File.WriteAllBytes(Path.Combine(folder, "binary.dat"), [0x50, 0x4b, 0, 1, 0x41]);
        scope.Dialogs.Query = new GrepQuery("needle", folder, "*", true, false, false);
        Pump(scope.Model.GrepCommand.ExecuteAsync(null));
        var result = scope.Model.SelectedGrep!;
        Assert.Equal(match ? 1 : 0, result.Matches.Count);
        Assert.Contains("1 ファイル", result.Status);
        if (binary) Assert.Contains("読めない 1 ファイルは除外", result.Status);
        else Assert.DoesNotContain("除外", result.Status);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FolderSearchReportsAllSkippedAndLimit(bool limit) => fixture.Run(() =>
    {
        using var scope = new Scope();
        var folder = Path.Combine(scope.Path, "search");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "binary.dat"), [0x50, 0x4b, 0, 1, 0x41]);
        if (limit) File.WriteAllText(Path.Combine(folder, "text.txt"), string.Concat(Enumerable.Repeat("needle\n", 5001)));
        scope.Dialogs.Query = new GrepQuery("needle", folder, "*", true, false, false);
        Pump(scope.Model.GrepCommand.ExecuteAsync(null));
        var result = scope.Model.SelectedGrep!;
        Assert.Equal(limit ? 5000 : 0, result.Matches.Count);
        if (limit) Assert.Contains("打ち切りました", result.Status);
        else
        {
            Assert.Contains("0 ファイルを検索", result.Status);
            Assert.Contains("読めない 1 ファイルは除外", result.Status);
        }
    });

    private static void Pump(Task task)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(30))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Assert.True(task.IsCompleted, "実経路の操作が30秒以内に終了しませんでした。");
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Scope : IDisposable
    {
        private readonly TemporaryStorage _storage = new();
        public string Path => _storage.Path;
        public MainWindow Window { get; }
        public MainWindowViewModel Model { get; }
        public Dialogs Dialogs { get; } = new();
        public Scope()
        {
            Window = new MainWindow(new AppSettings { RestoreSession = false, CheckUpdatesOnStartup = false });
            Model = (MainWindowViewModel)Window.DataContext!;
            typeof(MainWindowViewModel).GetField("_dialogs", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Model, Dialogs);
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }
        public void Dispose()
        {
            foreach (var document in Model.Documents) document.MarkSaved(System.IO.Path.Combine(Path, "closed.txt"));
            Model.Options.RestoreSession = false;
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            _storage.Dispose();
        }
    }

    private sealed class Dialogs : IEditorDialogService
    {
        public GrepQuery? Query { get; set; }
        public Task<GrepQuery?> PickGrepQueryAsync(GrepQuery initial) => Task.FromResult(Query);
        public Task<IReadOnlyList<string>> PickOpenPathsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);
        public Task<UnsavedDocumentDecision> ConfirmUnsavedAsync(string documentName) => Task.FromResult(UnsavedDocumentDecision.Discard);
        public Task ShowErrorAsync(string title, string message) => throw new InvalidOperationException($"{title}: {message}");
        public Task<int?> PickLineNumberAsync(int currentLine, int maximumLine) => Task.FromResult<int?>(null);
        public Task<string?> PromptTextAsync(string title, string message, string initialText) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task CheckForUpdatesAsync(bool manually) => Task.CompletedTask;
    }
}
