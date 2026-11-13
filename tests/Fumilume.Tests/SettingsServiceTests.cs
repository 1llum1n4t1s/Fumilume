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

    [Fact]
    public void LargeFileThresholdIsClampedBelowTheTextReaderLimitOnLoad()
    {
        using var storage = new TemporaryStorage();
        File.WriteAllText(
            Path.Combine(storage.Path, "settings.json"),
            """{"LargeFileThresholdMegabytes":4096}""");

        var loaded = SettingsService.Load();

        Assert.Equal(2047, AppSettingsDefaults.MaximumLargeFileThresholdMegabytes);
        Assert.Equal(AppSettingsDefaults.MaximumLargeFileThresholdMegabytes, loaded.LargeFileThresholdMegabytes);
    }

    [Fact]
    public void SidePanelWidthIsClampedToTheSmallerMinimumOnLoad()
    {
        using var storage = new TemporaryStorage();
        File.WriteAllText(
            Path.Combine(storage.Path, "settings.json"),
            """{"SidePanelWidth":1}""");

        var loaded = SettingsService.Load();

        Assert.Equal(120, AppSettingsDefaults.MinimumSidePanelWidth);
        Assert.Equal(AppSettingsDefaults.MinimumSidePanelWidth, loaded.SidePanelWidth);
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

    /// <summary>
    /// 上限に達した後も、覚えるたびに保存する通常の使い方で最新の位置が残ること。
    /// Dictionary の空きスロット再利用により、上限処理の直後に覚えた位置が列挙の先頭へ入り、
    /// 次の保存で真っ先に捨てられていた不具合の再発防止。
    /// </summary>
    [Fact]
    public void CappedCaretPositionsKeepNewestAcrossRepeatedSaves()
    {
        using var storage = new TemporaryStorage();
        var settings = new AppSettings();
        for (var index = 0; index < AppSettingsDefaults.MaximumCaretPositions + 1; index++)
        {
            settings.CaretPositions[$@"C:\tmp\file{index}.txt"] = index;
        }

        // 1 回目の上限処理で最古の 1 件を捨てる。
        SettingsService.Save(settings);

        // アプリと同じく、同じ設定オブジェクトへ覚えては保存する、を繰り返す。
        settings.CaretPositions[@"C:\tmp\newer.txt"] = 1;
        SettingsService.Save(settings);
        settings.CaretPositions[@"C:\tmp\newest.txt"] = 2;
        SettingsService.Save(settings);

        var loaded = SettingsService.Load();
        Assert.Equal(AppSettingsDefaults.MaximumCaretPositions, loaded.CaretPositions.Count);
        Assert.True(loaded.CaretPositions.ContainsKey(@"C:\tmp\newer.txt"));
        Assert.True(loaded.CaretPositions.ContainsKey(@"C:\tmp\newest.txt"));
        // 捨てるのは古い順（追加順の先頭から）。
        Assert.False(loaded.CaretPositions.ContainsKey(@"C:\tmp\file0.txt"));
        Assert.False(loaded.CaretPositions.ContainsKey(@"C:\tmp\file1.txt"));
        Assert.False(loaded.CaretPositions.ContainsKey(@"C:\tmp\file2.txt"));
        Assert.True(loaded.CaretPositions.ContainsKey(@"C:\tmp\file3.txt"));
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
