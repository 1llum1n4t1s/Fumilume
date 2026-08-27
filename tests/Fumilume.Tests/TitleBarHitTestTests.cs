using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Fumilume.Views;

namespace Fumilume.Tests;

/// <summary>
/// 自前タイトルバーが掴めるかどうかの根拠テスト。
///
/// Avalonia は Background が未設定（null）の Panel をヒットテストの対象にしないため、
/// タイトルバーの Grid に Background を置き忘れると PointerPressed が一度も発火せず、
/// ウィンドウを掴んで動かせなくなる（v1.0.0 の実際の不具合）。
///
/// 2 方向（Transparent＝発火する／未設定＝発火しない）を実際の入力で測る。片方だけだと、
/// ヘッドレス環境でヒットテスト自体が効いていない場合に「発火しない」が偽陽性で通ってしまう。
/// </summary>
[Collection(HeadlessAppCollection.Name)]
public sealed class TitleBarHitTestTests(HeadlessAppFixture fixture)
{
    [Fact]
    public void PanelWithBackgroundReceivesPointerPressed() => fixture.Run(() =>
        Assert.True(
            MeasurePointerPressed(Brushes.Transparent),
            "Background=Transparent の Grid は PointerPressed を受け取れなければならない。"));

    [Fact]
    public void PanelWithoutBackgroundDoesNotReceivePointerPressed() => fixture.Run(() =>
        Assert.False(
            MeasurePointerPressed(background: null),
            "Background 未設定の Grid は PointerPressed を受け取らない（＝ウィンドウを掴めない）。"));

    [Fact]
    public void MainWindowTitleBarIsHitTestable() => fixture.Run(() =>
    {
        using var storage = new TemporaryStorage();
        var window = new MainWindow();
        var titleBar = window.FindControl<Grid>("TitleBar");

        Assert.NotNull(titleBar);
        Assert.NotNull(titleBar.Background);
    });

    [Fact]
    public void DialogTitleBarIsHitTestable() => fixture.Run(() =>
    {
        var dialog = new AppDialogWindow(useAcrylic: false);
        var titleBar = dialog.FindControl<Grid>("TitleBar");

        Assert.NotNull(titleBar);
        Assert.NotNull(titleBar.Background);
    });

    /// <summary>タイトルバーと同じ形（子が全面を覆わない）の Grid を作り、空き領域を押して発火を見る。</summary>
    private static bool MeasurePointerPressed(IBrush? background)
    {
        var pressed = false;
        var bar = new Grid
        {
            Background = background,
            Height = 32,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        bar.PointerPressed += (_, _) => pressed = true;

        var window = new Window { Width = 400, Height = 200, Content = bar };
        window.Show();
        try
        {
            window.MouseDown(new Point(200, 16), MouseButton.Left);
            window.MouseUp(new Point(200, 16), MouseButton.Left);
        }
        finally
        {
            window.Close();
        }

        return pressed;
    }
}
