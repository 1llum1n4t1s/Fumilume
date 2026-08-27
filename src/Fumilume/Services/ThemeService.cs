using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Fumilume.Services;

/// <summary>
/// テーマ変種（システム追従 / ライト / ダーク）の切り替えと、Windows のアクセントカラーの取り込み。
///
/// アクセントカラーは Fluent テーマの SystemAccentColor 系リソースへ注入する。OS 側で色やテーマを
/// 変えたときは ColorValuesChanged で追従する（"System" のときのダーク/ライト自体は
/// RequestedThemeVariant = Default で Avalonia が自動追従する）。
/// </summary>
public static class ThemeService
{
    private static Color? _lastAppliedAccent;
    private static int _applyScheduled;

    public static void Initialize(Application app, AppSettings settings)
    {
        ApplyThemeMode(app, settings.ThemeMode);
        ApplyAccent(app);

        if (app.PlatformSettings is { } platformSettings)
        {
            platformSettings.ColorValuesChanged += (_, _) => ScheduleApplyAccent(app);
        }
    }

    /// <summary>設定タブのテーマ選択から呼ぶ。</summary>
    public static void ApplyThemeMode(Application app, string themeMode)
        => app.RequestedThemeVariant = themeMode switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

    /// <summary>短時間に重複する OS 通知を UI キューの 1 回へまとめる。</summary>
    private static void ScheduleApplyAccent(Application app)
    {
        if (Interlocked.Exchange(ref _applyScheduled, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _applyScheduled, 0);
            ApplyAccent(app);
        });
    }

    private static void ApplyAccent(Application app)
    {
        Color accent;
        try
        {
            if (app.PlatformSettings?.GetColorValues() is not { } colors)
            {
                return;
            }

            accent = colors.AccentColor1;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        {
            return; // 取得できなければ Fluent 既定色のまま
        }

        if (_lastAppliedAccent == accent)
        {
            return;
        }

        _lastAppliedAccent = accent;

        // Fluent テーマの選択色・チェック色・フォーカスリングなどがアクセントカラーになる
        app.Resources["SystemAccentColor"] = accent;
        app.Resources["SystemAccentColorDark1"] = Shade(accent, 0.82);
        app.Resources["SystemAccentColorDark2"] = Shade(accent, 0.66);
        app.Resources["SystemAccentColorDark3"] = Shade(accent, 0.50);
        app.Resources["SystemAccentColorLight1"] = Tint(accent, 0.18);
        app.Resources["SystemAccentColorLight2"] = Tint(accent, 0.36);
        app.Resources["SystemAccentColorLight3"] = Tint(accent, 0.54);

        // 自前スタイル用（設定タブのナビ選択線・タブの固定表示など）
        app.Resources["AccentBrush"] = new SolidColorBrush(accent);
        app.Resources["AccentSelectionBrush"] = new SolidColorBrush(Color.FromArgb(0x38, accent.R, accent.G, accent.B));
        app.Resources["AccentHoverBrush"] = new SolidColorBrush(Color.FromArgb(0x20, accent.R, accent.G, accent.B));
    }

    /// <summary>黒方向へ暗くする（factor = 残す明るさの割合）。</summary>
    private static Color Shade(Color color, double factor)
        => Color.FromRgb((byte)(color.R * factor), (byte)(color.G * factor), (byte)(color.B * factor));

    /// <summary>白方向へ明るくする。</summary>
    private static Color Tint(Color color, double amount)
        => Color.FromRgb(
            (byte)(color.R + ((255 - color.R) * amount)),
            (byte)(color.G + ((255 - color.G) * amount)),
            (byte)(color.B + ((255 - color.B) * amount)));
}
