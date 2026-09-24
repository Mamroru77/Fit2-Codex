using System.Collections.Concurrent;
using CodexQuota.Core.Quota;
using CodexQuota.Networking.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodexQuota.Networking.WebSockets;

/// <summary>
/// Fans quota state changes out to every authenticated WebSocket client.
/// </summary>
/// <remarks>
/// <para>
/// The hub is downstream of the quota state store, exactly like history persistence: it is told about
/// an update that has already been published, and a client that cannot keep up is dropped rather than
/// allowed to slow the publication down.
/// </para>
/// <para>
/// Authorization happens before this type ever sees a request. The upgrade route is not anonymous, so
/// the device authentication middleware rejects an unauthenticated upgrade with the standard v1 error
/// envelope and the socket is never accepted.
/// </para>
/// </remarks>
public sealed class QuotaWebSocketHub : IAsyncDisposable
{
    /// <summary>How long an orderly shutdown waits for queued frames to flush.</summary>
    public static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(3);

    private readonly IQuotaStateStore _store;
    private readonly BridgeEndpointOptions _options;
    private readonly ConcurrentDictionary<Guid, WebSocketConnection> _connections = new();

    public QuotaWebSocketHub(IQuotaStateStore store, BridgeEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        _store = store;
        _options = options;
    }

    /// <summary>How many clients are currently connected.</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>Maps <c>/api/v1/ws</c>. The route is authenticated like every other API route.</summary>
    public static void MapQuotaWebSocket(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.Map("/api/v1/ws", (HttpContext context, QuotaWebSocketHub hub) => hub.AcceptAsync(context));
    }

    /// <summary>
    /// Accepts one client: sends the hello frame first, then the current snapshot, then streams
    /// updates until the client goes away.
    /// </summary>
    public async Task AcceptAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var id = Guid.NewGuid();
        var connection = new WebSocketConnection(socket, context.RequestAborted);

        _connections[id] = connection;

        try
        {
            // hello is queued before anything else, so ordering is guaranteed by construction rather
            // than by the caller remembering to send it first.
            connection.TryEnqueue(WebSocketMessageWriter.Hello(_options));

            // A client that connects mid-stream starts from the current snapshot rather than waiting
            // for the next change, which may be minutes away.
            if (_store.Current is { } snapshot)
            {
                connection.TryEnqueue(WebSocketMessageWriter.QuotaUpdated(_store.Sequence, snapshot));
            }

            await connection.Completion.ConfigureAwait(false);
        }
        finally
        {
            _connections.TryRemove(id, out _);

            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Publishes one state update to every client.
    /// </summary>
    /// <remarks>
    /// A client whose outbound queue is full is aborted instead of being waited for: the queue only
    /// fills when the socket is not draining, and blocking here would make one slow phone a
    /// back-pressure source for the whole Bridge.
    /// </remarks>
    public async Task BroadcastQuotaUpdatedAsync(QuotaStateUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        var message = WebSocketMessageWriter.QuotaUpdated(update.Sequence, update.Current);

        foreach (var (id, connection) in _connections)
        {
            if (!connection.TryEnqueue(message))
            {
                _connections.TryRemove(id, out _);
                await connection.AbortAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Tells every client the Bridge is stopping, then closes their connections normally.
    /// </summary>
    public async Task BroadcastShutdownAsync(CancellationToken cancellationToken = default)
    {
        var message = WebSocketMessageWriter.Shutdown();
        var closing = new List<Task>();

        foreach (var (id, connection) in _connections)
        {
            _connections.TryRemove(id, out _);

            connection.TryEnqueue(message);
            closing.Add(connection.CloseAsync());
        }

        if (closing.Count == 0)
        {
            return;
        }

        try
        {
            // Bounded: a client that never drains must not hold up the shutdown it is being told about.
            await Task.WhenAll(closing).WaitAsync(ShutdownFlushTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Flushing is best effort; the connections are being closed either way.
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (id, connection) in _connections)
        {
            _connections.TryRemove(id, out _);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
