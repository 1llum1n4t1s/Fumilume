using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fumilume.Services;

namespace Fumilume.ViewModels;

/// <summary>
/// キーボードマクロ。秀丸エディタの「キーボードマクロの記録開始／終了・実行」に当たる。
///
/// 記録するのはキー入力そのものではなく、それが意味する操作（<see cref="MacroStep"/>）。
/// 再生も入力の再送ではなく、同じ操作を <see cref="DocumentViewModel"/> へ当て直す。
/// 入力を溜めて流し直す作りにしないのは、IME・フォーカス・タイミングに依存して不安定になり、
/// ヘッドレスのテストで挙動を固定できず、環境をまたいで保存もできないため。
///
/// 代わりにマウスでのカーソル移動は記録できない。キーボードマクロという名前のとおり、
/// 記録の対象はキーで起こした操作に限る。
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>1 回の実行で回せる上限。桁を打ち間違えても戻ってこられなくならないようにする。</summary>
    internal const int MaximumMacroRepeat = 1000;

    /// <summary>記録中のマクロ。名前を付けて保存するまではこの 1 本だけを持つ。</summary>
    private readonly List<MacroStep> _currentMacro = [];

    /// <summary>再生中は記録しない。マクロが自分自身を書き足して際限なく伸びるのを防ぐ。</summary>
    private bool _replayingMacro;

    private ObservableCollection<KeyboardMacro>? _savedMacros;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MacroRecordingText))]
    [NotifyPropertyChangedFor(nameof(MacroRecordingTitle))]
    private bool _isRecordingMacro;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MacroRecordingText))]
    [NotifyPropertyChangedFor(nameof(HasRecordedMacro))]
    [NotifyCanExecuteChangedFor(nameof(RunMacroCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunMacroRepeatedlyCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveMacroAsCommand))]
    private int _recordedStepCount;

    /// <summary>保存したマクロ。最初に必要になったときだけ読み込む。</summary>
    public ObservableCollection<KeyboardMacro> SavedMacros
        => _savedMacros ??= [.. MacroStore.Load().Macros];

    public bool HasRecordedMacro => RecordedStepCount > 0;

    /// <summary>記録中にステータスバーへ出す文言。</summary>
    public string MacroRecordingText => $"マクロを記録中（{RecordedStepCount:N0} 手）";

    public string MacroRecordingTitle => IsRecordingMacro ? "マクロの記録を終了" : "マクロの記録を開始";

    /// <summary>記録の対象になる状態か。View はこれを見てから <see cref="RecordMacroStep"/> を呼ぶ。</summary>
    internal bool IsCapturingMacro => IsRecordingMacro && !_replayingMacro;

    /// <summary>いま記録している手。キー入力の翻訳が正しいかを外から確かめるために公開する。</summary>
    internal IReadOnlyList<MacroStep> RecordedSteps => _currentMacro;

    /// <summary>
    /// 1 手を記録する。同じ向きの文字入力が続くときは 1 手へまとめる
    /// （1 文字 1 手のままだと、少し打っただけで手数が読めなくなる）。
    /// </summary>
    internal void RecordMacroStep(MacroStep step)
    {
        if (!IsCapturingMacro || _currentMacro.Count >= MacroStore.MaximumSteps)
        {
            return;
        }

        if (step.Kind is MacroStepKind.InsertText
            && _currentMacro is [.., { Kind: MacroStepKind.InsertText } last])
        {
            last.Text += step.Text;
        }
        else
        {
            _currentMacro.Add(step);
        }

        RecordedStepCount = _currentMacro.Count;
    }

    [RelayCommand]
    private void ToggleMacroRecording()
    {
        if (IsRecordingMacro)
        {
            IsRecordingMacro = false;
            StatusMessage = RecordedStepCount > 0
                ? $"マクロを記録しました（{RecordedStepCount:N0} 手）"
                : "記録した操作がありません";
            return;
        }

        _currentMacro.Clear();
        RecordedStepCount = 0;
        IsRecordingMacro = true;
        StatusMessage = "マクロの記録を始めました。終えるにはもう一度 Shift+F1 を押します";
    }

    [RelayCommand(CanExecute = nameof(HasRecordedMacro))]
    private Task RunMacroAsync() => PlayAsync(_currentMacro, repeat: 1);

    [RelayCommand(CanExecute = nameof(HasRecordedMacro))]
    private async Task RunMacroRepeatedlyAsync()
    {
        var answer = await _dialogs.PromptTextAsync(
            "マクロの実行",
            $"実行する回数（1〜{MaximumMacroRepeat:N0}）",
            "1");
        if (answer is null)
        {
            return;
        }

        if (!int.TryParse(answer.Trim(), out var repeat) || repeat < 1)
        {
            StatusMessage = "回数には 1 以上の数を入れてください";
            return;
        }

        await PlayAsync(_currentMacro, Math.Min(repeat, MaximumMacroRepeat));
    }

    [RelayCommand(CanExecute = nameof(HasRecordedMacro))]
    private async Task SaveMacroAsAsync()
    {
        if (SavedMacros.Count >= MacroStore.MaximumMacros)
        {
            await _dialogs.ShowErrorAsync(
                "マクロを保存できません",
                $"保存できるマクロは {MacroStore.MaximumMacros} 本までです。不要なものを消してください。");
            return;
        }

        var name = await _dialogs.PromptTextAsync("マクロの保存", "名前", $"マクロ {SavedMacros.Count + 1}");
        if (name is null)
        {
            return;
        }

        name = name.Trim();
        if (name.Length == 0)
        {
            StatusMessage = "名前を入れてください";
            return;
        }

        // 同じ名前は置き換える。増やし続けると、どれが最新か一覧から判断できなくなる。
        var saved = new KeyboardMacro { Name = name, Steps = [.. _currentMacro.Select(Copy)] };
        if (SavedMacros.FirstOrDefault(macro => macro.Name == name) is { } existing)
        {
            SavedMacros[SavedMacros.IndexOf(existing)] = saved;
        }
        else
        {
            SavedMacros.Add(saved);
        }

        StatusMessage = PersistMacros()
            ? $"「{name}」を保存しました"
            : "マクロを保存できませんでした";
    }

    [RelayCommand]
    private Task RunSavedMacroAsync(KeyboardMacro? macro)
        => macro is null ? Task.CompletedTask : PlayAsync(macro.Steps, repeat: 1);

    [RelayCommand]
    private void DeleteSavedMacro(KeyboardMacro? macro)
    {
        if (macro is null || !SavedMacros.Remove(macro))
        {
            return;
        }

        StatusMessage = PersistMacros()
            ? $"「{macro.Name}」を消しました"
            : "マクロを保存できませんでした";
    }

    private bool PersistMacros() => MacroStore.Save(new MacroLibrary { Macros = [.. SavedMacros] });

    /// <summary>
    /// 一連の手を当て直す。全体を 1 つの Undo の塊にするのは、100 回流したマクロを
    /// Ctrl+Z 100 回で戻すことになると、取り消せないのと変わらないため。
    /// </summary>
    private async Task PlayAsync(IReadOnlyList<MacroStep> steps, int repeat)
    {
        if (SelectedDocument is not { } document || steps.Count == 0)
        {
            return;
        }

        // 記録中に実行すると、実行そのものが記録へ積まれて意味が変わる。記録は止めずに手だけ止める。
        _replayingMacro = true;
        var undoStack = document.EditorDocument.UndoStack;
        undoStack.StartUndoGroup();
        try
        {
            for (var round = 0; round < repeat; round++)
            {
                foreach (var step in steps)
                {
                    await ApplyAsync(document, step);
                }
            }
        }
        finally
        {
            undoStack.EndUndoGroup();
            _replayingMacro = false;
        }

        StatusMessage = repeat == 1
            ? $"マクロを実行しました（{steps.Count:N0} 手）"
            : $"マクロを {repeat:N0} 回実行しました（{steps.Count:N0} 手）";
    }

    private Task ApplyAsync(DocumentViewModel document, MacroStep step)
    {
        switch (step.Kind)
        {
            case MacroStepKind.Command:
                return RunEditorCommandAsync(step.Command);
            case MacroStepKind.InsertText:
                document.InsertText(step.Text);
                break;
            case MacroStepKind.MoveCaret:
                document.MoveCaret(step.Motion, step.ExtendSelection);
                break;
            case MacroStepKind.DeleteBack:
                document.DeleteBack();
                break;
            case MacroStepKind.DeleteForward:
                document.DeleteForward();
                break;
            case MacroStepKind.FindNext:
                document.FindNext(step.Text, step.MatchCase, step.UseRegex);
                break;
        }

        return Task.CompletedTask;
    }

    private static MacroStep Copy(MacroStep step) => new()
    {
        Kind = step.Kind,
        Command = step.Command,
        Text = step.Text,
        Motion = step.Motion,
        ExtendSelection = step.ExtendSelection,
        MatchCase = step.MatchCase,
        UseRegex = step.UseRegex,
    };
}
