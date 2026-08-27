using System.Windows.Input;
using Fumilume.Services;

namespace Fumilume.ViewModels;

/// <summary>
/// メニュー 1 項目。区分（子を持つ）と機能（子を持たずコマンドを持つ）の両方をこの 1 型で表す。
///
/// 階層メニューを <c>ControlTheme</c> 1 つで組むには、どの段でも同じプロパティ名から
/// Header と ItemsSource を引けるほうが都合が良い。段ごとに型を分けると、
/// メニュー側で型を見分ける仕掛けが要る。
///
/// コマンドは項目自身が持つ。メニューのポップアップからウィンドウの DataContext を
/// <c>$parent[Window]</c> で辿らせると、ポップアップが視覚ツリーの外に出た瞬間に切れる。
/// </summary>
public sealed class EditorMenuNode
{
    private EditorMenuNode(
        string title,
        IReadOnlyList<EditorMenuNode> children,
        ICommand? command,
        object? commandParameter,
        string? gesture)
    {
        Title = title;
        Children = children;
        Command = command;
        CommandParameter = commandParameter;
        Gesture = gesture;
    }

    public string Title { get; }

    public IReadOnlyList<EditorMenuNode> Children { get; }

    public ICommand? Command { get; }

    public object? CommandParameter { get; }

    /// <summary>メニュー右端へ添えるキー表示。区分の段では <see langword="null"/>。</summary>
    public string? Gesture { get; }

    /// <summary>カタログ全体をメニュー 2 段（区分 → 機能）へ組み替える。</summary>
    public static IReadOnlyList<EditorMenuNode> BuildMenu(ICommand runCommand)
        =>
        [
            .. EditorCommandCatalog.Groups.Select(group => new EditorMenuNode(
                group.Category,
                [.. group.Commands.Select(command => Leaf(command, runCommand))],
                command: null,
                commandParameter: null,
                gesture: null)),
        ];

    private static EditorMenuNode Leaf(EditorCommandDefinition definition, ICommand runCommand)
        => new(definition.Title, [], runCommand, definition.Id, definition.Gesture);
}
