namespace Fumilume.Services;

/// <summary>macOS の関連付けはアプリバンドルの Info.plist で宣言する。</summary>
public static class FileAssociationService
{
    public static IReadOnlyList<SupportedFileType> SupportedTypes => SupportedFileTypes.All;

    public static IReadOnlyDictionary<string, bool> GetCurrentAssociationStatus()
        => new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> ApplyAssociations(IEnumerable<string> selectedExtensions)
        => throw new PlatformNotSupportedException("macOS のファイル関連付けは Finder で変更してください。");

    public static void RefreshAssociatedFileTypes()
    {
    }

    public static bool DisassociateAllFileTypes() => true;
}
