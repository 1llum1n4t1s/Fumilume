using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

/// <summary>
/// キーボードマクロ。記録した操作を当て直す作りなので、「記録が意味どおりに積まれること」と
/// 「再生が記録どおりの結果になること」を別々に確かめる。
/// </summary>
public sealed class MacroTests
{
    [Fact]
    public void TypingIsRecordedAsOneStep()
    {
        var viewModel = CreateViewModel();
        viewModel.ToggleMacroRecordingCommand.Execute(null);

        Type(viewModel, "a");
        Type(viewModel, "b");
        Type(viewModel, "c");

        Assert.Equal(1, viewModel.RecordedStepCount);
        Assert.True(viewModel.IsRecordingMacro);
    }

    [Fact]
    public void MovingBreaksTheRunOfTypedText()
    {
        var viewModel = CreateViewModel();
        viewModel.ToggleMacroRecordingCommand.Execute(null);

        Type(viewModel, "ab");
        viewModel.RecordMacroStep(Motion(MacroMotion.LineStart));
        Type(viewModel, "c");

        Assert.Equal(3, viewModel.RecordedStepCount);
    }

    [Fact]
    public async Task ReplayAppliesEveryStepInOrder()
    {
        var viewModel = CreateViewModel();
        var document = viewModel.Documents.Single();
        document.Text = "one";
        document.CaretIndex = document.Text.Length;

        viewModel.ToggleMacroRecordingCommand.Execute(null);
        viewModel.RecordMacroStep(Motion(MacroMotion.LineStart));
        Type(viewModel, "> ");
        viewModel.ToggleMacroRecordingCommand.Execute(null);

        document.Text = "two";
        document.CaretIndex = document.Text.Length;
        await viewModel.RunMacroCommand.ExecuteAsync(null);

        Assert.Equal("> two", document.Text);
    }

    [Fact]
    public async Task CommandsAreRecordedAndReplayed()
    {
        var viewModel = CreateViewModel();
        var document = viewModel.Documents.Single();
        document.Text = "abc";

        viewModel.ToggleMacroRecordingCommand.Execute(null);
        document.SelectionStart = 0;
        document.SelectionLength = 3;
        await viewModel.RunEditorCommandCommand.ExecuteAsync(EditorCommandId.ToUpper);
        viewModel.ToggleMacroRecordingCommand.Execute(null);

        Assert.Equal("ABC", document.Text);
        Assert.Equal(1, viewModel.RecordedStepCount);

        document.Text = "xyz";
        document.SelectionStart = 0;
        document.SelectionLength = 3;
        await viewModel.RunMacroCommand.ExecuteAsync(null);

        Assert.Equal("XYZ", document.Text);
    }

    /// <summary>入力を尋ねるコマンドは、記録できても再生で止まる。記録の時点で弾く。</summary>
    [Fact]
    public async Task CommandsThatAskForInputAreNotRecorded()
    {
        var viewModel = CreateViewModel();
        viewModel.ToggleMacroRecordingCommand.Execute(null);

        await viewModel.RunEditorCommandCommand.ExecuteAsync(EditorCommandId.GoToLine);

        Assert.Equal(0, viewModel.RecordedStepCount);
        Assert.Contains("記録できません", viewModel.StatusMessage);
    }

    /// <summary>100 回流したマクロを Ctrl+Z 100 回で戻すのは、取り消せないのと変わらない。</summary>
    [Fact]
    public async Task OneUndoTakesBackTheWholeRun()
    {
        var viewModel = CreateViewModel(new StubDialogService { Answer = "3" });
        var document = viewModel.Documents.Single();
        document.Text = "one\ntwo\nthree";

        viewModel.ToggleMacroRecordingCommand.Execute(null);
        viewModel.RecordMacroStep(Motion(MacroMotion.LineStart));
        Type(viewModel, "# ");
        viewModel.RecordMacroStep(Motion(MacroMotion.LineDown));
        viewModel.ToggleMacroRecordingCommand.Execute(null);

        document.CaretIndex = 0;
        var before = document.Text;
        await viewModel.RunMacroRepeatedlyCommand.ExecuteAsync(null);
        Assert.NotEqual(before, document.Text);

        document.EditorDocument.UndoStack.Undo();

        Assert.Equal(before, document.Text);
    }

    /// <summary>再生を記録へ積むと、マクロが自分自身を書き足して際限なく伸びる。</summary>
    [Fact]
    public async Task ReplayingWhileRecordingDoesNotGrowTheMacro()
    {
        var viewModel = CreateViewModel();
        var document = viewModel.Documents.Single();
        document.Text = "seed";

        viewModel.ToggleMacroRecordingCommand.Execute(null);
        Type(viewModel, "x");
        var recorded = viewModel.RecordedStepCount;

        await viewModel.RunMacroCommand.ExecuteAsync(null);

        Assert.Equal(recorded, viewModel.RecordedStepCount);
        Assert.True(viewModel.IsRecordingMacro);
    }

    [Fact]
    public async Task RepeatRunsTheMacroThatManyTimes()
    {
        var viewModel = CreateViewModel(new StubDialogService { Answer = "4" });
        var document = viewModel.Documents.Single();
        document.Text = string.Empty;

        viewModel.ToggleMacroRecordingCommand.Execute(null);
        Type(viewModel, "ab");
        viewModel.ToggleMacroRecordingCommand.Execute(null);

        document.Text = string.Empty;
        document.CaretIndex = 0;
        await viewModel.RunMacroRepeatedlyCommand.ExecuteAsync(null);

        Assert.Equal("abababab", document.Text);
    }

    [Fact]
    public async Task ARepeatCountThatIsNotANumberIsRefused()
    {
        var viewModel = CreateViewModel(new StubDialogService { Answer = "たくさん" });
        var document = viewModel.Documents.Single();
        document.Text = string.Empty;
        viewModel.ToggleMacroRecordingCommand.Execute(null);
        Type(viewModel, "x");
        viewModel.ToggleMacroRecordingCommand.Execute(null);
        document.Text = string.Empty;

        await viewModel.RunMacroRepeatedlyCommand.ExecuteAsync(null);

        Assert.Empty(document.Text);
        Assert.Contains("1 以上の数", viewModel.StatusMessage);
    }

    /// <summary>桁を打ち間違えても戻ってこられなくならないよう、回数には上限を置く。</summary>
    [Fact]
    public async Task ARepeatCountIsCapped()
    {
        var viewModel = CreateViewModel(new StubDialogService { Answer = "99999" });
        var document = viewModel.Documents.Single();
        document.Text = string.Empty;
        viewModel.ToggleMacroRecordingCommand.Execute(null);
        Type(viewModel, "x");
        viewModel.ToggleMacroRecordingCommand.Execute(null);
        document.Text = string.Empty;

        await viewModel.RunMacroRepeatedlyCommand.ExecuteAsync(null);

        Assert.Equal(MainWindowViewModel.MaximumMacroRepeat, document.Text.Length);
    }

    [Fact]
    public void RecordingStopsAtTheStepLimit()
    {
        var viewModel = CreateViewModel();
        viewModel.ToggleMacroRecordingCommand.Execute(null);

        // 文字入力は 1 手へまとまるので、まとまらない手で上限まで積む。
        for (var index = 0; index < MacroStore.MaximumSteps + 20; index++)
        {
            viewModel.RecordMacroStep(Motion(MacroMotion.CharacterRight));
        }

        Assert.Equal(MacroStore.MaximumSteps, viewModel.RecordedStepCount);
    }

    [Fact]
    public void NothingCanBeRunBeforeAnythingIsRecorded()
    {
        var viewModel = CreateViewModel();

        Assert.False(viewModel.HasRecordedMacro);
        Assert.False(viewModel.RunMacroCommand.CanExecute(null));
        Assert.False(viewModel.SaveMacroAsCommand.CanExecute(null));
    }

    [Fact]
    public void RecordingIsNotCapturedWhileItIsOff()
    {
        var viewModel = CreateViewModel();

        Type(viewModel, "見えないはず");

        Assert.Equal(0, viewModel.RecordedStepCount);
    }

    // ===== 保存 =====

    [Fact]
    public async Task SavedMacrosComeBackInANewWindow()
    {
        using var storage = new TemporaryStorage();
        var viewModel = CreateViewModel(new StubDialogService { Answer = "囲む" });
        viewModel.ToggleMacroRecordingCommand.Execute(null);
        Type(viewModel, "「");
        viewModel.ToggleMacroRecordingCommand.Execute(null);

        await viewModel.SaveMacroAsCommand.ExecuteAsync(null);

        var saved = Assert.Single(CreateViewModel().SavedMacros);
        Assert.Equal("囲む", saved.Name);
        Assert.Equal("「", Assert.Single(saved.Steps).Text);
    }

    [Fact]
    public async Task SavingTheSameNameReplacesTheOldOne()
    {
        using var storage = new TemporaryStorage();
        var dialogs = new StubDialogService { Answer = "同じ名前" };
        var viewModel = CreateViewModel(dialogs);

        viewModel.ToggleMacroRecordingCommand.Execute(null);
        Type(viewModel, "1");
        viewModel.ToggleMacroRecordingCommand.Execute(null);
        await viewModel.SaveMacroAsCommand.ExecuteAsync(null);

        viewModel.ToggleMacroRecordingCommand.Execute(null);
        Type(viewModel, "22");
        viewModel.ToggleMacroRecordingCommand.Execute(null);
        await viewModel.SaveMacroAsCommand.ExecuteAsync(null);

        var saved = Assert.Single(viewModel.SavedMacros);
        Assert.Equal("22", Assert.Single(saved.Steps).Text);
    }

    [Fact]
    public async Task DeletingASavedMacroSticks()
    {
        using var storage = new TemporaryStorage();
        var viewModel = CreateViewModel(new StubDialogService { Answer = "消す対象" });
        viewModel.ToggleMacroRecordingCommand.Execute(null);
        Type(viewModel, "x");
        viewModel.ToggleMacroRecordingCommand.Execute(null);
        await viewModel.SaveMacroAsCommand.ExecuteAsync(null);

        viewModel.DeleteSavedMacroCommand.Execute(viewModel.SavedMacros.Single());

        Assert.Empty(viewModel.SavedMacros);
        Assert.Empty(CreateViewModel().SavedMacros);
    }

    /// <summary>保存した手は写しを持つ。次の記録で中身が書き換わってはいけない。</summary>
    [Fact]
    public async Task SavedStepsAreNotChangedByLaterRecording()
    {
        using var storage = new TemporaryStorage();
        var viewModel = CreateViewModel(new StubDialogService { Answer = "写し" });
        viewModel.ToggleMacroRecordingCommand.Execute(null);
        Type(viewModel, "元");
        viewModel.ToggleMacroRecordingCommand.Execute(null);
        await viewModel.SaveMacroAsCommand.ExecuteAsync(null);

        var saved = viewModel.SavedMacros.Single();
        viewModel.ToggleMacroRecordingCommand.Execute(null);
        Type(viewModel, "後");

        Assert.Equal("元", Assert.Single(saved.Steps).Text);
    }

    [Fact]
    public void ABrokenLibraryReadsAsNoMacros()
    {
        using var storage = new TemporaryStorage();
        File.WriteAllText(Path.Combine(storage.Path, "macros.json"), "{ これは JSON ではない");

        Assert.Empty(MacroStore.Load().Macros);
    }

    // ===== 文書側のプリミティブ =====

    [Fact]
    public void MotionsMoveAndExtendTheSelection()
    {
        var document = CreateViewModel().Documents.Single();
        document.Text = "alpha beta";
        document.CaretIndex = 0;

        document.MoveCaret(MacroMotion.LineEnd, extendSelection: false);
        Assert.Equal(10, document.CaretIndex);
        Assert.False(document.HasSelection);

        document.MoveCaret(MacroMotion.WordLeft, extendSelection: true);
        Assert.Equal("beta", document.SelectedText);

        document.MoveCaret(MacroMotion.DocumentStart, extendSelection: false);
        Assert.Equal(0, document.CaretIndex);
        Assert.False(document.HasSelection);
    }

    [Fact]
    public void DeleteRemovesOneCharacterOnEachSide()
    {
        var document = CreateViewModel().Documents.Single();
        document.Text = "abcd";
        document.CaretIndex = 2;

        document.DeleteBack();
        Assert.Equal("acd", document.Text);

        document.DeleteForward();
        Assert.Equal("ad", document.Text);
    }

    [Fact]
    public void DeleteTakesTheSelectionWhenThereIsOne()
    {
        var document = CreateViewModel().Documents.Single();
        document.Text = "abcd";
        document.SelectionStart = 1;
        document.SelectionLength = 2;

        document.DeleteBack();

        Assert.Equal("ad", document.Text);
        Assert.False(document.HasSelection);
    }

    [Fact]
    public void FindNextSelectsTheMatchAndWrapsAround()
    {
        var document = CreateViewModel().Documents.Single();
        document.Text = "one two one";
        document.CaretIndex = 0;

        Assert.True(document.FindNext("one", matchCase: false, useRegex: false));
        Assert.Equal(0, document.SelectionStart);

        Assert.True(document.FindNext("one", matchCase: false, useRegex: false));
        Assert.Equal(8, document.SelectionStart);

        // 末尾まで行ったら先頭へ 1 度だけ回り込む。
        Assert.True(document.FindNext("one", matchCase: false, useRegex: false));
        Assert.Equal(0, document.SelectionStart);

        Assert.False(document.FindNext("見つからない", matchCase: false, useRegex: false));
    }

    [Fact]
    public void ABrokenRegexDoesNotChangeTheDocument()
    {
        var document = CreateViewModel().Documents.Single();
        document.Text = "abc";

        Assert.False(document.FindNext("[", matchCase: false, useRegex: true));
        Assert.Equal("abc", document.Text);
    }

    [Fact]
    public void InsertedNewLinesFollowTheDocument()
    {
        var document = CreateViewModel().Documents.Single();
        document.Text = "a\r\nb";
        document.CaretIndex = document.Text.Length;

        document.InsertText("\n");

        Assert.Equal("a\r\nb\r\n", document.Text);
    }

    private static void Type(MainWindowViewModel viewModel, string text)
        => viewModel.RecordMacroStep(new MacroStep { Kind = MacroStepKind.InsertText, Text = text });

    private static MacroStep Motion(MacroMotion motion)
        => new() { Kind = MacroStepKind.MoveCaret, Motion = motion };

    private static MainWindowViewModel CreateViewModel(StubDialogService? dialogs = null)
        => new(new StubFileService(), dialogs ?? new StubDialogService(), new AppSettings());

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
        /// <summary>名前や回数を尋ねられたときに返す答え。</summary>
        public string? Answer { get; set; }

        public Task<IReadOnlyList<string>> PickOpenPathsAsync()
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);

        public Task<UnsavedDocumentDecision> ConfirmUnsavedAsync(string documentName)
            => Task.FromResult(UnsavedDocumentDecision.Discard);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<int?> PickLineNumberAsync(int currentLine, int maximumLine) => Task.FromResult<int?>(null);

        public Task<string?> PromptTextAsync(string title, string message, string initialText)
            => Task.FromResult(Answer);

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);

        public Task<GrepQuery?> PickGrepQueryAsync(GrepQuery initial) => Task.FromResult<GrepQuery?>(null);

        public Task CheckForUpdatesAsync(bool manually) => Task.CompletedTask;
    }
}
