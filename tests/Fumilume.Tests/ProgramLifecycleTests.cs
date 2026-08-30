using Avalonia.Controls;
using Fumilume.Views;

namespace Fumilume.Tests;

public sealed class ProgramLifecycleTests
{
    [Fact]
    public void CleanupBeforeUninstall_RemovesFileAssociations()
    {
        var removed = false;

        Program.CleanupBeforeUninstall(() =>
        {
            removed = true;
            return true;
        });

        Assert.True(removed);
    }

    [Fact]
    public void RelativeForwardedPathsAreResolvedByTheSendingProcess()
    {
        var normalized = Program.NormalizeForwardedArguments(["notes.md", @"C:\docs\guide.md"]);

        Assert.Equal(Path.GetFullPath("notes.md"), normalized[0]);
        Assert.Equal(@"C:\docs\guide.md", normalized[1]);
    }

    [Theory]
    [InlineData(WindowCloseReason.OSShutdown, true)]
    [InlineData(WindowCloseReason.ApplicationShutdown, true)]
    [InlineData(WindowCloseReason.WindowClosing, false)]
    [InlineData(WindowCloseReason.OwnerWindowClosing, false)]
    public void OnlySystemShutdownUsesSynchronousSessionPersistence(
        WindowCloseReason reason,
        bool expected)
        => Assert.Equal(expected, MainWindow.RequiresSynchronousShutdownPersistence(reason));
}
