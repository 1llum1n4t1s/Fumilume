using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fumilume.Services;

namespace Fumilume.Views;

/// <summary>安全なローカル描画だけを行う Markdown プレビュー。</summary>
public sealed class MarkdownPreview : UserControl
{
    private const int MaximumWrappedFragmentLength = 4096;
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownPreview, string?>(nameof(Markdown));

    private readonly StackPanel _content = new()
    {
        Margin = new Thickness(36, 28),
        Spacing = 10,
        MaxWidth = 920,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private bool _renderPending;

    public MarkdownPreview()
    {
        // ScrollViewer を継承したカスタム型には Fluent テーマのテンプレートが当たらず、
        // Extent / Viewport が作られない。組み込み型を内包してテーマとスクロール入力を有効にする。
        var scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new Border
            {
                Background = Brushes.Transparent,
                Child = _content,
            },
        };
        Content = scrollViewer;
        ScheduleRender();
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty || change.Property == IsVisibleProperty)
        {
            ScheduleRender();
        }
    }

    internal int RenderedBlockCount => _content.Children.Count;

    /// <summary>
    /// Binding 更新と同じ UI ターンで直接 Children を作り直すとレイアウト中の再入になることがある。
    /// Background 優先度へ 1 回だけ積み、連続入力時は常に最新の Markdown を描く。
    /// </summary>
    private void ScheduleRender()
    {
        if (_renderPending)
        {
            return;
        }

        _renderPending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _renderPending = false;
                Render();
            },
            DispatcherPriority.Background);
    }

    private void Render()
    {
        _content.Children.Clear();
        if (!IsVisible)
        {
            return;
        }

        foreach (var block in MarkdownDocumentParser.Parse(Markdown))
        {
            _content.Children.Add(CreateBlock(block));
        }
    }

    private Control CreateBlock(MarkdownBlock block)
        => block.Kind switch
        {
            MarkdownBlockKind.Heading => CreateText(
                block.Text,
                block.Level switch { 1 => 30, 2 => 24, 3 => 20, 4 => 17, _ => 15 },
                FontWeight.SemiBold,
                new Thickness(0, block.Level <= 2 ? 16 : 8, 0, 2)),
            MarkdownBlockKind.Bullet => CreateText($"•  {block.Text}", 14, margin: new Thickness(18, 0, 0, 0)),
            MarkdownBlockKind.Numbered => CreateText(
                $"{Math.Max(1, block.Number)}.  {block.Text}",
                14,
                margin: new Thickness(18, 0, 0, 0)),
            MarkdownBlockKind.Quote => new Border
            {
                Classes = { "markdownquote" },
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(14, 4),
                Child = CreateText(block.Text, 14),
            },
            MarkdownBlockKind.Code => new Border
            {
                Classes = { "markdowncode" },
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12),
                Child = new SelectableTextBlock
                {
                    Text = block.Text,
                    FontFamily = new FontFamily(EditorFontFamily.ToAvalonia(AppSettingsDefaults.FontFamily)),
                    FontSize = 13,
                    TextWrapping = TextWrapping.NoWrap,
                },
            },
            MarkdownBlockKind.Rule => new Border
            {
                Classes = { "markdownrule" },
                Height = 1,
                Margin = new Thickness(0, 12),
            },
            _ => CreateText(block.Text, 14),
        };

    private SelectableTextBlock CreateText(
        string text,
        double fontSize,
        FontWeight? weight = null,
        Thickness? margin = null)
    {
        var paragraph = CreateTextFragment(text, fontSize, weight, margin);
        if (text.Length <= MaximumWrappedFragmentLength)
            return paragraph;

        // 長い段落を1つの TextLayout に渡すと、折返しの計算が UI を長時間占有する。
        // 同じ段落内の Run に分け、連続した折返しと選択・コピーを維持する。
        paragraph.Text = null;
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        for (var start = 0; start < text.Length;)
        {
            var end = Math.Min(start + MaximumWrappedFragmentLength, text.Length);
            if (end < text.Length)
            {
                var boundaryIndex = Array.BinarySearch(boundaries, end);
                if (boundaryIndex < 0) boundaryIndex = ~boundaryIndex - 1;
                var preferredIndex = boundaryIndex;
                while (boundaryIndex >= 0 && boundaries[boundaryIndex] > start
                    && !IsShapingBoundary(text, boundaries[boundaryIndex]))
                    boundaryIndex--;
                if (boundaryIndex >= 0 && boundaries[boundaryIndex] > start)
                    end = boundaries[boundaryIndex];
                else
                {
                    // 空白のない単語や接続形を持つ文字列は、上限より長くても同じ Run に保つ。
                    boundaryIndex = preferredIndex + 1;
                    while (boundaryIndex < boundaries.Length
                        && !IsShapingBoundary(text, boundaries[boundaryIndex]))
                        boundaryIndex++;
                    end = boundaryIndex < boundaries.Length ? boundaries[boundaryIndex] : text.Length;
                }
            }
            paragraph.Inlines!.Add(new Run(text[start..end]));
            start = end;
        }
        return paragraph;
    }

    private static bool IsShapingBoundary(string text, int index)
    {
        var previous = text[index - 1];
        var next = text[index];
        return char.IsWhiteSpace(previous) || char.IsWhiteSpace(next)
            || IsBoundaryPunctuation(previous) || IsBoundaryPunctuation(next);
    }

    private static bool IsBoundaryPunctuation(char character)
        => char.GetUnicodeCategory(character) is UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation or UnicodeCategory.OtherPunctuation;

    private static SelectableTextBlock CreateTextFragment(
        string text, double fontSize, FontWeight? weight, Thickness? margin = null)
        => new()
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = weight ?? FontWeight.Normal,
            Margin = margin ?? default,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = fontSize * 1.65,
        };
}
