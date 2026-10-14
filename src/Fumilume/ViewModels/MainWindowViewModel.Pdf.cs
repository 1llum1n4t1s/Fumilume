using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;

namespace Fumilume.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool CanCopyPdfSelection() => SelectedPdf?.HasTextSelection == true;

    private void OnPdfSelectionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PdfDocumentViewModel.HasTextSelection))
            CopyPdfSelectionCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCopyPdfSelection))]
    private async Task CopyPdfSelectionAsync()
    {
        if (SelectedPdf is not { HasTextSelection: true } pdf) return;
        var text = pdf.SelectedText;
        if (text.Length == 0) return;
        try
        {
            await _dialogs.CopyTextAsync(text);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("PDF の文字をコピーできませんでした", ex.Message);
        }
    }
}
