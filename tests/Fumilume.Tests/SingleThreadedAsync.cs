using System.Collections.Concurrent;

namespace Fumilume.Tests;

/// <summary>
/// 非同期の続きを、呼び出したスレッドへ戻して実行する。
///
/// 製品では Avalonia の同期コンテキストが働くので、<c>await</c> のあとは必ず UI スレッドへ戻る。
/// 素の xUnit にはそれが無く、<c>Task.Run</c> を挟んだ処理の続きはスレッドプールへ散る。
/// AvaloniaEdit の <c>TextDocument</c> は生成したスレッド以外からの操作を弾くため、そのままでは
/// 「製品では起きない失敗」がテストにだけ出る。ここで同じ条件を作って、その差を消す。
/// </summary>
public static class SingleThreadedAsync
{
    /// <summary>キューが空のまま待ち続けないための上限。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static void Run(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        var context = new QueueSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var task = body();
            context.DrainUntil(task);
            // 例外はここで元の形のまま投げ直す。
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class QueueSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Current == this)
            {
                d(state);
                return;
            }

            throw new NotSupportedException("別スレッドからの同期呼び出しには対応していません。");
        }

        /// <summary>指定した処理が終わるまで、戻ってきた続きを順に実行する。</summary>
        public void DrainUntil(Task task)
        {
            while (!task.IsCompleted)
            {
                if (!_queue.TryTake(out var work, Timeout))
                {
                    throw new TimeoutException("非同期処理が終わりませんでした。");
                }

                work.Callback(work.State);
            }

            // 完了後に積まれた続き（finally や継続）も流し切る。
            while (_queue.TryTake(out var remaining, TimeSpan.Zero))
            {
                remaining.Callback(remaining.State);
            }
        }
    }
}
