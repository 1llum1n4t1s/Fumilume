using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Fumilume.Views;

/// <summary>設定タブの中身。DataContext は <see cref="ViewModels.SettingsTabViewModel"/>。</summary>
public sealed partial class SettingsView : UserControl
{
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
    }

    /// <summary>OS に入っているフォントと、同梱している Inter。候補名は各フォント自身で描く。</summary>
    public IReadOnlyList<FontFamily> FontFamilies { get; }

    /// <summary>エディタ向け候補。OpenType の固定ピッチ情報がある等幅フォントだけを並べる。</summary>
    public IReadOnlyList<FontFamily> EditorFontFamilies { get; }

    internal static bool IsMonospaced(FontFamily fontFamily)
        => FontManager.Current.TryGetGlyphTypeface(new Typeface(fontFamily), out var glyphTypeface)
           && glyphTypeface.Metrics.IsFixedPitch;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
