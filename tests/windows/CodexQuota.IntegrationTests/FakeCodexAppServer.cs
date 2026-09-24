using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using CodexQuota.Codex.Process;
using CodexQuota.Codex.Protocol;

namespace CodexQuota.IntegrationTests;

/// <summary>
/// In-process stand-in for <c>codex-app-server.exe</c>. It speaks the same line-oriented
/// JSON-RPC as the real binary, but over channels instead of pipes, so the whole Bridge
/// pipeline can be driven end to end without launching a real child process.
/// </summary>
/// <remarks>
/// The handshake order is the point of this fake: it answers <c>initialize</c>, waits for the
/// client's <c>initialized</c> notification, answers <c>account/read</c> with an authenticated
/// account, answers <c>account/rateLimits/read</c> with the documented 300/10080-minute windows,
/// and then pushes one <c>account/rateLimits/updated</c> carrying different percentages.
/// </remarks>
public sealed class FakeCodexAppServer : ICodexProcess
{
    private const long ShortResetsAt = 1_800_000_000;
    private const long WeeklyResetsAt = 1_800_100_000;

    private readonly FakeJsonRpcTransport _transport = new();
    private readonly TaskCompletionSource _initializedObserved =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _exit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();

    private readonly Task _loop;
    private int _accountReads;
    private int _rateLimitReads;
    private int _updatedNotifications;

    public FakeCodexAppServer(double initialShortUsed = 20d, double updatedShortUsed = 55d)
    {
        InitialShortUsed = initialShortUsed;
        UpdatedShortUsed = updatedShortUsed;
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Percentage the first quota read reports for the 300-minute window.</summary>
    internal double InitialShortUsed { get; }

    /// <summary>Percentage the follow-up <c>account/rateLimits/updated</c> reports.</summary>
    internal double UpdatedShortUsed { get; }

    public IJsonRpcTransport Transport => _transport;

    /// <summary>Completes once the child has observed the client's <c>initialized</c> notification.</summary>
    public Task InitializedObserved => _initializedObserved.Task;

    public int AccountReadCount => Volatile.Read(ref _accountReads);

    public int RateLimitReadCount => Volatile.Read(ref _rateLimitReads);

    public int UpdatedNotificationCount => Volatile.Read(ref _updatedNotifications);

    /// <summary>
    /// Pushes one <c>account/rateLimits/updated</c> notification carrying
    /// <see cref="UpdatedShortUsed"/>, exactly as the real server does when quota moves.
    /// </summary>
    /// <remarks>
    /// The push is driven by the test rather than fired automatically after the first read:
    /// the notification is handled by a background worker, so an automatic push races with the
    /// first read's own publication and makes the resulting order unassertable.
    /// </remarks>
    public Task PushRateLimitUpdateAsync()
    {
        Interlocked.Increment(ref _updatedNotifications);

        return NotifyAsync("account/rateLimits/updated", RateLimits(UpdatedShortUsed), CancellationToken.None);
    }

    /// <summary>Simulates the child terminating, the way a crash would.</summary>
    public void Exit(int exitCode) => _exit.TrySetResult(exitCode);

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        => _exit.Task.WaitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _exit.TrySetResult(0);

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in _transport.ServerInbox.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;

                if (message.TryGetProperty("id", out var idElement)
                    && idElement.TryGetInt64(out var id)
                    && message.TryGetProperty("method", out var requestMethod))
                {
                    await RespondAsync(id, requestMethod.GetString(), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (message.TryGetProperty("method", out var notificationMethod)
                    && notificationMethod.GetString() == "initialized")
                {
                    _initializedObserved.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task RespondAsync(long id, string? method, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                await ReplyAsync(id, """{"userAgent":"fake-codex-app-server"}""", cancellationToken)
                    .ConfigureAwait(false);
                break;

            case "account/read":
                Interlocked.Increment(ref _accountReads);
                await ReplyAsync(id, """{"authMode":"chatgpt","email":"bridge@example.com"}""", cancellationToken)
                    .ConfigureAwait(false);
                break;

            case "account/rateLimits/read":
                Interlocked.Increment(ref _rateLimitReads);
                await ReplyAsync(id, RateLimits(InitialShortUsed), cancellationToken).ConfigureAwait(false);
                break;

            case "account/login/start":
                await ReplyAsync(id, """{"authUrl":"https://example.test/auth"}""", cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                await ReplyAsync(id, "{}", cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private Task ReplyAsync(long id, string resultJson, CancellationToken cancellationToken)
        => _transport.ClientInbox
            .WriteAsync($"{{\"id\":{id.ToString(CultureInfo.InvariantCulture)},\"result\":{resultJson}}}", cancellationToken)
            .AsTask();

    private Task NotifyAsync(string method, string paramsJson, CancellationToken cancellationToken)
        => _transport.ClientInbox
            .WriteAsync($"{{\"method\":\"{method}\",\"params\":{paramsJson}}}", cancellationToken)
            .AsTask();

    private static string RateLimits(double shortUsed)
        => "{\"primary\":{\"limitId\":\"short\",\"usedPercent\":"
           + shortUsed.ToString(CultureInfo.InvariantCulture)
           + ",\"windowDurationMins\":300,\"resetsAt\":"
           + ShortResetsAt.ToString(CultureInfo.InvariantCulture)
           + "},\"secondary\":{\"limitId\":\"weekly\",\"usedPercent\":40,\"windowDurationMins\":10080,\"resetsAt\":"
           + WeeklyResetsAt.ToString(CultureInfo.InvariantCulture)
           + "}}";
}

/// <summary>
/// A duplex channel pair that satisfies <see cref="IJsonRpcTransport"/> without any OS pipe.
/// </summary>
internal sealed class FakeJsonRpcTransport : IJsonRpcTransport
{
    private readonly Channel<string> _toServer = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _toClient = Channel.CreateUnbounded<string>();

    /// <summary>Lines the client wrote, read by the fake server.</summary>
    internal ChannelReader<string> ServerInbox => _toServer.Reader;

    /// <summary>Where the fake server writes lines the client will read.</summary>
    internal ChannelWriter<string> ClientInbox => _toClient.Writer;

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        => await _toServer.Writer.WriteAsync(line, cancellationToken).ConfigureAwait(false);

    public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken)
        => _toClient.Reader.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        _toServer.Writer.TryComplete();
        _toClient.Writer.TryComplete();

        return ValueTask.CompletedTask;
    }
}
