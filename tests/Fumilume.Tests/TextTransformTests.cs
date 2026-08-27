using Fumilume.Services;

namespace Fumilume.Tests;

/// <summary>
/// sakura エディタの「変換系」コマンド相当の変換規則。
/// 濁点の分解・合成と桁位置を保ったタブ変換は取り違えやすいので、境界を先に固定しておく。
/// </summary>
public sealed class TextTransformTests
{
    [Theory]
    [InlineData("ＡＢＣ１２３", "ABC123")]
    [InlineData("！？＃", "!?#")]
    [InlineData("　", " ")]
    public void FullWidthAlphanumericBecomesHalfWidth(string input, string expected)
        => Assert.Equal(expected, TextTransforms.ToHalfWidthAlphanumeric(input));

    [Theory]
    [InlineData("ABC123", "ＡＢＣ１２３")]
    [InlineData(" ", "　")]
    public void HalfWidthAlphanumericBecomesFullWidth(string input, string expected)
        => Assert.Equal(expected, TextTransforms.ToFullWidthAlphanumeric(input));

    [Theory]
    [InlineData("アイウ", "ｱｲｳ")]
    [InlineData("ガギグ", "ｶﾞｷﾞｸﾞ")]
    [InlineData("パピプ", "ﾊﾟﾋﾟﾌﾟ")]
    [InlineData("ヴ", "ｳﾞ")]
    public void FullWidthKatakanaSplitsVoicedMarks(string input, string expected)
        => Assert.Equal(expected, TextTransforms.ToHalfWidthKatakana(input));

    [Theory]
    [InlineData("ｱｲｳ", "アイウ")]
    [InlineData("ｶﾞｷﾞｸﾞ", "ガギグ")]
    [InlineData("ﾊﾟﾋﾟﾌﾟ", "パピプ")]
    [InlineData("｡｢｣､･", "。「」、・")]
    public void HalfWidthKatakanaCombinesVoicedMarks(string input, string expected)
        => Assert.Equal(expected, TextTransforms.ToFullWidthKatakana(input));

    /// <summary>濁点が付かない字の後ろに濁点が来ても、勝手に食べずそのまま残す。</summary>
    [Fact]
    public void StandaloneVoicedMarkSurvives()
        => Assert.Equal("ア゛", TextTransforms.ToFullWidthKatakana("ｱﾞ"));

    [Fact]
    public void KatakanaAndHiraganaConvertBothWays()
    {
        Assert.Equal("アイウ", TextTransforms.ToKatakana("あいう"));
        Assert.Equal("あいう", TextTransforms.ToHiragana("アイウ"));
    }

    [Fact]
    public void HalfWidthKatakanaBecomesHiragana()
        => Assert.Equal("がぎぐ", TextTransforms.HalfWidthKatakanaToHiragana("ｶﾞｷﾞｸﾞ"));

    /// <summary>タブは「次のタブ位置まで」を埋める。固定幅で置き換えると桁がずれる。</summary>
    [Theory]
    [InlineData("a\tb", "a   b")]
    [InlineData("\tx", "    x")]
    [InlineData("abcd\te", "abcd    e")]
    public void TabExpandsToTheNextTabStop(string input, string expected)
        => Assert.Equal(expected, TextTransforms.TabToSpace(input, 4));

    [Theory]
    [InlineData("a   b", "a\tb")]
    [InlineData("a b", "a b")] // 語間の 1 個は畳まない
    public void SpacesCollapseOnlyOnTabStops(string input, string expected)
        => Assert.Equal(expected, TextTransforms.SpaceToTab(input, 4));

    [Fact]
    public void TabAndSpaceRoundTrip()
    {
        const string source = "if\tx:\n\t\treturn";
        var expanded = TextTransforms.TabToSpace(source, 4);
        Assert.Equal(source, TextTransforms.SpaceToTab(expanded, 4));
    }

    [Fact]
    public void TrimRemovesLeadingAndTrailingBlanksPerLine()
    {
        Assert.Equal("a\nb", TextTransforms.TrimLineStarts("  a\n\tb"));
        Assert.Equal("a\nb", TextTransforms.TrimLineEnds("a  \nb\t"));
    }

    [Fact]
    public void SortOrdersLinesAndKeepsTheTrailingNewLine()
    {
        Assert.Equal("a\nb\nc", TextTransforms.SortLines("c\na\nb", descending: false));
        Assert.Equal("c\nb\na", TextTransforms.SortLines("c\na\nb", descending: true));
        Assert.Equal("a\nb\n", TextTransforms.SortLines("b\na\n", descending: false));
    }

    /// <summary>sakura の F_MERGE と同じく、直前の行と同じ行だけを落とす（全体の重複ではない）。</summary>
    [Fact]
    public void MergeDropsOnlyConsecutiveDuplicates()
    {
        Assert.Equal("a\nb\na", TextTransforms.MergeLines("a\na\nb\na"));
        Assert.Equal("a\nb\n", TextTransforms.MergeLines("a\na\nb\n"));
    }

    [Fact]
    public void CrLfDocumentsKeepTheirNewLine()
        => Assert.Equal("a\r\nb", TextTransforms.SortLines("b\r\na", descending: false));

    [Fact]
    public void Base64RoundTripsAndRejectsGarbage()
    {
        var encoded = TextTransforms.Base64Encode("こんにちは");
        Assert.Equal("こんにちは", TextTransforms.Base64Decode(encoded));
        Assert.Null(TextTransforms.Base64Decode("!!!not base64!!!"));
    }

    [Fact]
    public void UrlRoundTrips()
    {
        var encoded = TextTransforms.UrlEncode("a b&c=日本語");
        Assert.Equal("a b&c=日本語", TextTransforms.UrlDecode(encoded));
    }
}
