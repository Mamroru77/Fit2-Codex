using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace CodexQuota.Codex.Protocol;

/// <summary>
/// Minimal JSON-RPC client for the Codex App Server. It assigns numeric request ids, correlates
/// responses by id, and exposes server-initiated notifications (messages with a <c>method</c> and
/// no <c>id</c>) through a channel.
/// </summary>
/// <remarks>
/// The connection has three states: before the handshake, established, and terminal. Once the read
/// loop has ended or faulted, or the client has been disposed, the connection is terminal and every
/// further request fails immediately instead of waiting for a response that cannot arrive.
/// </remarks>
public sealed class CodexRpcClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly object InitializeParameters = new
    {
        clientInfo = new
        {
            name = "codex_quota_bridge",
            title = "Codex Quota Bridge",
            version = "1.0.0",
        },
    };

    /// <summary>Sent as <c>params</c> for a method that takes none.</summary>
    private static readonly object EmptyParameters = new Dictionary<string, object>();

    private readonly IJsonRpcTransport _transport;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<CodexNotification> _notifications = Channel.CreateUnbounded<CodexNotification>();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly Task _readLoop;

    private long _nextRequestId;
    private volatile bool _initialized;
    private volatile Exception? _terminalException;

    public CodexRpcClient(IJsonRpcTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>
    /// Performs the App Server handshake: one <c>initialize</c> request, and only once its
    /// response has arrived, one <c>initialized</c> notification. Concurrent callers take part in
    /// the same single handshake; a caller that arrives once it has completed returns without
    /// sending anything.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // A connection that has permanently failed must never report handshake success merely
            // because the handshake succeeded earlier, so the terminal check runs inside the gate
            // and before the already-initialized early return.
            ThrowIfTerminal();

            if (_initialized)
            {
                return;
            }

            await SendRequestAsync<JsonElement>("initialize", InitializeParameters, cancellationToken).ConfigureAwait(false);
            await _transport.WriteLineAsync(BuildNotification("initialized", null), cancellationToken).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>
    /// Sends one ordinary request and completes with its correlated result. Requires the handshake
    /// to have completed.
    /// </summary>
    public Task<T> CallAsync<T>(string method, object? @params, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        if (!_initialized)
        {
            throw new InvalidOperationException(
                $"The Codex App Server handshake has not completed, so '{method}' cannot be sent yet.");
        }

        return SendRequestAsync<T>(method, @params, cancellationToken);
    }

    /// <summary>
    /// Streams notifications until the client is disposed or the transport fails.
    /// </summary>
    public IAsyncEnumerable<CodexNotification> Notifications(CancellationToken cancellationToken)
        => _notifications.Reader.ReadAllAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _notifications.Writer.TryComplete();

        await _readLoop.ConfigureAwait(false);

        _shutdown.Dispose();
        _initGate.Dispose();
    }

    /// <summary>
    /// The raw correlated JSON-RPC request primitive. It carries no initialization requirement, so
    /// the handshake itself can use it without any special case in the ordinary call path.
    /// </summary>
    private async Task<T> SendRequestAsync<T>(string method, object? @params, CancellationToken cancellationToken)
    {
        ThrowIfTerminal();

        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException($"Request id {id} is already pending.");
        }

        // Close the terminal/pending registration race. ThrowIfTerminal() above and this
        // registration are not atomic with respect to a connection failure, so without this check a
        // failure recorded in between would drain _pending before this entry existed and the
        // request would wait forever. Fault() always records the terminal exception before
        // FaultPending() drains _pending, therefore:
        //   - terminal established before this check -> detected here: the entry is removed and the
        //     caller fails immediately;
        //   - terminal established after this check -> FaultPending() sees the entry and fails it.
        // No interleaving can leave a request pending after a permanent connection failure.
        if (_terminalException is { } terminal)
        {
            _pending.TryRemove(id, out _);
            throw terminal;
        }

        try
        {
            await _transport.WriteLineAsync(BuildRequest(method, id, @params), cancellationToken).ConfigureAwait(false);

            var result = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            return result.Deserialize<T>(SerializerOptions)
                ?? throw new JsonException($"The response to '{method}' could not be read as {typeof(T).Name}.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            await foreach (var line in _transport.ReadLinesAsync(_shutdown.Token).ConfigureAwait(false))
            {
                Dispatch(line);
            }

            // End of stream: no further response can arrive, so pending calls must not hang.
            Fault(new IOException("The Codex App Server stream ended unexpectedly."));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Disposal is terminal too: no response can arrive afterwards, so waiting callers must
            // fail instead of hanging. Notification readers still end cleanly.
            _notifications.Writer.TryComplete();

            var disposed = new ObjectDisposedException(nameof(CodexRpcClient));
            _terminalException = disposed;
            FaultPending(disposed);
        }
        catch (Exception exception)
        {
            // Any transport or protocol failure must surface to every waiting caller instead of
            // leaving requests pending forever.
            Fault(exception);
        }
    }

    private void Dispatch(string line)
    {
        using var document = JsonDocument.Parse(line);
        var message = document.RootElement;

        if (message.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The message is not a JSON object.");
        }

        if (message.TryGetProperty("id", out var idElement)
            && idElement.ValueKind == JsonValueKind.Number
            && idElement.TryGetInt64(out var id))
        {
            CompletePending(id, message);
            return;
        }

        if (message.TryGetProperty("method", out var methodElement)
            && methodElement.ValueKind == JsonValueKind.String)
        {
            var parameters = message.TryGetProperty("params", out var paramsElement)
                ? paramsElement.Clone()
                : (JsonElement?)null;

            _notifications.Writer.TryWrite(new CodexNotification(methodElement.GetString()!, parameters));
            return;
        }

        throw new JsonException("The message is neither a response nor a notification.");
    }

    private void CompletePending(long id, JsonElement message)
    {
        if (!_pending.TryRemove(id, out var completion))
        {
            return;
        }

        if (message.TryGetProperty("error", out var error))
        {
            completion.TrySetException(
                new InvalidOperationException($"The Codex App Server rejected request {id}: {error.GetRawText()}"));
            return;
        }

        if (message.TryGetProperty("result", out var result))
        {
            completion.TrySetResult(result.Clone());
            return;
        }

        completion.TrySetException(new JsonException($"Response {id} carries neither a result nor an error."));
    }

    /// <summary>
    /// Records a permanent failure: the notification channel ends with the exception, waiting
    /// callers are faulted, and later requests fail immediately instead of being queued into a
    /// pending set that nobody will ever complete.
    /// </summary>
    private void Fault(Exception exception)
    {
        _terminalException = exception;
        _notifications.Writer.TryComplete(exception);
        FaultPending(exception);
    }

    /// <summary>
    /// Fails every waiting caller. Used when the transport dies and when the client is disposed:
    /// in both cases no response can arrive any more, so calls must not stay pending.
    /// </summary>
    private void FaultPending(Exception exception)
    {
        foreach (var id in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(id, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private void ThrowIfTerminal()
    {
        var terminal = _terminalException;

        if (terminal is not null)
        {
            throw terminal;
        }
    }

    private static string BuildRequest(string method, long id, object? @params)
    {
        // The `params` member is always present, even for a method that takes none. JSON-RPC 2.0
        // makes it optional, but the real Codex App Server does not: a request without it is
        // rejected outright with
        //   {"code":-32600,"message":"Invalid request: missing field `params`"}
        // which is exactly the shape of `account/read`, `account/rateLimits/read` and
        // `account/logout`. Sending an empty object is the interoperable form.
        var parameters = @params ?? EmptyParameters;

        return JsonSerializer.Serialize(new { method, id, @params = parameters }, SerializerOptions);
    }

    private static string BuildNotification(string method, object? @params)
        => @params is null
            ? JsonSerializer.Serialize(new { method }, SerializerOptions)
            : JsonSerializer.Serialize(new { method, @params }, SerializerOptions);
}
