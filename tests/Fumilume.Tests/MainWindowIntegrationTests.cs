using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Indentation.CSharp;
using Fumilume.Models;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

/// <summary>
/// メインウィンドウを実際に組み立てて動かす検証。
///
/// XAML の読み込み失敗、テンプレートの取り違え、設定タブとエディタの出し分けは
/// ビューモデル単体のテストでは捕まらないため、ここで実ツリーを作って確かめる。
/// </summary>
[Collection(HeadlessAppCollection.Name)]
public sealed class MainWindowIntegrationTests(HeadlessAppFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FileDropOpensUnknownAndExtensionlessFilesWithoutInsertingText(bool enableTextDragDrop) => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var editor = scope.Window.FindControl<TextEditor>("Editor")!;
        scope.ViewModel.Options.EnableTextDragDrop = enableTextDragDrop;
        Assert.True(DragDrop.GetAllowDrop(editor.TextArea));
        var original = scope.ViewModel.SelectedDocument!;
        using var data = new DataTransfer();
        foreach (var name in new[] { "sample.unlisted-format", "no-extension" })
        {
            var path = Path.Combine(scope.StoragePath, name);
            File.WriteAllText(path, "ドロップした文書");
            var file = scope.Window.StorageProvider.TryGetFileFromPathAsync(new Uri(path)).GetAwaiter().GetResult();
            data.Add(DataTransferItem.CreateFile(Assert.IsAssignableFrom<Avalonia.Platform.Storage.IStorageFile>(file)));
        }
        data.Add(DataTransferItem.CreateText("挿入しない文字列"));

        Assert.True(DragDrop.GetAllowDrop(scope.Window));
        var over = new DragEventArgs(DragDrop.DragOverEvent, data, editor.TextArea, default, KeyModifiers.None)
        {
            DragEffects = DragDropEffects.Copy | DragDropEffects.Move,
        };
        editor.TextArea.RaiseEvent(over);
        Assert.True(over.Handled);
        Assert.Equal(DragDropEffects.Copy, over.DragEffects);

        void DropFiles()
        {
            var drop = new DragEventArgs(DragDrop.DropEvent, data, editor.TextArea, default, KeyModifiers.None)
            {
                DragEffects = DragDropEffects.Copy,
            };
            editor.TextArea.RaiseEvent(drop);
            Assert.True(drop.Handled);
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (scope.ViewModel.SelectedDocument?.FilePath != Path.Combine(scope.StoragePath, "no-extension")
                && timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Yield();
            }
        }

        DropFiles();
        Assert.Equal(2, scope.ViewModel.Documents.Count(document => document.FilePath is not null));
        Assert.All(scope.ViewModel.Documents.Where(document => document.FilePath is not null),
            document => Assert.Equal("ドロップした文書", document.Text));
        Assert.Equal(string.Empty, original.Text);
        var opened = scope.ViewModel.SelectedDocument;
        scope.ViewModel.SelectedTab = original;
        DropFiles();
        Assert.Same(opened, scope.ViewModel.SelectedDocument);
        Assert.Equal(2, scope.ViewModel.Documents.Count(document => document.FilePath is not null));
    });

    [Fact]
    public void FileDropRejectsFoldersAndLeavesPlainTextToTheEditor() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        using var folders = new DataTransfer();
        var folder = scope.Window.StorageProvider.TryGetFolderFromPathAsync(new Uri(scope.StoragePath)).GetAwaiter().GetResult();
        folders.Add(DataTransferItem.CreateFile(Assert.IsAssignableFrom<Avalonia.Platform.Storage.IStorageFolder>(folder)));
        foreach (var routedEvent in new[] { DragDrop.DragEnterEvent, DragDrop.DragOverEvent, DragDrop.DropEvent })
        {
            var args = new DragEventArgs(routedEvent, folders, scope.Window, default, KeyModifiers.None)
            {
                DragEffects = DragDropEffects.Copy,
            };
            scope.Window.RaiseEvent(args);
            Assert.True(args.Handled);
            Assert.Equal(DragDropEffects.None, args.DragEffects);
        }

        using var text = new DataTransfer();
        text.Add(DataTransferItem.CreateText("文字列"));
        var textDrop = new DragEventArgs(DragDrop.DropEvent, text, scope.Window, default, KeyModifiers.None);
        scope.Window.RaiseEvent(textDrop);
        Assert.False(textDrop.Handled);
        Assert.Single(scope.ViewModel.Documents);
    });

    [Fact]
    public void WindowLoadsWithADocumentTabAndAnEditor() => fixture.Run(() =>
    {
        using var scope = new WindowScope();

        Assert.NotNull(scope.Window.FindControl<TextEditor>("Editor"));
        Assert.Single(scope.ViewModel.Documents);
        Assert.True(scope.ViewModel.IsDocumentSelected);
        Assert.False(scope.ViewModel.IsSettingsSelected);
    });

    [Fact]
    public void NewOpenAndSettingsActionsAreInTheDocumentToolbar() => fixture.Run(() =>
    {
        using var scope = new WindowScope();

        var toolbar = scope.Window.FindControl<Grid>("DocumentToolbar");
        var sidePanel = scope.Window.FindControl<Grid>("SidePanelRoot");
        var open = scope.Window.FindControl<Button>("OpenToolbarButton");
        var create = scope.Window.FindControl<Button>("NewDocumentToolbarButton");
        var settings = scope.Window.FindControl<Button>("SettingsToolbarButton");

        Assert.NotNull(toolbar);
        Assert.NotNull(sidePanel);
        Assert.NotNull(open);
        Assert.NotNull(create);
        Assert.Contains(create, toolbar.GetVisualDescendants());
        Assert.DoesNotContain(create, sidePanel.GetVisualDescendants());
        Assert.Same(scope.ViewModel.NewDocumentCommand, create.Command);
        Assert.Same(create.Parent, open.Parent);
        Assert.True(create.Bounds.X < open.Bounds.X);
        Assert.NotNull(settings);
        Assert.Contains(open, toolbar.GetVisualDescendants());
        Assert.Contains(settings, toolbar.GetVisualDescendants());
        Assert.DoesNotContain(open, sidePanel.GetVisualDescendants());
        Assert.DoesNotContain(settings, sidePanel.GetVisualDescendants());
        Assert.Same(scope.ViewModel.OpenCommand, open.Command);
        Assert.Same(scope.ViewModel.OpenSettingsCommand, settings.Command);
    });

    [Fact]
    public void PdfFitsTheViewportAndExposesFitButtonsBesideZoom() => fixture.Run(() =>
    {
        using var scope = new WindowScope(width: 1100, height: 750);
        using var pdf = new PdfDocumentViewModel(@"C:\tmp\guide.pdf",
            new PdfDocumentViewModelTests.StubPdfRenderer(2), _ => Task.CompletedTask);
        scope.ViewModel.Tabs.Add(pdf);
        scope.ViewModel.SelectedTab = pdf;
        Dispatcher.UIThread.RunJobs();

        var viewer = scope.Window.FindControl<ScrollViewer>("PdfScrollViewer")!;
        var image = scope.Window.FindControl<Image>("PdfPageImage")!;
        var zoom = scope.Window.FindControl<Button>("PdfZoomButton")!;
        var height = scope.Window.FindControl<Button>("PdfFitHeightButton")!;
        var width = scope.Window.FindControl<Button>("PdfFitWidthButton")!;
        Assert.True(viewer.IsEffectivelyVisible);
        Assert.Same(pdf.FitHeightCommand, height.Command);
        Assert.Same(pdf.FitWidthCommand, width.Command);
        Assert.Same(zoom.Parent, height.Parent);
        Assert.Same(zoom.Parent, width.Parent);
        Assert.True(height.Bounds.X > zoom.Bounds.X);
        Assert.True(width.Bounds.X > height.Bounds.X);
        Assert.Equal(PdfZoomMode.FitWidth, pdf.ZoomMode);
        Assert.InRange(Math.Abs(image.Bounds.Width + 48 - viewer.Viewport.Width), 0, 1);
        Assert.InRange(viewer.Extent.Width - viewer.Viewport.Width, 0, 1);

        var originalWidth = image.Bounds.Width;
        scope.Window.Width = 950;
        Dispatcher.UIThread.RunJobs();
        Assert.True(image.Bounds.Width < originalWidth);
        Assert.InRange(Math.Abs(image.Bounds.Width + 48 - viewer.Viewport.Width), 0, 1);

        height.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PdfZoomMode.FitHeight, pdf.ZoomMode);
        Assert.InRange(Math.Abs(image.Bounds.Height + 48 - viewer.Viewport.Height), 0, 1);
        scope.Window.Height = 650;
        Dispatcher.UIThread.RunJobs();
        Assert.InRange(Math.Abs(image.Bounds.Height + 48 - viewer.Viewport.Height), 0, 1);

        var state = scope.ViewModel.CaptureSession().Tabs.Single(tab => tab.Kind == SessionTabKinds.Pdf);
        Assert.Equal("FitHeight", state.PdfZoomMode);
        Assert.True(SessionStateService.Save(new SessionState { Tabs = [state] }));
        Assert.Equal("FitHeight", Assert.Single(SessionStateService.Load().Tabs).PdfZoomMode);
        scope.ViewModel.OpenCommandPaletteCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(scope.ViewModel.CommandPaletteResults, entry => entry.Title == "PDF を幅に合わせる");
    });

    [Fact]
    public void SwitchingPdfTabsRecalculatesFitWithoutAWindowResize() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        using var first = new PdfDocumentViewModel(@"C:\tmp\first.pdf",
            new PdfDocumentViewModelTests.StubPdfRenderer(2), _ => Task.CompletedTask);
        using var second = new PdfDocumentViewModel(@"C:\tmp\second.pdf",
            new PdfDocumentViewModelTests.StubPdfRenderer(2), _ => Task.CompletedTask);
        second.CurrentPage = 2;
        scope.ViewModel.Tabs.Add(first);
        scope.ViewModel.Tabs.Add(second);
        scope.ViewModel.SelectedTab = first;
        Dispatcher.UIThread.RunJobs();
        scope.ViewModel.SelectedTab = second;
        Dispatcher.UIThread.RunJobs();

        var viewer = scope.Window.FindControl<ScrollViewer>("PdfScrollViewer")!;
        var image = scope.Window.FindControl<Image>("PdfPageImage")!;
        Assert.Same(second.PageImage, image.Source);
        Assert.InRange(Math.Abs(image.Bounds.Width + 48 - viewer.Viewport.Width), 0, 1);
        Assert.Equal(0.75, second.PageHeight / second.PageWidth, precision: 10);
    });

    [Fact]
    public void OpeningSettingsShowsTheSettingsViewAndHidesTheEditor() => fixture.Run(() =>
    {
        using var scope = new WindowScope();

        scope.ViewModel.OpenSettingsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var settingsView = scope.Window.GetVisualDescendants().OfType<SettingsView>().SingleOrDefault();
        Assert.NotNull(settingsView);
        Assert.True(settingsView.IsEffectivelyVisible);
        Assert.IsType<SettingsTabViewModel>(settingsView.DataContext);

        // エディタ側は行ごと隠れる（設定タブの裏で編集領域が生きていると誤操作の元になる）。
        var editor = scope.Window.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);
        Assert.False(editor.IsEffectivelyVisible);
    });

    [Fact]
    public void SwitchingBackToADocumentRestoresTheEditor() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();

        scope.ViewModel.OpenSettingsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        scope.ViewModel.SelectedTab = document;
        Dispatcher.UIThread.RunJobs();

        var editor = scope.Window.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);
        Assert.True(editor.IsEffectivelyVisible);
        Assert.Same(document.EditorDocument, editor.Document);
    });

    [Fact]
    public void TabListShowsBothDocumentAndSettingsEntries() => fixture.Run(() =>
    {
        using var scope = new WindowScope();

        scope.ViewModel.OpenSettingsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var tabList = scope.Window.GetVisualDescendants()
            .OfType<ListBox>()
            .Single(list => list.Classes.Contains("verticaltabs"));

        Assert.Equal(2, tabList.ItemCount);
    });

    [Fact]
    public void TabRowsShowAPinActionAndPinnedState() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        Dispatcher.UIThread.RunJobs();

        var document = scope.ViewModel.SelectedDocument!;
        var pin = scope.Window.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Classes.Contains("tabpin"));

        Assert.Same(document.TogglePinCommand, pin.Command);
        Assert.True(pin.IsEffectivelyVisible);
        Assert.Equal("タブをピン留め", ToolTip.GetTip(pin));

        document.TogglePinCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("pinned", pin.Classes);
        Assert.Equal("ピン留めを解除", ToolTip.GetTip(pin));
    });

    [Fact]
    public void EditorOptionsFollowTheSettings() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var editor = scope.Window.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);

        scope.ViewModel.Options.ShowLineNumbers = false;
        scope.ViewModel.Options.WordWrap = true;
        scope.ViewModel.Options.UiFontFamily = "Arial";
        scope.ViewModel.Options.UiFontSize = 17;
        scope.ViewModel.Options.EditorFontSize = 20;
        scope.ViewModel.Options.EditorFontFamily = "'Cascadia Code', Consolas, monospace";
        scope.ViewModel.Options.IndentationSize = 8;
        Dispatcher.UIThread.RunJobs();

        Assert.False(editor.ShowLineNumbers);
        Assert.True(editor.WordWrap);
        Assert.Contains("Arial", scope.Window.FontFamily.ToString(), StringComparison.Ordinal);
        Assert.Equal(17, scope.Window.FontSize);
        Assert.Equal(20, editor.FontSize);
        Assert.Contains("Cascadia Code", editor.FontFamily.ToString(), StringComparison.Ordinal);
        Assert.Equal(8, editor.Options.IndentationSize);
    });

    [Fact]
    public void FontPickersPreviewEachInstalledFont() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        scope.ViewModel.OpenSettingsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var settings = scope.Window.GetVisualDescendants().OfType<SettingsView>().Single();
        var uiPicker = settings.FindControl<ComboBox>("UiFontFamilyPicker");
        var editorPicker = settings.FindControl<ComboBox>("EditorFontFamilyPicker");
        Assert.NotNull(uiPicker);
        Assert.NotNull(editorPicker);
        Assert.True(uiPicker.IsEditable);
        Assert.True(editorPicker.IsEditable);

        var font = Assert.IsType<FontFamily>(uiPicker.ItemsSource!.Cast<object>().First());
        var preview = Assert.IsType<TextBlock>(uiPicker.ItemTemplate!.Build(font));
        preview.DataContext = font;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(font.Name, preview.Text);
        Assert.Equal(font, preview.FontFamily);

        var editorFonts = editorPicker.ItemsSource!.Cast<FontFamily>().ToArray();
        Assert.NotEmpty(editorFonts);
        Assert.NotSame(uiPicker.ItemsSource, editorPicker.ItemsSource);
        Assert.All(editorFonts, fontFamily => Assert.True(SettingsView.IsMonospaced(fontFamily)));

        uiPicker.Text = "Arial";
        editorPicker.Text = "Consolas";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Arial", scope.ViewModel.Options.UiFontFamily);
        Assert.Equal("Consolas", scope.ViewModel.Options.EditorFontFamily);
    });

    [Fact]
    public void CurrentLineHighlightUsesABlueThemeColor() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var editor = scope.Window.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);

        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(editor.TextArea.TextView.CurrentLineBackground);
        Assert.True(brush.Color.B > brush.Color.G);
        Assert.True(brush.Color.B > brush.Color.R);
    });

    [Fact]
    public void HyperlinksHaveReadableThemeColors() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var editor = scope.Window.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);

        AssertLinkContrast(ThemeVariant.Light, Color.Parse("#FBFBFC"), 4.5);
        AssertLinkContrast(ThemeVariant.Dark, Color.Parse("#242528"), 4.5);

        void AssertLinkContrast(ThemeVariant theme, Color background, double minimumRatio)
        {
            scope.Window.RequestedThemeVariant = theme;
            Dispatcher.UIThread.RunJobs();

            var link = Assert.IsAssignableFrom<ISolidColorBrush>(
                editor.TextArea.TextView.LinkTextForegroundBrush);
            Assert.True(editor.TextArea.TextView.LinkTextUnderline);
            Assert.True(
                ContrastRatio(link.Color, background) >= minimumRatio,
                $"{theme} のリンク色 {link.Color} は背景 {background} に対して十分なコントラストがありません。");
        }
    });

    /// <summary>
    /// 設定画面に並ぶ項目は、すべて実際にエディタへ効かなければならない。
    /// 効かないチェックボックスは「設定できたつもり」を作るぶん、無いより悪い。
    /// </summary>
    [Fact]
    public void EveryEditorOptionReachesTheEditor() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var editor = scope.Window.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);
        var options = scope.ViewModel.Options;

        options.InheritWordWrapIndentation = false;
        options.ShowColumnRuler = true;
        options.ColumnRulerPosition = 100;
        options.LineHeightFactor = 1.4;
        options.ShowSpaces = true;
        options.ShowTabs = true;
        options.ShowEndOfLine = true;
        options.ShowControlCharacters = false;
        options.AcceptsTab = false;
        options.EnableRectangularSelection = false;
        options.EnableVirtualSpace = true;
        options.EnableTextDragDrop = false;
        options.CutCopyWholeLine = false;
        options.AllowScrollBelowDocument = false;
        options.AllowToggleOverstrikeMode = false;
        options.HideCursorWhileTyping = false;
        options.EnableHyperlinks = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(editor.Options.InheritWordWrapIndentation);
        Assert.True(editor.Options.ShowColumnRulers);
        Assert.Equal([100], editor.Options.ColumnRulerPositions);
        Assert.Equal(1.4, editor.Options.LineHeightFactor);
        Assert.True(editor.Options.ShowSpaces);
        Assert.True(editor.Options.ShowTabs);
        Assert.True(editor.Options.ShowEndOfLine);
        Assert.False(editor.Options.ShowBoxForControlCharacters);
        Assert.False(editor.Options.AcceptsTab);
        Assert.False(editor.Options.EnableRectangularSelection);
        Assert.True(editor.Options.EnableVirtualSpace);
        Assert.False(editor.Options.EnableTextDragDrop);
        Assert.False(editor.Options.CutCopyWholeLine);
        Assert.False(editor.Options.AllowScrollBelowDocument);
        Assert.False(editor.Options.AllowToggleOverstrikeMode);
        Assert.False(editor.Options.HideCursorWhileTyping);
        Assert.False(editor.Options.EnableHyperlinks);
    });

    [Fact]
    public void TurningAcrylicOffShowsTheOpaqueFallbackLayer() => fixture.Run(() =>
    {
        using var scope = new WindowScope();

        scope.ViewModel.Options.UseAcrylic = false;
        Dispatcher.UIThread.RunJobs();

        var fallback = scope.Window.FindControl<Border>("FallbackLayer");
        var acrylic = scope.Window.FindControl<ExperimentalAcrylicBorder>("AcrylicLayer");
        Assert.NotNull(fallback);
        Assert.NotNull(acrylic);
        Assert.True(fallback.IsVisible);
        Assert.False(acrylic.IsVisible);
    });

    /// <summary>
    /// sakura の「変換」「編集」メニュー相当。ツールバーから右クリックメニューへ移したので、
    /// エディタの <c>ContextFlyout</c> にカタログ全体が載っているかを実ツリーで確かめる。
    /// データバインドではなくコードビハインドで組んでいる（PublishAot でリフレクション束縛が落ちるため）。
    /// </summary>
    [Fact]
    public void EditorContextMenuIsBuiltFromTheCatalog() => fixture.Run(() =>
    {
        using var scope = new WindowScope();

        var editor = scope.Window.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);
        var flyout = Assert.IsType<MenuFlyout>(editor.ContextFlyout);

        // 切り取り・コピー・貼り付け・すべて選択は葉なので、子を持つ項目がカタログの区分に対応する。
        var parents = flyout.Items.OfType<MenuItem>().Where(item => item.Items.Count > 0).ToList();
        Assert.Equal(
            EditorCommandCatalog.Groups.Select(group => group.Category),
            parents.Select(item => (string?)item.Header));

        var leaves = new List<MenuItem>();
        foreach (var parent in parents)
        {
            var expected = EditorCommandCatalog.Groups.Single(group => group.Category == (string?)parent.Header);
            var items = parent.Items.OfType<MenuItem>().ToList();
            Assert.Equal(expected.Commands.Select(command => command.Title), items.Select(item => item.Header));
            leaves.AddRange(items);
        }

        // 区分を合わせるとカタログ全体を覆う（どこからも呼べないコマンドを残さない）。
        Assert.Equal(EditorCommandCatalog.All.Count, leaves.Count);
        Assert.All(leaves, leaf =>
        {
            Assert.NotNull(leaf.Command);
            Assert.IsType<EditorCommandId>(leaf.CommandParameter);
        });
    });

    /// <summary>区分アイコンが 1 つでも欠けると、その区分の見出しからアイコンが消える。</summary>
    [Fact]
    public void EveryCategoryHasAnIcon() => fixture.Run(() =>
    {
        Assert.Equal(
            EditorCommandCatalog.Groups.Select(group => group.Category).Order(),
            EditorCommandCatalog.CategoryIcons.Select(icon => icon.Category).Order());
    });

    /// <summary>
    /// 左サイドの 3 つの面。押した面だけが active になり、その面の一覧だけが見える。
    /// XAML 側は Classes.active と x:Static の列挙値で組んでいるので、実ツリーで確かめる。
    /// </summary>
    [Fact]
    public void TheSidePanelSwitchShowsOnePanelAtATime() => fixture.Run(() =>
    {
        using var scope = new WindowScope();

        var switches = scope.Window.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Classes.Contains("panelswitch"))
            .ToList();
        Assert.Equal(3, switches.Count);
        Assert.Single(switches, button => button.Classes.Contains("active"));

        var outline = scope.Window.FindControl<ListBox>("OutlineList");
        var bookmarks = scope.Window.FindControl<ListBox>("BookmarkList");
        Assert.NotNull(outline);
        Assert.NotNull(bookmarks);
        Assert.False(outline.IsEffectivelyVisible);

        switches[1].Command?.Execute(switches[1].CommandParameter);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SidePanelKind.Outline, scope.ViewModel.SidePanel);
        Assert.True(outline.IsEffectivelyVisible);
        Assert.False(bookmarks.IsEffectivelyVisible);
        Assert.Equal([false, true, false], switches.Select(button => button.Classes.Contains("active")));
    });

    /// <summary>
    /// キー割り当ての引数は機能番号でなければならない。<c>CommandParameter</c> の型は object なので、
    /// XAML に <c>"ToUpper"</c> と書くと文字列のまま渡り、<c>RelayCommand&lt;EditorCommandId&gt;</c> の
    /// キャストで落ちる（押した瞬間まで気付けない）。
    /// </summary>
    [Fact]
    public void EditorCommandKeyBindingsCarryCommandIds() => fixture.Run(() =>
    {
        using var scope = new WindowScope();

        var bindings = scope.Window.KeyBindings
            .Where(binding => ReferenceEquals(binding.Command, scope.ViewModel.RunEditorCommandCommand))
            .ToList();

        Assert.NotEmpty(bindings);
        Assert.All(bindings, binding => Assert.IsType<EditorCommandId>(binding.CommandParameter));
    });

    [Fact]
    public void CSharpEnterUsesStructuralIndentation() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        scope.ViewModel.Options.ConvertTabsToSpaces = true;
        scope.ViewModel.Options.IndentationSize = 2;
        var document = scope.ViewModel.SelectedDocument!;
        document.Load(
            @"C:\tmp\Sample.cs",
            new TextDocumentContent("class Sample\n{", DocumentEncoding.Utf8, DocumentNewLines.Lf));
        var editor = scope.Window.FindControl<TextEditor>("Editor")!;
        editor.CaretOffset = editor.Document.TextLength;
        editor.Focus();
        Dispatcher.UIThread.RunJobs();

        editor.TextArea.PerformTextInput("\n");

        Assert.Equal("class Sample\n{\n  ", document.Text);
        Assert.IsType<CSharpIndentationStrategy>(editor.TextArea.IndentationStrategy);
    });

    [Fact]
    public void JsonEnterUsesBracketDepthAndRefreshesAfterSettingsChange() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        scope.ViewModel.Options.ConvertTabsToSpaces = true;
        scope.ViewModel.Options.IndentationSize = 2;
        var document = scope.ViewModel.SelectedDocument!;
        document.Load(
            @"C:\tmp\data.json",
            new TextDocumentContent("{\n  \"items\": [", DocumentEncoding.Utf8, DocumentNewLines.Lf));
        var editor = scope.Window.FindControl<TextEditor>("Editor")!;
        editor.CaretOffset = editor.Document.TextLength;
        editor.Focus();
        Dispatcher.UIThread.RunJobs();

        editor.TextArea.PerformTextInput("\n");

        Assert.Equal("{\n  \"items\": [\n    ", document.Text);
        Assert.IsType<JsonIndentationStrategy>(editor.TextArea.IndentationStrategy);
    });

    [Fact]
    public void BracketPairRendererIsConnectedToTheLiveEditor() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.SelectedDocument!;
        document.Load(
            @"C:\tmp\Sample.cs",
            new TextDocumentContent("{ value }", DocumentEncoding.Utf8, DocumentNewLines.Lf));
        var editor = scope.Window.FindControl<TextEditor>("Editor")!;
        editor.CaretOffset = 1;
        editor.Focus();
        scope.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var renderer = editor.TextArea.TextView.BackgroundRenderers
            .Single(item => item.GetType().Name.Contains("BracketPairRenderer", StringComparison.Ordinal));
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            renderer.Draw(editor.TextArea.TextView, context);
        }

        Assert.NotEmpty(drawing.Children);
        Assert.Contains(
            editor.TextArea.TextView.LineTransformers,
            item => item.GetType().Name.Contains("BracketColorizer", StringComparison.Ordinal));
    });

    [Fact]
    public void EditingAnEarlierBracketRecolorsAlreadyVisibleFollowingLines() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        scope.Window.RequestedThemeVariant = ThemeVariant.Light;
        var document = scope.ViewModel.SelectedDocument!;
        document.Load(
            @"C:\tmp\data.json",
            new TextDocumentContent("[\n  (value)\n]", DocumentEncoding.Utf8, DocumentNewLines.Lf));
        var editor = scope.Window.FindControl<TextEditor>("Editor")!;
        scope.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Color.Parse("#4078F2"), ReadBracketColor(editor, document.Text.IndexOf('(')));

        editor.Document.Insert(0, "{");
        Dispatcher.UIThread.RunJobs();
        scope.Window.UpdateLayout();

        Assert.Equal(Color.Parse("#C18401"), ReadBracketColor(editor, document.Text.IndexOf('(')));
    });

    [Fact]
    public void ControlKControlDFormatsTheWholeDocument() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        const string source = "class Sample\n{\nvoid Run()\n{\n}\n}";
        document.Load(
            @"C:\tmp\Sample.cs",
            new TextDocumentContent(source, DocumentEncoding.Utf8, DocumentNewLines.Lf));
        var editor = scope.Window.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);
        editor.Focus();
        Dispatcher.UIThread.RunJobs();

        PressKey(editor, Key.K, KeyModifiers.Control);

        Assert.Equal(source, document.Text);
        Assert.Contains("Ctrl+D", scope.ViewModel.StatusMessage);

        PressKey(editor, Key.D, KeyModifiers.Control);

        Assert.Equal("class Sample\n{\n\tvoid Run()\n\t{\n\t}\n}", document.Text);
        Assert.Equal("文書全体を書式整形しました", scope.ViewModel.StatusMessage);

        document.EditorDocument.UndoStack.Undo();
        Assert.Equal(source, document.Text);
    });

    [Fact]
    public void CommandPaletteOpensFiltersAndRuns() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.Text = "abc";
        document.SelectionStart = 0;
        document.SelectionLength = 3;

        scope.ViewModel.OpenCommandPaletteCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var list = scope.Window.FindControl<ListBox>("CommandPaletteList");
        Assert.NotNull(list);
        Assert.True(list.IsEffectivelyVisible);

        // パレットはカタログの 50 件に加えて、ファイル操作などのワークスペース操作も載せる。
        Assert.True(scope.ViewModel.CommandPaletteResults.Count > EditorCommandCatalog.All.Count);
        Assert.All(
            EditorCommandCatalog.All.Where(command => command.Id is not (EditorCommandId.AppendCsvRow or EditorCommandId.AppendCsvColumn)),
            command => Assert.Contains(scope.ViewModel.CommandPaletteResults, entry => entry.Title == command.Title));

        scope.ViewModel.CommandPaletteQuery = "大文字";
        Dispatcher.UIThread.RunJobs();
        Assert.All(scope.ViewModel.CommandPaletteResults, command => Assert.Contains("大文字", command.Title));

        scope.ViewModel.SelectedPaletteCommand =
            scope.ViewModel.CommandPaletteResults.Single(command => command.Title == "大文字");
        scope.ViewModel.RunSelectedPaletteCommandCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(scope.ViewModel.IsCommandPaletteOpen);
        Assert.Equal("ABC", document.Text);
    });

    /// <summary>エディタ側の選択が文書へ渡り、コマンドの対象になる。</summary>
    [Fact]
    public void EditorSelectionReachesTheDocument() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.Text = "hello world";

        var editor = scope.Window.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);
        editor.Select(0, 5);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, document.SelectionStart);
        Assert.Equal(5, document.SelectionLength);

        scope.ViewModel.RunEditorCommandCommand.Execute(EditorCommandId.ToUpper);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("HELLO world", document.Text);
        // 変換後も同じ範囲が選ばれたままで、続けて別の変換を掛けられる。
        Assert.Equal(5, editor.SelectionLength);
    });

    /// <summary>ウィンドウ 1 つ分の後始末（設定の隔離と、開いたウィンドウを閉じるところまで）。</summary>
    /// <summary>
    /// タブの厚みは項目テンプレートの Grid と ListBoxItem の ControlTheme の 2 箇所に効く。
    /// 片方だけ追従しても見た目は途中まで変わるので、実際に並んだ行の高さで確かめる。
    /// </summary>
    [Fact]
    public void TabHeightSettingReachesTheTabList() => fixture.Run(() =>
    {
        using var scope = new WindowScope();

        scope.ViewModel.Options.TabHeight = 60;
        Dispatcher.UIThread.RunJobs();
        scope.Window.UpdateLayout();

        var item = scope.Window.GetVisualDescendants().OfType<ListBoxItem>().First();
        Assert.Equal(60, item.MinHeight);
        Assert.Equal(60, item.Bounds.Height);
    });

    /// <summary>厚みは閉じるボタンが潰れない範囲へ丸める。</summary>
    [Fact]
    public void TabHeightIsClampedToTheAllowedRange() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var options = scope.ViewModel.Options;

        options.TabHeight = 4;
        Assert.Equal(AppSettingsDefaults.MinimumTabHeight, options.TabHeight);

        options.TabHeight = 400;
        Assert.Equal(AppSettingsDefaults.MaximumTabHeight, options.TabHeight);
    });

    [Fact]
    public void BundledFontsAreTheDefaultsAndAppearInThePickers() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var editor = scope.Window.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);

        Assert.Equal(AppFontFamilies.IbmPlexSansJpName, scope.ViewModel.Options.UiFontFamily);
        Assert.Equal(AppFontFamilies.UdevGothicJpDocName, scope.ViewModel.Options.EditorFontFamily);
        Assert.Contains(AppFontFamilies.IbmPlexSansJpName, scope.Window.FontFamily.ToString(), StringComparison.Ordinal);
        Assert.Contains(AppFontFamilies.UdevGothicJpDocName, editor.FontFamily.ToString(), StringComparison.Ordinal);

        scope.ViewModel.OpenSettingsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var settings = scope.Window.GetVisualDescendants().OfType<SettingsView>().Single();
        var uiFonts = settings.FontFamilies.Select(font => font.Name).ToArray();
        var editorFonts = settings.EditorFontFamilies.Select(font => font.Name).ToArray();
        Assert.Contains(AppFontFamilies.IbmPlexSansJpName, uiFonts);
        Assert.Contains(AppFontFamilies.UdevGothicJpDocName, editorFonts);
    });

    [Fact]
    public void SidePanelCanResizeOnlyInsideItsSafeRange() => fixture.Run(() =>
    {
        using var scope = new WindowScope(width: 720, height: 460);
        var grid = scope.Window.FindControl<Grid>("WorkspaceGrid");
        Assert.NotNull(grid);
        var sidePanel = grid.ColumnDefinitions[0];

        sidePanel.Width = new GridLength(1);
        scope.Window.UpdateLayout();
        Assert.Equal(AppSettingsDefaults.MinimumSidePanelWidth, sidePanel.ActualWidth, precision: 1);

        sidePanel.Width = new GridLength(1000);
        scope.Window.UpdateLayout();
        Assert.True(sidePanel.ActualWidth <= AppSettingsDefaults.MaximumSidePanelWidth);

        Assert.Equal(720, scope.Window.Bounds.Width);
        Assert.Equal(460, scope.Window.Bounds.Height);
        Assert.True(sidePanel.ActualWidth >= AppSettingsDefaults.MinimumSidePanelWidth);
        Assert.True(grid.ColumnDefinitions[2].ActualWidth >= 480);
    });

    [Fact]
    public void CsvPreviewDisablesHiddenTextCommandsAndRefreshesPalette() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        const string source = "a,\"first\nsecond\"\nx,y";
        document.Load(@"C:\tmp\commands.csv", new TextDocumentContent(source, DocumentEncoding.Utf8, "\n"));
        document.CaretIndex = source.IndexOf("second", StringComparison.Ordinal);
        scope.ViewModel.OpenCommandPaletteCommand.Execute(null);
        var deleteTitle = EditorCommandCatalog.TitleOf(EditorCommandId.DeleteLine);
        Assert.Contains(scope.ViewModel.CommandPaletteResults, entry => entry.Title == deleteTitle);
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(scope.ViewModel.CommandPaletteResults, entry => entry.Title == deleteTitle);
        scope.ViewModel.IsCommandPaletteOpen = false;
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        FindCsvCell(preview, "A1").Focus();
        scope.Window.KeyPress(Key.E, RawInputModifiers.Control | RawInputModifiers.Shift, default, null);
        scope.Window.KeyRelease(Key.E, RawInputModifiers.Control | RawInputModifiers.Shift, default, null);
        scope.Window.KeyPress(Key.I, RawInputModifiers.Control, default, null);
        scope.Window.KeyRelease(Key.I, RawInputModifiers.Control, default, null);
        scope.ViewModel.ToggleMacroRecordingCommand.Execute(null);
        foreach (var command in new[] { EditorCommandId.DeleteLine, EditorCommandId.DuplicateLine, EditorCommandId.ToUpper })
        {
            Assert.False(scope.ViewModel.RunEditorCommandCommand.CanExecute(command));
            scope.ViewModel.RunEditorCommandCommand.ExecuteAsync(command).GetAwaiter().GetResult();
        }
        Assert.Equal(0, scope.ViewModel.RecordedStepCount);
        Assert.Equal(source, document.Text);
        Assert.True(scope.ViewModel.RunEditorCommandCommand.CanExecute(EditorCommandId.AppendCsvRow));
        scope.ViewModel.OpenCommandPaletteCommand.Execute(null);
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Assert.True(scope.ViewModel.RunEditorCommandCommand.CanExecute(EditorCommandId.DeleteLine));
        Assert.Contains(scope.ViewModel.CommandPaletteResults, entry => entry.Title == deleteTitle);
    });

    [Fact]
    public void LargeCsvAppendKeepsTheUiResponsiveAndAppliesOneUndoOperation() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        var source = string.Join('\n', Enumerable.Repeat("a,b", 40_000));
        document.Load(@"C:\tmp\large-append.csv", new TextDocumentContent(source, DocumentEncoding.Utf8, "\n"));
        var task = scope.ViewModel.RunEditorCommandCommand.ExecuteAsync(EditorCommandId.AppendCsvRow);
        Assert.False(task.IsCompleted);
        var uiResponded = false;
        Dispatcher.UIThread.Post(() => uiResponded = true);
        CompleteCsvCommand(task);
        Assert.True(uiResponded);
        Assert.Equal(source + "\n,", document.Text);
        Assert.Contains("表示上限外", scope.ViewModel.StatusMessage);
        document.EditorDocument.UndoStack.Undo();
        Assert.Equal(source, document.Text);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeCsvAppendDoesNotApplyAfterDocumentOrTabChanges(bool switchTab) => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        var source = string.Join('\n', Enumerable.Repeat("a,b", 40_000));
        document.Load(@"C:\tmp\cancel-append.csv", new TextDocumentContent(source, DocumentEncoding.Utf8, "\n"));
        var task = scope.ViewModel.RunEditorCommandCommand.ExecuteAsync(EditorCommandId.AppendCsvColumn);
        Assert.False(task.IsCompleted);
        if (switchTab)
        {
            scope.ViewModel.NewDocumentCommand.Execute(null);
        }
        else
        {
            document.Text = "changed,row";
        }
        CompleteCsvCommand(task);
        Assert.Equal(switchTab ? source : "changed,row", document.Text);
        if (switchTab)
        {
            Assert.Equal(string.Empty, scope.ViewModel.SelectedDocument!.Text);
        }
    });

    [Fact]
    public void CsvMacroStopsAfterAnAsyncAppendIsCanceledByTabSwitch() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        var source = string.Join('\n', Enumerable.Repeat("a,b", 40_000));
        document.Load(@"C:\tmp\cancel-macro.csv", new TextDocumentContent(source, DocumentEncoding.Utf8, "\n"));
        var macro = new KeyboardMacro
        {
            Name = "CSVへの追加",
            Steps =
            [
                new MacroStep { Kind = MacroStepKind.Command, Command = EditorCommandId.AppendCsvRow },
                new MacroStep { Kind = MacroStepKind.InsertText, Text = "unexpected" },
            ],
        };
        var task = scope.ViewModel.RunSavedMacroCommand.ExecuteAsync(macro);
        Assert.False(task.IsCompleted);
        scope.ViewModel.NewDocumentCommand.Execute(null);
        CompleteCsvCommand(task);
        Assert.Equal(source, document.Text);
        Assert.Equal(string.Empty, scope.ViewModel.SelectedDocument!.Text);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CsvMacroWaitAllowsUndoAndKeepsUserEditsSeparate(bool editDuringWait) => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        var source = string.Join('\n', Enumerable.Repeat("a,b", 40_000));
        document.Load(@"C:\tmp\macro-undo.csv", new TextDocumentContent(source, DocumentEncoding.Utf8, "\n"));
        var macro = new KeyboardMacro
        {
            Name = "待機中のUndo",
            Steps =
            [
                new MacroStep { Kind = MacroStepKind.InsertText, Text = "macro" },
                new MacroStep { Kind = MacroStepKind.Command, Command = EditorCommandId.AppendCsvRow },
            ],
        };
        var task = scope.ViewModel.RunSavedMacroCommand.ExecuteAsync(macro);
        Assert.False(task.IsCompleted);
        if (editDuringWait)
        {
            document.InsertText("user");
        }
        scope.ViewModel.UndoCommand.Execute(null);
        CompleteCsvCommand(task);
        Assert.Equal(editDuringWait ? "macro" + source : source, document.Text);
        if (editDuringWait)
        {
            scope.ViewModel.UndoCommand.Execute(null);
            Assert.Equal(source, document.Text);
        }
    });

    [Fact]
    public void CsvMacroSnapshotsRecordingAndKeepsAsyncStepsInOneUndo() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        var source = string.Join('\n', Enumerable.Repeat("a,b", 40_000));
        document.Load(@"C:\tmp\macro-snapshot.csv", new TextDocumentContent(source, DocumentEncoding.Utf8, "\n"));
        scope.ViewModel.ToggleMacroRecordingCommand.Execute(null);
        scope.ViewModel.RecordMacroStep(new MacroStep { Kind = MacroStepKind.Command, Command = EditorCommandId.AppendCsvRow });
        scope.ViewModel.RecordMacroStep(new MacroStep { Kind = MacroStepKind.Command, Command = EditorCommandId.AppendCsvRow });
        scope.ViewModel.ToggleMacroRecordingCommand.Execute(null);
        var task = scope.ViewModel.RunMacroCommand.ExecuteAsync(null);
        Assert.False(task.IsCompleted);
        scope.ViewModel.ToggleMacroRecordingCommand.Execute(null);
        CompleteCsvCommand(task);
        Assert.Equal(source + "\n,\n,", document.Text);
        scope.ViewModel.UndoCommand.Execute(null);
        Assert.Equal(source, document.Text);
    });

    [Fact]
    public void CsvAppendDuringMacroWaitDoesNotOwnTheMacroUndoGroup() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var original = scope.ViewModel.Documents.Single();
        var source = string.Join('\n', Enumerable.Repeat("a,b", 40_000));
        original.Load(@"C:\tmp\macro-owner.csv", new TextDocumentContent(source, DocumentEncoding.Utf8, "\n"));
        var macro = new KeyboardMacro
        {
            Name = "グループ所有者",
            Steps = [new MacroStep { Kind = MacroStepKind.Command, Command = EditorCommandId.AppendCsvRow }],
        };
        var task = scope.ViewModel.RunSavedMacroCommand.ExecuteAsync(macro);
        Assert.False(task.IsCompleted);
        scope.ViewModel.NewDocumentCommand.Execute(null);
        var other = scope.ViewModel.SelectedDocument!;
        other.Load(@"C:\tmp\other-owner.csv", new TextDocumentContent("x,y", DocumentEncoding.Utf8, "\n"));
        CompleteCsvCommand(scope.ViewModel.RunEditorCommandCommand.ExecuteAsync(EditorCommandId.AppendCsvRow));
        CompleteCsvCommand(task);
        Assert.Equal(source, original.Text);
        Assert.Equal("x,y\n,", other.Text);
        scope.ViewModel.UndoCommand.Execute(null);
        Assert.Equal("x,y", other.Text);
    });

    private static void CompleteCsvCommand(Task task)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Yield();
        }
        Assert.True(task.IsCompleted, "CSV操作が完了しませんでした。");
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void CsvAppendAtPreviewLimitPreservesAppendPositionAndExplainsHiddenResult() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        var source = string.Join('\n', Enumerable.Range(0, CsvDocumentParser.MaxPreviewRows).Select(index => $"row-{index}"));
        document.Load(@"C:\tmp\limit.csv", new TextDocumentContent(source, DocumentEncoding.Utf8, "\n"));
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        scope.ViewModel.RunEditorCommandCommand.ExecuteAsync(EditorCommandId.AppendCsvRow).GetAwaiter().GetResult();
        Assert.StartsWith(source + "\n", document.Text);
        Assert.Equal(CsvDocumentParser.MaxPreviewRows + 1, CsvDocumentParser.Parse(document.Text, TestContext.Current.CancellationToken).TotalRowCount);
        Assert.Contains("表示上限外", scope.ViewModel.StatusMessage);
        Assert.Contains("編集へ戻る", scope.ViewModel.StatusMessage);

        source = string.Join(',', Enumerable.Range(0, CsvDocumentParser.MaxPreviewColumns).Select(index => $"col-{index}"));
        document.Text = source;
        scope.ViewModel.RunEditorCommandCommand.ExecuteAsync(EditorCommandId.AppendCsvColumn).GetAwaiter().GetResult();
        Assert.Equal(source + ",", document.Text);
        Assert.Contains("表示上限外", scope.ViewModel.StatusMessage);
        Assert.Contains("編集へ戻る", scope.ViewModel.StatusMessage);
    });

    [Fact]
    public void CsvAppendWithUnterminatedQuoteExplainsTheActualFailure() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.Load(@"C:\tmp\broken.csv", new TextDocumentContent("a,\"broken", DocumentEncoding.Utf8, "\n"));
        scope.ViewModel.RunEditorCommandCommand.ExecuteAsync(EditorCommandId.AppendCsvRow).GetAwaiter().GetResult();
        Assert.Contains("引用符", scope.ViewModel.StatusMessage);
        Assert.Contains("編集へ戻る", scope.ViewModel.StatusMessage);
        Assert.Equal("a,\"broken", document.Text);
    });

    [Fact]
    public void CsvStructureCommandsAreAvailableOnlyForCsvAndCanCreateAnEmptyTable() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        Assert.False(scope.ViewModel.RunEditorCommandCommand.CanExecute(EditorCommandId.AppendCsvRow));
        scope.ViewModel.OpenCommandPaletteCommand.Execute(null);
        Assert.DoesNotContain(scope.ViewModel.CommandPaletteResults, entry => entry.Title == "CSV の末尾に行を追加");
        scope.ViewModel.IsCommandPaletteOpen = false;
        var document = scope.ViewModel.Documents.Single();
        document.MarkSaved(@"C:\tmp\empty.csv");
        Assert.True(scope.ViewModel.RunEditorCommandCommand.CanExecute(EditorCommandId.AppendCsvRow));
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        scope.ViewModel.OpenCommandPaletteCommand.Execute(null);
        scope.ViewModel.SelectedPaletteCommand = scope.ViewModel.CommandPaletteResults.Single(entry => entry.Title == "CSV の末尾に行を追加");
        scope.ViewModel.RunSelectedPaletteCommandCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var table = CsvDocumentParser.Parse(document.Text, TestContext.Current.CancellationToken);
        Assert.Single(table.Rows);
        Assert.Single(table.Rows[0]);
        scope.ViewModel.OpenCommandPaletteCommand.Execute(null);
        scope.ViewModel.SelectedPaletteCommand = scope.ViewModel.CommandPaletteResults.Single(entry => entry.Title == "CSV の末尾に列を追加");
        scope.ViewModel.RunSelectedPaletteCommandCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, CsvDocumentParser.Parse(document.Text, TestContext.Current.CancellationToken).TotalColumnCount);
        scope.ViewModel.UndoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, CsvDocumentParser.Parse(document.Text, TestContext.Current.CancellationToken).TotalColumnCount);
    });

    [Fact]
    public void CsvHeaderSelectionsCopyWholeColumnsAndRows() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.Load(@"C:\tmp\select.csv", new TextDocumentContent("a,b,c\n1,2,3\n4,5,6", DocumentEncoding.Utf8, "\n"));
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        ClickCsvHeader(scope.Window, preview, "CsvColumnHeader:B");
        scope.Window.KeyPress(Key.C, RawInputModifiers.Control, default, null);
        scope.Window.KeyRelease(Key.C, RawInputModifiers.Control, default, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("b\r\n2\r\n5", scope.Window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult());
        ClickCsvHeader(scope.Window, preview, "CsvRowHeader:2");
        ClickCsvHeader(scope.Window, preview, "CsvRowHeader:3", RawInputModifiers.Shift);
        scope.Window.KeyPress(Key.C, RawInputModifiers.Control, default, null);
        scope.Window.KeyRelease(Key.C, RawInputModifiers.Control, default, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("1\t2\t3\r\n4\t5\t6", scope.Window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult());
        Assert.False(document.IsModified);
    });

    private static Control FindCsvHeader(CsvPreview preview, string name)
        => preview.GetVisualDescendants().OfType<Control>().Single(control => Avalonia.Automation.AutomationProperties.GetName(control) == name);

    [Theory]
    [InlineData("CsvAddFirstRowButton")]
    [InlineData("CsvAddFirstColumnButton")]
    public void CsvEmptyPreviewCanCreateAnEditableCell(string buttonName) => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.MarkSaved(@"C:\tmp\empty.csv");
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        var button = preview.GetVisualDescendants().OfType<Button>().Single(control => control.Name == buttonName);
        Assert.True(button.IsEffectivelyVisible);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Single(CsvDocumentParser.Parse(document.Text, TestContext.Current.CancellationToken).Rows);
        var input = OpenCsvCellEditor(preview, "A1");
        input.Text = "value";
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Control });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("value", document.Text);
    });

    [Fact]
    public void CsvCellEditingKeepsTextCopyInsteadOfHeaderCopy() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.Load(@"C:\tmp\copy.csv", new TextDocumentContent("one,two\nthree,four", DocumentEncoding.Utf8, "\n"));
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        ClickCsvHeader(scope.Window, preview, "CsvColumnHeader:A");
        var input = OpenCsvCellEditor(preview, "B1");
        input.Text = "draft";
        input.SelectAll();
        scope.Window.KeyPress(Key.C, RawInputModifiers.Control, default, null);
        scope.Window.KeyRelease(Key.C, RawInputModifiers.Control, default, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("draft", scope.Window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult());
        Assert.Equal("one,two\nthree,four", document.Text);
        Assert.False(document.IsModified);
    });

    [Fact]
    public void CsvHeaderMenusInsertAndDeleteRowsAndColumnsWithUndo() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.Load(@"C:\tmp\structure.csv", new TextDocumentContent("a,b,c\n1,2,3\n4,5,6", DocumentEncoding.Utf8, "\n"));
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        ClickCsvHeader(scope.Window, preview, "CsvRowHeader:2");
        ClickCsvHeader(scope.Window, preview, "CsvRowHeader:3", RawInputModifiers.Shift);
        ClickCsvHeader(scope.Window, preview, "CsvRowHeader:2", button: MouseButton.Right);
        Assert.Equal((true, 1, 2), preview.SelectedHeaders);
        InvokeCsvHeaderMenu(preview, "CsvRowHeader:2", "CsvDeleteSelectionMenuItem");
        Assert.Single(CsvDocumentParser.Parse(document.Text, TestContext.Current.CancellationToken).Rows);
        scope.ViewModel.UndoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("a,b,c\n1,2,3\n4,5,6", document.Text);

        ClickCsvHeader(scope.Window, preview, "CsvRowHeader:1");
        InvokeCsvHeaderMenu(preview, "CsvRowHeader:1", "CsvInsertAfterMenuItem");
        var parsed = CsvDocumentParser.Parse(document.Text, TestContext.Current.CancellationToken);
        Assert.Equal(4, parsed.Rows.Count);
        Assert.All(parsed.Rows[1], value => Assert.Equal(string.Empty, value));
        ClickCsvHeader(scope.Window, preview, "CsvColumnHeader:B");
        InvokeCsvHeaderMenu(preview, "CsvColumnHeader:B", "CsvDeleteSelectionMenuItem");
        parsed = CsvDocumentParser.Parse(document.Text, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "a", "c" }, parsed.Rows[0]);
        Assert.Equal(new[] { "1", "3" }, parsed.Rows[2]);
        ClickCsvHeader(scope.Window, preview, "CsvColumnHeader:A");
        InvokeCsvHeaderMenu(preview, "CsvColumnHeader:A", "CsvInsertBeforeMenuItem");
        parsed = CsvDocumentParser.Parse(document.Text, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "", "a", "c" }, parsed.Rows[0]);
        Assert.True(document.IsModified);
        Assert.Equal(DocumentEncoding.Utf8, document.CreateSaveContent().Encoding);
    });

    private static void InvokeCsvHeaderMenu(CsvPreview preview, string headerName, string itemName)
    {
        var header = FindCsvHeader(preview, headerName);
        var menu = header.ContextMenu;
        Assert.NotNull(menu);
        menu.Open(header);
        Dispatcher.UIThread.RunJobs();
        var item = menu.Items.OfType<MenuItem>().Single(item => item.Name == itemName);
        Assert.True(item.IsEnabled);
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void CsvSelectionCannotDeleteOrCopyFromAChangedSourceBeforeRedraw() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.Load(@"C:\tmp\stale.csv", new TextDocumentContent("a,b\n1,2", DocumentEncoding.Utf8, "\n"));
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        ClickCsvHeader(scope.Window, preview, "CsvRowHeader:1");
        var menu = FindCsvHeader(preview, "CsvRowHeader:1").ContextMenu!;
        var delete = menu.Items.OfType<MenuItem>().Single(item => item.Name == "CsvDeleteSelectionMenuItem");
        var copy = menu.Items.OfType<MenuItem>().Single(item => item.Name == "CsvCopySelectionMenuItem");
        scope.Window.Clipboard!.SetTextAsync("unchanged clipboard").GetAwaiter().GetResult();
        document.Text = "new,source\nkeep,this";
        delete.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        copy.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("new,source\nkeep,this", document.Text);
        Assert.Equal("unchanged clipboard", scope.Window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult());
        Assert.Null(preview.SelectedHeaders);
    });

    [Fact]
    public void CsvHeaderBoundaryDraggingChangesSizeWithoutEditingSource() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.Load(@"C:\tmp\sizes.csv", new TextDocumentContent("a,b\n1,2", DocumentEncoding.Utf8, "\n"));
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        PrepareCsvPointerInput(scope.Window);
        var column = FindCsvHeader(preview, "CsvColumnHeader:A");
        var originalWidth = column.Bounds.Width;
        DragCsvBoundary(scope.Window, column, new Point(originalWidth - 1, column.Bounds.Height / 2), new Vector(60, 0));
        var resizedWidth = FindCsvHeader(preview, "CsvColumnHeader:A").Bounds.Width;
        Assert.True(resizedWidth > originalWidth + 40);
        var row = FindCsvHeader(preview, "CsvRowHeader:1");
        var originalHeight = row.Bounds.Height;
        DragCsvBoundary(scope.Window, row, new Point(row.Bounds.Width / 2, originalHeight - 1), new Vector(0, 35));
        Assert.True(FindCsvHeader(preview, "CsvRowHeader:1").Bounds.Height > originalHeight + 20);
        Assert.False(document.IsModified);
        Assert.Equal("a,b\n1,2", document.Text);
        scope.ViewModel.NewDocumentCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        scope.ViewModel.SelectedTab = document;
        Dispatcher.UIThread.RunJobs();
        scope.Window.UpdateLayout();
        Assert.Equal(resizedWidth, FindCsvHeader(preview, "CsvColumnHeader:A").Bounds.Width);
    });

    private static void DragCsvBoundary(MainWindow window, Control header, Point localStart, Vector distance)
    {
        PrepareCsvPointerInput(window);
        var start = header.TranslatePoint(localStart, window)!.Value;
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + distance / 2, RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        window.MouseMove(start + distance, RawInputModifiers.LeftMouseButton);
        window.MouseUp(start + distance, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void PrepareCsvPointerInput(MainWindow window)
    {
        window.UpdateLayout();
        var island = window.FindControl<Border>("ContentIsland")!;
        island.Child!.Clip = new RectangleGeometry(new Rect(island.Child.Bounds.Size));
    }

    private static void ClickCsvHeader(MainWindow window, CsvPreview preview, string name, RawInputModifiers modifiers = RawInputModifiers.None, MouseButton button = MouseButton.Left)
    {
        PrepareCsvPointerInput(window);
        var header = FindCsvHeader(preview, name);
        var point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, button, modifiers);
        window.MouseUp(point, button, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void CsvPreviewDoubleClickEditsSourceAndSupportsUndoRedo() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.Load(@"C:\tmp\edit.csv", new TextDocumentContent("name,value\r\napple,001\r\n", DocumentEncoding.ShiftJis, "\r\n"));
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        scope.Window.UpdateLayout();
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        var cell = FindCsvCell(preview, "B2");
        // Headless の角丸 Geometry は内部点の FillContains も false を返すため、
        // 製品の角丸だけを矩形へ置き換え、セル自身のクリップと実ポインター入力を検証する。
        var island = scope.Window.FindControl<Border>("ContentIsland")!;
        island.Child!.Clip = new RectangleGeometry(new Rect(island.Child.Bounds.Size));
        var point = cell.TranslatePoint(new Point(20, 12), scope.Window)!.Value;
        scope.Window.MouseDown(point, MouseButton.Left);
        scope.Window.MouseUp(point, MouseButton.Left);
        scope.Window.MouseDown(point, MouseButton.Left);
        scope.Window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var input = preview.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.True(input.IsEffectivelyVisible, "ダブルクリックでセル編集欄が表示される");
        Assert.Equal("001", input.Text);
        input.Text = "a,\"b\"\nsecond";
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Control });
        Dispatcher.UIThread.RunJobs();
        Assert.False(input.IsEffectivelyVisible);
        const string changed = "name,value\r\napple,\"a,\"\"b\"\"\r\nsecond\"\r\n";
        Assert.Equal(changed, document.Text);
        Assert.True(document.IsModified, "セル値の適用で文書が変更済みになる");
        Assert.Equal(DocumentEncoding.ShiftJis, document.CreateSaveContent().Encoding);
        scope.Window.KeyPress(Key.Z, RawInputModifiers.Control, default, null);
        scope.Window.KeyRelease(Key.Z, RawInputModifiers.Control, default, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("name,value\r\napple,001\r\n", document.Text);
        Assert.False(document.IsModified);
        scope.Window.KeyPress(Key.Y, RawInputModifiers.Control, default, null);
        scope.Window.KeyRelease(Key.Y, RawInputModifiers.Control, default, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(changed, document.Text);
        Assert.Contains(preview.GetVisualDescendants().OfType<SelectableTextBlock>(), text => text.Text!.Contains("a,\"b\""));
    });

    [Fact]
    public void CsvPreviewEditsEmptyCellAndCancelsUnappliedDrafts() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.Load(@"C:\tmp\empty.csv", new TextDocumentContent("a,\nx,y", DocumentEncoding.Utf8, "\n"));
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        var input = OpenCsvCellEditor(preview, "B1");
        Assert.Equal(string.Empty, input.Text);
        input.Text = "new";
        preview.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ApplyCsvCellEdit")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("a,new\nx,y", document.Text);

        input = OpenCsvCellEditor(preview, "A1");
        input.Text = "cancelled";
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Dispatcher.UIThread.RunJobs();
        Assert.False(input.IsEffectivelyVisible);
        Assert.Equal("a,new\nx,y", document.Text);

        input = OpenCsvCellEditor(preview, "A1");
        input.Text = "stale";
        document.EditorDocument.Replace(0, 1, "external");
        Dispatcher.UIThread.RunJobs();
        Assert.False(input.IsEffectivelyVisible);
        Assert.Equal("external,new\nx,y", document.Text);

        input = OpenCsvCellEditor(preview, "B2");
        input.Text = "not applied";
        scope.ViewModel.NewDocumentCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        scope.ViewModel.SelectedTab = document;
        Dispatcher.UIThread.RunJobs();
        Assert.False(input.IsEffectivelyVisible);
        Assert.Equal("external,new\nx,y", document.Text);
    });

    [Fact]
    public void CsvCellRangesCopyPasteCutAndUndoThroughKeyboard() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        const string source = "a,b,c\n1,2,3\n4,5,6";
        document.Load(@"C:\tmp\cells.csv", new TextDocumentContent(source, DocumentEncoding.Utf8, "\n"));
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        ClickCsvCell(scope.Window, preview, "A1");
        SendCsvKey(scope.Window, Key.Right, RawInputModifiers.Shift);
        SendCsvKey(scope.Window, Key.Down, RawInputModifiers.Shift);
        Assert.Equal(new CsvCellRange(0, 0, 2, 2), preview.SelectedCellRange);
        SendCsvKey(scope.Window, Key.C, RawInputModifiers.Control);
        Assert.Equal("a\tb\r\n1\t2", scope.Window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult());
        SendCsvKey(scope.Window, Key.X, RawInputModifiers.Control);
        Assert.Equal(",,c\n,,3\n4,5,6", document.Text);
        scope.ViewModel.UndoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(source, document.Text);
        ClickCsvCell(scope.Window, preview, "B2");
        scope.Window.Clipboard!.SetTextAsync("x\ty\r\n\"p\nq\"\t001").GetAwaiter().GetResult();
        SendCsvKey(scope.Window, Key.V, RawInputModifiers.Control);
        Assert.Equal("a,b,c\n1,x,y\n4,\"p\nq\",001", document.Text);
        scope.ViewModel.UndoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(source, document.Text);
    });

    [Fact]
    public void CsvRangeFillClearAndRaggedEditingPreserveTableStructure() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.Load(@"C:\tmp\ragged.csv", new TextDocumentContent("a,b,c\nx\n1,2,3", DocumentEncoding.Utf8, "\n"));
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        var input = OpenCsvCellEditor(preview, "C2");
        input.Text = "new";
        SendCsvKey(scope.Window, Key.Enter, RawInputModifiers.Control);
        Assert.Equal("a,b,c\nx,,new\n1,2,3", document.Text);
        ClickCsvCell(scope.Window, preview, "A1");
        SendCsvKey(scope.Window, Key.Right, RawInputModifiers.Shift);
        SendCsvKey(scope.Window, Key.Down, RawInputModifiers.Shift);
        SendCsvKey(scope.Window, Key.D, RawInputModifiers.Control);
        Assert.Equal("a,b,c\na,b,new\n1,2,3", document.Text);
        SendCsvKey(scope.Window, Key.R, RawInputModifiers.Control);
        Assert.Equal("a,a,c\na,a,new\n1,2,3", document.Text);
        SendCsvKey(scope.Window, Key.Delete);
        Assert.Equal(",,c\n,,new\n1,2,3", document.Text);
        scope.ViewModel.UndoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("a,a,c\na,a,new\n1,2,3", document.Text);
    });

    [Fact]
    public void CsvKeyboardEditingMovesActiveCellAndKeepsMultilineValues() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.Load(@"C:\tmp\navigation.csv", new TextDocumentContent("a,b\nx,y", DocumentEncoding.Utf8, "\n"));
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        ClickCsvCell(scope.Window, preview, "A1");
        SendCsvKey(scope.Window, Key.Tab);
        Assert.Equal((0, 1), preview.ActiveCell);
        SendCsvKey(scope.Window, Key.Tab, RawInputModifiers.Shift);
        Assert.Equal((0, 0), preview.ActiveCell);
        SendCsvKey(scope.Window, Key.F2);
        var input = preview.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.True(input.IsEffectivelyVisible);
        input.Text = "first";
        input.CaretIndex = input.Text.Length;
        SendCsvKey(scope.Window, Key.Enter, RawInputModifiers.Alt);
        Assert.True(input.IsEffectivelyVisible);
        Assert.Contains("\n", input.Text);
        input.Text += "second";
        SendCsvKey(scope.Window, Key.Enter);
        Assert.Equal("\"first\nsecond\",b\nx,y", document.Text);
        Assert.Equal((1, 0), preview.ActiveCell);
        SendCsvKey(scope.Window, Key.F2);
        input.Text = "z";
        SendCsvKey(scope.Window, Key.Tab);
        Assert.Equal((1, 1), preview.ActiveCell);
        Assert.Equal("\"first\nsecond\",b\nz,y", document.Text);
    });

    [Fact]
    public void CsvHeaderInsertionUsesSelectedRowAndColumnCounts() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.Load(@"C:\tmp\insert.csv", new TextDocumentContent("a,b,c\n1,2,3\n4,5,6", DocumentEncoding.Utf8, "\n"));
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        ClickCsvHeader(scope.Window, preview, "CsvRowHeader:1");
        ClickCsvHeader(scope.Window, preview, "CsvRowHeader:2", RawInputModifiers.Shift);
        InvokeCsvHeaderMenu(preview, "CsvRowHeader:1", "CsvInsertBeforeMenuItem");
        Assert.Equal(5, CsvDocumentParser.Parse(document.Text, TestContext.Current.CancellationToken).TotalRowCount);
        Assert.Equal((true, 0, 2), preview.SelectedHeaders);
        ClickCsvHeader(scope.Window, preview, "CsvColumnHeader:A");
        ClickCsvHeader(scope.Window, preview, "CsvColumnHeader:B", RawInputModifiers.Shift);
        InvokeCsvHeaderMenu(preview, "CsvColumnHeader:A", "CsvInsertAfterMenuItem");
        Assert.Equal(5, CsvDocumentParser.Parse(document.Text, TestContext.Current.CancellationToken).TotalColumnCount);
        Assert.Equal((false, 2, 2), preview.SelectedHeaders);
    });

    [Fact]
    public void CsvPointerDragAndCellContextMenuUseTheRectangle() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.Load(@"C:\tmp\drag.csv", new TextDocumentContent("a,b,c\n1,2,3\n4,5,6", DocumentEncoding.Utf8, "\n"));
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        PrepareCsvPointerInput(scope.Window);
        var from = FindCsvCell(preview, "A1").TranslatePoint(new Point(20, 12), scope.Window)!.Value;
        var to = FindCsvCell(preview, "B2").TranslatePoint(new Point(20, 12), scope.Window)!.Value;
        scope.Window.MouseDown(from, MouseButton.Left);
        scope.Window.MouseMove(to);
        scope.Window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new CsvCellRange(0, 0, 2, 2), preview.SelectedCellRange);
        scope.Window.MouseDown(from, MouseButton.Right);
        scope.Window.MouseUp(from, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new CsvCellRange(0, 0, 2, 2), preview.SelectedCellRange);
        var cell = FindCsvCell(preview, "A1");
        var menu = cell.ContextMenu!;
        menu.Open(cell);
        menu.Items.OfType<MenuItem>().Single(item => item.Name == "CsvClearCellsMenuItem")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(",,c\n,,3\n4,5,6", document.Text);
        Assert.True(document.CanUndo);
    });

    private static void SendCsvKey(MainWindow window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, default, null);
        window.KeyRelease(key, modifiers, default, null);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void ClickCsvCell(MainWindow window, CsvPreview preview, string address)
    {
        PrepareCsvPointerInput(window);
        var cell = FindCsvCell(preview, address);
        var point = cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static Border FindCsvCell(CsvPreview preview, string address)
        => preview.GetVisualDescendants().OfType<Border>().Single(cell => Avalonia.Automation.AutomationProperties.GetName(cell) == address);

    private static TextBox OpenCsvCellEditor(CsvPreview preview, string address)
    {
        var cell = FindCsvCell(preview, address);
        cell.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Dispatcher.UIThread.RunJobs();
        var input = preview.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.True(input.IsEffectivelyVisible);
        return input;
    }

    [Fact]
    public void CsvPreviewRendersCurrentSourceAndReturnsToEditing() => fixture.Run(() =>
    {
        using var scope = new WindowScope(width: 720, height: 460);
        var document = scope.ViewModel.Documents.Single();
        document.MarkSaved(@"C:\tmp\table.csv");
        document.Text = "名前,値\nりんご,001\n\"a,b\",\"行1\n行2\"";
        Assert.True(scope.ViewModel.TogglePreviewCommand.CanExecute(null));
        scope.ViewModel.OpenCommandPaletteCommand.Execute(null);
        scope.ViewModel.SelectedPaletteCommand = scope.ViewModel.CommandPaletteResults.Single(entry => entry.Title == "Markdown / CSV プレビューを切り替え");
        scope.ViewModel.RunSelectedPaletteCommandCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        scope.Window.UpdateLayout();
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        var editor = scope.Window.FindControl<TextEditor>("Editor")!;
        Assert.True(preview.IsEffectivelyVisible);
        Assert.False(editor.IsEffectivelyVisible);
        Assert.Equal(document.Text, preview.Csv);
        Assert.Contains(scope.Window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "CSV");
        Assert.Contains(preview.GetVisualDescendants().OfType<SelectableTextBlock>(), cell => cell.Text == "001");
        Assert.Contains(preview.GetVisualDescendants().OfType<SelectableTextBlock>(), cell => cell.Text == "a,b");
        var source = document.Text;
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(preview.IsEffectivelyVisible);
        Assert.True(editor.IsEffectivelyVisible);
        Assert.Equal(source, document.Text);
        document.Text = "更新,002";
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(preview.GetVisualDescendants().OfType<SelectableTextBlock>(), cell => cell.Text == "002");
        document.MarkSaved(@"C:\tmp\plain.txt");
        Dispatcher.UIThread.RunJobs();
        Assert.False(scope.ViewModel.TogglePreviewCommand.CanExecute(null));
        Assert.True(editor.IsEffectivelyVisible);
    });

    [Fact]
    public void CsvPreviewScrollsInBothDirectionsAndKeepsTabState() => fixture.Run(() =>
    {
        using var scope = new WindowScope(width: 720, height: 460);
        var document = scope.ViewModel.Documents.Single();
        document.MarkSaved(@"C:\tmp\wide.csv");
        document.Text = string.Join("\n", Enumerable.Range(1, 80).Select(row => string.Join(",", Enumerable.Range(1, 20).Select(column => $"{row}:{column}"))));
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        scope.Window.UpdateLayout();
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        var scroll = preview.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Assert.True(scroll.Extent.Width > scroll.Viewport.Width);
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        scroll.Offset = new Vector(180, 120);
        Dispatcher.UIThread.RunJobs();
        scope.Window.UpdateLayout();
        Assert.Equal(180, scroll.Offset.X);
        Assert.Equal(120, scroll.Offset.Y);
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        scope.Window.UpdateLayout();
        Assert.Equal(180, scroll.Offset.X);
        Assert.Equal(120, scroll.Offset.Y);
        scope.ViewModel.NewDocumentCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(preview.IsEffectivelyVisible);
        scope.ViewModel.SelectedTab = document;
        Dispatcher.UIThread.RunJobs();
        Assert.True(preview.IsEffectivelyVisible);
        Assert.Equal(document.Text, preview.Csv);
    });

    [Fact]
    public void CsvPreviewBoundsLongCellDisplayWithoutChangingSource() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.MarkSaved(@"C:\tmp\long.csv");
        var source = new string('a', 100_000);
        document.Text = source;
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        scope.Window.UpdateLayout();
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        var cell = Assert.Single(preview.GetVisualDescendants().OfType<SelectableTextBlock>());
        Assert.True(cell.Text!.Length < 600);
        Assert.Contains("続きは編集画面", cell.Text);
        var tip = Assert.IsType<string>(ToolTip.GetTip(cell));
        Assert.True(tip.Length < 4200);
        Assert.Equal(source, document.Text);
    });

    [Fact]
    public void CsvPreviewCellColorsFollowThemeChanges() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.MarkSaved(@"C:\tmp\theme.csv");
        document.Text = "表示,確認";
        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var preview = scope.Window.GetVisualDescendants().OfType<CsvPreview>().Single();
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Light })
        {
            scope.Window.RequestedThemeVariant = theme;
            Dispatcher.UIThread.RunJobs();
            scope.Window.UpdateLayout();
            Assert.True(scope.Window.TryFindResource("TextPrimary", theme, out var expected));
            Assert.All(preview.GetVisualDescendants().OfType<SelectableTextBlock>(), cell => Assert.Equal(expected, cell.Foreground));
        }
    });

    [Fact]
    public void MarkdownPreviewRendersTheCurrentDocumentAndUsesAnExplicitLabel() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();
        document.MarkSaved(@"C:\tmp\readme.md");
        document.Text = "# 見出し\n\n本文\n\n7. 項目";

        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var preview = scope.Window.GetVisualDescendants().OfType<MarkdownPreview>().Single();
        var editor = scope.Window.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);
        Assert.True(preview.IsEffectivelyVisible);
        Assert.False(editor.IsEffectivelyVisible);
        Assert.Equal(3, preview.RenderedBlockCount);
        Assert.All(
            preview.GetVisualDescendants().OfType<SelectableTextBlock>(),
            textBlock => Assert.NotNull(textBlock.Foreground));
        Assert.Equal("編集へ戻る", document.PreviewToggleLabel);
    });

    [Fact]
    public void MarkdownPreviewExposesAScrollableViewportForLongDocuments() => fixture.Run(() =>
    {
        using var scope = new WindowScope(width: 720, height: 460);
        var document = scope.ViewModel.Documents.Single();
        document.MarkSaved(@"C:\tmp\readme.md");
        document.Text = string.Join("\n\n", Enumerable.Range(1, 80).Select(number => $"段落 {number}"));

        scope.ViewModel.TogglePreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        scope.Window.UpdateLayout();

        var preview = scope.Window.GetVisualDescendants().OfType<MarkdownPreview>().Single();
        var scrollViewer = preview.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Assert.True(
            scrollViewer.Extent.Height > scrollViewer.Viewport.Height,
            $"Extent={scrollViewer.Extent.Height}, Viewport={scrollViewer.Viewport.Height}, Bounds={scrollViewer.Bounds.Height}");
        scrollViewer.Offset = new Vector(0, 120);
        scope.Window.UpdateLayout();

        Assert.Equal(120, scrollViewer.Offset.Y);
    });

    [Fact]
    public void MarkdownPreviewRespondsToTheMouseWheel() => fixture.Run(() =>
    {
        var preview = new MarkdownPreview
        {
            Markdown = string.Join("\n\n", Enumerable.Range(1, 80).Select(number => $"段落 {number}")),
        };
        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = preview,
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var scrollViewer = preview.GetVisualDescendants().OfType<ScrollViewer>().Single();
            Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height);

            window.MouseWheel(new Point(100, 100), new Vector(0, -3), RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.True(scrollViewer.Offset.Y > 0);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void StatusCommandsChangeEncodingAndNewLines() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var document = scope.ViewModel.Documents.Single();

        scope.ViewModel.SetDocumentEncodingCommand.Execute(DocumentEncoding.ShiftJis);
        scope.ViewModel.SetDocumentNewLineCommand.Execute(DocumentNewLines.Lf);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(DocumentEncoding.ShiftJis, document.Encoding);
        Assert.Equal("Shift_JIS", document.EncodingLabel);
        Assert.Equal(DocumentNewLines.Lf, document.NewLine);
        Assert.Equal("LF", document.NewLineLabel);
        Assert.True(document.IsModified);

        var statusButtons = scope.Window.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Classes.Contains("statusitem"))
            .ToArray();
        Assert.Equal(2, statusButtons.Length);
        Assert.All(statusButtons, button => Assert.NotNull(button.Flyout));
    });

    [Fact]
    public void DocumentStatusTextIsVerticallyCentered() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var lineColumn = scope.Window.FindControl<TextBlock>("LineColumnStatus");
        var statistics = scope.Window.FindControl<TextBlock>("DocumentStatisticsStatus");

        Assert.NotNull(lineColumn);
        Assert.NotNull(statistics);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Center, lineColumn.VerticalAlignment);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Center, statistics.VerticalAlignment);
    });

    [Fact]
    public void EditorKeepsTheStandardScrollBarWithoutAScrollMap() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var editor = scope.Window.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);
        Assert.Equal(
            global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            editor.VerticalScrollBarVisibility);
        var editorHost = Assert.IsType<Grid>(editor.Parent);
        Assert.Empty(editorHost.ColumnDefinitions);
    });

    [Fact]
    public void SelectingAGrepResultTabShowsTheResultListInsteadOfTheEditor() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var tab = new GrepResultTabViewModel(
            new GrepQuery("探す語", @"C:\tmp", "*.txt", true, false, false),
            new EmptyGrepService(),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);

        scope.ViewModel.Tabs.Add(tab);
        scope.ViewModel.SelectedTab = tab;
        Dispatcher.UIThread.RunJobs();

        Assert.True(scope.ViewModel.IsGrepSelected);
        var results = scope.Window.FindControl<ListBox>("GrepResultList");
        Assert.NotNull(results);
        Assert.True(results.IsEffectivelyVisible);

        // 検索結果を見ている間、編集領域は行ごと隠れる（設定タブと同じ扱い）。
        Assert.False(scope.Window.FindControl<TextEditor>("Editor")!.IsEffectivelyVisible);
    });

    private sealed class EmptyGrepService : IGrepService
    {
        public Task<GrepResult> SearchAsync(GrepQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(GrepResult.Empty);
    }

    /// <summary>閉じる経路（<c>OnClosing</c>）から復元までを、実ウィンドウ 2 枚で往復させる。</summary>
    [Fact]
    public void ClosingWithUnsavedTextBringsItBackOnTheNextStart() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();

        var first = ShowWindow();
        ((MainWindowViewModel)first.DataContext!).SelectedDocument!.Text = "閉じても消えない";
        CloseWindow(first);

        var second = ShowWindow();
        try
        {
            Assert.Equal("閉じても消えない", second.FindControl<TextEditor>("Editor")!.Document.Text);
            Assert.True(((MainWindowViewModel)second.DataContext!).SelectedDocument!.IsModified);
        }
        finally
        {
            CloseWindow(second);
        }
    });

    private static MainWindow ShowWindow()
    {
        // 起動時の更新確認は外部通信になるためテストでは切る。
        var window = new MainWindow(new AppSettings { CheckUpdatesOnStartup = false });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>閉じる確認は非同期なので、実際に閉じ切るまでディスパッチャを回す。</summary>
    private static void CloseWindow(MainWindow window)
    {
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// マクロの記録は、実際のキー入力を意味づけして積む。翻訳の取りこぼしと二重取りは
    /// ViewModel 単体では出ないので、実エディタへ入力を流して積まれた手を確かめる。
    ///
    /// 突き合わせるのは「入力した結果の本文」ではなく「積まれた手」。ヘッドレスでは
    /// AvaloniaEdit が合成したキーの編集コマンドを実行しないため、本文は実機と揃わない。
    /// </summary>
    [Fact]
    public void KeysAreTranslatedIntoStepsOnceEach() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var editor = scope.Window.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);
        editor.Focus();
        Dispatcher.UIThread.RunJobs();

        scope.ViewModel.ToggleMacroRecordingCommand.Execute(null);
        TypeInto(editor, "ab");
        PressKey(editor, Key.Enter);
        TypeInto(editor, "c");
        PressKey(editor, Key.Left, KeyModifiers.Shift);
        PressKey(editor, Key.Back);
        PressKey(editor, Key.Home, KeyModifiers.Control);
        scope.ViewModel.ToggleMacroRecordingCommand.Execute(null);

        var steps = scope.ViewModel.RecordedSteps;

        // 改行は構造インデントを伴う独立した操作。TextEntered からも重複記録されないことを確かめる。
        Assert.Equal(
            [
                MacroStepKind.InsertText,
                MacroStepKind.InsertNewLine,
                MacroStepKind.InsertText,
                MacroStepKind.MoveCaret,
                MacroStepKind.DeleteBack,
                MacroStepKind.MoveCaret,
            ],
            steps.Select(step => step.Kind));
        Assert.Equal("ab", steps[0].Text);
        Assert.Equal("c", steps[2].Text);
        Assert.Equal(MacroMotion.CharacterLeft, steps[3].Motion);
        Assert.True(steps[3].ExtendSelection);
        Assert.Equal(MacroMotion.DocumentStart, steps[5].Motion);
    });

    /// <summary>記録した手を当て直すと、記録どおりの本文になる。</summary>
    [Fact]
    public void ReplayingTheRecordedStepsRebuildsTheText() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var editor = scope.Window.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);
        var document = scope.ViewModel.Documents.Single();
        editor.Focus();
        Dispatcher.UIThread.RunJobs();

        scope.ViewModel.ToggleMacroRecordingCommand.Execute(null);
        TypeInto(editor, "ab");
        PressKey(editor, Key.Enter);
        TypeInto(editor, "c");
        scope.ViewModel.ToggleMacroRecordingCommand.Execute(null);

        document.Text = string.Empty;
        document.CaretIndex = 0;
        Dispatcher.UIThread.RunJobs();

        scope.ViewModel.RunMacroCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal($"ab{Environment.NewLine}c", document.Text);
    });

    /// <summary>記録していない間は、同じ入力を流しても 1 手も積まれない。</summary>
    [Fact]
    public void NothingIsRecordedWhileRecordingIsOff() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var editor = scope.Window.FindControl<TextEditor>("Editor");
        Assert.NotNull(editor);
        editor.Focus();
        Dispatcher.UIThread.RunJobs();

        TypeInto(editor, "abc");
        PressKey(editor, Key.Left);

        Assert.Equal(0, scope.ViewModel.RecordedStepCount);
        Assert.False(scope.ViewModel.IsRecordingMacro);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImePreeditIsDrawnWithoutEditingTheDocument(bool dark) => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        scope.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var editor = scope.Window.FindControl<TextEditor>("Editor")!;
        editor.Focus();
        Dispatcher.UIThread.RunJobs();
        var client = RequestImeClient(editor);
        Assert.True(client.SupportsPreedit);
        var initialCaret = client.CursorRectangle;
        var version = editor.Document.Version;
        scope.ViewModel.ToggleMacroRecordingCommand.Execute(null);

        client.SetPreeditText("にほんご", 2);
        Assert.NotEmpty(DrawPreedit(editor).Children);
        var preeditBackground = Assert.IsType<GeometryDrawing>(DrawPreedit(editor).Children[0]);
        Assert.Equal(Color.Parse(dark ? "#242528" : "#FBFBFC"),
            Assert.IsAssignableFrom<ISolidColorBrush>(preeditBackground.Brush).Color);
        var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(editor.Foreground).Color;
        Assert.True(ContrastRatio(foreground, Assert.IsAssignableFrom<ISolidColorBrush>(preeditBackground.Brush).Color) >= 4.5);

        // 変換中にテーマを切り替えても、未確定文字を再送せず背景色が追従する。
        scope.Window.RequestedThemeVariant = dark ? ThemeVariant.Light : ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();
        var switchedBackground = Assert.IsType<GeometryDrawing>(DrawPreedit(editor).Children[0]);
        Assert.Equal(Color.Parse(dark ? "#FBFBFC" : "#242528"),
            Assert.IsAssignableFrom<ISolidColorBrush>(switchedBackground.Brush).Color);
        Assert.True(client.CursorRectangle.X > initialCaret.X);
        Assert.Equal("", editor.Text);
        Assert.Same(version, editor.Document.Version);
        Assert.False(editor.Document.UndoStack.CanUndo);
        Assert.Equal(0, scope.ViewModel.RecordedStepCount);

        client.SetPreeditText("日本語", 1);
        Assert.NotEmpty(DrawPreedit(editor).Children);
        client.SetPreeditText(null, null);
        Assert.Empty(DrawPreedit(editor).Children);
        Assert.Equal(initialCaret, client.CursorRectangle);
        Assert.Equal("", editor.Text);

        client.SetPreeditText("日本語", 3);
        TypeInto(editor, "日本語");
        Assert.Empty(DrawPreedit(editor).Children);
        Assert.Equal("日本語", editor.Text);
        Assert.Equal(1, scope.ViewModel.RecordedStepCount);
        editor.Undo();
        Assert.Equal("", editor.Text);
    });

    [Fact]
    public void ImePreeditClearsOnDocumentAndFocusChanges() => fixture.Run(() =>
    {
        using var scope = new WindowScope();
        var editor = scope.Window.FindControl<TextEditor>("Editor")!;
        editor.Focus();
        Dispatcher.UIThread.RunJobs();
        var client = RequestImeClient(editor);
        client.SetPreeditText("未確定", int.MaxValue);
        Assert.NotEmpty(DrawPreedit(editor).Children);
        scope.ViewModel.NewDocumentCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(DrawPreedit(editor).Children);
        Assert.All(scope.ViewModel.Documents, document => Assert.Equal("", document.Text));

        Assert.True(editor.TextArea.Focus());
        Dispatcher.UIThread.RunJobs();
        Assert.True(editor.TextArea.IsFocused);
        client = RequestImeClient(editor);
        client.SetPreeditText("入力中", -1);
        Assert.NotEmpty(DrawPreedit(editor).Children);
        scope.ViewModel.OpenCommandPaletteCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(DrawPreedit(editor).Children);
    });

    private static TextInputMethodClient RequestImeClient(TextEditor editor)
    {
        var args = new TextInputMethodClientRequestedEventArgs
        {
            RoutedEvent = InputElement.TextInputMethodClientRequestedEvent,
        };
        editor.TextArea.RaiseEvent(args);
        return Assert.IsAssignableFrom<TextInputMethodClient>(args.Client);
    }

    private static DrawingGroup DrawPreedit(TextEditor editor)
    {
        var layer = editor.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "EditorPreeditLayer");
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
            layer.Render(context);
        return drawing;
    }

    private static Color ReadBracketColor(TextEditor editor, int offset)
    {
        var line = editor.Document.GetLineByOffset(offset);
        var visualLine = editor.TextArea.TextView.GetOrConstructVisualLine(line);
        var relativeOffset = offset - visualLine.FirstDocumentLine.Offset;
        var element = Assert.Single(visualLine.Elements, item =>
            item.RelativeTextOffset <= relativeOffset
            && relativeOffset < item.RelativeTextOffset + item.DocumentLength);
        return Assert.IsAssignableFrom<ISolidColorBrush>(element.TextRunProperties.ForegroundBrush).Color;
    }

    private static void TypeInto(TextEditor editor, string text)
    {
        editor.TextArea.RaiseEvent(new TextInputEventArgs
        {
            RoutedEvent = InputElement.TextInputEvent,
            Source = editor.TextArea,
            Text = text,
        });
        Dispatcher.UIThread.RunJobs();
    }

    private static void PressKey(TextEditor editor, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        editor.TextArea.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Source = editor.TextArea,
            Key = key,
            KeyModifiers = modifiers,
        });
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class WindowScope : IDisposable
    {
        private readonly TemporaryStorage _storage = new();

        public WindowScope(double? width = null, double? height = null)
        {
            // 起動時の更新確認は外部通信になるためテストでは切る。
            Window = new MainWindow(new AppSettings { CheckUpdatesOnStartup = false });
            ViewModel = (MainWindowViewModel)Window.DataContext!;
            if (width is not null)
            {
                Window.Width = width.Value;
            }

            if (height is not null)
            {
                Window.Height = height.Value;
            }

            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public MainWindow Window { get; }

        public string StoragePath => _storage.Path;

        public MainWindowViewModel ViewModel { get; }

        public void Dispose()
        {
            Window.Close();
            _storage.Dispose();
        }
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
        => (0.2126 * Linearize(color.R)) + (0.7152 * Linearize(color.G)) + (0.0722 * Linearize(color.B));

    private static double Linearize(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
