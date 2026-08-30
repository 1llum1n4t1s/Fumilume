using Fumilume.Services;

namespace Fumilume.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public async Task ASecondInstanceForwardsItsArgumentsToTheFirstInstance()
    {
        using var storage = new TemporaryStorage();
        var instanceName = $"test-{Guid.NewGuid():N}";
        using var first = SingleInstanceCoordinator.Create(instanceName, storage.Path);
        using var second = SingleInstanceCoordinator.Create(instanceName, storage.Path);
        var received = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var forwarded = await second.ForwardArgumentsAsync(
            [@"C:\docs\one.md", @"C:\docs\日本語.txt"],
            TestContext.Current.CancellationToken);
        first.SetArgumentsHandler(arguments => received.TrySetResult(arguments));

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
        Assert.True(forwarded);
        Assert.Equal(
            [@"C:\docs\one.md", @"C:\docs\日本語.txt"],
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
}
