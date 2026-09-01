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
    public void CSharpUsesOneDarkAndOneLightSemanticColorsWithoutDrift()
    {
        var definition = HighlightingManager.Instance.GetDefinition("C#")!;
        var comment = definition.GetNamedColor("Comment")!;
        var text = definition.GetNamedColor("String")!;
        var number = definition.GetNamedColor("NumberLiteral")!;
        var keyword = definition.GetNamedColor("Keywords")!;
        var method = definition.GetNamedColor("MethodCall")!;
        var type = definition.GetNamedColor("TypeKeywords")!;
        var valueType = definition.GetNamedColor("ValueTypeKeywords")!;

        SyntaxHighlightingService.Resolve(@"C:\tmp\Program.cs", isDark: true);
        Assert.Equal(Color.Parse("#5C6370"), ReadColor(comment));
        Assert.Equal(Color.Parse("#98C379"), ReadColor(text));
        Assert.Equal(Color.Parse("#D19A66"), ReadColor(number));
        Assert.Equal(Color.Parse("#C678DD"), ReadColor(keyword));
        Assert.Equal(Color.Parse("#61AFEF"), ReadColor(method));
        Assert.Equal(Color.Parse("#E5C07B"), ReadColor(type));
        Assert.Equal(Color.Parse("#E5C07B"), ReadColor(valueType));

        SyntaxHighlightingService.Resolve(@"C:\tmp\Program.cs", isDark: false);
        Assert.Equal(Color.Parse("#A0A1A7"), ReadColor(comment));
        Assert.Equal(Color.Parse("#50A14F"), ReadColor(text));
        Assert.Equal(Color.Parse("#986801"), ReadColor(number));
        Assert.Equal(Color.Parse("#A626A4"), ReadColor(keyword));
        Assert.Equal(Color.Parse("#4078F2"), ReadColor(method));
        Assert.Equal(Color.Parse("#C18401"), ReadColor(type));
        Assert.Equal(Color.Parse("#C18401"), ReadColor(valueType));

        SyntaxHighlightingService.Resolve(@"C:\tmp\Program.cs", isDark: true);
        Assert.Equal(Color.Parse("#5C6370"), ReadColor(comment));
        Assert.Equal(Color.Parse("#98C379"), ReadColor(text));
    }

    [Fact]
    public void JsonXmlAndMarkdownShareTheOneDarkPalette()
    {
        var json = SyntaxHighlightingService.Resolve(@"C:\tmp\data.json", isDark: true)!;
        Assert.Equal(Color.Parse("#98C379"), ReadColor(json.GetNamedColor("String")!));
        Assert.Equal(Color.Parse("#D19A66"), ReadColor(json.GetNamedColor("Number")!));
        Assert.Equal(Color.Parse("#D19A66"), ReadColor(json.GetNamedColor("FieldName")!));

        var xml = SyntaxHighlightingService.Resolve(@"C:\tmp\view.axaml", isDark: true)!;
        Assert.Equal(Color.Parse("#E06C75"), ReadColor(xml.GetNamedColor("XmlTag")!));
        Assert.Equal(Color.Parse("#D19A66"), ReadColor(xml.GetNamedColor("AttributeName")!));
        Assert.Equal(Color.Parse("#98C379"), ReadColor(xml.GetNamedColor("AttributeValue")!));

        var markdown = SyntaxHighlightingService.Resolve(@"C:\tmp\note.md", isDark: true)!;
        Assert.Equal(Color.Parse("#E06C75"), ReadColor(markdown.GetNamedColor("Heading")!));
        Assert.Equal(Color.Parse("#98C379"), ReadColor(markdown.GetNamedColor("Code")!));
        Assert.Equal(Color.Parse("#61AFEF"), ReadColor(markdown.GetNamedColor("Link")!));
    }

    [Fact]
    public void MarkdownIndentedCodeAlsoUsesTheOneDarkPalette()
    {
        var markdown = SyntaxHighlightingService.Resolve(@"C:\tmp\SKILL.md", isDark: true)!;
        var importedCSharp = markdown.MainRuleSet.Spans
            .Select(span => span.RuleSet)
            .First(ruleSet => ruleSet is not null)!;

        var keyword = importedCSharp.Rules
            .Select(rule => rule.Color)
            .First(color => color?.Name == "Keywords")!;
        var text = importedCSharp.Spans
            .Select(span => span.SpanColor)
            .First(color => color?.Name == "String")!;
        var comment = importedCSharp.Spans
            .Select(span => span.SpanColor)
            .First(color => color?.Name == "Comment")!;

        Assert.Equal(Color.Parse("#C678DD"), ReadColor(keyword));
        Assert.Equal(Color.Parse("#98C379"), ReadColor(text));
        Assert.Equal(Color.Parse("#5C6370"), ReadColor(comment));
    }

    private static Color ReadColor(HighlightingColor color) => color.Foreground!.GetColor(null!)!.Value;
}
