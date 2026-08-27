using SuperLightLogger;
using System.Diagnostics;

namespace Fumilume.Services;

/// <summary>アプリ全体のログ設定と終了処理を 1 箇所で管理する。</summary>
public static class AppLogger
{
    private static int _initialized;

    public static string LogDirectory => Path.Combine(AppStoragePaths.Directory, "logs");

    public static bool IsInitialized => Volatile.Read(ref _initialized) != 0;

    public static ILog For<T>() => LogManager.GetLogger<T>();

    public static ILog For(string category) => LogManager.GetLogger(category);

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(LogDirectory);
            LogManager.Configure(builder =>
            {
                builder.SetMinimumLevel("Info");
                builder.AddSuperLightFile(options =>
                {
                    options.FileName = Path.Combine(LogDirectory, "fumilume_${shortdate}.log");
                    options.Layout =
                        @"${date:format=yyyy-MM-dd HH\:mm\:ss.ffff} [${level:uppercase=true}] " +
                        @"[${logger}] ${message}${onexception:${newline}${exception:format=tostring}}";
                    options.ArchiveAboveSize = 5L * 1024 * 1024;
                    options.ArchiveNumbering = ArchiveNumbering.Rolling;
                    options.MaxArchiveFiles = 10;
                    options.Async = true;
                    options.AsyncBufferSize = 4096;
                    options.AsyncFlushInterval = TimeSpan.FromSeconds(1);
                    options.MinLevelName = "Info";
                });
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // ログ先だけが書き込み不可でもエディタは起動させる。構成済みの無出力 factory なら
            // GetLogger 時の「未構成」警告も出ず、呼び出し側の分岐を増やさずに済む。
            Debug.WriteLine($"Fumilume のログを初期化できませんでした: {ex}");
            LogManager.Configure(builder => builder.SetMinimumLevel("None"));
        }
    }

    public static void Shutdown()
    {
        if (Interlocked.Exchange(ref _initialized, 0) == 0)
        {
            return;
        }

        LogManager.Shutdown();
    }
}
