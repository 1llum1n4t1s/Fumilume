using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Fumilume.ViewModels;

/// <summary>
/// 左の縦タブに並ぶ項目の共通契約。
///
/// タブの実体を文書（<see cref="DocumentViewModel"/>）に固定せず、設定タブ
/// （<see cref="SettingsTabViewModel"/>）のような「ファイルを持たないタブ」も同じ一覧へ並べるための基底。
/// 文書型をそのまま流用して設定タブを表すと、未保存判定・保存・エンコーディング表示といった
/// 文書固有の処理に設定タブが混ざり込む。
/// </summary>
public abstract partial class WorkspaceTabViewModel : ObservableObject
{
    private readonly Func<WorkspaceTabViewModel, Task> _closeAsync;

    protected WorkspaceTabViewModel(Func<WorkspaceTabViewModel, Task> closeAsync)
        => _closeAsync = closeAsync;

    /// <summary>タブ一覧に出す表示名。</summary>
    public abstract string TabTitle { get; }

    /// <summary>タブ一覧のアイコン（Segoe Fluent Icons のグリフ）。</summary>
    public abstract string TabGlyph { get; }

    /// <summary>タブ一覧のツールチップ。</summary>
    public abstract string TabTooltip { get; }

    /// <summary>設定タブかどうか。コンテンツ領域の出し分けに使う。</summary>
    public virtual bool IsSettingsTab => false;

    /// <summary>編集可能なテキスト文書タブかどうか。</summary>
    public virtual bool IsDocumentTab => false;

    /// <summary>PDF 表示タブかどうか。</summary>
    public virtual bool IsPdfTab => false;

    /// <summary>フォルダ横断検索の結果タブかどうか。</summary>
    public virtual bool IsGrepTab => false;

    /// <summary>設定以外のタブを一覧の先頭へ固定できるか。</summary>
    public bool CanPin => !IsSettingsTab;

    /// <summary>タブ一覧の先頭グループへ固定されているか。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinGlyph))]
    [NotifyPropertyChangedFor(nameof(PinTooltip))]
    private bool _isPinned;

    /// <summary>現在の状態が一目で分かるピン操作のグリフ。</summary>
    public string PinGlyph => IsPinned ? "\uE77A" : "\uE718";

    /// <summary>現在の状態に対応するピン操作の説明。</summary>
    public string PinTooltip => IsPinned ? "ピン留めを解除" : "タブをピン留め";

    /// <summary>ピン状態が変わったことを、一覧を所有する ViewModel へ知らせる。</summary>
    public event EventHandler? PinStateChanged;

    partial void OnIsPinnedChanged(bool value) => PinStateChanged?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanPin))]
    private void TogglePin() => IsPinned = !IsPinned;

    [RelayCommand]
    private Task CloseTabAsync() => _closeAsync(this);
}
