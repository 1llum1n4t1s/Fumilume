using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Headless;
using Fumilume.Services;

namespace Fumilume.Tests;

/// <summary>
/// Avalonia のコントロールツリーをプロセス内で動かすための UI スレッド。
///
/// Avalonia 公式の <c>HeadlessUnitTestSession</c> はこの環境（Avalonia 12.1.1 / net10 / xunit.v3）で
/// 生成時に戻ってこないため使わない。代わりに専用スレッドを 1 本立てて、そこで
/// <see cref="AppBuilder.SetupWithoutStarting"/> を済ませ、テストの処理を同じスレッドへ送る。
/// Dispatcher はセットアップしたスレッドに紐づくので、UI 操作はすべてここを通す必要がある。
/// </summary>
public sealed class HeadlessAppFixture : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public HeadlessAppFixture()
    {
        var ready = new ManualResetEventSlim();
        Exception? setupFailure = null;

        _thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<App>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                    .ConfigureFonts(fontManager =>
                        fontManager.AddFontCollection(new FumilumeFontCollection()))
                    .SetupWithoutStarting();
            }
            catch (Exception ex)
            {
                setupFailure = ex;
            }
            finally
            {
                ready.Set();
            }

            foreach (var action in _queue.GetConsumingEnumerable())
            {
                action();
            }
        })
        {
            IsBackground = true,
            Name = "avalonia-headless",
        };

        if (OperatingSystem.IsWindows())
        {
            _thread.SetApartmentState(ApartmentState.STA);
        }

        _thread.Start();
        ready.Wait();

        if (setupFailure is not null)
        {
            throw new InvalidOperationException("ヘッドレス Avalonia を初期化できませんでした。", setupFailure);
        }
    }

    /// <summary>UI スレッドで処理を実行し、終わるまで待つ。例外は呼び出し側へそのまま投げ直す。</summary>
    public void Run(Action action)
    {
        Exception? failure = null;
        var done = new ManualResetEventSlim();

        _queue.Add(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();
        if (failure is not null)
        {
            throw failure;
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>UI スレッドは 1 本しか無いので、使うテストは同じコレクションへ入れて直列化する。</summary>
[CollectionDefinition(HeadlessAppCollection.Name)]
public sealed class HeadlessAppCollection : ICollectionFixture<HeadlessAppFixture>
{
    public const string Name = "avalonia-headless";
}

/// <summary>設定の読み書きを一時ディレクトリへ隔離する。実ユーザーの settings.json を壊さないため。</summary>
public sealed class TemporaryStorage : IDisposable
{
    public TemporaryStorage()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"fumilume-tests-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Path);
        AppStoragePaths.OverrideDirectory(Path);
    }

    /// <summary>この隔離中に使われる設定の置き場。</summary>
    public string Path { get; }

    public void Dispose()
    {
        AppStoragePaths.OverrideDirectory(null);
        try
        {
            System.IO.Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 後始末の失敗はテスト結果に影響させない。
        }
    }
}
