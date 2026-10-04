using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

/// <summary>保存ファイルから実ウィンドウのパレット表示・再生まで、不正なマクロを隔離する。</summary>
[Collection(HeadlessAppCollection.Name)]
public sealed class GogoMacroE2ETests(HeadlessAppFixture fixture)
{
    [Theory]
    [InlineData("root-null", "null")]
    [InlineData("collection-null", "{\"Macros\":null}")]
    [InlineData("element-null", "{\"Macros\":[null]}")]
    [InlineData("steps-null", "{\"Macros\":[{\"Name\":\"破損\",\"Steps\":null}]}")]
    [InlineData("step-null", "{\"Macros\":[{\"Name\":\"破損\",\"Steps\":[null]}]}")]
    [InlineData("insert-text-null", "{\"Macros\":[{\"Name\":\"破損\",\"Steps\":[{\"Kind\":\"InsertText\",\"Text\":null}]}]}")]
    [InlineData("mixed-invalid", "{\"Macros\":[{\"Name\":\"途中まで有効\",\"Steps\":[{\"Kind\":\"InsertText\",\"Text\":\"混入\"}]},{\"Name\":\"破損\",\"Steps\":[null]}]}")]
    [InlineData("collection-type", "{\"Macros\":{}}")]
    [InlineData("steps-type", "{\"Macros\":[{\"Name\":\"破損\",\"Steps\":{}}]}")]
    public void InvalidLibraryLeavesPaletteAndEditingUsable(string scenario, string json) => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        File.WriteAllText(Path.Combine(storage.Path, "settings.json"),
            "{\"CheckUpdatesOnStartup\":false,\"RestoreSession\":false}");
        File.WriteAllText(Path.Combine(storage.Path, "macros.json"), json);
        MainWindow? window = null;
        var passed = false;
        string? failure = null;
        try
        {
            window = new MainWindow(SettingsService.Load());
            var vm = (MainWindowViewModel)window.DataContext!;
            window.Show();
            OpenPalette(window, vm);
            // 現行コードでは一覧を通過しても、nullの手や本文が再生時に例外になる。
            foreach (var macro in vm.SavedMacros.ToArray())
            {
                vm.SelectedPaletteCommand = vm.CommandPaletteResults.Single(entry => entry.Title == $"{macro.Name}（{macro.Summary}）");
                Complete(vm.RunSelectedPaletteCommandCommand.ExecuteAsync(null));
                OpenPalette(window, vm);
            }
            Assert.Empty(vm.SavedMacros);
            vm.CloseCommandPaletteCommand.Execute(null);
            Assert.False(vm.IsCommandPaletteOpen);
            OpenPalette(window, vm);
            var document = vm.Documents.Single();
            document.Text = "abc";
            document.SelectionStart = 0;
            document.SelectionLength = 3;
            vm.SelectedPaletteCommand = vm.CommandPaletteResults.Single(entry => entry.Title == "大文字");
            Complete(vm.RunSelectedPaletteCommandCommand.ExecuteAsync(null));
            Assert.False(vm.IsCommandPaletteOpen);
            Assert.Equal("ABC", document.Text);
            Assert.Equal(json, File.ReadAllText(Path.Combine(storage.Path, "macros.json")));
            passed = true;
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            throw;
        }
        finally
        {
            CloseWindow(window, storage.Path);
            WriteArtifact(scenario, new { Scenario = scenario, Input = json, Passed = passed, Failure = failure });
        }
    });

    [Fact]
    public void ValidLibraryRetainsFilteringAndRunsAfterPaletteReopens() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var macros = new List<KeyboardMacro>
        {
            new() { Name = " ", Steps = null! },
            new() { Name = null! },
        };
        for (var index = 0; index < MacroStore.MaximumMacros + 2; index++)
            macros.Add(new KeyboardMacro
            {
                Name = $"保存{index:D2}",
                Steps = index >= MacroStore.MaximumMacros
                    ? null!
                    : [new MacroStep { Kind = MacroStepKind.InsertText, Text = "復元" },
                        new MacroStep { Kind = MacroStepKind.FindNext, Text = null! }],
            });
        Assert.True(MacroStore.Save(new MacroLibrary { Macros = macros }));
        var window = new MainWindow(new AppSettings { CheckUpdatesOnStartup = false, RestoreSession = false });
        var vm = (MainWindowViewModel)window.DataContext!;
        window.Show();
        try
        {
            OpenPalette(window, vm);
            Assert.Equal(MacroStore.MaximumMacros, vm.SavedMacros.Count);
            Assert.Equal("保存00", vm.SavedMacros[0].Name);
            Assert.Equal("保存19", vm.SavedMacros[^1].Name);
            vm.CloseCommandPaletteCommand.Execute(null);
            OpenPalette(window, vm);
            vm.CommandPaletteQuery = "保存00";
            Dispatcher.UIThread.RunJobs();
            vm.SelectedPaletteCommand = Assert.Single(vm.CommandPaletteResults);
            Complete(vm.RunSelectedPaletteCommandCommand.ExecuteAsync(null));
            Assert.Equal("復元", vm.Documents.Single().Text);
            vm.Documents.Single().EditorDocument.UndoStack.Undo();
            Assert.Equal(string.Empty, vm.Documents.Single().Text);
            WriteArtifact("valid-library", new
            {
                Passed = true,
                MacroCount = vm.SavedMacros.Count,
                PlaybackText = "復元",
                UndoText = string.Empty,
                EmptyNameWithNullStepsIgnored = true,
                OverflowWithNullStepsIgnored = true,
                NullSearchTextRemainsNoOp = true,
            });
        }
        finally
        {
            CloseWindow(window, storage.Path);
        }
    });

    private static void OpenPalette(MainWindow window, MainWindowViewModel vm)
    {
        vm.OpenCommandPaletteCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.True(vm.IsCommandPaletteOpen);
        Assert.True(window.FindControl<ListBox>("CommandPaletteList")!.IsEffectivelyVisible);
        Assert.Contains(vm.CommandPaletteResults, entry => entry.Title == "新しい文書");
    }

    private static void Complete(Task task)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20), "マクロのUI操作が20秒以内に完了しませんでした。");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private static void CloseWindow(MainWindow? window, string storagePath)
    {
        if (window is null) return;
        if (window.DataContext is MainWindowViewModel vm)
            foreach (var document in vm.Documents)
                document.MarkSaved(Path.Combine(storagePath, "closed.txt"));
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void WriteArtifact(string scenario, object value)
    {
        if (Environment.GetEnvironmentVariable("FUMILUME_GOGO_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"macro-{scenario}.json"),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
