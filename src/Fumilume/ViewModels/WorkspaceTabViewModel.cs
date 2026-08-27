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

    [RelayCommand]
    private Task CloseTabAsync() => _closeAsync(this);
}
