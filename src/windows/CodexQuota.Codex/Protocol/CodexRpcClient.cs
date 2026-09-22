using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace CodexQuota.Codex.Protocol;

/// <summary>
/// Minimal JSON-RPC client for the Codex App Server. It assigns numeric request ids, correlates
/// responses by id, and exposes server-initiated notifications (messages with a <c>method</c> and
/// no <c>id</c>) through a channel.
/// </summary>
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

    private readonly IJsonRpcTransport _transport;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<CodexNotification> _notifications = Channel.CreateUnbounded<CodexNotification>();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _readLoop;

    private long _nextRequestId;

    public CodexRpcClient(IJsonRpcTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>
    /// Performs the App Server handshake: one <c>initialize</c> request, and only once its
    /// response has arrived, one <c>initialized</c> notification.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await CallAsync<JsonElement>("initialize", InitializeParameters, cancellationToken).ConfigureAwait(false);
        await _transport.WriteLineAsync(BuildNotification("initialized", null), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one request and completes with its correlated result.
    /// </summary>
    public async Task<T> CallAsync<T>(string method, object? @params, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException($"Request id {id} is already pending.");
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
            _notifications.Writer.TryComplete();
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

    private void Fault(Exception exception)
    {
        _notifications.Writer.TryComplete(exception);

        foreach (var id in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(id, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private static string BuildRequest(string method, long id, object? @params)
        => @params is null
            ? JsonSerializer.Serialize(new { method, id }, SerializerOptions)
            : JsonSerializer.Serialize(new { method, id, @params }, SerializerOptions);

    private static string BuildNotification(string method, object? @params)
        => @params is null
            ? JsonSerializer.Serialize(new { method }, SerializerOptions)
            : JsonSerializer.Serialize(new { method, @params }, SerializerOptions);
}
