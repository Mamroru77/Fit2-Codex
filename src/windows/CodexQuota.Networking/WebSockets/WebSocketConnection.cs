using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace CodexQuota.Networking.WebSockets;

/// <summary>
/// One client connection, with a single writer.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="WebSocket"/> must never be written from two threads at once, and a slow client must
/// never be able to hold up the Bridge. Both problems are solved the same way: producers only ever
/// enqueue a fully serialised message onto a bounded channel, and one dedicated send loop owns the
/// socket.
/// </para>
/// <para>
/// When that channel fills, the client is not keeping up. It is aborted rather than allowed to
/// apply back-pressure to quota state updates, which belong to the whole Bridge and not to one
/// connection.
/// </para>
/// </remarks>
internal sealed class WebSocketConnection : IAsyncDisposable
{
    /// <summary>Messages that may be queued before a client is considered too slow.</summary>
    internal const int OutboundCapacity = 64;

    private readonly WebSocket _socket;
    private readonly Channel<string> _outbound;
    private readonly CancellationTokenSource _closed;
    private readonly Task _pump;

    internal WebSocketConnection(WebSocket socket, CancellationToken requestAborted)
    {
        ArgumentNullException.ThrowIfNull(socket);

        _socket = socket;
        _closed = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        _outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(OutboundCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Completes once the connection has ended and the socket is closed.</summary>
    internal Task Completion => _pump;

    /// <summary>True while this connection is still accepting messages.</summary>
    internal bool IsOpen => !_closed.IsCancellationRequested;

    /// <summary>
    /// Queues a message. Returns <c>false</c> when the client is not keeping up, which the caller
    /// turns into an abort.
    /// </summary>
    internal bool TryEnqueue(string message)
        => IsOpen && _outbound.Writer.TryWrite(message);

    /// <summary>
    /// Ends the connection gracefully: queued messages are flushed, then the socket is closed
    /// normally. Used for an orderly Bridge shutdown.
    /// </summary>
    internal Task CloseAsync()
    {
        _outbound.Writer.TryComplete();
        return _pump;
    }

    /// <summary>
    /// Ends the connection immediately. Used for a client that cannot keep up and for disposal.
    /// </summary>
    internal async Task AbortAsync()
    {
        _outbound.Writer.TryComplete();
        await _closed.CancelAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await AbortAsync().ConfigureAwait(false);

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The pump already absorbs its own failures; this is the last line of defence.
        }

        _closed.Dispose();
    }

    private async Task PumpAsync()
    {
        var sending = SendLoopAsync();
        var receiving = ReceiveLoopAsync();

        // Whichever side finishes first ends the connection: a closed socket must not leave the
        // other loop parked on a read that will never complete.
        await Task.WhenAny(sending, receiving).ConfigureAwait(false);

        await _closed.CancelAsync().ConfigureAwait(false);
        _outbound.Writer.TryComplete();

        await Task.WhenAll(Ignore(sending), Ignore(receiving)).ConfigureAwait(false);

        await CloseSocketAsync().ConfigureAwait(false);
    }

    private async Task SendLoopAsync()
    {
        await foreach (var message in _outbound.Reader.ReadAllAsync(_closed.Token).ConfigureAwait(false))
        {
            await _socket
                .SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, _closed.Token)
                .ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[4096];

        while (!_closed.IsCancellationRequested)
        {
            var result = await _socket.ReceiveAsync(buffer, _closed.Token).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            // Client frames are not part of v1. They are read and discarded, which is what keeps a
            // heartbeat or a stray frame from stalling the connection.
        }
    }

    private async Task CloseSocketAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

                await _socket
                    .CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The client is already gone, or the close could not be delivered. Nothing left to do.
        }
    }

    private static async Task Ignore(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A socket that failed mid-send is an ordinary disconnect.
        }
    }
}
