using CommunityToolkit.Mvvm.ComponentModel;

namespace Fumilume.ViewModels;

public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    private double _folderTreeWidth = 240;

    /// <summary>既存の直列オープン経路へ接続し、読込成功後にのみ以前の一時タブを置換する。</summary>
    public async Task OpenFolderFileAsync(string path, bool permanent)
    {
        await WaitForInitializationAsync();
        await _openPathsGate.WaitAsync();
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (FindOpenFileTab(fullPath) is { } existing)
            {
                if (permanent)
                {
                    existing.IsPreview = false;
                }
                SelectedTab = existing;
                return;
            }

            var previousPreview = Tabs.FirstOrDefault(tab => tab.IsPreview);
            await OpenPathsCoreAsync([fullPath]);
            if (FindOpenFileTab(fullPath) is not { } opened)
            {
                return;
            }

            opened.IsPreview = !permanent;
            if (!permanent && previousPreview is { IsPreview: true, IsPinned: false }
                && previousPreview is not DocumentViewModel { IsModified: true })
            {
                await CloseTabCoreAsync(previousPreview);
            }
        }
        finally
        {
            _openPathsGate.Release();
        }
    }
}
