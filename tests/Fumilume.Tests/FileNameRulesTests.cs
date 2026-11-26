using Fumilume.Services;

namespace Fumilume.Tests;

/// <summary>
/// 名前の検証（Validate / HasInvalidChar）と file URI への変換（ToFileUri / EscapeNonAscii）。
/// 認知的複雑度が 5 を超える関数だけを対象にする。
/// </summary>
public sealed class FileNameRulesTests
{
    [Theory]
    [InlineData("memo.txt")]
    [InlineData("日本語 の名前.md")]
    [InlineData("a.b.c")]
    [InlineData(".gitignore")]
    [InlineData("CONSOLE.txt")]
    [InlineData("COM10")]
    [InlineData("  trimmed.txt  ")]
    public void WindowsRulesAcceptOrdinaryNames(string name)
        => Assert.Null(FileNameRules.Validate(name, windowsRules: true));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a\tb")]
    [InlineData("name.")]
    [InlineData("name. ")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Nul.txt")]
    [InlineData("COM1.log")]
    [InlineData("LPT9")]
    [InlineData("AUX .txt")]
    public void WindowsRulesRejectInvalidNames(string name)
    {
        var message = FileNameRules.Validate(name, windowsRules: true);

        Assert.False(string.IsNullOrEmpty(message));
    }

    [Theory]
    [InlineData("a*b?.txt")]
    [InlineData("CON")]
    [InlineData("name.")]
    [InlineData("a|b")]
    public void MacRulesOnlyRejectWhatTheOsRejects(string name)
        => Assert.Null(FileNameRules.Validate(name, windowsRules: false));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    public void MacRulesRejectSlashColonAndEmpty(string name)
        => Assert.NotNull(FileNameRules.Validate(name, windowsRules: false));

    [Fact]
    public void ErrorMessagesUseFolderWording()
    {
        Assert.Contains("フォルダ", FileNameRules.Validate("", windowsRules: true));
        Assert.Contains("フォルダ", FileNameRules.Validate("a?b", windowsRules: true));
        Assert.Contains("a?b", FileNameRules.Validate("a?b", windowsRules: true));
        Assert.Contains("フォルダ", FileNameRules.ExistsMessage("x"));
    }

    [Fact]
    public void ToFileUriEscapesSpecialCharactersInLocalPaths()
    {
        Assert.Equal("file:///C:/a%20b/%23x%25y.txt", FileNameRules.ToFileUri(@"C:\a b\#x%y.txt"));
    }

    [Fact]
    public void ToFileUriEscapesNonAsciiAsUtf8()
    {
        Assert.Equal("file:///C:/%E6%97%A5%E6%9C%AC/%E3%83%A1%E3%83%A2.txt", FileNameRules.ToFileUri(@"C:\日本\メモ.txt"));
    }

    [Fact]
    public void ToFileUriKeepsUncHostAndEscapesTheRest()
    {
        Assert.Equal("file://server/share/a%20b/%E3%83%A1%E3%83%A2.txt", FileNameRules.ToFileUri(@"\\server\share\a b\メモ.txt"));
    }

    [Fact]
    public void ToFileUriRejectsRelativePaths()
        => Assert.Null(FileNameRules.ToFileUri(@"a\b.txt"));
}
