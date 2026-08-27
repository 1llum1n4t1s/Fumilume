using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Fumilume.Views;

/// <summary>
/// 設定画面の検索。カード（<c>HeaderedContentControl.settingscard</c>）を単位に、
/// 見出し・項目名・補足文をまとめた文字列で照合する。
///
/// 走査に視覚ツリーを使わないのは、<see cref="TabControl"/> が選んでいないタブの中身を
/// 組み立てないため。XAML の読み込み時点で <c>Content</c> と <c>Children</c> は埋まっているので、
/// そこだけを辿れば選んでいないタブの中身も検索できる。
/// </summary>
internal static class SettingsSearch
{
    /// <summary>カードに付けておく目印。これが付いた入れ物だけが検索と絞り込みの単位になる。</summary>
    internal const string CardClass = "settingscard";

    /// <summary>この枝に含まれるカードを、XAML に並んでいる順で拾う。</summary>
    public static List<Control> FindCards(object? root)
    {
        var cards = new List<Control>();
        CollectCards(root, cards);
        return cards;
    }

    /// <summary>この枝に出てくる文字をすべて連ねる。照合はこの 1 本の文字列に対して行う。</summary>
    public static string CollectText(object? node)
    {
        var builder = new StringBuilder();
        AppendText(node, builder);
        return builder.ToString();
    }

    private static void CollectCards(object? node, List<Control> cards)
    {
        if (node is HeaderedContentControl headered && headered.Classes.Contains(CardClass))
        {
            cards.Add(headered);
            return;
        }

        foreach (var child in Children(node))
        {
            CollectCards(child, cards);
        }
    }

    private static void AppendText(object? node, StringBuilder builder)
    {
        switch (node)
        {
            case null:
                return;
            case string text:
                builder.Append(text).Append('\n');
                return;
            case TextBlock textBlock:
                builder.Append(textBlock.Text).Append('\n');
                return;
            case TextBox textBox:
                builder.Append(textBox.PlaceholderText).Append('\n');
                return;
        }

        foreach (var child in Children(node))
        {
            AppendText(child, builder);
        }
    }

    /// <summary>XAML が組み立てた時点で埋まっている子。テンプレートの適用には依存しない。</summary>
    private static IEnumerable<object?> Children(object? node)
    {
        switch (node)
        {
            case HeaderedContentControl headered:
                yield return headered.Header;
                yield return headered.Content;
                break;
            case ContentControl content:
                yield return content.Content;
                break;
            case ItemsControl items:
                foreach (var item in items.Items)
                {
                    yield return item;
                }

                break;
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    yield return child;
                }

                break;
            case Decorator decorator:
                yield return decorator.Child;
                break;
        }
    }
}
