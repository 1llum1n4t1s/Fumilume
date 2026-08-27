using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace Fumilume.Views;

/// <summary>アプリ共通のダイアログ枠。メインウィンドウと同じ地・キャプション・角丸を使う。</summary>
public sealed partial class AppDialogWindow : Window
{
    private readonly ContentControl _contentHost;
    private readonly StackPanel _actionHost;
    private readonly bool _useAcrylic;

    /// <summary>XAML ローダー用（プレビューア）。</summary>
    public AppDialogWindow()
        : this(useAcrylic: true)
    {
    }

    public AppDialogWindow(bool useAcrylic)
    {
        InitializeComponent();
        _useAcrylic = useAcrylic;
        _contentHost = this.FindControl<ContentControl>("DialogContentHost")
            ?? throw new InvalidOperationException("ダイアログの内容領域を初期化できませんでした。");
        _actionHost = this.FindControl<StackPanel>("DialogActionHost")
            ?? throw new InvalidOperationException("ダイアログの操作領域を初期化できませんでした。");

        RoundedClip.Attach(this.FindControl<Border>("DialogSurface"));
        TransparencyLevelHint = useAcrylic
            ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None]
            : [WindowTransparencyLevel.None];
        UpdateBackdropLayers();
    }

    public AppDialogWindow(string title, Control content, params Button[] actions)
        : this(title, useAcrylic: true, content, actions)
    {
    }

    public AppDialogWindow(string title, bool useAcrylic, Control content, params Button[] actions)
        : this(useAcrylic)
    {
        Title = title;
        var titleBlock = this.FindControl<TextBlock>("DialogTitle")
            ?? throw new InvalidOperationException("ダイアログのタイトル領域を初期化できませんでした。");
        titleBlock.Text = title;
        _contentHost.Content = content;

        foreach (var action in actions)
        {
            action.Classes.Add("dialog");
            _actionHost.Children.Add(action);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActualTransparencyLevelProperty)
        {
            UpdateBackdropLayers();
        }
    }

    private void UpdateBackdropLayers()
    {
        var acrylicActive = _useAcrylic
            && ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur;
        var acrylic = this.FindControl<ExperimentalAcrylicBorder>("AcrylicLayer");
        var scrim = this.FindControl<Border>("AcrylicScrim");
        var fallback = this.FindControl<Border>("FallbackLayer");
        if (acrylic is not null) acrylic.IsVisible = acrylicActive;
        if (scrim is not null) scrim.IsVisible = acrylicActive;
        if (fallback is not null) fallback.IsVisible = !acrylicActive;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs args)
    {
        // 閉じるボタンの上はボタン自身の操作に任せる。
        if (args.Source is Visual visual && visual.FindAncestorOfType<Button>() is not null)
        {
            return;
        }

        if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(args);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs args) => Close();
}
