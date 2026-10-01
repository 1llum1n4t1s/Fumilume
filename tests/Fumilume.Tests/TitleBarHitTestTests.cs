using Avalonia.Controls;
using Fumilume.Views;

namespace Fumilume.Tests;

/// <summary>
/// 自前タイトルバーが掴めるかどうかの根拠テスト。
///
/// Avalonia は Background が未設定（null）の Panel をヒットテストの対象にしないため、
/// タイトルバーの Grid に Background を置き忘れると PointerPressed が一度も発火せず、
/// ウィンドウを掴んで動かせなくなる（v1.0.0 の実際の不具合）。
///
/// 実際のウィンドウとダイアログで、タイトルバーの Background の設定漏れを検出する。
/// </summary>
[Collection(HeadlessAppCollection.Name)]
public sealed class TitleBarHitTestTests(HeadlessAppFixture fixture)
{
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

}
