using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Fumilume.Views;

/// <summary>設定タブの中身。DataContext は <see cref="ViewModels.SettingsTabViewModel"/>。</summary>
public sealed partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
