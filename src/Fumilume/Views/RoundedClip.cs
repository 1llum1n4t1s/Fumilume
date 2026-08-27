using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Fumilume.Views;

/// <summary>
/// 角丸の <see cref="Border"/> の中身を、枠線の内側で角丸に切り抜く。
///
/// <see cref="Visual.ClipToBounds"/> は矩形でしか切り抜かないため、角丸の Border に色の付いた子
/// （メイン画面のステータスバー、ダイアログの操作バー）を敷くと、子の四角い塗りが角の内側まで届き、
/// その角だけ枠線が上書きされて消える。上端は背景を持たない行なので影響が出ず、
/// 「下の 2 つの角だけ線が欠けて見える」という症状になる。
///
/// 親の Border 自身をクリップしても直らない。切り抜きの縁と枠線が同じ位置に重なるため、
/// 子は依然として枠線の帯を塗りつぶすからである。そこで切り抜くのは子のほうにし、
/// 半径も枠線ぶん内側（CornerRadius − BorderThickness）へ縮める。
/// <see cref="Visual.Clip"/> は要素自身の座標系（原点 0,0）で効き、子は既に枠線の内側へ
/// 配置されているので、子の実寸をそのまま使えばよい。
/// </summary>
internal static class RoundedClip
{
    /// <summary>角丸クリップを適用し、以後のサイズ変更にも追従させる。</summary>
    public static void Attach(Border? border)
    {
        if (border?.Child is not Control child)
        {
            return;
        }

        Apply(border, child);
        child.SizeChanged += (_, _) => Apply(border, child);
    }

    private static void Apply(Border border, Control child)
    {
        var size = child.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            child.Clip = null;
            return;
        }

        // 枠線は全辺同じ太さで使っているので左だけを見る。
        var inset = border.BorderThickness.Left;
        var radius = Math.Max(0, border.CornerRadius.TopLeft - inset);
        child.Clip = new RectangleGeometry(new Rect(size))
        {
            RadiusX = radius,
            RadiusY = radius,
        };
    }
}
