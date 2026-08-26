using Fumilume.Services;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace Fumilume.Tests;

[SupportedOSPlatform("windows")]
public sealed class FileAssociationServiceTests : IDisposable
{
    private readonly string _testRootPath =
        $@"Software\Fumilume.Tests\FileAssociation\{Guid.NewGuid():N}";
    private readonly string _applicationPath =
        Path.Combine(Path.GetTempPath(), "Fumilume", "Fumilume.exe");

    [Fact]
    public void AssociateFileType_RegistersProgIdIconAndQuotedOpenCommand()
    {
        FileAssociationService.AssociateFileType(
            Registry.CurrentUser,
            _testRootPath,
            "txt",
            _applicationPath);

        using var extensionKey = Registry.CurrentUser.OpenSubKey($@"{_testRootPath}\.txt");
        using var iconKey = Registry.CurrentUser.OpenSubKey($@"{_testRootPath}\Fumilume.txt\DefaultIcon");
        using var commandKey = Registry.CurrentUser.OpenSubKey(
            $@"{_testRootPath}\Fumilume.txt\shell\open\command");

        Assert.Equal("Fumilume.txt", extensionKey?.GetValue(""));
        Assert.Equal($"\"{_applicationPath}\",0", iconKey?.GetValue(""));
        Assert.Equal($"\"{_applicationPath}\" \"%1\"", commandKey?.GetValue(""));
        Assert.True(FileAssociationService.IsFileTypeAssociated(
            Registry.CurrentUser,
            _testRootPath,
            ".txt",
            _applicationPath));
    }

    [Fact]
    public void DisassociateFileType_PreservesSharedExtensionData()
    {
        var extensionPath = $@"{_testRootPath}\.md";
        using (var extensionKey = Registry.CurrentUser.CreateSubKey(extensionPath))
        {
            extensionKey.SetValue("", "Fumilume.md");
            extensionKey.SetValue("Content Type", "text/markdown");
            using var openWithKey = extensionKey.CreateSubKey("OpenWithProgids");
            openWithKey.SetValue("Fumilume.md", Array.Empty<byte>(), RegistryValueKind.None);
            openWithKey.SetValue("OtherEditor.md", Array.Empty<byte>(), RegistryValueKind.None);
        }
        Registry.CurrentUser.CreateSubKey($@"{_testRootPath}\Fumilume.md")!.Dispose();

        FileAssociationService.DisassociateFileType(
            Registry.CurrentUser,
            _testRootPath,
            ".md");

        using var remainingExtensionKey = Registry.CurrentUser.OpenSubKey(extensionPath);
        using var remainingOpenWithKey = remainingExtensionKey?.OpenSubKey("OpenWithProgids");
        Assert.Null(remainingExtensionKey?.GetValue(""));
        Assert.Equal("text/markdown", remainingExtensionKey?.GetValue("Content Type"));
        Assert.Null(remainingOpenWithKey?.GetValue("Fumilume.md"));
        Assert.NotNull(remainingOpenWithKey?.GetValue("OtherEditor.md"));
        Assert.Null(Registry.CurrentUser.OpenSubKey($@"{_testRootPath}\Fumilume.md"));
    }

    [Fact]
    public void DisassociateFileType_PreservesAnotherApplicationsDefault()
    {
        using (var extensionKey = Registry.CurrentUser.CreateSubKey($@"{_testRootPath}\.json"))
        {
            extensionKey.SetValue("", "OtherEditor.json");
        }
        Registry.CurrentUser.CreateSubKey($@"{_testRootPath}\Fumilume.json")!.Dispose();

        FileAssociationService.DisassociateFileType(
            Registry.CurrentUser,
            _testRootPath,
            ".json");

        using var remainingExtensionKey = Registry.CurrentUser.OpenSubKey($@"{_testRootPath}\.json");
        Assert.Equal("OtherEditor.json", remainingExtensionKey?.GetValue(""));
        Assert.Null(Registry.CurrentUser.OpenSubKey($@"{_testRootPath}\Fumilume.json"));
    }

    public void Dispose()
        => Registry.CurrentUser.DeleteSubKeyTree(_testRootPath, throwOnMissingSubKey: false);
}
