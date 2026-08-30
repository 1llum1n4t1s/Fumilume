using Fumilume.Services;

namespace Fumilume.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void SavedSettingsAreReadBack()
    {
        using var storage = new TemporaryStorage();

        SettingsService.Save(new AppSettings
        {
            ShowLineNumbers = false,
            WordWrap = true,
            UiFontFamily = "Yu Gothic UI",
            UiFontSize = 16,
            FontFamily = "Consolas",
            FontSize = 18,
            ThemeMode = "Dark",
            WindowWidth = 1000,
            WindowHeight = 700,
        });
        var loaded = SettingsService.Load();

        Assert.False(loaded.ShowLineNumbers);
        Assert.True(loaded.WordWrap);
        Assert.Equal("Yu Gothic UI", loaded.UiFontFamily);
        Assert.Equal(16, loaded.UiFontSize);
        Assert.Equal("Consolas", loaded.FontFamily);
        Assert.Equal(18, loaded.FontSize);
        Assert.Equal("Dark", loaded.ThemeMode);
        Assert.Equal(1000, loaded.WindowWidth);
        Assert.Equal(700, loaded.WindowHeight);
    }

    [Fact]
    public void MissingFileFallsBackToDefaults()
    {
        using var storage = new TemporaryStorage();

        var loaded = SettingsService.Load();

        Assert.True(loaded.ShowLineNumbers);
        Assert.Equal("System", loaded.ThemeMode);
        Assert.True(loaded.UseAcrylic);
        Assert.Equal(AppFontFamilies.IbmPlexSansJpName, loaded.UiFontFamily);
        Assert.Equal(AppFontFamilies.UdevGothicJpDocName, loaded.FontFamily);
        Assert.Equal(14, loaded.UiFontSize);
    }

    [Fact]
    public void ExistingEditorFontSettingsGainUiFontDefaults()
    {
        using var storage = new TemporaryStorage();
        File.WriteAllText(
            Path.Combine(storage.Path, "settings.json"),
            """{"FontFamily":"Consolas","FontSize":18}""");

        var loaded = SettingsService.Load();

        Assert.Equal("Consolas", loaded.FontFamily);
        Assert.Equal(18, loaded.FontSize);
        Assert.Equal(AppFontFamilies.IbmPlexSansJpName, loaded.UiFontFamily);
        Assert.Equal(14, loaded.UiFontSize);
    }

    [Fact]
    public void LegacyDefaultFontsMigrateToTheBundledDefaults()
    {
        using var storage = new TemporaryStorage();
        File.WriteAllText(
            Path.Combine(storage.Path, "settings.json"),
            """{"UiFontFamily":"Inter","FontFamily":"'Cascadia Mono', Consolas, monospace"}""");

        var loaded = SettingsService.Load();

        Assert.Equal(AppFontFamilies.IbmPlexSansJpName, loaded.UiFontFamily);
        Assert.Equal(AppFontFamilies.UdevGothicJpDocName, loaded.FontFamily);
    }

    [Fact]
    public void CorruptFileFallsBackToDefaultsInsteadOfThrowing()
    {
        using var storage = new TemporaryStorage();
        File.WriteAllText(Path.Combine(storage.Path, "settings.json"), "{ これは JSON ではない");

        var loaded = SettingsService.Load();

        Assert.Equal("System", loaded.ThemeMode);
        Assert.Equal(15, loaded.FontSize);
    }

    [Fact]
    public void OutOfRangeValuesAreClampedOnLoad()
    {
        using var storage = new TemporaryStorage();
        File.WriteAllText(
            Path.Combine(storage.Path, "settings.json"),
            """{"UiFontFamily":"  ","UiFontSize":1,"FontSize":900,"IndentationSize":0,"ThemeMode":"Rainbow","FontFamily":"  "}""");

        var loaded = SettingsService.Load();

        Assert.Equal(48, loaded.FontSize);
        Assert.Equal(AppFontFamilies.IbmPlexSansJpName, loaded.UiFontFamily);
        Assert.Equal(8, loaded.UiFontSize);
        Assert.Equal(1, loaded.IndentationSize);
        Assert.Equal("System", loaded.ThemeMode);
        Assert.Equal(AppFontFamilies.UdevGothicJpDocName, loaded.FontFamily);
    }

    /// <summary>設定項目を増やしたぶん、範囲外の値もそれぞれ丸められる必要がある。</summary>
    [Fact]
    public void NewNumericSettingsAreClampedOnLoad()
    {
        using var storage = new TemporaryStorage();
        File.WriteAllText(
            Path.Combine(storage.Path, "settings.json"),
            """{"ColumnRulerPosition":9999,"LineHeightFactor":0.1,"LargeFileThresholdMegabytes":0,"SidePanelWidth":9999}""");

        var loaded = SettingsService.Load();

        Assert.Equal(512, loaded.ColumnRulerPosition);
        Assert.Equal(0.8, loaded.LineHeightFactor);
        Assert.Equal(1, loaded.LargeFileThresholdMegabytes);
        Assert.Equal(AppSettingsDefaults.MaximumSidePanelWidth, loaded.SidePanelWidth);
    }

    /// <summary>覚えたカーソル位置が青天井に増えて settings.json を太らせないこと。</summary>
    [Fact]
    public void RememberedCaretPositionsAreCapped()
    {
        using var storage = new TemporaryStorage();
        var settings = new AppSettings();
        for (var index = 0; index < 260; index++)
        {
            settings.CaretPositions[$@"C:\tmp\file{index}.txt"] = index;
        }

        SettingsService.Save(settings);
        var loaded = SettingsService.Load();

        Assert.Equal(200, loaded.CaretPositions.Count);
        // 捨てるのは古いほうなので、最後に入れたものは残る。
        Assert.True(loaded.CaretPositions.ContainsKey(@"C:\tmp\file259.txt"));
    }

    [Fact]
    public void CaretPositionsRoundTrip()
    {
        using var storage = new TemporaryStorage();

        SettingsService.Save(new AppSettings
        {
            CaretPositions = { [@"C:\tmp\a.txt"] = 42 },
        });

        Assert.Equal(42, SettingsService.Load().CaretPositions[@"C:\tmp\a.txt"]);
    }

    [Fact]
    public void SavingDoesNotLeaveTemporaryFilesBehind()
    {
        using var storage = new TemporaryStorage();

        SettingsService.Save(new AppSettings());
        SettingsService.Save(new AppSettings { WordWrap = true });

        Assert.Empty(Directory.GetFiles(storage.Path, "*.tmp"));
        Assert.True(File.Exists(Path.Combine(storage.Path, "settings.json")));
    }
}
