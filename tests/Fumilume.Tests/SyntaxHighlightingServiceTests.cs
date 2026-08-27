using Avalonia.Media;
using AvaloniaEdit.Highlighting;
using Fumilume.Services;

namespace Fumilume.Tests;

public sealed class SyntaxHighlightingServiceTests
{
    [Theory]
    [InlineData(@"C:\tmp\Program.cs", "C#")]
    [InlineData(@"C:\tmp\data.json", "Json")]
    [InlineData(@"C:\tmp\page.html", "HTML")]
    [InlineData(@"C:\tmp\style.css", "CSS")]
    [InlineData(@"C:\tmp\script.ps1", "PowerShell")]
    public void KnownExtensionsGetTheirDefinition(string path, string expected)
        => Assert.Equal(expected, SyntaxHighlightingService.Resolve(path, isDark: false)?.Name);

    /// <summary>Markdown は見出しの文字サイズまで変える定義を避ける（プレビューが別にあるため）。</summary>
    [Fact]
    public void MarkdownUsesTheDefinitionThatOnlyChangesColors()
        => Assert.Equal("MarkDown", SyntaxHighlightingService.Resolve(@"C:\tmp\note.md", isDark: false)?.Name);

    /// <summary>AvaloniaEdit に定義が無い形式は、文法の近いもので代用する。</summary>
    [Theory]
    [InlineData(@"C:\tmp\MainWindow.axaml", "XML")]
    [InlineData(@"C:\tmp\Fumilume.csproj", "XML")]
    [InlineData(@"C:\tmp\module.ts", "JavaScript")]
    public void UnsupportedFormatsFallBackToASimilarDefinition(string path, string expected)
        => Assert.Equal(expected, SyntaxHighlightingService.Resolve(path, isDark: false)?.Name);

    [Theory]
    [InlineData(@"C:\tmp\memo.txt")]
    [InlineData(@"C:\tmp\app.log")]
    [InlineData(@"C:\tmp\Makefile")]
    [InlineData(null)]
    [InlineData("")]
    public void PlainTextIsNotHighlighted(string? path)
        => Assert.Null(SyntaxHighlightingService.Resolve(path, isDark: false));

    [Fact]
    public void DarkThemeBrightensDimColorsAndLightPutsThemBack()
    {
        var comment = HighlightingManager.Instance.GetDefinition("C#")!.GetNamedColor("Comment")!;

        SyntaxHighlightingService.Resolve(@"C:\tmp\Program.cs", isDark: false);
        var light = ReadColor(comment);

        SyntaxHighlightingService.Resolve(@"C:\tmp\Program.cs", isDark: true);
        var dark = ReadColor(comment);

        SyntaxHighlightingService.Resolve(@"C:\tmp\Program.cs", isDark: false);
        var restored = ReadColor(comment);

        Assert.True(dark.ToHsl().L > light.ToHsl().L, $"dark={dark} light={light}");
        // テーマを往復しても色が積み上がらない（毎回、元の色から計算し直す）。
        Assert.Equal(light, restored);
    }

    [Fact]
    public void BrighteningKeepsTheHueAndLeavesReadableColorsAlone()
    {
        var navy = Color.FromRgb(0, 0, 128);
        var brightened = SyntaxHighlightingService.Brighten(navy);

        Assert.True(brightened.ToHsl().L > navy.ToHsl().L);
        Assert.Equal(navy.ToHsl().H, brightened.ToHsl().H, 1);

        var pale = Color.FromRgb(240, 240, 240);
        Assert.Equal(pale, SyntaxHighlightingService.Brighten(pale));
    }

    private static Color ReadColor(HighlightingColor color) => color.Foreground!.GetColor(null!)!.Value;
}
