using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Fumilume.Services;

/// <summary>アプリへ同梱したフォントを <c>fonts:Fumilume</c> で参照できるようにする。</summary>
public sealed class FumilumeFontCollection : EmbeddedFontCollection
{
    public FumilumeFontCollection()
        : base(
            new Uri("fonts:Fumilume", UriKind.Absolute),
            new Uri("avares://Fumilume/Assets/Fonts", UriKind.Absolute))
    {
    }
}

/// <summary>設定へ保存する表示名と、Avalonia が解決する埋め込み URI の対応。</summary>
public static class AppFontFamilies
{
    public const string IbmPlexSansJpName = "IBM Plex Sans JP";
    public const string UdevGothicJpDocName = "UDEV Gothic JPDOC";

    public const string IbmPlexSansJpUri = "fonts:Fumilume#IBM Plex Sans JP";
    public const string UdevGothicJpDocUri = "fonts:Fumilume#UDEV Gothic JPDOC";

    public static FontFamily BundledUiFont { get; } = new(IbmPlexSansJpUri);

    public static FontFamily BundledEditorFont { get; } = new(UdevGothicJpDocUri);

    public static FontFamily ResolveUiFont(string? family)
        => new(string.Equals(family, IbmPlexSansJpName, StringComparison.OrdinalIgnoreCase)
            ? IbmPlexSansJpUri
            : family?.Trim() is { Length: > 0 } value
                ? value
                : IbmPlexSansJpUri);

    public static string ResolveEditorFont(string family)
        => string.Equals(family, UdevGothicJpDocName, StringComparison.OrdinalIgnoreCase)
            ? UdevGothicJpDocUri
            : family;
}
