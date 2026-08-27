using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Fumilume.Views;

namespace Fumilume.Services;

public sealed class EditorDialogService(Window owner) : IEditorDialogService
{
    private static readonly FilePickerFileType TextFileType = new("テキストファイル")
    {
        Patterns = FileAssociationService.SupportedTypes
            .Select(type => $"*{type.Extension}")
            .ToArray(),
    };

    /// <summary>ダイアログの地は設定のアクリル可否に合わせる（本体だけ透けてダイアログが浮くのを防ぐ）。
    /// 所有ウィンドウの DataContext はこのサービスより後に決まるため、生成時ではなく都度読む。</summary>
    private bool UseAcrylic
        => (owner.DataContext as ViewModels.MainWindowViewModel)?.Options.UseAcrylic ?? true;

    public async Task<IReadOnlyList<string>> PickOpenPathsAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "テキストファイルを開く",
            AllowMultiple = true,
            FileTypeFilter = [TextFileType, FilePickerFileTypes.All],
        });

        return files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();
    }

    public async Task<string?> PickSavePathAsync(string suggestedFileName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "名前を付けて保存",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "txt",
            FileTypeChoices = [TextFileType, FilePickerFileTypes.All],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<UnsavedDocumentDecision> ConfirmUnsavedAsync(string documentName)
    {
        var decision = UnsavedDocumentDecision.Cancel;
        var save = new Button { Content = "保存", IsDefault = true, MinWidth = 88 };
        var discard = new Button { Content = "保存しない", MinWidth = 104 };
        var cancel = new Button { Content = "キャンセル", IsCancel = true, MinWidth = 88 };
        var dialog = new AppDialogWindow(
            "変更の保存",
            UseAcrylic,
            CreateMessage($"{documentName} の変更を保存しますか？"),
            save,
            discard,
            cancel);

        save.Click += (_, _) =>
        {
            decision = UnsavedDocumentDecision.Save;
            dialog.Close();
        };
        discard.Click += (_, _) =>
        {
            decision = UnsavedDocumentDecision.Discard;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner);
        return decision;
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        var close = new Button { Content = "閉じる", IsDefault = true, IsCancel = true, MinWidth = 88 };
        var dialog = new AppDialogWindow(title, UseAcrylic, CreateMessage(message), close);
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }

    public Task CheckForUpdatesAsync(bool manually)
        => UpdateService.CheckAsync(owner, manually);

    public async Task<int?> PickLineNumberAsync(int currentLine, int maximumLine)
    {
        var input = new TextBox
        {
            Text = currentLine.ToString(System.Globalization.CultureInfo.CurrentCulture),
            Width = 140,
            HorizontalContentAlignment = HorizontalAlignment.Right,
        };
        var move = new Button { Content = "移動", IsDefault = true, MinWidth = 88 };
        var cancel = new Button { Content = "キャンセル", IsCancel = true, MinWidth = 88 };
        var dialog = new AppDialogWindow(
            "指定行へ移動",
            UseAcrylic,
            new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = $"行番号を入力してください（1～{maximumLine:N0}）" },
                    input,
                },
            },
            move,
            cancel)
        {
            Width = 380,
        };

        void UpdateMoveState()
            => move.IsEnabled = int.TryParse(input.Text, out var value) && value >= 1 && value <= maximumLine;

        int? selectedLine = null;
        input.TextChanged += (_, _) => UpdateMoveState();
        move.Click += (_, _) =>
        {
            if (int.TryParse(input.Text, out var value) && value >= 1 && value <= maximumLine)
            {
                selectedLine = value;
                dialog.Close();
            }
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.AddHandler(
            InputElement.KeyDownEvent,
            (_, args) =>
            {
                if (args.Key == Key.Escape)
                {
                    args.Handled = true;
                    dialog.Close();
                }
            },
            RoutingStrategies.Tunnel);
        dialog.Opened += (_, _) =>
        {
            input.SelectAll();
            input.Focus();
        };
        UpdateMoveState();

        await dialog.ShowDialog(owner);
        return selectedLine;
    }

    public async Task<string?> PromptTextAsync(string title, string message, string initialText)
    {
        var input = new TextBox { Text = initialText, MinWidth = 300 };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 88 };
        var cancel = new Button { Content = "キャンセル", IsCancel = true, MinWidth = 88 };
        var dialog = new AppDialogWindow(
            title,
            UseAcrylic,
            new StackPanel
            {
                Spacing = 14,
                Children = { CreateMessage(message), input },
            },
            ok,
            cancel)
        {
            Width = 440,
        };

        string? result = null;
        ok.Click += (_, _) =>
        {
            result = input.Text ?? string.Empty;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.AddHandler(
            InputElement.KeyDownEvent,
            (_, args) =>
            {
                if (args.Key == Key.Escape)
                {
                    args.Handled = true;
                    dialog.Close();
                }
            },
            RoutingStrategies.Tunnel);
        dialog.Opened += (_, _) =>
        {
            input.SelectAll();
            input.Focus();
        };

        await dialog.ShowDialog(owner);
        return result;
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var yes = new Button { Content = "はい", IsDefault = true, MinWidth = 88 };
        var no = new Button { Content = "いいえ", IsCancel = true, MinWidth = 88 };
        var dialog = new AppDialogWindow(title, UseAcrylic, CreateMessage(message), yes, no)
        {
            Width = 440,
        };

        var accepted = false;
        yes.Click += (_, _) =>
        {
            accepted = true;
            dialog.Close();
        };
        no.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner);
        return accepted;
    }

    private static TextBlock CreateMessage(string message)
        => new()
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 14,
        };
}
