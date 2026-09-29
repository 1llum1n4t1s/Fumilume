namespace Fumilume.Services;

/// <summary>
/// アプリのユーザーデータ置き場。
///
/// テストが実ユーザーの settings.json を書き換えてしまわないよう、置き場を差し替えられるようにしてある
/// （差し替えはテストからのみ）。
/// </summary>
public static class AppStoragePaths
{
    private static string? _overrideDirectory;

    /// <summary>設定などを置くディレクトリ。存在しない場合は書き込み側で作る。</summary>
    public static string Directory => _overrideDirectory ?? (OperatingSystem.IsMacOS()
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Fumilume")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fumilume"));

    /// <summary>テスト用に置き場を差し替える。null で既定へ戻す。</summary>
    internal static void OverrideDirectory(string? directory) => _overrideDirectory = directory;
}
