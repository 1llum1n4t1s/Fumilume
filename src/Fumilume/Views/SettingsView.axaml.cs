using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Fumilume.Views;

/// <summary>設定タブの中身。DataContext は <see cref="ViewModels.SettingsTabViewModel"/>。</summary>
public sealed partial class SettingsView : UserControl
{
    /// <summary>タブごとのカード一覧と、その検索対象文字列。読み込み時に 1 度だけ作る。</summary>
    private readonly List<TabIndexEntry> _searchIndex = [];

    public SettingsView()
    {
        var systemFonts = FontManager.Current.SystemFonts.ToArray();
        FontFamilies =
        [
            .. systemFonts
                .Append(new FontFamily("Inter"))
                .GroupBy(font => font.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(font => font.Name, StringComparer.CurrentCultureIgnoreCase),
        ];
        // 手入力した名前を TryGetGlyphTypeface へ渡すと代替フォントが返り、固定ピッチと
        // 誤判定する場合がある。OS が実在を保証した SystemFonts だけを判定対象にする。
        EditorFontFamilies = [.. systemFonts.Where(IsMonospaced)];
        InitializeComponent();
        BuildSearchIndex();
    }

    /// <summary>OS に入っているフォントと、同梱している Inter。候補名は各フォント自身で描く。</summary>
    public IReadOnlyList<FontFamily> FontFamilies { get; }

    /// <summary>エディタ向け候補。OpenType の固定ピッチ情報がある等幅フォントだけを並べる。</summary>
    public IReadOnlyList<FontFamily> EditorFontFamilies { get; }

    internal static bool IsMonospaced(FontFamily fontFamily)
        => FontManager.Current.TryGetGlyphTypeface(new Typeface(fontFamily), out var glyphTypeface)
           && glyphTypeface.Metrics.IsFixedPitch;

    /// <summary>検索語に当たるカードだけを残す。空にすると元へ戻る。戻り値は残ったカードの数。</summary>
    internal int ApplySearch(string? query)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        var tabs = this.FindControl<TabControl>("SettingsTabs");
        if (tabs is null)
        {
            return 0;
        }

        var matches = 0;
        TabItem? firstVisible = null;
        foreach (var entry in _searchIndex)
        {
            var visibleCards = 0;
            foreach (var card in entry.Cards)
            {
                var visible = trimmed.Length == 0
                    || card.Text.Contains(trimmed, StringComparison.CurrentCultureIgnoreCase);
                card.Control.IsVisible = visible;
                if (visible)
                {
                    visibleCards++;
                }
            }

            matches += visibleCards;
            entry.Tab.IsVisible = trimmed.Length == 0 || visibleCards > 0;
            firstVisible ??= entry.Tab.IsVisible ? entry.Tab : null;
        }

        // 選んでいたタブが消えたままだと、絞り込みの結果が本文側に出ない。
        if (tabs.SelectedItem is TabItem { IsVisible: false } && firstVisible is not null)
        {
            tabs.SelectedItem = firstVisible;
        }

        UpdateSearchStatus(trimmed, matches);
        return matches;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void BuildSearchIndex()
    {
        var tabs = this.FindControl<TabControl>("SettingsTabs");
        if (tabs is null)
        {
            return;
        }

        foreach (var tab in tabs.Items.OfType<TabItem>())
        {
            _searchIndex.Add(new TabIndexEntry(
                tab,
                [.. SettingsSearch.FindCards(tab.Content)
                    .Select(card => new CardIndexEntry(card, SettingsSearch.CollectText(card)))]));
        }
    }

    private void UpdateSearchStatus(string query, int matches)
    {
        if (this.FindControl<TextBlock>("SettingsSearchSummary") is { } summary)
        {
            summary.IsVisible = query.Length > 0;
            summary.Text = $"{matches:N0} 件";
        }

        if (this.FindControl<TextBlock>("SettingsNoMatch") is { } empty)
        {
            empty.IsVisible = query.Length > 0 && matches == 0;
        }

        if (this.FindControl<TabControl>("SettingsTabs") is { } tabs)
        {
            tabs.IsVisible = query.Length == 0 || matches > 0;
        }
    }

    private void SettingsSearch_TextChanged(object? sender, TextChangedEventArgs args)
        => ApplySearch((sender as TextBox)?.Text);

    /// <summary>Esc で検索を解いて全体へ戻す（探し直しのたびに選択し直さなくて済む）。</summary>
    private void SettingsSearch_KeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key is Key.Escape && sender is TextBox box && box.Text is { Length: > 0 })
        {
            box.Text = string.Empty;
            args.Handled = true;
        }
    }

    private sealed record TabIndexEntry(TabItem Tab, IReadOnlyList<CardIndexEntry> Cards);

    private sealed record CardIndexEntry(Control Control, string Text);
}
