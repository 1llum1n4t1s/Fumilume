using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fumilume.Services;

namespace Fumilume.Views;

/// <summary>安全なローカル描画だけを行う Markdown プレビュー。</summary>
public sealed class MarkdownPreview : ScrollViewer
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownPreview, string?>(nameof(Markdown));

    private readonly StackPanel _content = new()
    {
        Margin = new Thickness(36, 28),
        Spacing = 10,
        MaxWidth = 920,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private readonly DispatcherTimer _renderTimer;

    public MarkdownPreview()
    {
        HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        Content = _content;
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _renderTimer.Tick += (_, _) =>
        {
            _renderTimer.Stop();
            Render();
        };
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
        {
            _renderTimer.Stop();
            _renderTimer.Start();
        }
    }

    private void Render()
    {
        _content.Children.Clear();
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
            MarkdownBlockKind.Numbered => CreateText($"1.  {block.Text}", 14, margin: new Thickness(18, 0, 0, 0)),
            MarkdownBlockKind.Quote => new Border
            {
                BorderBrush = FindBrush("AccentBrush"),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(14, 4),
                Child = CreateText(block.Text, 14),
            },
            MarkdownBlockKind.Code => new Border
            {
                Background = FindBrush("SettingsPageBg"),
                BorderBrush = FindBrush("SettingsCardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12),
                Child = new SelectableTextBlock
                {
                    Text = block.Text,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.NoWrap,
                },
            },
            MarkdownBlockKind.Rule => new Border
            {
                Height = 1,
                Margin = new Thickness(0, 12),
                Background = FindBrush("Divider"),
            },
            _ => CreateText(block.Text, 14),
        };

    private SelectableTextBlock CreateText(
        string text,
        double fontSize,
        FontWeight? weight = null,
        Thickness? margin = null)
        => new()
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = weight ?? FontWeight.Normal,
            Margin = margin ?? default,
            Foreground = FindBrush("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = fontSize * 1.65,
        };

    private IBrush? FindBrush(string key)
        => this.TryFindResource(key, out var value) ? value as IBrush : null;
}
