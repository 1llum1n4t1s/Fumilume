using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace Fumilume.Services;

public sealed class EditorDialogService(Window owner) : IEditorDialogService
{
    private static readonly FilePickerFileType TextFileType = new("テキストファイル")
    {
        Patterns = FileAssociationService.SupportedTypes
            .Select(type => $"*{type.Extension}")
            .ToArray(),
    };

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
        var dialog = CreateDialog($"{documentName} の変更を保存しますか？", out var buttons);
        var save = new Button { Content = "保存", IsDefault = true, MinWidth = 88 };
        var discard = new Button { Content = "保存しない", MinWidth = 104 };
        var cancel = new Button { Content = "キャンセル", IsCancel = true, MinWidth = 88 };
        buttons.Children.Add(save);
        buttons.Children.Add(discard);
        buttons.Children.Add(cancel);

        save.Click += (_, _) => dialog.Close(UnsavedDocumentDecision.Save);
        discard.Click += (_, _) => dialog.Close(UnsavedDocumentDecision.Discard);
        cancel.Click += (_, _) => dialog.Close(UnsavedDocumentDecision.Cancel);

        return await dialog.ShowDialog<UnsavedDocumentDecision>(owner);
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        var dialog = CreateDialog(message, out var buttons);
        dialog.Title = title;
        var close = new Button { Content = "閉じる", IsDefault = true, IsCancel = true, MinWidth = 88 };
        buttons.Children.Add(close);
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }

    public Task CheckForUpdatesAsync(bool manually)
        => UpdateService.CheckAsync(owner, manually);

    public async Task ConfigureFileAssociationsAsync()
    {
        var status = FileAssociationService.GetCurrentAssociationStatus();
        var checkBoxes = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        var associationList = new StackPanel { Spacing = 2 };

        foreach (var type in FileAssociationService.SupportedTypes)
        {
            var checkBox = new CheckBox
            {
                Content = type.Description,
                IsChecked = status.GetValueOrDefault(type.Extension),
                Padding = new Avalonia.Thickness(8, 5),
            };
            checkBoxes[type.Extension] = checkBox;
            associationList.Children.Add(checkBox);
        }

        var selectAll = new Button { Content = "すべて選択", MinWidth = 96 };
        var clearAll = new Button { Content = "すべて解除", MinWidth = 96 };
        var apply = new Button { Content = "適用", IsDefault = true, MinWidth = 88 };
        var cancel = new Button { Content = "キャンセル", IsCancel = true, MinWidth = 88 };
        var selectionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { selectAll, clearAll },
        };
        var actionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { apply, cancel },
        };
        var dialog = new Window
        {
            Title = "ファイルの関連付け",
            Width = 500,
            Height = 650,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Fumilumeで開く拡張子を選択してください。変更は現在のユーザーにだけ適用されます。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    selectionButtons,
                    new Border
                    {
                        BorderBrush = Avalonia.Media.Brushes.Gray,
                        BorderThickness = new Avalonia.Thickness(1),
                        CornerRadius = new Avalonia.CornerRadius(8),
                        Padding = new Avalonia.Thickness(8),
                        Child = new ScrollViewer
                        {
                            Height = 430,
                            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                            Content = associationList,
                        },
                    },
                    actionButtons,
                },
            },
        };

        selectAll.Click += (_, _) => SetCheckedState(true);
        clearAll.Click += (_, _) => SetCheckedState(false);
        apply.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);

        if (!await dialog.ShowDialog<bool>(owner))
        {
            return;
        }

        var selectedExtensions = checkBoxes
            .Where(pair => pair.Value.IsChecked == true)
            .Select(pair => pair.Key);
        var failures = FileAssociationService.ApplyAssociations(selectedExtensions);
        if (failures.Count > 0)
        {
            await ShowErrorAsync(
                "ファイルの関連付けを変更できません",
                $"次の拡張子を変更できませんでした。\n\n{string.Join(", ", failures)}");
        }

        void SetCheckedState(bool isChecked)
        {
            foreach (var checkBox in checkBoxes.Values)
            {
                checkBox.IsChecked = isChecked;
            }
        }
    }

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
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { move, cancel },
        };
        var dialog = new Window
        {
            Title = "指定行へ移動",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = $"行番号を入力してください（1～{maximumLine:N0}）" },
                    input,
                    buttons,
                },
            },
        };

        void UpdateMoveState()
            => move.IsEnabled = int.TryParse(input.Text, out var value) && value >= 1 && value <= maximumLine;

        input.TextChanged += (_, _) => UpdateMoveState();
        move.Click += (_, _) =>
        {
            if (int.TryParse(input.Text, out var value) && value >= 1 && value <= maximumLine)
            {
                dialog.Close((int?)value);
            }
        };
        cancel.Click += (_, _) => dialog.Close((int?)null);
        dialog.AddHandler(
            InputElement.KeyDownEvent,
            (_, args) =>
            {
                if (args.Key == Key.Escape)
                {
                    args.Handled = true;
                    dialog.Close((int?)null);
                }
            },
            RoutingStrategies.Tunnel);
        dialog.Opened += (_, _) =>
        {
            input.SelectAll();
            input.Focus();
        };
        UpdateMoveState();

        return await dialog.ShowDialog<int?>(owner);
    }

    private static Window CreateDialog(string message, out StackPanel buttons)
    {
        buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        return new Window
        {
            Title = "Fumilume",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 22,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        FontSize = 14,
                    },
                    buttons,
                },
            },
        };
    }
}
