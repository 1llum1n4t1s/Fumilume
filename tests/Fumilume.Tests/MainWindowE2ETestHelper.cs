using System.Diagnostics;
using System.Reflection;
using Avalonia.Threading;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

internal static class MainWindowE2ETestHelper
{
    internal static void Pump(Task task)
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

    internal sealed class Scope : IDisposable
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

    internal sealed class Dialogs : IEditorDialogService
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
