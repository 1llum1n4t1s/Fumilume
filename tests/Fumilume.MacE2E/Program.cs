using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaEdit;
using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.MacE2E;

internal static class Program
{
    private static readonly List<object> Results = [];
    private static string _output = "";
    private static int _exitCode;
    private static bool _verifyRestart;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--verify-updates", var artifacts, var updateOutput])
            return UpdatePackageE2E.RunAsync(artifacts, updateOutput).GetAwaiter().GetResult();

        if (args is ["--forward", var storage, var path])
        {
            using var secondary = SingleInstanceCoordinator.Create("MacE2E", storage);
            return !secondary.IsPrimary && secondary.ForwardArgumentsAsync(
                Fumilume.Program.NormalizeForwardedArguments([path])).GetAwaiter().GetResult() ? 0 : 1;
        }

        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            Console.Error.WriteLine("This E2E requires an Apple Silicon macOS desktop session.");
            return 2;
        }

        if (args is [var driverOutput])
            return RunDriverAsync(Path.GetFullPath(driverOutput)).GetAwaiter().GetResult();
        if (args is not ["--native-e2e" or "--verify-session", var nativeOutput])
            throw new ArgumentException("Use <output>, --native-e2e <output>, or --verify-session <output>.");
        _verifyRestart = args[0] == "--verify-session";
        _output = Path.GetFullPath(nativeOutput);
        if (!_verifyRestart && Directory.Exists(_output) && Directory.EnumerateFileSystemEntries(_output).Any())
        {
            throw new InvalidOperationException("Use an empty artifact directory.");
        }

        Directory.CreateDirectory(_output);
        AppStoragePaths.OverrideDirectory(Path.Combine(_output, "storage"));
        if (!_verifyRestart)
            SettingsService.Save(new AppSettings { RestoreSession = true, CheckUpdatesOnStartup = false, ConfirmOnExit = false });
        try
        {
            Fumilume.Program.BuildAvaloniaApp()
                .AfterSetup(_ => Dispatcher.UIThread.Post(async () => await RunAsync()))
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception exception)
        {
            Results.Add(new { name = "native-startup", passed = false, error = exception.ToString() });
            _exitCode = 1;
        }
        finally
        {
            WriteResults();
            AppStoragePaths.OverrideDirectory(null);
        }

        return _exitCode;
    }

    private static async Task<int> RunDriverAsync(string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidOperationException("Use an empty artifact directory.");
        var first = await RunChildAsync("--native-e2e", output);
        var restart = first == 0 ? await RunChildAsync("--verify-session", output) : -1;
        await File.WriteAllTextAsync(Path.Combine(output, "process-results.json"),
            JsonSerializer.Serialize(new { schemaVersion = 1, passed = first == 0 && restart == 0,
                firstProcessExitCode = first, restartProcessExitCode = restart, utc = DateTimeOffset.UtcNow },
                new JsonSerializerOptions { WriteIndented = true }));
        return first == 0 && restart == 0 ? 0 : 1;
    }

    private static async Task<int> RunChildAsync(string mode, string output)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!);
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(output);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start native E2E process");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
        return child.ExitCode;
    }

    private static async Task RunAsync()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            await WaitAsync(() => desktop.MainWindow is { IsVisible: true });
            var window = (MainWindow)desktop.MainWindow!;
            var vm = (MainWindowViewModel)window.DataContext!;
            await vm.InitializeAsync([]);
            if (_verifyRestart)
            {
                await CaseAsync("new-process-native-session-restoration", async () =>
                {
                    var path = Path.Combine(_output, "復元する文書.txt");
                    var document = vm.Documents.Single(d => d.FilePath == path);
                    Require(document.Text == "未保存の日本語\n復元対象\n" && document.IsModified && document.CaretIndex == 3,
                        "New process did not restore unsaved Japanese buffer/caret");
                    Require(vm.Options.EditorFontSize == 19 && vm.Options.WordWrap, "New process did not restore settings");
                    Require(vm.SettingsTab is not null, "New process did not restore settings tab");
                    vm.SelectedTab = document;
                    await CaptureAsync(window, "restart-restored-document.png");
                });
                return;
            }
            await CaseAsync("native-window-editor-bindings", async () =>
            {
                Require(window.FindControl<TextEditor>("Editor") is not null, "Editor control missing");
                Require(window.WindowDecorations == WindowDecorations.Full, "macOS native decorations missing");
                Require(window.FindControl<Grid>("TitleBar") is { IsVisible: false }, "Windows title bar visible");
                await CaptureAsync(window, "native-window.png");
            });

            await CaseAsync("document-open-edit-save-encoding-newline-japanese-path", async () =>
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var formats = new (string Name, Encoding Encoding, DocumentEncoding Kind)[]
                {
                    ("utf8", new UTF8Encoding(false), DocumentEncoding.Utf8),
                    ("utf8bom", new UTF8Encoding(true), DocumentEncoding.Utf8Bom),
                    ("utf16le", new UnicodeEncoding(false, true), DocumentEncoding.Utf16LittleEndian),
                    ("utf16be", new UnicodeEncoding(true, true), DocumentEncoding.Utf16BigEndian),
                    ("shiftjis", Encoding.GetEncoding(932), DocumentEncoding.ShiftJis),
                    ("eucjp", Encoding.GetEncoding(51932), DocumentEncoding.EucJp),
                    ("iso2022jp", Encoding.GetEncoding(50220), DocumentEncoding.Iso2022Jp),
                };
                foreach (var format in formats)
                foreach (var newline in new[] { "\n", "\r\n", "\r" })
                {
                    var name = $"日本語 {format.Name}-{(newline == "\n" ? "lf" : newline == "\r" ? "cr" : "crlf")}.txt";
                    var path = Path.Combine(_output, name);
                    var original = $"日本語の文書です{newline}こんにちは世界{newline}";
                    await File.WriteAllBytesAsync(path, Encode(format.Encoding, original));
                    await vm.OpenPathsAsync([path, path]);
                    var document = vm.SelectedDocument ?? throw new InvalidOperationException("Document not selected");
                    Require(vm.Documents.Count(d => d.FilePath == path) == 1, "Duplicate document tab");
                    Require(document.Encoding == format.Kind && document.NewLine == newline && document.Text == original,
                        $"Read metadata/text changed for {name}: {document.Encoding}/{document.NewLine}");
                    document.Text += $"追記{newline}";
                    await vm.SaveCommand.ExecuteAsync(null);
                    Require(!document.IsModified, "Save command left modified state");
                    Require((await File.ReadAllBytesAsync(path)).SequenceEqual(Encode(format.Encoding, document.Text)),
                        $"Save changed BOM/encoding/newline for {name}");
                }
                await CaptureAsync(window, "saved-japanese-document.png");
            });

            await CaseAsync("native-markdown-csv-preview", async () =>
            {
                var markdown = Path.Combine(_output, "日本語プレビュー.md");
                await File.WriteAllTextAsync(markdown, "# 見出し\n\n**日本語**のプレビュー\n");
                await vm.OpenPathsAsync([markdown]);
                vm.SelectedDocument!.TogglePreview();
                Require(vm.SelectedDocument.IsMarkdownPreview, "Markdown preview not selected");
                await CaptureAsync(window, "markdown-preview.png");
                var csv = Path.Combine(_output, "日本語表.csv");
                await File.WriteAllTextAsync(csv, "名前,値\nりんご,001\n");
                await vm.OpenPathsAsync([csv]);
                vm.SelectedDocument!.TogglePreview();
                Require(vm.SelectedDocument.IsCsvPreview, "CSV preview not selected");
                await CaptureAsync(window, "csv-preview.png");
            });

            await CaseAsync("native-editor-delete-modifiers-macro-record-replay", async () =>
            {
                var path = Path.Combine(_output, "マクロ削除.txt");
                await File.WriteAllTextAsync(path, "alpha.beta 日本語 gamma");
                await vm.OpenPathsAsync([path]);
                var document = vm.SelectedDocument!;
                var editor = window.FindControl<TextEditor>("Editor")!;
                // プレビューからの切り替えは binding/layout の反映後にフォーカスを当てる。
                // AvaloniaEdit の RoutedCommand は GotFocus の入力元へ実行される。
                await WaitAsync(() => editor.IsVisible && ReferenceEquals(editor.Document, document.EditorDocument));
                editor.TextArea.Focus();
                await WaitAsync(() => editor.TextArea.IsFocused);
                foreach (var modifier in new[] { KeyModifiers.Alt, KeyModifiers.Meta })
                foreach (var key in new[] { Key.Back, Key.Delete })
                {
                    const string original = "alpha.beta 日本語 gamma";
                    document.Text = original;
                    editor.Select(0, 0);
                    editor.CaretOffset = 6;
                    vm.ToggleMacroRecordingCommand.Execute(null);
                    SendKey(editor, key, modifier);
                    vm.ToggleMacroRecordingCommand.Execute(null);
                    Require(document.Text == original, $"{modifier}+{key} changed the native editor");
                    Require(vm.RecordedStepCount == 0, $"Unbound {modifier}+{key} was recorded");
                    await vm.RunMacroCommand.ExecuteAsync(null);
                    Require(document.Text == original, "Unbound deletion changed text during replay");
                }

                foreach (var original in new[] { "alpha.beta 日本語 gamma", "alpha  ++ beta\ngamma", "日本語、次の語。末尾", "a\r\n\r\nb" })
                foreach (var key in new[] { Key.Back, Key.Delete })
                foreach (var selected in new[] { false, true })
                {
                    document.Text = original;
                    var offset = key == Key.Back ? original.Length : 0;
                    if (selected) { editor.Select(2, 4); editor.CaretOffset = 6; }
                    else { editor.Select(offset, 0); editor.CaretOffset = offset; }
                    vm.ToggleMacroRecordingCommand.Execute(null);
                    SendKey(editor, key, KeyModifiers.Control);
                    vm.ToggleMacroRecordingCommand.Execute(null);
                    var expectedText = document.Text;
                    var expectedCaret = editor.CaretOffset;
                    Require(expectedText != original && vm.RecordedStepCount == 1,
                        $"Control word deletion did not execute/record: key={key}, offset={offset}, selected={selected}, " +
                        $"source={JsonSerializer.Serialize(original)}, actual={JsonSerializer.Serialize(expectedText)}, " +
                        $"steps={vm.RecordedStepCount}, visible={editor.IsVisible}, focus={editor.TextArea.IsFocused}, " +
                        $"boundDocument={ReferenceEquals(editor.Document, document.EditorDocument)}");
                    if (selected) Require(expectedText == original.Remove(2, 4), "Word deletion ignored selected text");
                    document.Text = original;
                    if (selected) { editor.Select(2, 4); editor.CaretOffset = 6; }
                    else { editor.Select(offset, 0); editor.CaretOffset = offset; }
                    await vm.RunMacroCommand.ExecuteAsync(null);
                    Require(document.Text == expectedText && document.CaretIndex == expectedCaret,
                        $"Native deletion and replay differ: {key}, selection={selected}, source={original}");
                }
                foreach (var key in new[] { Key.Back, Key.Delete })
                {
                    document.Text = "a\r\nb";
                    var offset = key == Key.Back ? 3 : 1;
                    editor.Select(offset, 0);
                    editor.CaretOffset = offset;
                    vm.ToggleMacroRecordingCommand.Execute(null);
                    SendKey(editor, key, KeyModifiers.None);
                    vm.ToggleMacroRecordingCommand.Execute(null);
                    Require(document.Text == "ab", "Native deletion failed to join CRLF lines");
                    var expectedCaret = editor.CaretOffset;
                    document.Text = "a\r\nb";
                    editor.Select(offset, 0);
                    editor.CaretOffset = offset;
                    await vm.RunMacroCommand.ExecuteAsync(null);
                    Require(document.Text == "ab" && document.CaretIndex == expectedCaret,
                        $"CRLF join replay differs from native editor: {key}");
                }
                await CaptureAsync(window, "macro-delete-replay.png");
            });

            await CaseAsync("coregraphics-normal-rotated-pdf-and-broken-input", async () =>
            {
                var path = Path.Combine(_output, "日本語 通常と回転.pdf");
                PdfFixture.Write(path);
                using (var renderer = await MacPdfRenderer.OpenAsync(path))
                {
                    Require(renderer.PageCount == 2, "PDF page count");
                    for (var page = 0; page < 2; page++)
                    {
                        var size = renderer.GetPageSize(page);
                        Require(size == (page == 0 ? new Size(240, 320) : new Size(320, 240)), "PDF rotated dimensions");
                        using var bitmap = await renderer.RenderAsync(page, 1);
                        bitmap.Save(Path.Combine(_output, $"pdf-page-{page + 1}.png"), PngBitmapEncoderOptions.Default);
                        Require(bitmap.PixelSize == new PixelSize((int)size.Width, (int)size.Height), "Rendered PDF dimensions");
                        RequireColoredPixels(bitmap, page);
                    }
                }
                await vm.OpenPathsAsync([path, path]);
                var pdf = vm.SelectedPdf ?? throw new InvalidOperationException("PDF tab not selected");
                Require(vm.Tabs.OfType<PdfDocumentViewModel>().Count(p => p.FilePath == path) == 1, "Duplicate PDF tab");
                Require(!vm.SaveCommand.CanExecute(null), "PDF enabled text save command");
                await pdf.NextPageCommand.ExecuteAsync(null);
                Require(pdf.CurrentPage == 2 && pdf.PageImage is not null && pdf.ErrorMessage is null, "PDF UI navigation failed");
                await CaptureAsync(window, "pdf-rotated-window.png");
                var broken = Path.Combine(_output, "壊れた.pdf");
                await File.WriteAllTextAsync(broken, "%PDF-1.7\ninvalid document\n");
                try
                {
                    using var unexpected = await MacPdfRenderer.OpenAsync(broken);
                    throw new InvalidOperationException("Broken PDF accepted");
                }
                catch (InvalidDataException) { }
                // エラーダイアログを出す UI へ壊れた PDF を渡すと無人 CI が停止するため、
                // 同じ製品 renderer の入力境界で拒否を確認する。
            });

            await CaseAsync("settings-unsaved-session-restoration-native-window", async () =>
            {
                var path = Path.Combine(_output, "復元する文書.txt");
                await File.WriteAllTextAsync(path, "保存済み\n");
                await vm.OpenPathsAsync([path]);
                vm.SelectedDocument!.Text = "未保存の日本語\n復元対象\n";
                vm.SelectedDocument.CaretIndex = 3;
                vm.Options.EditorFontSize = 19;
                vm.Options.WordWrap = true;
                vm.OpenSettingsCommand.Execute(null);
                Require(await vm.PersistSessionStateAsync(), "Session persistence failed");
                var settings = SettingsService.Load();
                Require(settings.FontSize == 19 && settings.WordWrap, "Settings persistence failed");
                var reopened = new MainWindow(settings);
                reopened.Show();
                var restored = (MainWindowViewModel)reopened.DataContext!;
                await restored.InitializeAsync([]);
                var document = restored.Documents.Single(d => d.FilePath == path);
                Require(document.Text == "未保存の日本語\n復元対象\n" && document.IsModified && document.CaretIndex == 3,
                    "Unsaved buffer/caret not restored");
                // 設定の開閉は SettingsTabOpen として保存し、復元時の選択は文書へ戻す仕様。
                Require(restored.SettingsTab is not null && restored.Tabs[^1] == restored.SettingsTab
                    && restored.SelectedTab is DocumentViewModel, "Settings tab/content selection not restored");
                Require(restored.Options.EditorFontSize == 19 && restored.Options.WordWrap, "Restored options changed");
                await CaptureAsync(reopened, "restored-settings-window.png");
                desktop.MainWindow = reopened;
                window.Close();
                await WaitAsync(() => !window.IsVisible);
                vm = restored;
                window = reopened;
            });

            await CaseAsync("single-instance-cross-process-forward-relative-japanese-path", async () =>
            {
                using var primary = SingleInstanceCoordinator.Create("MacE2E", AppStoragePaths.Directory);
                Require(primary.IsPrimary, "First process is not primary");
                var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
                primary.SetArgumentsHandler(paths => received.TrySetResult(paths));
                var path = Path.Combine(_output, "後続起動.txt");
                await File.WriteAllTextAsync(path, "後続プロセスから開く\n");
                var start = new ProcessStartInfo(Environment.ProcessPath!) { WorkingDirectory = _output };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                foreach (var argument in new[] { "--forward", AppStoragePaths.Directory, Path.GetFileName(path) })
                    start.ArgumentList.Add(argument);
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start secondary process");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await child.WaitForExitAsync(timeout.Token); }
                catch { if (!child.HasExited) child.Kill(); throw; }
                Require(child.ExitCode == 0, "Secondary process failed or acquired primary lease");
                var forwarded = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Require(forwarded.SequenceEqual([path]), "Relative Japanese argument not normalized/forwarded");
                window.OpenForwardedArguments(forwarded);
                await WaitAsync(() => vm.SelectedDocument?.FilePath == path);
                Require(vm.SelectedDocument!.Text == "後続プロセスから開く\n", "Forwarded document not opened after restoration");
                await CaptureAsync(window, "forwarded-document.png");
            });
        }
        catch (Exception exception)
        {
            Results.Add(new { name = "native-runner", passed = false, error = exception.ToString() });
            _exitCode = 1;
        }
        finally
        {
            try
            {
                if (desktop.MainWindow is { } window && window.DataContext is MainWindowViewModel vm)
                {
                    Require(await vm.PersistSessionStateAsync(), "Final session persistence failed");
                    window.Close();
                    await WaitAsync(() => !window.IsVisible);
                }
            }
            catch (Exception exception)
            {
                Results.Add(new { name = "native-normal-close", passed = false, error = exception.ToString() });
                _exitCode = 1;
            }
            WriteResults();
            desktop.Shutdown(_exitCode);
        }
    }

    private static async Task CaseAsync(string name, Func<Task> run)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await run();
            Results.Add(new { name, passed = true, elapsedMilliseconds = watch.ElapsedMilliseconds });
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            Results.Add(new { name, passed = false, elapsedMilliseconds = watch.ElapsedMilliseconds, error = exception.ToString() });
            Console.Error.WriteLine($"FAIL {name}: {exception}");
            _exitCode = 1;
        }
        WriteResults();
    }

    private static void WriteResults() => File.WriteAllText(Path.Combine(_output, _verifyRestart ? "restart-results.json" : "results.json"),
        JsonSerializer.Serialize(new { schemaVersion = 1, os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), passed = _exitCode == 0,
            utc = DateTimeOffset.UtcNow, cases = Results }, new JsonSerializerOptions { WriteIndented = true }));

    private static async Task WaitAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Native UI condition timed out");
            await Task.Delay(50);
        }
    }

    private static async Task CaptureAsync(Window window, string name)
    {
        await Task.Delay(250);
        Require(window.Bounds.Width > 0 && window.Bounds.Height > 0, "Native window has no layout");
        using var screenshot = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        screenshot.Render(window);
        screenshot.Save(Path.Combine(_output, name), PngBitmapEncoderOptions.Default);
    }

    private static byte[] Encode(Encoding encoding, string text) => [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];

    private static void SendKey(TextEditor editor, Key key, KeyModifiers modifiers)
    {
        var before = new { text = editor.Text, caret = editor.CaretOffset,
            selectionStart = editor.SelectionStart, selectionLength = editor.SelectionLength };
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent,
            Source = editor.TextArea, Key = key, KeyModifiers = modifiers };
        editor.TextArea.RaiseEvent(args);
        var vm = TopLevel.GetTopLevel(editor)?.DataContext as MainWindowViewModel;
        File.AppendAllText(Path.Combine(_output, "key-input.jsonl"),
            JsonSerializer.Serialize(new { key = key.ToString(), modifiers = modifiers.ToString(), before,
                after = new { text = editor.Text, caret = editor.CaretOffset,
                    selectionStart = editor.SelectionStart, selectionLength = editor.SelectionLength },
                args.Handled, source = args.Source?.GetType().Name, editor.IsVisible,
                editor.IsKeyboardFocusWithin, textAreaFocused = editor.TextArea.IsFocused,
                boundDocument = ReferenceEquals(editor.Document, vm?.SelectedDocument?.EditorDocument),
                recordedStepCount = vm?.RecordedStepCount }) + Environment.NewLine);
    }

    private static void RequireColoredPixels(Bitmap bitmap, int page)
    {
        var stride = bitmap.PixelSize.Width * 4;
        var bytes = new byte[stride * bitmap.PixelSize.Height];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(bitmap.PixelSize), handle.AddrOfPinnedObject(), bytes.Length, stride); }
        finally { handle.Free(); }
        var colored = 0;
        for (var index = 0; index < bytes.Length; index += 4)
            if (Math.Max(bytes[index], Math.Max(bytes[index + 1], bytes[index + 2])) -
                Math.Min(bytes[index], Math.Min(bytes[index + 1], bytes[index + 2])) > 100) colored++;
        Require(colored > bitmap.PixelSize.Width * bitmap.PixelSize.Height / 20, "PDF rendered blank/without colored fixture content");
        // PDF 座標の非対称な赤/青の矩形が、回転後も期待する画面位置へ写ることを確認。
        // 色の存在とサイズだけでは上下反転・回転方向・移動量の誤りを見逃す。
        var red = page == 0 ? (X: 50, Y: 230) : (X: 90, Y: 50);
        var blue = page == 0 ? (X: 170, Y: 70) : (X: 250, Y: 170);
        var redIndex = red.Y * stride + red.X * 4;
        var blueIndex = blue.Y * stride + blue.X * 4;
        Require(bytes[redIndex + 2] > 200 && bytes[redIndex] < 40 && bytes[redIndex + 1] < 40,
            $"PDF page {page + 1} red marker orientation/position incorrect");
        Require(bytes[blueIndex] > 200 && bytes[blueIndex + 1] < 40 && bytes[blueIndex + 2] < 40,
            $"PDF page {page + 1} blue marker orientation/position incorrect");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
