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
}
