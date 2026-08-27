using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
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
    /// sakura の「変換」「編集」メニュー相当。データバインドではなくコードビハインドで組んでいる
    /// （PublishAot でリフレクション束縛が落ちるため）ので、実ツリーで並びを確かめる。
    /// </summary>
    [Fact]
    public void CommandMenuIsBuiltFromTheCatalog() => fixture.Run(() =>
    {
        using var scope = new WindowScope();

        var leaves = new List<MenuItem>();
        foreach (var icon in EditorCommandCatalog.CategoryIcons)
        {
            var button = scope.Window.FindControl<Button>(icon.ButtonName);
            Assert.NotNull(button);
            var flyout = Assert.IsType<MenuFlyout>(button.Flyout);

            var expected = EditorCommandCatalog.Groups.Single(group => group.Category == icon.Category);
            var items = flyout.Items.OfType<MenuItem>().ToList();
            Assert.Equal(expected.Commands.Select(command => command.Title), items.Select(item => item.Header));
            leaves.AddRange(items);
        }

        // 区分アイコンを合わせるとカタログ全体を覆う（どこからも呼べないコマンドを残さない）。
        Assert.Equal(EditorCommandCatalog.All.Count, leaves.Count);
        Assert.All(leaves, leaf =>
        {
            Assert.NotNull(leaf.Command);
            Assert.IsType<EditorCommandId>(leaf.CommandParameter);
        });
    });

    /// <summary>区分アイコンが 1 つでも欠けると、その区分のコマンドがツールバーから消える。</summary>
    [Fact]
    public void EveryCategoryHasAToolbarIcon() => fixture.Run(() =>
    {
        Assert.Equal(
            EditorCommandCatalog.Groups.Select(group => group.Category).Order(),
            EditorCommandCatalog.CategoryIcons.Select(icon => icon.Category).Order());
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
        Assert.Equal(EditorCommandCatalog.All.Count, scope.ViewModel.CommandPaletteResults.Count);

        scope.ViewModel.CommandPaletteQuery = "大文字";
        Dispatcher.UIThread.RunJobs();
        Assert.All(scope.ViewModel.CommandPaletteResults, command => Assert.Contains("大文字", command.Title));

        scope.ViewModel.SelectedPaletteCommand =
            scope.ViewModel.CommandPaletteResults.Single(command => command.Id == EditorCommandId.ToUpper);
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

    private sealed class WindowScope : IDisposable
    {
        private readonly TemporaryStorage _storage = new();

        public WindowScope()
        {
            // 起動時の更新確認は外部通信になるためテストでは切る。
            Window = new MainWindow(new AppSettings { CheckUpdatesOnStartup = false });
            ViewModel = (MainWindowViewModel)Window.DataContext!;
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public MainWindow Window { get; }

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
