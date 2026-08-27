using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

/// <summary>
/// 設定画面の検索。6 タブ 20 枚のカードを端から開いて回らずに済むことが目的なので、
/// 「選んでいないタブの中身も当たること」を中心に確かめる。
/// </summary>
[Collection(HeadlessAppCollection.Name)]
public sealed class SettingsSearchTests(HeadlessAppFixture fixture)
{
    [Fact]
    public void EveryTabIsIndexedIncludingTheOnesNeverShown() => fixture.Run(() =>
    {
        using var scope = new SettingsScope();

        // 「更新」は最後のタブで、既定では一度も組み立てられない。
        var matches = scope.View.ApplySearch("更新");

        Assert.True(matches > 0);
        Assert.True(scope.Tabs.Items.OfType<TabItem>().Last().IsVisible);
    });

    [Fact]
    public void OnlyMatchingCardsAndTabsStayVisible() => fixture.Run(() =>
    {
        using var scope = new SettingsScope();

        scope.View.ApplySearch("インデント");

        var visibleTabs = scope.Tabs.Items.OfType<TabItem>().Where(tab => tab.IsVisible).ToList();
        Assert.Single(visibleTabs);
        Assert.Single(scope.CardsOf(visibleTabs[0]), card => card.IsVisible);
    });

    [Fact]
    public void TheCaptionUnderAnItemIsSearchableToo() => fixture.Run(() =>
    {
        using var scope = new SettingsScope();

        // 「横スクロール」は項目名ではなく、折り返しの説明文にしか出てこない。
        Assert.Equal(1, scope.View.ApplySearch("横スクロール"));
    });

    [Fact]
    public void ClearingTheQueryBringsEverythingBack() => fixture.Run(() =>
    {
        using var scope = new SettingsScope();
        var allCards = scope.Tabs.Items.OfType<TabItem>().Sum(tab => scope.CardsOf(tab).Count);

        scope.View.ApplySearch("インデント");
        Assert.Equal(allCards, scope.View.ApplySearch(string.Empty));

        Assert.All(scope.Tabs.Items.OfType<TabItem>(), tab =>
        {
            Assert.True(tab.IsVisible);
            Assert.All(scope.CardsOf(tab), card => Assert.True(card.IsVisible));
        });
    });

    [Fact]
    public void AQueryThatMatchesNothingSaysSo() => fixture.Run(() =>
    {
        using var scope = new SettingsScope();

        Assert.Equal(0, scope.View.ApplySearch("該当しない語句"));

        Assert.False(scope.Tabs.IsVisible);
        Assert.True(scope.View.FindControl<TextBlock>("SettingsNoMatch")!.IsVisible);
    });

    /// <summary>選んでいたタブが絞り込みで消えたら、残っているタブへ移らないと本文が空になる。</summary>
    [Fact]
    public void TheSelectionMovesToATabThatStillHasMatches() => fixture.Run(() =>
    {
        using var scope = new SettingsScope();
        var tabs = scope.Tabs.Items.OfType<TabItem>().ToList();
        scope.Tabs.SelectedItem = tabs[0];

        scope.View.ApplySearch("更新");

        var selected = Assert.IsType<TabItem>(scope.Tabs.SelectedItem);
        Assert.True(selected.IsVisible);
        Assert.NotSame(tabs[0], selected);
    });

    [Fact]
    public void EscapeClearsTheSearchBox() => fixture.Run(() =>
    {
        using var scope = new SettingsScope();
        var box = scope.View.FindControl<TextBox>("SettingsSearchBox");
        Assert.NotNull(box);

        box.Text = "インデント";
        Dispatcher.UIThread.RunJobs();
        Assert.Single(scope.Tabs.Items.OfType<TabItem>(), tab => tab.IsVisible);

        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(box.Text ?? string.Empty);
        Assert.All(scope.Tabs.Items.OfType<TabItem>(), tab => Assert.True(tab.IsVisible));
    });

    private sealed class SettingsScope : IDisposable
    {
        private readonly TemporaryStorage _storage = new();
        private readonly Window _window;

        public SettingsScope()
        {
            var options = new AppOptionsViewModel(new AppSettings());
            View = new SettingsView
            {
                DataContext = new SettingsTabViewModel(
                    options,
                    new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }),
                    (_, _) => Task.CompletedTask,
                    _ => Task.CompletedTask),
            };

            _window = new Window { Content = View, Width = 1000, Height = 700 };
            _window.Show();
            Dispatcher.UIThread.RunJobs();
            Tabs = View.FindControl<TabControl>("SettingsTabs")!;
        }

        public SettingsView View { get; }

        public TabControl Tabs { get; }

        public IReadOnlyList<Control> CardsOf(TabItem tab)
            => tab.Content is null ? [] : FindCards(tab.Content);

        public void Dispose()
        {
            _window.Close();
            _storage.Dispose();
        }

        /// <summary>本体と同じ「XAML が組み立てた枝を辿る」やり方で数える。</summary>
        private static List<Control> FindCards(object? node)
        {
            var found = new List<Control>();
            switch (node)
            {
                case HeaderedContentControl headered when headered.Classes.Contains("settingscard"):
                    found.Add(headered);
                    break;
                case HeaderedContentControl headered:
                    found.AddRange(FindCards(headered.Content));
                    break;
                case ContentControl content:
                    found.AddRange(FindCards(content.Content));
                    break;
                case Panel panel:
                    foreach (var child in panel.Children)
                    {
                        found.AddRange(FindCards(child));
                    }

                    break;
            }

            return found;
        }
    }
}
