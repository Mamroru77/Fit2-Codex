using System.Threading.Channels;
using CodexQuota.Codex.Protocol;

namespace CodexQuota.Codex.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IJsonRpcTransport"/> for RPC tests. Inbound lines are supplied by the
/// test; written lines are recorded in order and can be awaited without sleeping.
/// </summary>
internal sealed class FakeJsonRpcTransport : IJsonRpcTransport
{
    private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
    private readonly List<string> _written = [];
    private readonly object _gate = new();

    private TaskCompletionSource _writeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Lines written by the client, in write order.</summary>
    internal IReadOnlyList<string> WrittenLines
    {
        get
        {
            lock (_gate)
            {
                return _written.ToArray();
            }
        }
    }

    /// <summary>Queues a line that the client will read as if it came from the child process.</summary>
    internal void EnqueueLine(string line) => _inbound.Writer.TryWrite(line);

    /// <summary>Signals end of the inbound stream.</summary>
    internal void CompleteInbound() => _inbound.Writer.TryComplete();

    /// <summary>Waits until at least <paramref name="count"/> lines have been written.</summary>
    internal async Task WaitForWritesAsync(int count, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task signal;

            lock (_gate)
            {
                if (_written.Count >= count)
                {
                    return;
                }

                signal = _writeSignal.Task;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource signal;

        lock (_gate)
        {
            _written.Add(line);
            signal = _writeSignal;
            _writeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.TrySetResult();
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken)
        => _inbound.Reader.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
