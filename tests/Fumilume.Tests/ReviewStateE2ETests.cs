using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaEdit;
using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class ReviewStateE2ETests(HeadlessAppFixture fixture)
{
    [Fact]
    public void SavingPreviewWhileAnotherFileLoadsKeepsSavedTabInWorkspace() => fixture.Run(() =>
    {
        using var scope = new Scope();
        var first = Path.Combine(scope.StoragePath, "preview-first.txt");
        var second = Path.Combine(scope.StoragePath, "preview-second.txt");
        File.WriteAllText(first, "保存するプレビュー");
        File.WriteAllText(second, "次のプレビュー");
        Pump(scope.ViewModel.OpenFolderFileAsync(first, false));
        var preview = scope.ViewModel.SelectedDocument!;
        Assert.True(preview.IsPreview);
        var gate = new TaskCompletionSource();
        scope.Files.ReadGate = gate;
        var opening = scope.ViewModel.OpenFolderFileAsync(second, false);
        PumpUntil(() => scope.Files.ReadStarted);
        var saving = scope.ViewModel.SaveCommand.ExecuteAsync(null);
        Assert.False(saving.IsCompleted);
        gate.SetResult();
        Pump(Task.WhenAll(opening, saving));
        Artifact("preview-save-race", new
        {
            SavedTabRetained = scope.ViewModel.Tabs.Contains(preview),
            SelectedTabBelongsToWorkspace = scope.ViewModel.Tabs.Contains(scope.ViewModel.SelectedTab!),
            SavedText = File.ReadAllText(first),
        });
        Assert.Contains(preview, scope.ViewModel.Tabs);
        Assert.Same(preview, scope.ViewModel.SelectedTab);
        Assert.False(preview.IsPreview);
        Assert.Same(preview.EditorDocument, scope.Editor.Document);
        Assert.Equal("保存するプレビュー", File.ReadAllText(first));
        Assert.Single(scope.ViewModel.Documents, document => document.FilePath == second);
    });

    [Theory]
    [InlineData("reload-read", false)]
    [InlineData("reload-read", true)]
    [InlineData("reload-confirm", false)]
    [InlineData("reload-confirm", true)]
    [InlineData("external-confirm", false)]
    [InlineData("external-confirm", true)]
    [InlineData("discard", false)]
    [InlineData("discard", true)]
    [InlineData("exit", false)]
    [InlineData("exit", true)]
    public void PendingApprovalKeepsStatusBarFormatChanges(string operation, bool newLine) => fixture.Run(() =>
    {
        using var scope = new Scope();
        var document = scope.Open("format.txt", "disk\r\nline");
        var version = document.EditorDocument.Version;
        Task pending;
        Action release;
        if (operation == "reload-read")
        {
            File.WriteAllText(document.FilePath!, "external\r\nline");
            var gate = new TaskCompletionSource();
            scope.Files.ReadGate = gate;
            pending = scope.ViewModel.ReloadCommand.ExecuteAsync(null);
            PumpUntil(() => scope.Files.ReadStarted);
            release = gate.SetResult;
        }
        else if (operation is "reload-confirm" or "external-confirm")
        {
            scope.Editor.Document.Insert(0, "old-");
            version = document.EditorDocument.Version;
            File.WriteAllText(document.FilePath!, "external\r\nline");
            var gate = new TaskCompletionSource<bool>();
            scope.Dialogs.Confirmation = () => gate.Task;
            pending = operation == "external-confirm"
                ? scope.ViewModel.ProcessExternalFileChangeAsync(document.FilePath!)
                : scope.ViewModel.ReloadCommand.ExecuteAsync(null);
            PumpUntil(() => scope.Dialogs.ConfirmationStarted);
            release = () => gate.SetResult(true);
        }
        else if (operation == "discard")
        {
            scope.Editor.Document.Insert(0, "old-");
            version = document.EditorDocument.Version;
            var gate = new TaskCompletionSource<UnsavedDocumentDecision>();
            scope.Dialogs.Unsaved = _ => gate.Task;
            pending = document.CloseTabCommand.ExecuteAsync(null);
            release = () => gate.SetResult(UnsavedDocumentDecision.Discard);
        }
        else
        {
            scope.Editor.Document.Insert(0, "old-");
            version = document.EditorDocument.Version;
            var second = scope.Open("second-format.txt", "second");
            scope.Editor.Document.Insert(0, "old-");
            var gate = new TaskCompletionSource<UnsavedDocumentDecision>();
            scope.Dialogs.Unsaved = name => name == second.DisplayName ? gate.Task
                : Task.FromResult(UnsavedDocumentDecision.Discard);
            pending = scope.ViewModel.CanCloseAsync();
            scope.ViewModel.SelectedTab = document;
            Dispatcher.UIThread.RunJobs();
            release = () => gate.SetResult(UnsavedDocumentDecision.Discard);
        }

        var text = document.Text;
        ChangeFormat(scope.ViewModel, newLine);
        Assert.Same(version, document.EditorDocument.Version);
        Assert.True(document.IsModified);
        release();
        Pump(pending);
        if (operation == "exit") Assert.False(((Task<bool>)pending).Result);
        Assert.Contains(document, scope.ViewModel.Tabs);
        Assert.Equal(text, document.Text);
        Assert.Equal(newLine ? DocumentEncoding.Utf8 : DocumentEncoding.Utf16LittleEndian, document.Encoding);
        Assert.Equal(newLine ? DocumentNewLines.Lf : DocumentNewLines.CrLf, document.NewLine);
        Assert.True(document.IsModified);
        Artifact($"format-{operation}-{newLine}", new { document.Text, document.Encoding, document.NewLine,
            document.IsModified, TextVersionUnchanged = true, scope.ViewModel.StatusMessage });
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SaveDuringCloseKeepsConcurrentStatusBarFormat(bool exit, bool newLine) => fixture.Run(() =>
    {
        using var scope = new Scope();
        var document = scope.Open("save-format.txt", "disk\r\nline");
        scope.Editor.Document.Insert(0, "snapshot-");
        var version = document.EditorDocument.Version;
        var gate = new TaskCompletionSource();
        scope.Files.WriteGate = gate;
        var close = exit ? scope.ViewModel.CanCloseAsync() : document.CloseTabCommand.ExecuteAsync(null);
        PumpUntil(() => scope.Files.WriteStarted);
        ChangeFormat(scope.ViewModel, newLine);
        Assert.Same(version, document.EditorDocument.Version);
        gate.SetResult();
        Pump(close);
        if (exit) Assert.False(((Task<bool>)close).Result);
        Assert.Contains(document, scope.ViewModel.Tabs);
        Assert.True(document.IsModified);
        var disk = new DocumentFileService().ReadAsync(document.FilePath!);
        Pump(disk);
        Assert.Equal(DocumentEncoding.Utf8, disk.Result.Encoding);
        Assert.Equal(DocumentNewLines.CrLf, disk.Result.NewLine);
        Assert.Equal(newLine ? DocumentEncoding.Utf8 : DocumentEncoding.Utf16LittleEndian, document.Encoding);
        Assert.Equal(newLine ? DocumentNewLines.Lf : DocumentNewLines.CrLf, document.NewLine);
        Artifact($"save-format-{exit}-{newLine}", new { document.Encoding, document.NewLine, document.IsModified,
            DiskEncoding = disk.Result.Encoding, DiskNewLine = disk.Result.NewLine });
    });

    private static void ChangeFormat(MainWindowViewModel viewModel, bool newLine)
    {
        if (newLine) viewModel.SetDocumentNewLineCommand.Execute(DocumentNewLines.Lf);
        else viewModel.SetDocumentEncodingCommand.Execute(DocumentEncoding.Utf16LittleEndian);
        Dispatcher.UIThread.RunJobs();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveDuringCloseKeepsConcurrentEditorInput(bool exit) => fixture.Run(() =>
    {
        using var scope = new Scope();
        var document = scope.Open("save.txt", "disk");
        scope.Editor.Text = "snapshot";
        scope.Files.WriteGate = new TaskCompletionSource();
        var close = exit ? scope.ViewModel.CanCloseAsync() : document.CloseTabCommand.ExecuteAsync(null);
        PumpUntil(() => scope.Files.WriteStarted);
        scope.Editor.TextArea.Caret.Offset = scope.Editor.Document.TextLength;
        scope.Editor.Document.Insert(scope.Editor.Document.TextLength, "+new");
        scope.Files.WriteGate.SetResult();
        Pump(close);
        if (exit) Assert.False(((Task<bool>)close).Result);
        Assert.Contains(document, scope.ViewModel.Tabs);
        Assert.Equal("snapshot+new", scope.Editor.Text);
        Assert.True(document.IsModified);
        Assert.Equal("snapshot", File.ReadAllText(document.FilePath!));
        Artifact($"save-{exit}", new { document.Text, document.IsModified, Disk = File.ReadAllText(document.FilePath!) });
        Pump(scope.ViewModel.SaveCommand.ExecuteAsync(null));
        Assert.False(document.IsModified);
        Assert.Equal("snapshot+new", File.ReadAllText(document.FilePath!));
        Pump(document.CloseTabCommand.ExecuteAsync(null));
        Assert.DoesNotContain(document, scope.ViewModel.Tabs);
    });

    [Fact]
    public void ReloadDuringReadKeepsEditorInputAndUndo() => fixture.Run(() =>
    {
        using var scope = new Scope();
        var document = scope.Open("reload.txt", "disk");
        File.WriteAllText(document.FilePath!, "external");
        scope.Files.ReadGate = new TaskCompletionSource();
        var reload = scope.ViewModel.ReloadCommand.ExecuteAsync(null);
        PumpUntil(() => scope.Files.ReadStarted);
        scope.Editor.Document.Insert(scope.Editor.Document.TextLength, "+new");
        scope.Files.ReadGate.SetResult();
        Pump(reload);
        Assert.Equal("disk+new", scope.Editor.Text);
        Assert.True(document.IsModified);
        Assert.True(document.CanUndo);
        Artifact("reload-read", new { document.Text, document.IsModified, document.CanUndo, scope.ViewModel.StatusMessage });
        scope.ViewModel.UndoCommand.Execute(null);
        Assert.Equal("disk", scope.Editor.Text);
        Pump(scope.ViewModel.ReloadCommand.ExecuteAsync(null));
        Assert.Equal("external", scope.Editor.Text);
        Assert.False(document.IsModified);
    });

    [Fact]
    public void DiscardConfirmationDoesNotAuthorizeNewEditorInput() => fixture.Run(() =>
    {
        using var scope = new Scope();
        var document = scope.Open("discard.txt", "disk");
        scope.Editor.Document.Insert(0, "old-");
        var gate = new TaskCompletionSource<UnsavedDocumentDecision>();
        scope.Dialogs.Unsaved = _ => gate.Task;
        var close = document.CloseTabCommand.ExecuteAsync(null);
        scope.Editor.Document.Insert(0, "new-");
        gate.SetResult(UnsavedDocumentDecision.Discard);
        Pump(close);
        Assert.Contains(document, scope.ViewModel.Tabs);
        Assert.Equal("new-old-disk", scope.Editor.Text);
        Artifact("discard-confirmation", new { document.Text, Retained = true });
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReloadConfirmationDoesNotDiscardNewEditorInput(bool external) => fixture.Run(() =>
    {
        using var scope = new Scope();
        var document = scope.Open("confirm-reload.txt", "disk");
        scope.Editor.Document.Insert(0, "old-");
        File.WriteAllText(document.FilePath!, "external");
        var gate = new TaskCompletionSource<bool>();
        scope.Dialogs.Confirmation = () => gate.Task;
        var reload = external ? scope.ViewModel.ProcessExternalFileChangeAsync(document.FilePath!)
            : scope.ViewModel.ReloadCommand.ExecuteAsync(null);
        PumpUntil(() => scope.Dialogs.ConfirmationStarted);
        scope.Editor.Document.Insert(0, "new-");
        gate.SetResult(true);
        Pump(reload);
        Assert.Equal("new-old-disk", scope.Editor.Text);
        Assert.True(document.IsModified);
        Assert.True(document.CanUndo);
        Artifact($"reload-confirmation-{external}", new { document.Text, document.IsModified, document.CanUndo });
    });

    [Fact]
    public void ExitRechecksPreviouslyResolvedDocuments() => fixture.Run(() =>
    {
        using var scope = new Scope();
        var first = scope.Open("first.txt", "first");
        scope.Editor.Document.Insert(0, "edit-");
        var second = scope.Open("second.txt", "second");
        scope.Editor.Document.Insert(0, "edit-");
        scope.Dialogs.Unsaved = name =>
        {
            if (name == second.DisplayName) first.EditorDocument.Insert(0, "later-");
            return Task.FromResult(UnsavedDocumentDecision.Discard);
        };
        var close = scope.ViewModel.CanCloseAsync();
        Pump(close);
        Assert.False(close.Result);
        Assert.Equal("later-edit-first", first.Text);
        Artifact("exit-later-edit", new { first.Text, CanClose = close.Result });
        scope.Dialogs.Unsaved = _ => Task.FromResult(UnsavedDocumentDecision.Discard);
        close = scope.ViewModel.CanCloseAsync();
        Pump(close);
        Assert.True(close.Result);
    });

    [Fact]
    public void NullCaretSettingsStartWindowAndRemainBoundedOnDisk() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var path = Path.Combine(storage.Path, "settings.json");
        File.WriteAllText(path, "{\"CaretPositions\":null,\"FontSize\":18,\"CheckUpdatesOnStartup\":false}");
        var settings = SettingsService.Load();
        Assert.Empty(settings.CaretPositions);
        Assert.Equal(18, settings.FontSize);
        var window = new MainWindow(settings);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(18, window.FindControl<TextEditor>("Editor")!.FontSize);
            for (var i = 0; i < AppSettingsDefaults.MaximumCaretPositions + 5; i++)
                settings.CaretPositions[$"document-{i}"] = i;
            SettingsService.Save(settings);
            Assert.Equal(AppSettingsDefaults.MaximumCaretPositions, SettingsService.Load().CaretPositions.Count);
            File.WriteAllText(path, "invalid json");
            Assert.Equal(18, SettingsService.Load().FontSize);
            File.Delete(path + ".bak");
            Assert.Equal(AppSettingsDefaults.FontSize, SettingsService.Load().FontSize);
            Artifact("settings-null", new { NullNormalized = true, Maximum = AppSettingsDefaults.MaximumCaretPositions,
                BackupRecovered = true, DefaultRecovered = true });
        }
        finally
        {
            ((MainWindowViewModel)window.DataContext!).Options.RestoreSession = true;
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    private static void Pump(Task task)
    {
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private static void PumpUntil(Func<bool> done)
    {
        var timeout = Stopwatch.StartNew();
        while (!done() && timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Yield();
        }
        Assert.True(done(), "非同期UI処理が15秒以内に完了しませんでした");
    }

    private static void Artifact(string name, object result)
    {
        var path = Environment.GetEnvironmentVariable("FUMILUME_E2E_ARTIFACTS");
        if (string.IsNullOrEmpty(path)) return;
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, $"review-state-{name}.json"), JsonSerializer.Serialize(result));
    }

    private sealed class Scope : IDisposable
    {
        private readonly TemporaryStorage _storage = new();
        public MainWindow Window { get; }
        public MainWindowViewModel ViewModel { get; }
        public TextEditor Editor { get; }
        public string StoragePath => _storage.Path;
        public DelayedFiles Files { get; } = new();
        public Dialogs Dialogs { get; } = new();

        public Scope()
        {
            Window = new MainWindow(new AppSettings { RestoreSession = false, CheckUpdatesOnStartup = false });
            ViewModel = (MainWindowViewModel)Window.DataContext!;
            // 実ウィンドウの入力経路を保ち、ディスク処理の待ち時間だけを制御する。
            SetField("_files", Files);
            SetField("_dialogs", Dialogs);
            SetField("_fileChangeMonitor", null);
            Window.Show();
            Dispatcher.UIThread.RunJobs();
            Editor = Window.FindControl<TextEditor>("Editor")!;
        }

        private void SetField(string name, object? value) => typeof(MainWindowViewModel)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ViewModel, value);

        public DocumentViewModel Open(string name, string text)
        {
            var path = Path.Combine(_storage.Path, name);
            File.WriteAllText(path, text);
            Pump(ViewModel.OpenPathsAsync([path]));
            return ViewModel.SelectedDocument!;
        }

        public void Dispose()
        {
            ViewModel.Options.RestoreSession = true;
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            _storage.Dispose();
        }
    }

    private sealed class DelayedFiles : IDocumentFileService
    {
        private readonly DocumentFileService _real = new();
        public TaskCompletionSource? ReadGate { get; set; }
        public TaskCompletionSource? WriteGate { get; set; }
        public bool ReadStarted { get; private set; }
        public bool WriteStarted { get; private set; }
        public async Task<TextDocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (ReadGate is { } gate)
            {
                ReadStarted = true;
                await gate.Task;
                ReadGate = null;
            }
            return await _real.ReadAsync(path, cancellationToken);
        }
        public async Task WriteAsync(string path, TextDocumentContent content, bool createBackup = false,
            CancellationToken cancellationToken = default)
        {
            if (WriteGate is { } gate)
            {
                WriteStarted = true;
                await gate.Task;
                WriteGate = null;
            }
            await _real.WriteAsync(path, content, createBackup, cancellationToken);
        }
    }

    private sealed class Dialogs : IEditorDialogService
    {
        public Func<Task<bool>> Confirmation { get; set; } = () => Task.FromResult(true);
        public bool ConfirmationStarted { get; private set; }
        public Func<string, Task<UnsavedDocumentDecision>> Unsaved { get; set; }
            = _ => Task.FromResult(UnsavedDocumentDecision.Save);
        public Task<IReadOnlyList<string>> PickOpenPathsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);
        public Task<UnsavedDocumentDecision> ConfirmUnsavedAsync(string documentName) => Unsaved(documentName);
        public Task ShowErrorAsync(string title, string message) => throw new InvalidOperationException($"{title}: {message}");
        public Task<int?> PickLineNumberAsync(int currentLine, int maximumLine) => Task.FromResult<int?>(null);
        public Task<string?> PromptTextAsync(string title, string message, string initialText) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmationStarted = true;
            return Confirmation();
        }
        public Task<GrepQuery?> PickGrepQueryAsync(GrepQuery initial) => Task.FromResult<GrepQuery?>(null);
        public Task CheckForUpdatesAsync(bool manually) => Task.CompletedTask;
    }
}
