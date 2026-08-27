using Fumilume.Services;

namespace Fumilume.Tests;

public sealed class EditorFontFamilyTests
{
    [Theory]
    [InlineData("'Cascadia Code', Consolas, monospace", "Cascadia Code, Consolas, monospace")]
    [InlineData("\"Fira Code\", 'MS Gothic'", "Fira Code, MS Gothic, Cascadia Mono, Consolas, monospace")]
    [InlineData("Consolas", "Consolas, Cascadia Mono, monospace")]
    [InlineData("  ", "Cascadia Mono, Consolas, monospace")]
    public void VsCodeFontListsAreConvertedToAvaloniaFallbacks(string input, string expected)
        => Assert.Equal(expected, EditorFontFamily.ToAvalonia(input));

    [Fact]
    public void DuplicateFamiliesAreRemovedIgnoringCase()
        => Assert.Equal(
            "Consolas, Cascadia Mono, monospace",
            EditorFontFamily.ToAvalonia("Consolas, consolas"));
}
