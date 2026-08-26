using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Fumilume.Services;

public sealed record SupportedFileType(string Extension, string Description);

/// <summary>
/// 現在のWindowsユーザーに対するファイル関連付けを管理します。
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileAssociationService
{
    private const string ClassesRootPath = @"Software\Classes";
    private const string ProgIdPrefix = "Fumilume";
    private const uint AssociationChanged = 0x08000000;
    private const uint IdList = 0x0000;

    public static IReadOnlyList<SupportedFileType> SupportedTypes { get; } =
    [
        new(".txt", "テキスト文書 (.txt)"),
        new(".md", "Markdown文書 (.md)"),
        new(".log", "ログファイル (.log)"),
        new(".csv", "CSVファイル (.csv)"),
        new(".json", "JSONファイル (.json)"),
        new(".xml", "XMLファイル (.xml)"),
        new(".yaml", "YAMLファイル (.yaml)"),
        new(".yml", "YAMLファイル (.yml)"),
        new(".ini", "設定ファイル (.ini)"),
        new(".config", "構成ファイル (.config)"),
        new(".cs", "C#ソースファイル (.cs)"),
        new(".axaml", "Avalonia XAMLファイル (.axaml)"),
        new(".js", "JavaScriptファイル (.js)"),
        new(".ts", "TypeScriptファイル (.ts)"),
        new(".html", "HTMLファイル (.html)"),
        new(".css", "CSSファイル (.css)"),
    ];

    private static string ApplicationPath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Fumilume.exe");

    public static IReadOnlyDictionary<string, bool> GetCurrentAssociationStatus()
    {
        var status = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in SupportedTypes)
        {
            status[type.Extension] = IsFileTypeAssociated(
                Registry.CurrentUser,
                ClassesRootPath,
                type.Extension,
                ApplicationPath);
        }

        return status;
    }

    public static IReadOnlyList<string> ApplyAssociations(IEnumerable<string> selectedExtensions)
    {
        var selected = selectedExtensions
            .Select(NormalizeExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        var changed = false;

        foreach (var type in SupportedTypes)
        {
            try
            {
                if (selected.Contains(type.Extension))
                {
                    if (!IsFileTypeAssociated(
                            Registry.CurrentUser,
                            ClassesRootPath,
                            type.Extension,
                            ApplicationPath))
                    {
                        AssociateFileType(
                            Registry.CurrentUser,
                            ClassesRootPath,
                            type.Extension,
                            ApplicationPath);
                        changed = true;
                    }
                }
                else if (IsOwnedByFumilume(Registry.CurrentUser, ClassesRootPath, type.Extension))
                {
                    DisassociateFileType(Registry.CurrentUser, ClassesRootPath, type.Extension);
                    changed = true;
                }
            }
            catch
            {
                failures.Add(type.Extension);
            }
        }

        if (changed)
        {
            NotifyExplorer();
        }

        return failures;
    }

    public static void RefreshAssociatedFileTypes()
    {
        var changed = false;
        foreach (var type in SupportedTypes)
        {
            try
            {
                if (!IsOwnedByFumilume(Registry.CurrentUser, ClassesRootPath, type.Extension))
                {
                    continue;
                }

                AssociateFileType(
                    Registry.CurrentUser,
                    ClassesRootPath,
                    type.Extension,
                    ApplicationPath);
                changed = true;
            }
            catch
            {
                // Velopackの高速コールバックでは、1拡張子の失敗で更新を止めない。
            }
        }

        if (changed)
        {
            NotifyExplorer();
        }
    }

    public static bool DisassociateAllFileTypes()
    {
        var succeeded = true;
        foreach (var type in SupportedTypes)
        {
            try
            {
                DisassociateFileType(Registry.CurrentUser, ClassesRootPath, type.Extension);
            }
            catch
            {
                succeeded = false;
            }
        }

        NotifyExplorer();
        return succeeded;
    }

    internal static void AssociateFileType(
        RegistryKey root,
        string classesRootPath,
        string extension,
        string applicationPath)
    {
        extension = NormalizeExtension(extension);
        var progId = GetProgId(extension);
        var description = $"Fumilume {extension.ToUpperInvariant()} ファイル";

        using (var extensionKey = root.CreateSubKey($@"{classesRootPath}\{extension}"))
        {
            extensionKey.SetValue("", progId, RegistryValueKind.String);
            using var openWithKey = extensionKey.CreateSubKey("OpenWithProgids");
            openWithKey.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (var progIdKey = root.CreateSubKey($@"{classesRootPath}\{progId}"))
        {
            progIdKey.SetValue("", description, RegistryValueKind.String);
            progIdKey.SetValue("FriendlyTypeName", description, RegistryValueKind.String);
        }

        using (var iconKey = root.CreateSubKey($@"{classesRootPath}\{progId}\DefaultIcon"))
        {
            iconKey.SetValue("", $"\"{applicationPath}\",0", RegistryValueKind.String);
        }

        using var commandKey = root.CreateSubKey($@"{classesRootPath}\{progId}\shell\open\command");
        commandKey.SetValue("", BuildOpenCommand(applicationPath), RegistryValueKind.String);
    }

    internal static void DisassociateFileType(
        RegistryKey root,
        string classesRootPath,
        string extension)
    {
        extension = NormalizeExtension(extension);
        var progId = GetProgId(extension);
        var extensionKeyPath = $@"{classesRootPath}\{extension}";

        using (var extensionKey = root.OpenSubKey(extensionKeyPath, writable: true))
        {
            if (extensionKey is not null &&
                string.Equals(extensionKey.GetValue("") as string, progId, StringComparison.OrdinalIgnoreCase))
            {
                extensionKey.DeleteValue("", throwOnMissingValue: false);
            }

            using var openWithKey = extensionKey?.OpenSubKey("OpenWithProgids", writable: true);
            openWithKey?.DeleteValue(progId, throwOnMissingValue: false);
        }

        root.DeleteSubKeyTree($@"{classesRootPath}\{progId}", throwOnMissingSubKey: false);
    }

    internal static bool IsFileTypeAssociated(
        RegistryKey root,
        string classesRootPath,
        string extension,
        string applicationPath)
    {
        extension = NormalizeExtension(extension);
        if (!IsOwnedByFumilume(root, classesRootPath, extension))
        {
            return false;
        }

        using var commandKey = root.OpenSubKey(
            $@"{classesRootPath}\{GetProgId(extension)}\shell\open\command");
        return string.Equals(
            commandKey?.GetValue("") as string,
            BuildOpenCommand(applicationPath),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsOwnedByFumilume(
        RegistryKey root,
        string classesRootPath,
        string extension)
    {
        extension = NormalizeExtension(extension);
        using var extensionKey = root.OpenSubKey($@"{classesRootPath}\{extension}");
        return string.Equals(
            extensionKey?.GetValue("") as string,
            GetProgId(extension),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        return (extension.StartsWith('.') ? extension : $".{extension}").ToLowerInvariant();
    }

    private static string GetProgId(string extension) => $"{ProgIdPrefix}{extension}";

    private static string BuildOpenCommand(string applicationPath) =>
        $"\"{applicationPath}\" \"%1\"";

    private static void NotifyExplorer()
    {
        try
        {
            SHChangeNotify(AssociationChanged, IdList, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // 関連付け自体は完了しているため、Explorer通知の失敗は致命扱いにしない。
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        IntPtr item1,
        IntPtr item2);
}
