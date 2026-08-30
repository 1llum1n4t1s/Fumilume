using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Fumilume.Services;

/// <summary>
/// 同じユーザーの Fumilume を 1 プロセスにまとめ、後続起動の引数を最初のプロセスへ渡す。
/// セッションの一覧と未保存バッファを複数プロセスが別々の時点から上書きしないための境界でもある。
/// </summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly FileStream? _lease;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _handlerGate = new();
    private readonly Queue<IReadOnlyList<string>> _pendingArguments = new();
    private Action<IReadOnlyList<string>>? _argumentsHandler;
    private bool _disposed;

    private SingleInstanceCoordinator(FileStream? lease, string pipeName)
    {
        _lease = lease;
        _pipeName = pipeName;
        if (IsPrimary)
        {
            _ = ListenAsync();
        }
    }

    public bool IsPrimary => _lease is not null;

    public static SingleInstanceCoordinator Create(
        string instanceName = "Fumilume",
        string? lockDirectory = null)
    {
        var directory = Path.GetFullPath(lockDirectory ?? AppStoragePaths.Directory);
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, $"{instanceName}.instance.lock");

        FileStream? lease;
        try
        {
            lease = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            lease = null;
        }

        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lockPath)))[..24];
        return new SingleInstanceCoordinator(lease, $"Kagayoi.Fumilume.{identity}");
    }

    /// <summary>後続プロセスから届いた引数の受け手を登録する。UI 準備前に届いた分も順に渡す。</summary>
    public void SetArgumentsHandler(Action<IReadOnlyList<string>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        IReadOnlyList<string>[] pending;
        lock (_handlerGate)
        {
            _argumentsHandler = handler;
            pending = [.. _pendingArguments];
            _pendingArguments.Clear();
        }

        foreach (var arguments in pending)
        {
            handler(arguments);
        }
    }

    /// <summary>後続プロセスの起動引数を最初のプロセスへ転送する。</summary>
    public async Task<bool> ForwardArgumentsAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        if (IsPrimary)
        {
            return false;
        }

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(3000, cancellationToken);

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
            foreach (var argument in arguments)
            {
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(argument));
                await writer.WriteLineAsync(encoded.AsMemory(), cancellationToken);
            }

            await writer.FlushAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            AppLogger.For<SingleInstanceCoordinator>().Warn("起動引数を既存の Fumilume へ転送できませんでした。", ex);
            return false;
        }
    }

    private async Task ListenAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stopping.Token);

                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var encoded = await reader.ReadToEndAsync(_stopping.Token);
                DispatchArguments(DecodeArguments(encoded));
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                AppLogger.For<SingleInstanceCoordinator>().Warn("後続起動の引数を受信できませんでした。", ex);
            }
        }
    }

    private static IReadOnlyList<string> DecodeArguments(string payload)
        => payload.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line)))
            .ToArray();

    private void DispatchArguments(IReadOnlyList<string> arguments)
    {
        Action<IReadOnlyList<string>>? handler;
        lock (_handlerGate)
        {
            handler = _argumentsHandler;
            if (handler is null)
            {
                _pendingArguments.Enqueue(arguments);
                return;
            }
        }

        handler(arguments);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();
        _lease?.Dispose();
        _stopping.Dispose();
    }
}
