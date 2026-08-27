using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Fumilume.Services;

namespace Fumilume.ViewModels;

/// <summary>
/// 設定タブ。文書と同じ縦タブ一覧へ並ぶが、ファイルの実体を持たない。
///
/// 設定画面から呼びたいコマンド（更新確認）と、失敗をユーザーへ伝える手段は
/// <see cref="MainWindowViewModel"/> から受け取って公開する。ビュー側で <c>$parent[Window]</c> を
/// 辿らせないための持ち回しで、SettingsView の DataContext はこのタブだけで閉じる。
///
/// 関連付けは別ウィンドウを出さず、この画面のトグルを動かした時点でレジストリへ反映する。
/// </summary>
public sealed partial class SettingsTabViewModel : WorkspaceTabViewModel
{
    private readonly Func<string, string, Task> _showErrorAsync;

    /// <summary>一括切り替え中に 1 項目ずつ書き込まないための門。</summary>
    private bool _suppressAssociationApply;

    public SettingsTabViewModel(
        AppOptionsViewModel options,
        ICommand checkForUpdatesCommand,
        Func<string, string, Task> showErrorAsync,
        Func<WorkspaceTabViewModel, Task> closeAsync)
        : base(closeAsync)
    {
        Options = options;
        CheckForUpdatesCommand = checkForUpdatesCommand;
        _showErrorAsync = showErrorAsync;

        // 選択肢に現在値が無いとコンボボックスが空欄になるため、手書き編集された値もその場で足す。
        int[] presets = [2, 4, 8];
        IndentationSizeOptions = presets.Contains(options.IndentationSize)
            ? presets
            : [.. presets.Append(options.IndentationSize).Order()];

        Associations =
        [
            .. FileAssociationService.SupportedTypes
                .Select(type => new FileAssociationItemViewModel(type.Extension, type.Description)),
        ];
        LoadAssociationStatus();
        foreach (var item in Associations)
        {
            item.PropertyChanged += OnAssociationChanged;
        }
    }

    public AppOptionsViewModel Options { get; }

    public ICommand CheckForUpdatesCommand { get; }

    /// <summary>関連付けの一覧。トグルを動かすとその場でレジストリへ反映する。</summary>
    public ObservableCollection<FileAssociationItemViewModel> Associations { get; }

    public override string TabTitle => "設定";

    /// <summary>Segoe Fluent Icons の歯車。</summary>
    public override string TabGlyph => "";

    public override string TabTooltip => "アプリの設定";

    public override bool IsSettingsTab => true;

    /// <summary>テーマ選択の選択肢（表示名と保存値の対）。</summary>
    public IReadOnlyList<ThemeModeOption> ThemeModeOptions { get; } =
    [
        new("システムに合わせる", "System"),
        new("ライト", "Light"),
        new("ダーク", "Dark"),
    ];

    /// <summary>インデント幅の選択肢。</summary>
    public IReadOnlyList<int> IndentationSizeOptions { get; }

    public string AppVersion => Version;

    [RelayCommand]
    private void SelectAllAssociations() => SetAllAssociations(true);

    [RelayCommand]
    private void DeselectAllAssociations() => SetAllAssociations(false);

    /// <summary>一括切り替え。項目ごとに書き込むとレジストリ往復が拡張子の数だけ走るので、1 回にまとめる。</summary>
    private void SetAllAssociations(bool isAssociated)
    {
        _suppressAssociationApply = true;
        foreach (var item in Associations)
        {
            item.IsAssociated = isAssociated;
        }

        _suppressAssociationApply = false;
        ApplyAssociations();
    }

    /// <summary>レジストリの現在値をトグルへ映す。</summary>
    private void LoadAssociationStatus()
    {
        var status = FileAssociationService.GetCurrentAssociationStatus();
        _suppressAssociationApply = true;
        foreach (var item in Associations)
        {
            item.IsAssociated = status.GetValueOrDefault(item.Extension);
        }

        _suppressAssociationApply = false;
    }

    private void OnAssociationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(FileAssociationItemViewModel.IsAssociated) &&
            !_suppressAssociationApply)
        {
            ApplyAssociations();
        }
    }

    private void ApplyAssociations()
    {
        var failures = FileAssociationService.ApplyAssociations(
            Associations.Where(item => item.IsAssociated).Select(item => item.Extension));
        if (failures.Count == 0)
        {
            return;
        }

        // 失敗した拡張子はレジストリが変わっていないので、トグルを実際の状態へ戻してから知らせる。
        LoadAssociationStatus();
        _ = _showErrorAsync(
            "ファイルの関連付けを変更できません",
            $"次の拡張子を変更できませんでした。\n\n{string.Join(", ", failures)}");
    }

    private static readonly string Version = ReadInformationalVersion();

    private static string ReadInformationalVersion()
    {
        var assembly = typeof(SettingsTabViewModel).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return assembly.GetName().Version?.ToString(3) ?? "不明";
        }

        // "1.0.0+abcdef" のビルドメタデータは利用者に意味が無いので落とす。
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}

/// <summary>テーマ選択の 1 項目。</summary>
/// <param name="DisplayName">画面に出す名前。</param>
/// <param name="Value">settings.json へ保存する値。</param>
public sealed record ThemeModeOption(string DisplayName, string Value);
