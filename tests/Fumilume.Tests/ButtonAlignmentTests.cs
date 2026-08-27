using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fumilume.Services;
using Fumilume.ViewModels;
using Fumilume.Views;

namespace Fumilume.Tests;

/// <summary>各画面のButtonが、内容の種類やUIフォントに左右されず中央揃えになることを確かめる。</summary>
[Collection(HeadlessAppCollection.Name)]
public sealed class ButtonAlignmentTests(HeadlessAppFixture fixture)
{
    [Fact]
    public void MainWindowAndSettingsButtonsCenterTheirContent() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var window = new MainWindow(new AppSettings { CheckUpdatesOnStartup = false });
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            AssertCentered(window.GetVisualDescendants().Where(control => control.GetType() == typeof(Button)).Cast<Button>());

            var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
            viewModel.OpenSettingsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            AssertCentered(window.GetVisualDescendants().Where(control => control.GetType() == typeof(Button)).Cast<Button>());
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void DialogActionButtonsCenterTheirContent() => fixture.Run(() =>
    {
        var actions = new[]
        {
            new Button { Content = "保存" },
            new Button { Content = "保存しない" },
            new Button { Content = "キャンセル" },
        };
        var dialog = new AppDialogWindow(
            "変更の保存",
            useAcrylic: false,
            new TextBlock { Text = "変更を保存しますか？" },
            actions);

        try
        {
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            dialog.UpdateLayout();

            AssertCentered(actions);
        }
        finally
        {
            dialog.Close();
        }
    });

    /// <summary>
    /// コマンドパレットの入口だけは横方向の例外。検索欄に見せるため中身を左右いっぱいへ広げており、
    /// 中央揃えにすると右端のキー表示が文言へ貼り付く。縦方向は他のボタンと同じ扱いにする。
    /// </summary>
    private const string StretchedButtonClass = "palette";

    private static void AssertCentered(IEnumerable<Button> buttons)
    {
        var targets = buttons.ToArray();
        Assert.NotEmpty(targets);
        Assert.All(targets, button =>
        {
            if (!button.Classes.Contains(StretchedButtonClass))
            {
                Assert.Equal(HorizontalAlignment.Center, button.HorizontalContentAlignment);
            }

            Assert.Equal(VerticalAlignment.Center, button.VerticalContentAlignment);
        });
    }
}
