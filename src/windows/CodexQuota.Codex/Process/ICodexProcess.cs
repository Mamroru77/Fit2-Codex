using System.Diagnostics;
using CodexQuota.Codex.Protocol;

namespace CodexQuota.Codex.Process;

/// <summary>
/// A running Codex App Server child process together with the transport bound to its stdio.
/// </summary>
public interface ICodexProcess : IAsyncDisposable
{
    /// <summary>The JSONL transport reading from and writing to this process.</summary>
    IJsonRpcTransport Transport { get; }

    /// <summary>Completes with the child's exit code once it has terminated.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The single continuous consumer of a redirected stderr stream.
/// </summary>
/// <remarks>
/// A redirected stderr that nobody reads eventually fills its pipe. The child then blocks inside
/// its own stderr write, stops servicing stdin/stdout, and — because it never actually exits — is
/// invisible to the restart supervisor. That is a silent permanent stall, not a crash, so it can
/// never be repaired by restarting. Exactly one drain therefore owns stderr for the whole child
/// lifetime, from the moment the process starts until disposal.
/// <para>
/// The optional sink is isolated from the drain for the same reason: it is third-party code (a
/// logging provider, a redactor, a diagnostic writer), and an exception from it must never be able
/// to end the drain and bring the stall back.
/// </para>
/// </remarks>
public static class CodexStandardErrorDrain
{
    /// <summary>
    /// Reads <paramref name="standardError"/> until end of stream, handing every line to
    /// <paramref name="onLine"/> (which may be <c>null</c> to discard). Expected end-of-stream and
    /// disposal conditions complete the drain normally: an ordinary shutdown must never be turned
    /// into a fault.
    /// </summary>
    public static async Task DrainAsync(
        TextReader standardError,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(standardError);

        try
        {
            while (true)
            {
                var line = await standardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (line is null)
                {
                    // End of stream: the child closed stderr, so there is nothing left to drain.
                    return;
                }

                Publish(onLine, line);
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or ObjectDisposedException
                                              or InvalidOperationException
                                              or OperationCanceledException)
        {
            // The child died, stderr was disposed underneath the drain, or the drain is being
            // cancelled as part of shutdown: in every case the drain simply ends.
        }
    }

    /// <summary>Hands one line to the sink without letting the sink end the drain.</summary>
    private static void Publish(Action<string>? onLine, string line)
    {
        if (onLine is null)
        {
            return;
        }

        try
        {
            onLine(line);
        }
        catch (Exception)
        {
            // A throwing sink is a sink problem, not a reason to stop consuming stderr.
        }
    }
}

/// <summary>
/// The production <see cref="ICodexProcess"/>: an owned <c>codex-app-server.exe</c> child process
/// started over stdio. Disposing this instance terminates the child and disposes its transport.
/// </summary>
public sealed class CodexAppServerProcess : ICodexProcess
{
    /// <summary>Upper bound for reaping a killed child, so disposal can never hang.</summary>
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound for each drain shutdown step, so disposal can never hang.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    // Fully qualified because this file's namespace ends in ".Process", which would otherwise
    // shadow the System.Diagnostics.Process type.
    private readonly System.Diagnostics.Process _process;
    private readonly ProcessTerminationHandle _handle;
    private readonly TextReader _standardError;
    private readonly Action<string>? _onDiagnostic;
    private readonly CancellationTokenSource _drainShutdown;
    private readonly Task _stderrDrain;

    private CodexAppServerProcess(
        System.Diagnostics.Process process,
        IJsonRpcTransport transport,
        TextReader standardError,
        Action<string>? onDiagnostic,
        CancellationTokenSource drainShutdown,
        Task stderrDrain)
    {
        _process = process;
        _handle = new ProcessTerminationHandle(process);
        Transport = transport;
        _standardError = standardError;
        _onDiagnostic = onDiagnostic;
        _drainShutdown = drainShutdown;
        _stderrDrain = stderrDrain;
    }

    public IJsonRpcTransport Transport { get; }

    /// <summary>The OS process id, exposed for tests that must observe the real child.</summary>
    internal int ProcessId => _process.Id;

    /// <summary>
    /// Builds the launch configuration for the supported App Server binary.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string appServerPath, string codexHomePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appServerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);

        var startInfo = new ProcessStartInfo(appServerPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(appServerPath)) ?? string.Empty,
        };

        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");

        // Isolate Bridge authentication/configuration from Codex Desktop state.
        startInfo.Environment["CODEX_HOME"] = codexHomePath;

        return startInfo;
    }

    /// <summary>
    /// Starts the App Server, gives stderr its only continuous consumer, and binds a JSONL
    /// transport to the child's stdout.
    /// </summary>
    /// <param name="appServerPath">Path of the supported <c>codex-app-server.exe</c>.</param>
    /// <param name="codexHomePath">Isolated <c>CODEX_HOME</c> for the child.</param>
    /// <param name="onStderrLine">
    /// Optional sink for stderr lines, so the child's own diagnostics reach the log. It is isolated
    /// from the drain: a sink that throws can never end the drain.
    /// </param>
    /// <param name="onDiagnostic">
    /// Optional sink for lifecycle conditions the Bridge cannot fix by itself, such as a child that
    /// survived termination.
    /// </param>
    public static CodexAppServerProcess Start(
        string appServerPath,
        string codexHomePath,
        Action<string>? onStderrLine = null,
        Action<string>? onDiagnostic = null)
        => StartFrom(CreateStartInfo(appServerPath, codexHomePath), onStderrLine, onDiagnostic);

    /// <summary>
    /// Starts an already-configured child. The production path above builds the supported
    /// <c>codex-app-server.exe</c> launch configuration; tests use this seam to exercise the real
    /// process ownership and termination behaviour without a real App Server binary.
    /// </summary>
    internal static CodexAppServerProcess StartFrom(
        ProcessStartInfo startInfo,
        Action<string>? onStderrLine = null,
        Action<string>? onDiagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{startInfo.FileName}'.");

        // stderr must have a consumer before anything can fill it, so the drain is started as soon
        // as the child exists.
        var drainShutdown = new CancellationTokenSource();
        var stderrDrain = Task.Run(
            () => CodexStandardErrorDrain.DrainAsync(process.StandardError, onStderrLine, drainShutdown.Token));

        var transport = new JsonlProcessTransport(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream);

        return new CodexAppServerProcess(
            process,
            transport,
            process.StandardError,
            onDiagnostic,
            drainShutdown,
            stderrDrain);
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Bounded and best effort: shutdown must not hang waiting for a child that will not die,
            // and a child that survives must be reported rather than silently abandoned.
            await ChildProcessTermination
                .TerminateAsync(_handle, ExitTimeout, _onDiagnostic, CancellationToken.None)
                .ConfigureAwait(false);

            // Approved Task 3 semantics: disposing the transport aborts a blocked stdout reader,
            // so the child does not have to be gone for disposal to be deterministic.
            await Transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await StopStderrDrainAsync().ConfigureAwait(false);

            _drainShutdown.Dispose();
            _process.Dispose();
        }
    }

    /// <summary>
    /// Ends the stderr drain. A read that is still blocked is ended by disposing stderr, exactly
    /// the way the approved transport ends a blocked stdout reader, and the drain treats that as
    /// an ordinary end rather than a fault.
    /// </summary>
    private async Task StopStderrDrainAsync()
    {
        await _drainShutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            await _stderrDrain.WaitAsync(DrainTimeout).ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
            // Still blocked: dispose stderr underneath it, which ends the pending read.
        }

        _standardError.Dispose();

        try
        {
            await _stderrDrain.WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Nothing more can be done from here; the drain ends when the child's stderr closes.
        }
    }
}
