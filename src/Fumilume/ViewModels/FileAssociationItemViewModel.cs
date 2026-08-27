using CommunityToolkit.Mvvm.ComponentModel;

namespace Fumilume.ViewModels;

/// <summary>
/// 関連付け一覧の 1 行（拡張子・表示名・関連付けの有無）。
///
/// トグルの操作を <see cref="SettingsTabViewModel"/> が購読して、その場でレジストリへ書き戻す。
/// </summary>
public sealed partial class FileAssociationItemViewModel : ObservableObject
{
    public FileAssociationItemViewModel(string extension, string description)
    {
        Extension = extension;
        Description = description;
    }

    /// <summary>先頭のドットを含む拡張子（例: <c>.txt</c>）。</summary>
    public string Extension { get; }

    /// <summary>画面に出す説明（例: テキスト文書 (.txt)）。</summary>
    public string Description { get; }

    /// <summary>この拡張子を Fumilume へ関連付けるかどうか。</summary>
    [ObservableProperty]
    private bool _isAssociated;
}
