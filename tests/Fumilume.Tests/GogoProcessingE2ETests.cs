using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaEdit;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class GogoProcessingE2ETests(HeadlessAppFixture fixture)
{
    [Theory]
    [InlineData("markdown", ".md")]
    [InlineData("braces", ".cs")]
    [InlineData("unclosed-frontmatter", ".md")]
    public void OutlineEditingKeepsFirstTwoThousandItems(string kind, string extension) => fixture.Run(() =>
    {
        using var scope = new Scope();
        var prefix = kind switch
        {
            "markdown" => "---\r\ntitle: front matter\r\n---\r\n```\r\n# ignored\r\n```\r\nsetext\r\n======\r\n",
            "unclosed-frontmatter" => "---\r\ntitle: unclosed\r\n",
            _ => "/* class Ignored {} */\r\n",
        };
        var text = prefix + string.Concat(Enumerable.Range(0, 50_000).Select(index =>
            extension == ".cs" ? $"class Item{index} {{}}\r\n" : $"# Item{index}\r\n"));
        var file = Path.Combine(scope.Path, "outline" + extension);
        File.WriteAllText(file, text);
        Pump(scope.Model.OpenPathsAsync([file]));
        scope.Model.SidePanel.Kind = SidePanelKind.Outline;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2000, scope.Model.SidePanel.Outline.Count);
        var original = scope.Model.SidePanel.Outline.ToArray();
        Assert.Equal(kind == "markdown" ? "setext" : "Item0", original[0].Title);
        Assert.Equal(kind == "markdown" ? "Item1998" : "Item1999", original[^1].Title);
        var editor = scope.Window.FindControl<TextEditor>("Editor")!;
        // 表示中の本文変更を通し、上限後ろの編集でも公開アウトラインが同じことを確認する。
        var measurements = new List<object>();
        for (var repeat = 0; repeat < 3; repeat++)
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            var elapsed = Stopwatch.StartNew();
            editor.Document.Insert(editor.Document.TextLength, " ");
            Dispatcher.UIThread.RunJobs();
            elapsed.Stop();
            measurements.Add(new { Milliseconds = elapsed.Elapsed.TotalMilliseconds,
                AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - start });
            Assert.Equal(original, scope.Model.SidePanel.Outline.ToArray());
            editor.Undo();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(text, editor.Text);
        }
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(original))));
        Artifact("outline-" + kind, new { Items = original.Length, Digest = digest, Measurements = measurements });
    });

    [Fact]
    public void FolderSearchLimitsShortLinesAndPreservesMixedNewlinePositions() => fixture.Run(() =>
    {
        using var scope = new Scope();
        File.WriteAllText(Path.Combine(scope.Path, "short-lines.txt"), string.Concat(Enumerable.Repeat("needle\n", 200_000)));
        var measurements = new List<object>();
        for (var repeat = 0; repeat < 3; repeat++)
        {
            scope.Dialogs.Query = new GrepQuery("needle", scope.Path, "short-lines.txt", false, false, false);
            var start = GC.GetTotalAllocatedBytes(precise: true);
            var elapsed = Stopwatch.StartNew();
            Pump(scope.Model.GrepCommand.ExecuteAsync(null));
            elapsed.Stop();
            measurements.Add(new { Milliseconds = elapsed.Elapsed.TotalMilliseconds,
                AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - start });
            var result = scope.Model.SelectedGrep!;
            Assert.Equal(5000, result.Matches.Count);
            Assert.Equal(1, result.Matches[0].Match.LineNumber);
            Assert.Equal(5000, result.Matches[^1].Match.LineNumber);
            Assert.All(result.Matches, match =>
            {
                Assert.Equal(1, match.Match.Column);
                Assert.Equal("needle", match.Match.LineText);
            });
            Assert.Contains("打ち切り", result.Status);
            Pump(result.CloseTabCommand.ExecuteAsync(null));
        }
        Artifact("grep-short-lines", new { Matches = 5000, Measurements = measurements });

        File.WriteAllText(Path.Combine(scope.Path, "mixed.txt"), "needle\r\n xneedle\rneedle\n");
        scope.Dialogs.Query = new GrepQuery("^", scope.Path, "mixed.txt", false, false, true);
        Pump(scope.Model.GrepCommand.ExecuteAsync(null));
        var mixed = scope.Model.SelectedGrep!.Matches.Select(item => item.Match).ToArray();
        Assert.Equal(new[] { 1, 2, 3, 4 }, mixed.Select(match => match.LineNumber));
        Assert.Equal(new[] { "needle", " xneedle", "needle", "" }, mixed.Select(match => match.LineText));
        Artifact("grep-mixed-lines", new { Lines = mixed.Select(match => new { match.LineNumber, match.Column, match.LineText }) });
    });

    private static void Artifact(string name, object value)
    {
        if (Environment.GetEnvironmentVariable("FUMILUME_E2E_ARTIFACTS") is not { Length: > 0 } path) return;
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, $"gogo-processing-{name}.json"), JsonSerializer.Serialize(value));
    }

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
