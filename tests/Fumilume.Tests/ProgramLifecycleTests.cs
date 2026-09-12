using Avalonia.Controls;
using Fumilume.Services;
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

    [Fact]
    public void AFailedForwardIsReportedInsteadOfEndingSilently()
    {
        using var storage = new TemporaryStorage();
        var instanceName = $"blocked-{Guid.NewGuid():N}";
        var lockPath = Path.Combine(storage.Path, $"{instanceName}.instance.lock");
        using var blockingLease = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        using var secondary = SingleInstanceCoordinator.Create(instanceName, storage.Path);
        var messages = new List<string>();

        Program.ForwardArgumentsOrReport(
            secondary,
            ["notes.md"],
            messages.Add,
            connectionTimeoutMilliseconds: 20);

        Assert.False(secondary.IsPrimary);
        Assert.Single(messages);
        Assert.Contains("渡せませんでした", messages[0]);
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
