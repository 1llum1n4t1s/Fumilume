using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fumilume.Services;

namespace Fumilume.ViewModels;

/// <summary>
/// フォルダ横断検索の結果タブ（秀丸の grep 結果に当たる）。
///
/// 文書と同じ縦タブ一覧へ並べるのは、結果を「一時的なウィンドウ」ではなく
/// 開いている作業のひとつとして残すため。行を選べば該当ファイルの該当行へ飛べる。
/// </summary>
public sealed partial class GrepResultTabViewModel : WorkspaceTabViewModel, IDisposable
{
    private readonly IGrepService _grep;
    private readonly Func<GrepMatch, Task> _openMatchAsync;
    private CancellationTokenSource? _cancellation;
    private bool _disposed;

    public GrepResultTabViewModel(
        GrepQuery query,
        IGrepService grep,
        Func<GrepMatch, Task> openMatchAsync,
        Func<WorkspaceTabViewModel, Task> closeAsync)
        : base(closeAsync)
    {
        Query = query;
        _grep = grep;
        _openMatchAsync = openMatchAsync;
    }

    public GrepQuery Query { get; }

    public ObservableCollection<GrepMatchItem> Matches { get; } = [];

    /// <summary>Segoe Fluent Icons の虫眼鏡。</summary>
    public override string TabGlyph => "";

    public override string TabTitle => $"検索: {Query.Pattern}";

    public override string TabTooltip => Query.Describe();

    public override bool IsGrepTab => true;

    [ObservableProperty]
    private GrepMatchItem? _selectedMatch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    private bool _isSearching;

    [ObservableProperty]
    private string _status = "検索しています…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    private bool _hasCompleted;

    /// <summary>検索が終わって 1 件も無かったか（「見つかりません」を出すかどうか）。</summary>
    public bool HasNoMatches => HasCompleted && !IsSearching && Matches.Count == 0;

    /// <summary>検索を実行する。すでに走っているときは何もしない。</summary>
    public async Task RunAsync()
    {
        if (IsSearching || _disposed)
        {
            return;
        }

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        Matches.Clear();
        SelectedMatch = null;
        HasCompleted = false;
        IsSearching = true;
        Status = $"{Query.Folder} を検索しています…";
        NotifyCommandStates();

        try
        {
            // ファイルの読み込みと照合は UI スレッドから外す（件数が多いと目に見えて固まる）。
            var result = await Task.Run(() => _grep.SearchAsync(Query, token), token);
            foreach (var match in result.Matches)
            {
                Matches.Add(new GrepMatchItem(match));
            }

            SelectedMatch = Matches.FirstOrDefault();
            Status = Describe(result);
        }
        catch (OperationCanceledException)
        {
            Status = $"検索を中止しました（{Matches.Count:N0} 件）";
        }
        catch (Exception ex)
        {
            AppLogger.For<GrepResultTabViewModel>().Error($"検索に失敗しました: {Query.Describe()}", ex);
            Status = $"検索できませんでした（{ex.Message}）";
        }
        finally
        {
            IsSearching = false;
            HasCompleted = true;
            OnPropertyChanged(nameof(HasNoMatches));
            NotifyCommandStates();
        }
    }

    private static string Describe(GrepResult result)
    {
        if (result.Matches.Count == 0)
        {
            return $"見つかりませんでした（{result.SearchedFiles:N0} ファイルを検索）";
        }

        var status = $"{result.Matches.Count:N0} 件（{result.SearchedFiles:N0} ファイル）";
        if (result.ReachedLimit)
        {
            status += $" ※ {GrepService.MaximumMatches:N0} 件で打ち切りました";
        }

        if (result.SkippedFiles > 0)
        {
            status += $" ／ 読めない {result.SkippedFiles:N0} ファイルは除外";
        }

        return status;
    }

    private bool CanOpenSelected() => SelectedMatch is not null;

    /// <summary>選んだ行のファイルを開いて、その行へ飛ぶ。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenSelected))]
    private Task OpenSelectedAsync()
        => SelectedMatch is { } item ? _openMatchAsync(item.Match) : Task.CompletedTask;

    private bool CanCancelSearch() => IsSearching;

    [RelayCommand(CanExecute = nameof(CanCancelSearch))]
    private void CancelSearch() => _cancellation?.Cancel();

    private bool CanSearchAgain() => !IsSearching;

    [RelayCommand(CanExecute = nameof(CanSearchAgain))]
    private Task SearchAgainAsync() => RunAsync();

    partial void OnSelectedMatchChanged(GrepMatchItem? value)
        => OpenSelectedCommand.NotifyCanExecuteChanged();

    private void NotifyCommandStates()
    {
        CancelSearchCommand.NotifyCanExecuteChanged();
        SearchAgainCommand.NotifyCanExecuteChanged();
        OpenSelectedCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }
}

/// <summary>結果一覧の 1 行。表示に使う形へ整えるだけで、元の一致情報はそのまま持つ。</summary>
public sealed class GrepMatchItem(GrepMatch match)
{
    public GrepMatch Match { get; } = match;

    /// <summary>「ファイル名 (行番号)」。</summary>
    public string Location => $"{Path.GetFileName(Match.FilePath)} ({Match.LineNumber:N0})";

    /// <summary>行の中身。字下げは詰めて、1 行の見出しとして読めるようにする。</summary>
    public string Preview => Match.LineText.Trim();

    public string Tooltip => $"{Match.FilePath}\n{Match.LineNumber:N0} 行 {Match.Column:N0} 桁";
}
