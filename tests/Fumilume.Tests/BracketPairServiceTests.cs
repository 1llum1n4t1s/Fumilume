using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Indentation;
using AvaloniaEdit.Indentation.CSharp;
using Fumilume.Services;

namespace Fumilume.Tests;

public sealed class BracketPairServiceTests
{
    [Theory]
    [InlineData(@"C:\tmp\Program.cs", "CSharp")]
    [InlineData(@"C:\tmp\data.json", "Json")]
    [InlineData(@"C:\tmp\settings.jsonc", "JsonWithComments")]
    [InlineData(@"C:\tmp\note.txt", "None")]
    public void FileExtensionSelectsTheBracketLanguage(string path, string expected)
        => Assert.Equal(expected, BracketPairService.LanguageFor(path).ToString());

    [Fact]
    public void NestedBracketsGetStableDepthsAndPairs()
    {
        var analysis = BracketPairService.Analyze("{[()]}", BracketLanguage.Json);

        Assert.Equal([0, 1, 2, 2, 1, 0], analysis.Tokens.Select(token => token.Depth));
        Assert.Equal([5, 4, 3, 2, 1, 0], analysis.Tokens.Select(token => token.PairOffset));
    }

    [Fact]
    public void BracketsInsideCSharpStringsCharactersAndCommentsAreIgnored()
    {
        const string text = """
            class C
            {
                var a = "[ignored]";
                var b = @"{ignored}";
                var c = '}';
                // (ignored)
                /* [ignored] */
                void M() { }
            }
            """;

        var analysis = BracketPairService.Analyze(text, BracketLanguage.CSharp);

        Assert.Equal("{(){}}", new string(analysis.Tokens.Select(token => token.Character).ToArray()));
        Assert.All(analysis.Tokens, token => Assert.True(token.PairOffset >= 0));
    }

    [Fact]
    public void JsonStringsDoNotCreateBracketPairs()
    {
        const string text = """{"value":"([{}])","items":[1]}""";

        var analysis = BracketPairService.Analyze(text, BracketLanguage.Json);

        Assert.Equal("{[]}", new string(analysis.Tokens.Select(token => token.Character).ToArray()));
    }

    [Theory]
    [InlineData("$\"ignored { (value) }\"")]
    [InlineData("$\"\"\"ignored { (value) }\"\"\"")]
    [InlineData("$$\"\"\"ignored {{ (value) }}\"\"\"")]
    public void InterpolatedStringContentDoesNotCreateFalseBracketPairs(string literal)
    {
        var analysis = BracketPairService.Analyze($"M({literal});", BracketLanguage.CSharp);

        Assert.Equal("()", new string(analysis.Tokens.Select(token => token.Character).ToArray()));
    }

    [Theory]
    [InlineData("$\"\"")]
    [InlineData("$\"{Get(\"[\")}\"")]
    [InlineData("$@\"{Get(\"[\")}\"")]
    [InlineData("@$\"{Get(\"[\")}\"")]
    [InlineData("$@\"\"\"text\"")]
    [InlineData("@$\"\"\"text\"")]
    public void InterpolatedStringEndsBeforeFollowingCode(string literal)
    {
        var analysis = BracketPairService.Analyze($"M({literal}); N();", BracketLanguage.CSharp);

        Assert.Equal("()()", new string(analysis.Tokens.Select(token => token.Character).ToArray()));
        Assert.All(analysis.Tokens, token => Assert.True(token.PairOffset >= 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    public void PairIsFoundOnEitherSideOfTheCaret(int caretOffset)
    {
        var analysis = BracketPairService.Analyze("{test}", BracketLanguage.CSharp);

        Assert.True(analysis.TryGetPairBesideCaret(caretOffset, out var token, out var pair));
        Assert.Equal(5, token.Offset + pair.Offset);
    }

    [Fact]
    public void PlainTextProducesNoBracketTokens()
        => Assert.Empty(BracketPairService.Analyze("{text}", BracketLanguage.None).Tokens);

    [Fact]
    public void CSharpFilesUseTheLanguageAwareIndentationStrategy()
    {
        var strategy = EditorIndentationService.Resolve("Program.cs", new TextEditorOptions());

        Assert.IsType<CSharpIndentationStrategy>(strategy);
    }

    [Fact]
    public void PlainTextKeepsTheDefaultIndentationStrategy()
    {
        var strategy = EditorIndentationService.Resolve("memo.txt", new TextEditorOptions());

        Assert.IsType<DefaultIndentationStrategy>(strategy);
    }

    [Fact]
    public void JsonIndentationFollowsTheOpenBracketDepth()
    {
        var options = new TextEditorOptions { ConvertTabsToSpaces = true, IndentationSize = 2 };
        var strategy = EditorIndentationService.Resolve("data.json", options);
        var document = new TextDocument("{\n\"items\": [\nvalue");

        strategy.IndentLine(document, document.GetLineByNumber(2));
        strategy.IndentLine(document, document.GetLineByNumber(3));

        Assert.Equal("{\n  \"items\": [\n    value", document.Text);
    }

    [Fact]
    public void JsonClosingBracketReturnsToItsParentIndent()
    {
        var strategy = new JsonIndentationStrategy("  ");
        var document = new TextDocument("{\n\"items\": [\n]\n}");

        strategy.IndentLine(document, document.GetLineByNumber(2));
        strategy.IndentLine(document, document.GetLineByNumber(3));
        strategy.IndentLine(document, document.GetLineByNumber(4));

        Assert.Equal("{\n  \"items\": [\n  ]\n}", document.Text);
    }
}
