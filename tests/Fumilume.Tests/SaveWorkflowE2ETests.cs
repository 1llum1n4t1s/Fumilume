using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Threading;
using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class SaveWorkflowE2ETests(HeadlessAppFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UndoReturnsToTheSnapshotWrittenDuringConcurrentEditing(bool mixedNewLines) => fixture.Run(() =>
    {
        using var scope = new Scope();
        var document = scope.Open("disk");
        document.NewLine = "\n";
        document.EditorDocument.Insert(document.Text.Length, mixedNewLines ? "\r\nfirst\nsecond\rthird" : "\nfirst");
        var snapshot = document.Text;
        var save = scope.BeginDelayedSave();
        document.EditorDocument.Insert(document.Text.Length, " later");
        scope.CompleteSave(save);
        Assert.True(document.IsModified);
        Assert.Equal(DocumentFileService.NormalizeNewLines(snapshot, "\n"), File.ReadAllText(scope.FilePath));

        document.EditorDocument.UndoStack.Undo();
        Assert.Equal(snapshot, document.Text);
        Assert.False(document.IsModified);
        Assert.DoesNotContain("●", document.DisplayTitle);
        Pump(scope.Model.SaveAllCommand.ExecuteAsync(null));
        Assert.Equal(1, scope.Files.Writes);

        document.EditorDocument.UndoStack.Undo();
        Assert.Equal("disk", document.Text);
        Assert.True(document.IsModified);
        document.EditorDocument.UndoStack.Redo();
        Assert.Equal(snapshot, document.Text);
        Assert.False(document.IsModified);
        document.EditorDocument.UndoStack.Redo();
        Assert.Equal(snapshot + " later", document.Text);
        Assert.True(document.IsModified);
        document.EditorDocument.UndoStack.Undo();
        Pump(document.CloseTabCommand.ExecuteAsync(null));
        Assert.Equal(0, scope.Dialogs.UnsavedPrompts);
        Record("concurrent-" + mixedNewLines, new { Snapshot = snapshot, Disk = File.ReadAllText(scope.FilePath), scope.Files.Writes });
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataChangedDuringSaveRemainsDirtyUntilRestored(bool encoding) => fixture.Run(() =>
    {
        using var scope = new Scope();
        var document = scope.Open("disk");
        document.EditorDocument.Insert(document.Text.Length, " snapshot");
        var savedEncoding = document.Encoding;
        var savedNewLine = document.NewLine;
        var save = scope.BeginDelayedSave();
        if (encoding) document.Encoding = DocumentEncoding.Utf8Bom;
        else document.NewLine = savedNewLine == "\n" ? "\r\n" : "\n";
        scope.CompleteSave(save);
        Assert.True(document.IsModified);
        document.EditorDocument.Insert(document.Text.Length, " later");
        document.EditorDocument.UndoStack.Undo();
        Assert.Equal("disk snapshot", document.Text);
        Assert.True(document.IsModified);
        document.Encoding = savedEncoding;
        document.NewLine = savedNewLine;
        Assert.False(document.IsModified);
        document.EditorDocument.UndoStack.Undo();
        Assert.Equal("disk", document.Text);
        Assert.True(document.IsModified);
        document.EditorDocument.UndoStack.Redo();
        Assert.False(document.IsModified);
        Record("metadata-" + encoding, new { document.Text, document.Encoding, document.NewLine, document.IsModified });
    });

    [Fact]
    public void UnchangedAndRestoredDocumentsBecomeCleanOnlyAfterSuccessfulSave() => fixture.Run(() =>
    {
        using var scope = new Scope();
        var document = scope.Open("disk");
        document.EditorDocument.Insert(0, "saved ");
        scope.CompleteSave(scope.BeginDelayedSave());
        Assert.False(document.IsModified);
        document.EditorDocument.Insert(0, "later ");
        document.EditorDocument.UndoStack.Undo();
        Assert.False(document.IsModified);

        document.RestoreUnsaved(scope.FilePath, new TextDocumentContent("restored", DocumentEncoding.Utf8, "\n"));
        document.EditorDocument.Insert(0, "later ");
        document.EditorDocument.UndoStack.Undo();
        Assert.True(document.IsModified);
        var save = scope.BeginDelayedSave();
        document.EditorDocument.Insert(0, "later ");
        scope.CompleteSave(save);
        Assert.True(document.IsModified);
        document.EditorDocument.UndoStack.Undo();
        Assert.Equal("restored", document.Text);
        Assert.False(document.IsModified);
        Record("restored", new { document.Text, Disk = File.ReadAllText(scope.FilePath), document.IsModified });
    });

    [Fact]
    public void FailedAndCanceledSavesKeepThePreviousBaseline() => fixture.Run(() =>
    {
        using var scope = new Scope();
        var document = scope.Open("disk");
        document.EditorDocument.Insert(0, "later ");
        scope.Files.FailWrite = true;
        Pump(scope.Model.SaveCommand.ExecuteAsync(null));
        Assert.Equal(1, scope.Dialogs.Errors);
        Assert.True(document.IsModified);
        Assert.Equal("disk", File.ReadAllText(scope.FilePath));
        document.EditorDocument.UndoStack.Undo();
        Assert.False(document.IsModified);
        document.EditorDocument.UndoStack.Redo();
        Pump(scope.Model.SaveAsCommand.ExecuteAsync(null));
        Assert.True(document.IsModified);
        Assert.Equal("disk", File.ReadAllText(scope.FilePath));
        document.EditorDocument.UndoStack.Undo();
        Assert.False(document.IsModified);
        Record("failure-cancel", new { scope.Dialogs.Errors, Disk = File.ReadAllText(scope.FilePath), document.IsModified });
    });

    [Fact]
    public void ExternalDeletionRemainsDirtyEvenAfterUndoReturnsToTheSavedSnapshot() => fixture.Run(() =>
    {
        using var scope = new Scope();
        var document = scope.Open("disk");
        document.EditorDocument.Insert(0, "snapshot ");
        var save = scope.BeginDelayedSave();
        document.EditorDocument.Insert(0, "later ");
        scope.CompleteSave(save);
        File.Delete(scope.FilePath);
        Pump(scope.Model.ProcessExternalFileChangeAsync(scope.FilePath));
        document.EditorDocument.UndoStack.Undo();
        Assert.Equal("snapshot disk", document.Text);
        Assert.True(document.IsModified);
        File.WriteAllText(scope.FilePath, "snapshot disk");
        Pump(scope.Model.ProcessExternalFileChangeAsync(scope.FilePath));
        Assert.False(document.IsModified);
        Record("external-deletion", new { document.Text, document.IsModified });
    });

    private static void Record(string name, object value)
    {
        if (Environment.GetEnvironmentVariable("FUMILUME_E2E_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"save-workflow-{name}.json"), JsonSerializer.Serialize(value));
    }

    private static void Pump(Task task)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(30))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Assert.True(task.IsCompleted, "保存操作が30秒以内に終了しませんでした。");
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Scope : IDisposable
    {
        private readonly TemporaryStorage _storage = new();
        public string FilePath => Path.Combine(_storage.Path, "document.txt");
        public MainWindow Window { get; }
        public MainWindowViewModel Model { get; }
        public DelayedFileService Files { get; } = new();
        public Dialogs Dialogs { get; } = new();

        public Scope()
        {
            Window = new MainWindow(new AppSettings { RestoreSession = false, CheckUpdatesOnStartup = false });
            Model = (MainWindowViewModel)Window.DataContext!;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            // 外部通知はテストから実処理へ明示的に渡し、保存ゲートとの競合を防ぐ。
            ((IDisposable)typeof(MainWindowViewModel).GetField("_fileChangeMonitor", flags)!.GetValue(Model)!).Dispose();
            typeof(MainWindowViewModel).GetField("_fileChangeMonitor", flags)!.SetValue(Model, null);
            typeof(MainWindowViewModel).GetField("_files", flags)!.SetValue(Model, Files);
            typeof(MainWindowViewModel).GetField("_dialogs", flags)!.SetValue(Model, Dialogs);
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public DocumentViewModel Open(string text)
        {
            File.WriteAllText(FilePath, text);
            Pump(Model.OpenPathsAsync([FilePath]));
            return Model.SelectedDocument!;
        }

        public Task BeginDelayedSave()
        {
            Files.HoldNextWrite();
            var save = Model.SaveCommand.ExecuteAsync(null);
            Pump(Files.Written.Task);
            Assert.False(save.IsCompleted);
            return save;
        }

        public void CompleteSave(Task save)
        {
            Files.Release.TrySetResult();
            Pump(save);
        }

        public void Dispose()
        {
            Files.Release.TrySetResult();
            Model.Options.RestoreSession = false;
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            _storage.Dispose();
        }
    }

    private sealed class DelayedFileService : IDocumentFileService
    {
        private readonly DocumentFileService _inner = new();
        private bool _hold;
        public bool FailWrite { get; set; }
        public int Writes { get; private set; }
        public TaskCompletionSource Written { get; private set; } = new();
        public TaskCompletionSource Release { get; private set; } = new();
        public void HoldNextWrite()
        {
            _hold = true;
            Written = new();
            Release = new();
        }
        public Task<TextDocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(path, cancellationToken);
        public async Task WriteAsync(string path, TextDocumentContent content, bool createBackup = false, CancellationToken cancellationToken = default)
        {
            if (FailWrite) throw new IOException("E2Eの書き込み失敗");
            await _inner.WriteAsync(path, content, createBackup, cancellationToken);
            Writes++;
            if (_hold)
            {
                _hold = false;
                Written.TrySetResult();
                await Release.Task;
            }
        }
    }

    private sealed class Dialogs : IEditorDialogService
    {
        public int UnsavedPrompts { get; private set; }
        public int Errors { get; private set; }
        public Task<IReadOnlyList<string>> PickOpenPathsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);
        public Task<UnsavedDocumentDecision> ConfirmUnsavedAsync(string documentName)
        {
            UnsavedPrompts++;
            return Task.FromResult(UnsavedDocumentDecision.Discard);
        }
        public Task ShowErrorAsync(string title, string message) { Errors++; return Task.CompletedTask; }
        public Task<int?> PickLineNumberAsync(int currentLine, int maximumLine) => Task.FromResult<int?>(null);
        public Task<string?> PromptTextAsync(string title, string message, string initialText) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<GrepQuery?> PickGrepQueryAsync(GrepQuery initial) => Task.FromResult<GrepQuery?>(null);
        public Task CheckForUpdatesAsync(bool manually) => Task.CompletedTask;
    }
}
