using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

/// <summary>設定タブを文書と同じ一覧へ並べたことで生じる境界（重複・閉じる・コマンドの可否）の検証。</summary>
public sealed class WorkspaceTabTests
{
    [Fact]
    public void SettingsOpensAsATabAndIsNotDuplicated()
    {
        var viewModel = CreateViewModel();

        viewModel.OpenSettingsCommand.Execute(null);
        viewModel.OpenSettingsCommand.Execute(null);

        Assert.Single(viewModel.Tabs, tab => tab.IsSettingsTab);
        Assert.True(viewModel.IsSettingsSelected);
        Assert.False(viewModel.IsDocumentSelected);
        Assert.Null(viewModel.SelectedDocument);
    }

    [Fact]
    public void SettingsTabSitsAfterDocumentsWhenNewOnesAreAdded()
    {
        var viewModel = CreateViewModel();

        viewModel.OpenSettingsCommand.Execute(null);
        viewModel.NewDocumentCommand.Execute(null);

        Assert.True(viewModel.Tabs[^1].IsSettingsTab);
        Assert.Equal(2, viewModel.Tabs.Count(tab => tab.IsDocumentTab));
    }

    [Fact]
    public async Task ClosingSettingsTabKeepsDocumentsOpen()
    {
        var viewModel = CreateViewModel();
        viewModel.OpenSettingsCommand.Execute(null);
        var settingsTab = viewModel.Tabs.First(tab => tab.IsSettingsTab);

        await settingsTab.CloseTabCommand.ExecuteAsync(null);

        Assert.DoesNotContain(viewModel.Tabs, tab => tab.IsSettingsTab);
        Assert.Null(viewModel.SettingsTab);
        Assert.Single(viewModel.Documents);
        Assert.True(viewModel.IsDocumentSelected);
    }

    [Fact]
    public async Task ClosingTheLastDocumentLeavesAnEmptyOneEvenWithSettingsOpen()
    {
        var viewModel = CreateViewModel();
        viewModel.OpenSettingsCommand.Execute(null);
        var document = viewModel.Documents.Single();

        await document.CloseTabCommand.ExecuteAsync(null);

        Assert.Single(viewModel.Documents);
        Assert.NotSame(document, viewModel.Documents.Single());
        Assert.Contains(viewModel.Tabs, tab => tab.IsSettingsTab);
    }

    [Fact]
    public void DocumentCommandsAreDisabledWhileTheSettingsTabIsSelected()
    {
        var viewModel = CreateViewModel();

        viewModel.OpenSettingsCommand.Execute(null);

        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.SaveAsCommand.CanExecute(null));
        Assert.False(viewModel.GoToLineCommand.CanExecute(null));
        Assert.False(viewModel.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void SelectingADocumentAgainReenablesDocumentCommands()
    {
        var viewModel = CreateViewModel();
        var document = viewModel.Documents.Single();

        viewModel.OpenSettingsCommand.Execute(null);
        viewModel.SelectedTab = document;

        Assert.True(viewModel.SaveCommand.CanExecute(null));
        Assert.True(viewModel.GoToLineCommand.CanExecute(null));
        Assert.Same(document, viewModel.SelectedDocument);
    }

    [Fact]
    public void OptionChangesAreWrittenThroughToTheSettingsObject()
    {
        using var storage = new TemporaryStorage();
        var settings = new AppSettings();
        var viewModel = CreateViewModel(settings);

        viewModel.Options.WordWrap = true;
        viewModel.Options.UiFontSize = 1;
        viewModel.Options.EditorFontSize = 999;

        Assert.True(settings.WordWrap);
        Assert.Equal(8, settings.UiFontSize); // 下限で丸められる
        Assert.Equal(48, settings.FontSize); // 上限で丸められる
        Assert.Equal(8, SettingsService.Load().UiFontSize);
        Assert.Equal(48, SettingsService.Load().FontSize);
    }

    [Fact]
    public void TabTitleFollowsTheDocumentDirtyMarker()
    {
        var viewModel = CreateViewModel();
        var document = viewModel.Documents.Single();

        document.Text = "変更";

        Assert.Equal(document.DisplayTitle, document.TabTitle);
        Assert.Contains("●", document.TabTitle);
    }

    /// <summary>
    /// 関連付けは別ダイアログをやめて設定タブへ直接並べた。一覧が
    /// <see cref="FileAssociationService.SupportedTypes"/> と 1 対 1 で対応していないと、
    /// トグルの位置と実際に書き換わる拡張子がずれる。
    ///
    /// トグルの操作そのものは実レジストリを書き換えてしまうため、ここでは検証しない。
    /// </summary>
    [Fact]
    public void AssociationRowsMirrorTheSupportedFileTypes()
    {
        var viewModel = CreateViewModel();
        viewModel.OpenSettingsCommand.Execute(null);
        var settingsTab = Assert.IsType<SettingsTabViewModel>(viewModel.SettingsTab);

        Assert.Equal(
            FileAssociationService.SupportedTypes.Select(type => type.Extension),
            settingsTab.Associations.Select(item => item.Extension));
        Assert.Equal(
            FileAssociationService.SupportedTypes.Select(type => type.Description),
            settingsTab.Associations.Select(item => item.Description));
    }

    private static MainWindowViewModel CreateViewModel(AppSettings? settings = null)
        => new(new StubFileService(), new StubDialogService(), settings);

    private sealed class StubFileService : IDocumentFileService
    {
        public Task<TextDocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(new TextDocumentContent(string.Empty, DocumentEncoding.Utf8, Environment.NewLine));

        public Task WriteAsync(
            string path,
            TextDocumentContent content,
            bool createBackup = false,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubDialogService : IEditorDialogService
    {
        public Task<IReadOnlyList<string>> PickOpenPathsAsync()
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);

        public Task<UnsavedDocumentDecision> ConfirmUnsavedAsync(string documentName)
            => Task.FromResult(UnsavedDocumentDecision.Discard);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<int?> PickLineNumberAsync(int currentLine, int maximumLine) => Task.FromResult<int?>(null);

        public Task<string?> PromptTextAsync(string title, string message, string initialText) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<GrepQuery?> PickGrepQueryAsync(GrepQuery initial) => Task.FromResult<GrepQuery?>(null);

        public Task CheckForUpdatesAsync(bool manually) => Task.CompletedTask;
    }
}
