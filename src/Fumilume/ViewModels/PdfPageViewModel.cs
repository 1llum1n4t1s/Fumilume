using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Fumilume.Models;

namespace Fumilume.ViewModels;

/// <summary>連続表示の 1 ページ。配置・画像・文字情報・描画状態を保持する。</summary>
public sealed partial class PdfPageViewModel : ObservableObject
{
    internal PdfPageViewModel(PdfDocumentViewModel owner, int index)
    {
        Owner = owner;
        Index = index;
    }

    public PdfDocumentViewModel Owner { get; }

    /// <summary>0 始まりのページ番号。</summary>
    public int Index { get; }

    /// <summary>1 始まりのページ番号。</summary>
    public int Number => Index + 1;

    [ObservableProperty]
    private double _left;

    [ObservableProperty]
    private double _top;

    [ObservableProperty]
    private double _width;

    [ObservableProperty]
    private double _height;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoadingText))]
    private Bitmap? _image;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoadingText))]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private PdfTextPage? _textPage;

    /// <summary>画像がまだ無いページで描画を待っている間だけ、読み込み中の表示を出す。</summary>
    public bool ShowLoadingText => IsLoading && Image is null;

    /// <summary>画像を描いた世代。倍率が変わると世代が進み、古い画像は描き直しの対象になる。</summary>
    internal int RenderedGeneration { get; set; } = -1;

    internal void ReleaseImage()
    {
        var image = Image;
        Image = null;
        RenderedGeneration = -1;
        image?.Dispose();
    }
}
