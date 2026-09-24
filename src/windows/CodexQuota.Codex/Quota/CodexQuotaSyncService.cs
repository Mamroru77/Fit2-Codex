using System.Text.Json;
using System.Threading.Channels;
using CodexQuota.Codex.Account;
using CodexQuota.Codex.Protocol;
using CodexQuota.Core.Quota;
using CodexQuota.Core.Runtime;

namespace CodexQuota.Codex.Quota;

/// <summary>
/// Keeps the Bridge's quota state in step with the Codex App Server. It performs the documented
/// startup sequence (initialize → account read → quota read), reacts to server notifications,
/// and reconciles with a low-frequency read while authenticated.
/// </summary>
/// <remarks>
/// Server notifications are queued onto an internal channel and handled by a separate worker, so
/// the handler's async work can never stall the App Server stdout reader. Handling is isolated per
/// notification: one bad notification, or one observer that throws while the runtime state
/// changes, must never end that worker.
/// </remarks>
/// <remarks>
/// One instance is bound to one <see cref="CodexRpcClient"/>, and therefore to one Codex App
/// Server session. A faulted client is permanently terminal, and an App Server restart produces a
/// new client rather than repairing the old one, so this service never rebinds its client: when
/// the host observes a replacement session it must dispose this service and construct and start a
/// new one for the new client. A transport fault is therefore reported as a source error and never
/// as something this instance recovers from by itself.
/// </remarks>
public sealed class CodexQuotaSyncService : IAsyncDisposable
{
    /// <summary>Reconciliation interval used while the Bridge is authenticated.</summary>
    public static readonly TimeSpan WatchdogInterval = TimeSpan.FromMinutes(5);

    private const string ReadRateLimitsMethod = "account/rateLimits/read";
    private const string RateLimitsUpdatedMethod = "account/rateLimits/updated";
    private const string AccountUpdatedMethod = "account/updated";
    private const string LoginCompletedMethod = "account/login/completed";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly CodexRpcClient _client;
    private readonly CodexAccountService _account;
    private readonly RateLimitAdapter _adapter;
    private readonly IQuotaStateStore _store;
    private readonly BridgeRuntimeState _runtime;

    private readonly Channel<CodexNotification> _inbox = Channel.CreateUnbounded<CodexNotification>();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _watchdogGate = new();

    private Task? _notificationPump;
    private Task? _notificationWorker;
    private CancellationTokenSource? _watchdogCancellation;
    private Task? _watchdog;
    private volatile bool _authenticated;

    public CodexQuotaSyncService(
        CodexRpcClient client,
        CodexAccountService account,
        RateLimitAdapter adapter,
        IQuotaStateStore store,
        BridgeRuntimeState runtime)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runtime);

        _client = client;
        _account = account;
        _adapter = adapter;
        _store = store;
        _runtime = runtime;
    }

    /// <summary>
    /// True while the reconciliation watchdog is running. It only ever runs while the account is
    /// authenticated, so an unauthenticated Bridge never polls Codex.
    /// </summary>
    public bool IsWatchdogRunning
    {
        get
        {
            lock (_watchdogGate)
            {
                return _watchdog is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// Runs the startup sequence: initialize the App Server, read the account, and either read
    /// quota or expose <see cref="BridgeRuntimePhase.AuthRequired"/>.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_notificationPump is not null)
        {
            throw new InvalidOperationException("The Codex quota synchronization service has already been started.");
        }

        _runtime.SetPhase(BridgeRuntimePhase.CodexInitializing);
        await _client.InitializeAsync(cancellationToken).ConfigureAwait(false);

        _notificationPump = Task.Run(() => PumpNotificationsAsync(_shutdown.Token));
        _notificationWorker = Task.Run(() => ProcessInboxAsync(_shutdown.Token));

        _runtime.SetPhase(BridgeRuntimePhase.CheckingAuth);
        var account = await _account.ReadAccountAsync(cancellationToken).ConfigureAwait(false);

        await ApplyAccountAsync(account, resyncWhenAuthenticated: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Manual "Refresh Now": one immediate quota read, and nothing else.
    /// </summary>
    public Task RefreshNowAsync(CancellationToken cancellationToken)
        => ReadRateLimitsAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        StopWatchdog();
        _inbox.Writer.TryComplete();

        await _shutdown.CancelAsync().ConfigureAwait(false);

        foreach (var worker in new[] { _notificationPump, _notificationWorker })
        {
            if (worker is null)
            {
                continue;
            }

            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected while shutting down.
            }
        }

        _runtime.SetPhase(BridgeRuntimePhase.Stopped);
        _shutdown.Dispose();
    }

    private async Task PumpNotificationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var notification in _client.Notifications(cancellationToken).ConfigureAwait(false))
            {
                // Queue only: handling a notification performs async work and must never stall
                // this reader.
                _inbox.Writer.TryWrite(notification);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // A transport fault ends the notification stream for good, because the RPC client is
            // terminal once it has faulted. Report the source as errored and leave the recovery to
            // the host, which replaces this session-bound service with one for the next session.
            MarkSourceErrorBestEffort();
        }
        finally
        {
            _inbox.Writer.TryComplete();
        }
    }

    private async Task ProcessInboxAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var notification in _inbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await HandleNotificationAsync(notification, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // One bad notification, or one observer that threw while the runtime state
                    // changed, must never cost the processing of the notifications that follow.
                    MarkSourceErrorBestEffort();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleNotificationAsync(CodexNotification notification, CancellationToken cancellationToken)
    {
        switch (notification.Method)
        {
            case RateLimitsUpdatedMethod:
                ApplyRateLimits(notification.Params);
                break;

            case AccountUpdatedMethod:
                if (notification.Params is { } accountPayload)
                {
                    var account = AccountState.FromPayload(accountPayload);
                    await ApplyAccountAsync(account, resyncWhenAuthenticated: false, cancellationToken)
                        .ConfigureAwait(false);
                }

                break;

            case LoginCompletedMethod:
                await HandleLoginCompletedAsync(notification.Params, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleLoginCompletedAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        var succeeded = payload is { } value
            && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.True;

        if (!succeeded)
        {
            // A cancelled or failed login must not start a retry loop; the user re-initiates
            // login explicitly.
            _authenticated = false;
            StopWatchdog();
            _runtime.SetPhase(BridgeRuntimePhase.AuthRequired, QuotaSourceStatus.AuthRequired);
            return;
        }

        // Login succeeded: confirm the account, then pull the first authoritative quota read.
        var account = await _account.ReadAccountAsync(cancellationToken).ConfigureAwait(false);
        await ApplyAccountAsync(account, resyncWhenAuthenticated: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyAccountAsync(
        AccountState account,
        bool resyncWhenAuthenticated,
        CancellationToken cancellationToken)
    {
        var wasAuthenticated = _authenticated;
        _authenticated = account.Authenticated;

        if (!account.Authenticated)
        {
            StopWatchdog();
            _runtime.SetPhase(BridgeRuntimePhase.AuthRequired, QuotaSourceStatus.AuthRequired);
            return;
        }

        if (wasAuthenticated && !resyncWhenAuthenticated)
        {
            // Already syncing: a repeated account update carries no new quota information, so it
            // must not trigger another source read.
            return;
        }

        _runtime.SetPhase(BridgeRuntimePhase.Syncing);

        var synchronized = await TrySynchronizeQuotaAsync(cancellationToken).ConfigureAwait(false);

        // The watchdog is the reconciliation path for a transient quota-read failure, so it starts
        // either way: without it a single failed read would leave this service unable to ever read
        // quota again, because a repeated account update carries no new quota information.
        StartWatchdog();

        // Phase and source availability are separate concerns. A failed first read still leaves a
        // running Bridge holding the last trusted snapshot, so the service settles in Ready and
        // reports the source as errored instead of staying in Syncing forever.
        if (synchronized)
        {
            _runtime.SetPhase(BridgeRuntimePhase.Ready);
        }
        else
        {
            _runtime.SetPhase(BridgeRuntimePhase.Ready, QuotaSourceStatus.SourceError);
        }
    }

    /// <summary>
    /// Performs one quota read and reports whether it succeeded. A transient source or RPC failure
    /// is reported instead of propagated: propagating it would strand the caller in
    /// <see cref="BridgeRuntimePhase.Syncing"/> with no watchdog, no recorded error and no way back.
    /// Cancellation requested by the caller still propagates unchanged, because that is not a
    /// source error.
    /// </summary>
    private async Task<bool> TrySynchronizeQuotaAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort source-error report. <see cref="BridgeRuntimeState.SetSourceStatus"/> raises
    /// <see cref="BridgeRuntimeState.Changed"/>, so an observer that throws must not be able to
    /// kill the worker that is reporting the error; the report is isolated here for that reason.
    /// </summary>
    private void MarkSourceErrorBestEffort()
    {
        try
        {
            _runtime.SetSourceStatus(QuotaSourceStatus.SourceError);
        }
        catch (Exception)
        {
            // Losing the error report must never cost the worker that produced it.
        }
    }

    private async Task ReadRateLimitsAsync(CancellationToken cancellationToken)
    {
        var payload = await _client
            .CallAsync<JsonElement>(ReadRateLimitsMethod, null, cancellationToken)
            .ConfigureAwait(false);

        ApplyRateLimits(payload);
    }

    private void ApplyRateLimits(JsonElement? payload)
    {
        var receivedAt = DateTimeOffset.UtcNow;

        RateLimitsReadResult? source = null;

        if (payload is { } value)
        {
            try
            {
                source = value.Deserialize<RateLimitsReadResult>(SerializerOptions);
            }
            catch (JsonException)
            {
                source = null;
            }
        }

        if (source is null)
        {
            _runtime.SetSourceStatus(QuotaSourceStatus.SourceSchemaUnsupported);
            return;
        }

        switch (_adapter.TryAdapt(source, receivedAt))
        {
            case RateLimitAdaptResult.Success success:
                _store.Replace(success.Snapshot);
                _runtime.RecordSuccessfulSync(receivedAt);
                break;

            case RateLimitAdaptResult.Unsupported:
                // Never guess: keep the last trusted snapshot and flag the schema instead.
                _runtime.SetSourceStatus(QuotaSourceStatus.SourceSchemaUnsupported);
                break;
        }
    }

    private void StartWatchdog()
    {
        lock (_watchdogGate)
        {
            if (_watchdog is { IsCompleted: false })
            {
                return;
            }

            _watchdogCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _watchdog = Task.Run(() => WatchdogLoopAsync(_watchdogCancellation.Token));
        }
    }

    private void StopWatchdog()
    {
        CancellationTokenSource? cancellation;

        lock (_watchdogGate)
        {
            cancellation = _watchdogCancellation;
            _watchdogCancellation = null;
            _watchdog = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async Task WatchdogLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(WatchdogInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await RefreshNowAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    _runtime.SetSourceStatus(QuotaSourceStatus.SourceError);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
