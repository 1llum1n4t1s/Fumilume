using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

/// <summary>実ファイルを実ウィンドウへ読み込み、構文解析の境界とテーマの表示色を検証する。</summary>
[Collection(HeadlessAppCollection.Name)]
public sealed class SyntaxHighlightingE2ETests(HeadlessAppFixture fixture)
{
    private const string Toml = """"
        # first comment
        "quoted.key".bare = "hash # stays string and escaped \" quote"
        literal = 'literal # value'
        [server."primary"]
        enabled = true
        count = 1_234
        ratio = -1.25e+2
        timestamp = 1979-05-27T07:32:00Z
        localdate = 1979-05-27
        [[products]]
        array = [1, false, "array # value"]
        inline = { name = "inline value", active = false }
        basic = """multiline basic
        # still basic string
        escaped \" quote inside
        end basic"""
        # after basic
        afterbasic = 42
        multiline = '''multiline literal
        # still literal string
        end literal'''
        # after literal
        afterliteral = false
        """";

    private static readonly Probe[] TomlProbes =
    [
        new("# first comment", "Comment"), new("quoted.key", "Property"), new("bare", "Property"),
        new("hash # stays string", "String"), new("quote\"", "String"), new("literal # value", "String"),
        new("server", "Section"), new("true", "Bool"), new("1_234", "Number"),
        new("-1.25e+2", "Number"), new("1979-05-27T07:32:00Z", "Date"), new("1979-05-27", "Date", 1),
        new("products", "Section"), new("array # value", "String"), new("name", "Property"),
        new("inline value", "String"), new("false", "Bool"),
        new("# still basic string", "String"), new(" quote inside", "String"), new("end basic", "String"),
        new("# after basic", "Comment"), new("afterbasic", "Property"), new("42", "Number"),
        new("# still literal string", "String"), new("end literal", "String"),
        new("# after literal", "Comment"), new("afterliteral", "Property"), new("false", "Bool", 2),
    ];

    [Fact]
    public void TomlTokensRecoverAfterBothMultilineStringsAndThemeChanges() => fixture.Run(() =>
    {
        using var scope = new Scope();
        var path = scope.Open("settings.toml", Toml);
        var original = scope.Model.SelectedDocument!;
        Assert.Equal("TOML", scope.Editor.SyntaxHighlighting?.Name);
        foreach (var dark in new[] { false, true, false })
        {
            scope.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            Layout(scope.Window);
            var observations = CheckTokens(scope.Editor, TomlProbes, dark);
            Evidence($"toml-{(dark ? "dark" : "light")}", scope, observations);
            Pump(scope.Model.OpenPathsAsync([path, path.ToUpperInvariant()]));
            Assert.Same(original, scope.Model.SelectedDocument);
            Assert.Single(scope.Model.Documents, document => document.FilePath == path);
            Assert.Equal(Toml, scope.Editor.Text);
        }

        scope.Model.Options.EnableSyntaxHighlighting = false;
        Layout(scope.Window);
        Assert.Null(scope.Editor.SyntaxHighlighting);
        Assert.Equal(Toml, scope.Editor.Text);
        scope.Model.Options.EnableSyntaxHighlighting = true;
        Layout(scope.Window);
        Assert.Equal("TOML", scope.Editor.SyntaxHighlighting?.Name);
        CheckTokens(scope.Editor, TomlProbes, dark: false);
        Assert.False(original.IsModified);
    });

    [Theory]
    [InlineData("yaml", "YAML")]
    [InlineData("yml", "YAML")]
    [InlineData("ini", "INI")]
    [InlineData("jsonc", "JSONC")]
    public void ConfigurationTokensKeepCommentMarkersInsideStrings(string extension, string definition) => fixture.Run(() =>
    {
        using var scope = new Scope();
        var (text, probes) = extension switch
        {
            "ini" => ("[server]\nname = \"hash # value\"\nliteral = 'literal # text'\n; real comment\nport = 42\n", new Probe[]
            {
                new("server", "Section"), new("name", "Property"), new("hash # value", "String"),
                new("literal # text", "String"), new("; real comment", "Comment"), new("42", "Number"),
            }),
            "jsonc" => ("{\n  \"name\": \"https://host/#value\", // real comment\n  /* block\n     comment */\n  \"after\": true,\n  \"count\": 42\n}\n", new Probe[]
            {
                new("name", "Property"), new("https://host/#value", "String"), new("// real comment", "Comment"),
                new("block", "Comment"), new("comment */", "Comment"), new("after", "Property"),
                new("true", "Bool"), new("42", "Number"),
            }),
            _ => ("name: \"hash # value\"\nliteral: 'literal # text'\n# real comment\nenabled: true\ncount: 42\n", new Probe[]
            {
                new("name", "Property"), new("hash # value", "String"), new("literal # text", "String"),
                new("# real comment", "Comment"), new("true", "Bool"), new("42", "Number"),
            }),
        };
        scope.Open($"config.{extension}", text);
        Assert.Equal(definition, scope.Editor.SyntaxHighlighting?.Name);
        foreach (var dark in new[] { false, true })
        {
            scope.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            Layout(scope.Window);
            Evidence($"{extension}-{(dark ? "dark" : "light")}", scope, CheckTokens(scope.Editor, probes, dark));
        }
    });

    [Theory]
    [InlineData("csx", "C#", "class Example {}")]
    [InlineData("cxx", "C++", "int count = 42;")]
    [InlineData("hh", "C++", "int count = 42;")]
    [InlineData("hxx", "C++", "int count = 42;")]
    [InlineData("mdown", "MarkDown", "# Heading")]
    [InlineData("mkd", "MarkDown", "# Heading")]
    public void AdditionalExtensionsSelectTheirLanguageInTheEditor(string extension, string definition, string text) => fixture.Run(() =>
    {
        using var scope = new Scope();
        scope.Open($"sample.{extension}", text);
        Assert.Equal(definition, scope.Editor.SyntaxHighlighting?.Name);
        using var highlighter = new DocumentHighlighter(scope.Editor.Document, scope.Editor.SyntaxHighlighting);
        Assert.NotEmpty(highlighter.HighlightLine(1).Sections);
        Assert.Equal(text, scope.Editor.Text);
    });

    [Fact]
    public void EmptyAndUnknownFilesRemainEditable() => fixture.Run(() =>
    {
        using var scope = new Scope();
        scope.Open("empty.toml", "");
        Assert.Equal("TOML", scope.Editor.SyntaxHighlighting?.Name);
        using (var highlighter = new DocumentHighlighter(scope.Editor.Document, scope.Editor.SyntaxHighlighting))
            Assert.Empty(highlighter.HighlightLine(1).Sections);
        scope.Open("plain.unlisted-syntax", "name = \"value # text\"");
        Assert.Null(scope.Editor.SyntaxHighlighting);
        scope.Editor.Document.Insert(scope.Editor.Document.TextLength, "\neditable");
        Assert.EndsWith("\neditable", scope.Model.SelectedDocument!.Text);
    });

    [Fact]
    public void TomlNestedArraysAndQuotedBracketInTableNameKeepTheirRoles() => fixture.Run(() =>
    {
        using var scope = new Scope();
        scope.Open("nested.toml", "matrix = [\n [1, 2],\n [3, \"nested # string\"],\n]\n[servers.\"east]west\"]\nport = 42\n");
        Evidence("toml-nested-boundaries", scope, CheckTokens(scope.Editor,
        [
            new("matrix", "Property"), new("1", "Number"), new("2", "Number"), new("3", "Number"),
            new("nested # string", "String"), new("east]west", "Section"), new("port", "Property"),
            new("42", "Number"),
        ], dark: false));
    });

    [Fact]
    public void YamlBlockScalarsEndAtMappingAndSequenceSiblings() => fixture.Run(() =>
    {
        var cases = new (string Name, string Source, Probe[] Probes)[]
        {
            ("root", "|+\n  # root scalar\n---\nnext: 42\n",
                [new("# root scalar", "String"), new("next", "Property"), new("42", "Number")]),
            ("mapping", "body: |\n  # mapping scalar\nnext: 42\n",
                [new("body", "Property"), new("# mapping scalar", "String"), new("next", "Property"), new("42", "Number")]),
            ("nested-mapping", "outer:\n  body: >-\n    # nested scalar\n  next: 42\n",
                [new("body", "Property"), new("# nested scalar", "String"), new("next", "Property"), new("42", "Number")]),
            ("sequence-mapping", "- body: |\n    # sequence mapping scalar\n  next: 42\n- other: true\n",
                [new("body", "Property"), new("# sequence mapping scalar", "String"), new("next", "Property"), new("42", "Number"), new("other", "Property"), new("true", "Bool")]),
            ("sequence", "- |\n  # direct scalar\n- 42\n",
                [new("# direct scalar", "String"), new("42", "Number")]),
        };
        foreach (var item in cases)
        {
            using var scope = new Scope();
            scope.Open($"{item.Name}.yaml", item.Source);
            Evidence($"yaml-block-{item.Name}", scope, CheckTokens(scope.Editor, item.Probes, dark: false));
        }
    });

    [Fact]
    public void YamlExplicitBlockIndentAndHeaderCommentHaveSeparateColors() => fixture.Run(() =>
    {
        using var scope = new Scope();
        scope.Open("explicit.yaml", "body: |2- # header comment\n  # explicit scalar\nnext: 42\n");
        Evidence("yaml-block-explicit", scope, CheckTokens(scope.Editor,
        [
            new("body", "Property"), new("# header comment", "Comment"), new("# explicit scalar", "String"),
            new("next", "Property"), new("42", "Number"),
        ], dark: false));
    });

    [Fact]
    public void YamlQuotesInsidePlainScalarsDoNotStartMultilineStrings() => fixture.Run(() =>
    {
        var plainValues = new[] { "it's fine", "foo\"bar", "a 'quote in plain" };
        for (var index = 0; index < plainValues.Length; index++)
        {
            using var scope = new Scope();
            scope.Open($"plain-quote-{index}.yaml", $"message: {plainValues[index]}\nnext: true\n");
            var observations = CheckTokens(scope.Editor,
                [new("message", "Property"), new("next", "Property"), new("true", "Bool")], dark: false);
            var quoteOffset = scope.Editor.Text.IndexOfAny(['\'', '"']);
            Assert.True(quoteOffset >= 0);
            using var highlighter = new DocumentHighlighter(scope.Editor.Document, scope.Editor.SyntaxHighlighting!);
            var line = scope.Editor.Document.GetLineByOffset(quoteOffset);
            Assert.DoesNotContain(highlighter.HighlightLine(line.LineNumber).Sections, section =>
                section.Color.Name == "String" && section.Offset <= quoteOffset
                && section.Offset + section.Length > quoteOffset);
            Evidence($"yaml-plain-quote-{index}", scope, observations);
        }

        foreach (var delimiter in new[] { '\'', '"' })
        {
            using var scope = new Scope();
            scope.Open($"quoted-{(delimiter == '\'' ? "single" : "double")}.yaml",
                $"message: {delimiter}quoted # scalar{delimiter}\nnext: true\n");
            Evidence($"yaml-quoted-{(delimiter == '\'' ? "single" : "double")}", scope, CheckTokens(scope.Editor,
                [new("quoted # scalar", "String"), new("next", "Property"), new("true", "Bool")], dark: false));
        }
    });

    [Theory]
    [InlineData("toml")]
    [InlineData("yaml")]
    [InlineData("ini")]
    [InlineData("jsonc")]
    public void LongPartialInputDoesNotBlockFileOpeningAndHighlighting(string extension) => fixture.Run(() =>
    {
        using var scope = new Scope();
        var source = new string('a', 16_384);
        var watch = Stopwatch.StartNew();
        scope.Open($"partial.{extension}", source);
        Assert.NotNull(scope.Editor.SyntaxHighlighting);
        using (var highlighter = new DocumentHighlighter(scope.Editor.Document, scope.Editor.SyntaxHighlighting))
            highlighter.HighlightLine(1);
        Layout(scope.Window);
        watch.Stop();
        if (Environment.GetEnvironmentVariable("FUMILUME_E2E_ARTIFACTS") is { Length: > 0 } directory)
        {
            directory = SafeExternalDirectory(directory);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"syntax-long-line-{extension}.json"),
                JsonSerializer.Serialize(new { ElapsedMillis = watch.Elapsed.TotalMilliseconds, SourceLength = source.Length }));
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5),
            $"{extension} の長行の読込・強調表示が5秒以内に完了しませんでした: {watch.Elapsed.TotalMilliseconds:F0}ms");
    });

    [Fact(Skip = "実描画E2E: FUMILUME_E2E_RENDER=1 で個別実行",
        SkipType = typeof(HeadlessAppFixture), SkipUnless = nameof(HeadlessAppFixture.UsesSkia))]
    public void RenderTomlInBothThemes() => fixture.Run(() =>
    {
        using var scope = new Scope();
        const string source = """"
            # TOML settings
            [server."primary"]
            "quoted.key" = "hash # and escaped \" quote"
            literal = 'literal string'
            enabled = true
            count = 1_234
            timestamp = 1979-05-27T07:32:00Z
            [[products]]
            array = [1, false, "value"]
            basic = """multiline
            # inside basic"""
            literalblock = '''multiline
            # inside literal'''
            # after strings
            next = 42
            """";
        Probe[] probes =
        [
            new("# TOML settings", "Comment"), new("server", "Section"), new("quoted.key", "Property"),
            new("hash # and escaped", "String"), new("literal string", "String"), new("true", "Bool"),
            new("1_234", "Number"), new("1979-05-27T07:32:00Z", "Date"), new("products", "Section"),
            new("# inside basic", "String"), new("# inside literal", "String"), new("# after strings", "Comment"),
            new("next", "Property"), new("42", "Number"),
        ];
        scope.Open("render.toml", source);
        foreach (var dark in new[] { false, true })
        {
            scope.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            Layout(scope.Window);
            Evidence($"render-toml-{(dark ? "dark" : "light")}", scope, CheckTokens(scope.Editor, probes, dark), capture: true);
        }
    });

    private static object[] CheckTokens(TextEditor editor, Probe[] probes, bool dark)
    {
        using var highlighter = new DocumentHighlighter(editor.Document, Assert.IsAssignableFrom<IHighlightingDefinition>(editor.SyntaxHighlighting));
        return probes.Select(probe =>
        {
            var offset = editor.Text.IndexOf(probe.Text, StringComparison.Ordinal);
            for (var occurrence = 0; occurrence < probe.Occurrence && offset >= 0; occurrence++)
                offset = editor.Text.IndexOf(probe.Text, offset + probe.Text.Length, StringComparison.Ordinal);
            Assert.True(offset >= 0, $"検証トークンがありません: {probe.Text}");
            var line = editor.Document.GetLineByOffset(offset);
            var section = highlighter.HighlightLine(line.LineNumber).Sections.LastOrDefault(value =>
                value.Offset <= offset && value.Offset + value.Length >= offset + probe.Text.Length);
            Assert.NotNull(section);
            Assert.Equal(probe.Role, section.Color.Name);
            var expected = probe.Role switch
            {
                "Comment" => dark ? "#5C6370" : "#A0A1A7",
                "String" => dark ? "#98C379" : "#50A14F",
                "Section" => dark ? "#E06C75" : "#E45649",
                _ => dark ? "#D19A66" : "#986801",
            };
            Assert.NotNull(section.Color.Foreground);
            var actual = section.Color.Foreground.GetColor(null!)!.Value;
            Assert.Equal(Color.Parse(expected), actual);
            return (object)new { probe.Text, probe.Role, Line = line.LineNumber, Offset = offset, Color = actual.ToString() };
        }).ToArray();
    }

    private static void Evidence(string name, Scope scope, object[] tokens, bool capture = false)
    {
        if (Environment.GetEnvironmentVariable("FUMILUME_E2E_ARTIFACTS") is not { Length: > 0 } directory) return;
        directory = SafeExternalDirectory(directory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"syntax-{name}.json"), JsonSerializer.Serialize(new
        {
            HeadlessAppFixture.UsesSkia, Definition = scope.Editor.SyntaxHighlighting?.Name,
            Theme = scope.Window.ActualThemeVariant.ToString(), scope.StoragePath,
            File = scope.Model.SelectedDocument!.FilePath, Source = scope.Editor.Text, Tokens = tokens,
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (!capture) return;
        using var frame = scope.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, $"syntax-{name}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static string SafeExternalDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "../.."));
        var codex = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        AssertOutside(repo);
        AssertOutside(codex);
        if (Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codexHome)
            AssertOutside(Path.GetFullPath(codexHome));
        Assert.DoesNotContain(full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            segment => segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase));
        for (var ancestor = new DirectoryInfo(full); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Exists)
                Assert.False(ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint), $"リンク経由の保存先は使用しません: {ancestor.FullName}");
        return full;

        void AssertOutside(string forbidden)
        {
            forbidden = Path.TrimEndingDirectorySeparator(forbidden);
            Assert.False(full.Equals(forbidden, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(forbidden + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        if (HeadlessAppFixture.UsesSkia) AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Pump(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Yield();
        }
        Assert.True(task.IsCompleted, "UI処理が15秒以内に完了しませんでした");
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed record Probe(string Text, string Role, int Occurrence = 0);

    private sealed class Scope : IDisposable
    {
        public string StoragePath { get; }
        public MainWindow Window { get; }
        public MainWindowViewModel Model { get; }
        public TextEditor Editor { get; }

        public Scope()
        {
            var root = Environment.GetEnvironmentVariable("FUMILUME_SYNTAX_TEST_ROOT")
                ?? Path.Combine(Path.GetTempPath(), "Codex", "Fumilume", $"syntax-{Guid.NewGuid():N}");
            StoragePath = SafeExternalDirectory(Path.Combine(root, Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(StoragePath);
            AppStoragePaths.OverrideDirectory(StoragePath);
            Window = new MainWindow(new AppSettings { RestoreSession = false, CheckUpdatesOnStartup = false });
            Model = (MainWindowViewModel)Window.DataContext!;
            Window.RequestedThemeVariant = ThemeVariant.Light;
            Window.Show();
            Layout(Window);
            Editor = Window.FindControl<TextEditor>("Editor")!;
        }

        public string Open(string name, string text)
        {
            var path = Path.Combine(StoragePath, name);
            File.WriteAllText(path, text);
            Pump(Model.OpenPathsAsync([path]));
            Layout(Window);
            Assert.Equal(text, Editor.Text);
            return path;
        }

        public void Dispose()
        {
            Model.Options.RestoreSession = true;
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            AppStoragePaths.OverrideDirectory(null);
            // 今回の一時物は実行担当がPrunlet CLIをCMDから直接呼び、検証後に清掃する。
        }
    }
}
