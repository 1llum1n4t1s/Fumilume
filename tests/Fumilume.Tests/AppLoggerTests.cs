using Fumilume.Services;

namespace Fumilume.Tests;

public sealed class AppLoggerTests
{
    [Fact]
    public void SuperLightLoggerWritesAndFlushesTheApplicationLog()
    {
        using var storage = new TemporaryStorage();
        AppLogger.Initialize();
        try
        {
            AppLogger.For<AppLoggerTests>().Info("logger integration test");
        }
        finally
        {
            AppLogger.Shutdown();
        }

        var logFile = Assert.Single(Directory.GetFiles(AppLogger.LogDirectory, "fumilume_*.log"));
        Assert.Contains("logger integration test", File.ReadAllText(logFile), StringComparison.Ordinal);
    }
}
