using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Fumilume.Services;

namespace Fumilume.ViewModels;

/// <summary>
/// 設定タブとエディタ本体が共有するオプション。<see cref="AppSettings"/> をそのまま包み、
/// 変更のたびに settings.json へ書き戻す。
///
/// オプションを <see cref="MainWindowViewModel"/> ではなくこの型に置くのは、設定タブ
/// （<see cref="SettingsTabViewModel"/>）とメイン画面の双方から同じ実体を参照させるため。
/// どちらか一方に置くと、もう一方が親を辿るバインディングになって参照方向が二重になる。
/// </summary>
public sealed class AppOptionsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private IReadOnlyList<string>? _fontFamilyOptions;

    public AppOptionsViewModel(AppSettings settings) => _settings = settings;

    /// <summary>永続化される実体。ウィンドウ位置のようにビューが直接書き込む値もここから触る。</summary>
    public AppSettings Settings => _settings;

    // ===== 表示 =====

    public bool ShowLineNumbers
    {
        get => _settings.ShowLineNumbers;
        set => SetOption(_settings.ShowLineNumbers, value, v => _settings.ShowLineNumbers = v);
    }

    public bool WordWrap
    {
        get => _settings.WordWrap;
        set => SetOption(_settings.WordWrap, value, v => _settings.WordWrap = v);
    }

    public bool InheritWordWrapIndentation
    {
        get => _settings.InheritWordWrapIndentation;
        set => SetOption(_settings.InheritWordWrapIndentation, value, v => _settings.InheritWordWrapIndentation = v);
    }

    public bool HighlightCurrentLine
    {
        get => _settings.HighlightCurrentLine;
        set => SetOption(_settings.HighlightCurrentLine, value, v => _settings.HighlightCurrentLine = v);
    }

    public bool ShowColumnRuler
    {
        get => _settings.ShowColumnRuler;
        set => SetOption(_settings.ShowColumnRuler, value, v => _settings.ShowColumnRuler = v);
    }

    public int ColumnRulerPosition
    {
        get => _settings.ColumnRulerPosition;
        set => SetOption(
            _settings.ColumnRulerPosition,
            Math.Clamp(
                value,
                AppSettingsDefaults.MinimumColumnRulerPosition,
                AppSettingsDefaults.MaximumColumnRulerPosition),
            v => _settings.ColumnRulerPosition = v);
    }

    public double LineHeightFactor
    {
        get => _settings.LineHeightFactor;
        set => SetOption(
            _settings.LineHeightFactor,
            Math.Clamp(
                value,
                AppSettingsDefaults.MinimumLineHeightFactor,
                AppSettingsDefaults.MaximumLineHeightFactor),
            v => _settings.LineHeightFactor = v);
    }

    // ===== 記号の表示 =====

    public bool ShowSpaces
    {
        get => _settings.ShowSpaces;
        set => SetOption(_settings.ShowSpaces, value, v => _settings.ShowSpaces = v);
    }

    public bool ShowTabs
    {
        get => _settings.ShowTabs;
        set => SetOption(_settings.ShowTabs, value, v => _settings.ShowTabs = v);
    }

    public bool ShowEndOfLine
    {
        get => _settings.ShowEndOfLine;
        set => SetOption(_settings.ShowEndOfLine, value, v => _settings.ShowEndOfLine = v);
    }

    public bool ShowControlCharacters
    {
        get => _settings.ShowControlCharacters;
        set => SetOption(_settings.ShowControlCharacters, value, v => _settings.ShowControlCharacters = v);
    }

    // ===== フォント =====

    public string FontFamily
    {
        get => _settings.FontFamily;
        set => SetOption(_settings.FontFamily, value, v => _settings.FontFamily = v);
    }

    public double FontSize
    {
        get => _settings.FontSize;
        set => SetOption(
            _settings.FontSize,
            Math.Clamp(value, AppSettingsDefaults.MinimumFontSize, AppSettingsDefaults.MaximumFontSize),
            v => _settings.FontSize = v);
    }

    /// <summary>フォント一覧はインストール済みフォントの列挙が要るため、実際に開かれるまで作らない
    /// （ビューモデル単体のテストが Avalonia の初期化を必要としないようにする）。</summary>
    public IReadOnlyList<string> FontFamilyOptions => _fontFamilyOptions ??= BuildFontFamilyOptions();

    // ===== 編集 =====

    public int IndentationSize
    {
        get => _settings.IndentationSize;
        set => SetOption(
            _settings.IndentationSize,
            Math.Clamp(
                value,
                AppSettingsDefaults.MinimumIndentationSize,
                AppSettingsDefaults.MaximumIndentationSize),
            v => _settings.IndentationSize = v);
    }

    public bool ConvertTabsToSpaces
    {
        get => _settings.ConvertTabsToSpaces;
        set => SetOption(_settings.ConvertTabsToSpaces, value, v => _settings.ConvertTabsToSpaces = v);
    }

    public bool AcceptsTab
    {
        get => _settings.AcceptsTab;
        set => SetOption(_settings.AcceptsTab, value, v => _settings.AcceptsTab = v);
    }

    public bool EnableRectangularSelection
    {
        get => _settings.EnableRectangularSelection;
        set => SetOption(_settings.EnableRectangularSelection, value, v => _settings.EnableRectangularSelection = v);
    }

    public bool EnableVirtualSpace
    {
        get => _settings.EnableVirtualSpace;
        set => SetOption(_settings.EnableVirtualSpace, value, v => _settings.EnableVirtualSpace = v);
    }

    public bool EnableTextDragDrop
    {
        get => _settings.EnableTextDragDrop;
        set => SetOption(_settings.EnableTextDragDrop, value, v => _settings.EnableTextDragDrop = v);
    }

    public bool CutCopyWholeLine
    {
        get => _settings.CutCopyWholeLine;
        set => SetOption(_settings.CutCopyWholeLine, value, v => _settings.CutCopyWholeLine = v);
    }

    public bool AllowScrollBelowDocument
    {
        get => _settings.AllowScrollBelowDocument;
        set => SetOption(_settings.AllowScrollBelowDocument, value, v => _settings.AllowScrollBelowDocument = v);
    }

    public bool AllowToggleOverstrikeMode
    {
        get => _settings.AllowToggleOverstrikeMode;
        set => SetOption(_settings.AllowToggleOverstrikeMode, value, v => _settings.AllowToggleOverstrikeMode = v);
    }

    public bool HideCursorWhileTyping
    {
        get => _settings.HideCursorWhileTyping;
        set => SetOption(_settings.HideCursorWhileTyping, value, v => _settings.HideCursorWhileTyping = v);
    }

    public bool EnableHyperlinks
    {
        get => _settings.EnableHyperlinks;
        set => SetOption(_settings.EnableHyperlinks, value, v => _settings.EnableHyperlinks = v);
    }

    // ===== 検索 =====

    public bool SearchMatchCase
    {
        get => _settings.SearchMatchCase;
        set => SetOption(_settings.SearchMatchCase, value, v => _settings.SearchMatchCase = v);
    }

    public bool SearchUseRegex
    {
        get => _settings.SearchUseRegex;
        set => SetOption(_settings.SearchUseRegex, value, v => _settings.SearchUseRegex = v);
    }

    public bool SearchUseCaretWord
    {
        get => _settings.SearchUseCaretWord;
        set => SetOption(_settings.SearchUseCaretWord, value, v => _settings.SearchUseCaretWord = v);
    }

    // ===== ファイル =====

    public bool RestoreCaretPosition
    {
        get => _settings.RestoreCaretPosition;
        set => SetOption(_settings.RestoreCaretPosition, value, v => _settings.RestoreCaretPosition = v);
    }

    public bool WarnOnLargeFile
    {
        get => _settings.WarnOnLargeFile;
        set => SetOption(_settings.WarnOnLargeFile, value, v => _settings.WarnOnLargeFile = v);
    }

    public int LargeFileThresholdMegabytes
    {
        get => _settings.LargeFileThresholdMegabytes;
        set => SetOption(
            _settings.LargeFileThresholdMegabytes,
            Math.Clamp(
                value,
                AppSettingsDefaults.MinimumLargeFileThresholdMegabytes,
                AppSettingsDefaults.MaximumLargeFileThresholdMegabytes),
            v => _settings.LargeFileThresholdMegabytes = v);
    }

    public bool CreateBackupOnSave
    {
        get => _settings.CreateBackupOnSave;
        set => SetOption(_settings.CreateBackupOnSave, value, v => _settings.CreateBackupOnSave = v);
    }

    public bool ConfirmOnExit
    {
        get => _settings.ConfirmOnExit;
        set => SetOption(_settings.ConfirmOnExit, value, v => _settings.ConfirmOnExit = v);
    }

    // ===== 外観 =====

    /// <summary>"System" / "Light" / "Dark"。設定タブの ComboBox が Tag 経由でこの文字列を渡す。</summary>
    public string ThemeMode
    {
        get => _settings.ThemeMode;
        set
        {
            if (!AppSettingsDefaults.ThemeModes.Contains(value, StringComparer.Ordinal))
            {
                return;
            }

            if (SetOption(_settings.ThemeMode, value, v => _settings.ThemeMode = v)
                && Application.Current is { } app)
            {
                ThemeService.ApplyThemeMode(app, value);
            }
        }
    }

    public bool UseAcrylic
    {
        get => _settings.UseAcrylic;
        set => SetOption(_settings.UseAcrylic, value, v => _settings.UseAcrylic = v);
    }

    /// <summary>左のタブ一覧の 1 行の厚み（px）。</summary>
    public double TabHeight
    {
        get => _settings.TabHeight;
        set => SetOption(
            _settings.TabHeight,
            Math.Clamp(value, AppSettingsDefaults.MinimumTabHeight, AppSettingsDefaults.MaximumTabHeight),
            v => _settings.TabHeight = v);
    }

    // ===== ウィンドウ / 更新 =====

    public bool RememberWindowBounds
    {
        get => _settings.RememberWindowBounds;
        set => SetOption(_settings.RememberWindowBounds, value, v => _settings.RememberWindowBounds = v);
    }

    public bool CheckUpdatesOnStartup
    {
        get => _settings.CheckUpdatesOnStartup;
        set => SetOption(_settings.CheckUpdatesOnStartup, value, v => _settings.CheckUpdatesOnStartup = v);
    }

    /// <summary>ビュー側（ウィンドウ位置の保存など）が設定を直接書き換えたあとの保存経路。</summary>
    public void Persist() => SettingsService.Save(_settings);

    private bool SetOption<T>(T current, T value, Action<T> assign, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
        {
            return false;
        }

        assign(value);
        OnPropertyChanged(propertyName);
        Persist();
        return true;
    }

    /// <summary>等幅フォントの候補のうち、実際にインストールされているものだけを出す。
    /// 現在値は未インストールでも消さない（別 PC で作った設定を勝手に書き換えないため）。</summary>
    private IReadOnlyList<string> BuildFontFamilyOptions()
    {
        string[] candidates =
        [
            "Cascadia Mono", "Cascadia Code", "Consolas", "Courier New",
            "MS Gothic", "Meiryo", "BIZ UDGothic", "Yu Gothic UI",
            "Segoe UI", "Lucida Console", "DejaVu Sans Mono",
        ];

        HashSet<string> installed;
        try
        {
            installed = FontManager.Current.SystemFonts
                .Select(family => family.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            // フォント一覧を取れない環境では候補を絞り込まずそのまま出す。
            installed = [.. candidates];
        }

        var options = candidates.Where(installed.Contains).ToList();
        if (!options.Contains(FontFamily, StringComparer.OrdinalIgnoreCase))
        {
            options.Insert(0, FontFamily);
        }

        return options;
    }
}
