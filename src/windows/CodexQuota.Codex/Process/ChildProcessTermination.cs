using System.ComponentModel;
using System.Diagnostics;

namespace CodexQuota.Codex.Process;

/// <summary>Outcome of a bounded child-termination attempt.</summary>
internal enum ChildTerminationOutcome
{
    /// <summary>The child is gone.</summary>
    Exited,

    /// <summary>The child survived every termination attempt.</summary>
    StillRunning,
}

/// <summary>
/// The slice of child-process behaviour that termination needs, so the policy below can be
/// exercised without a real process.
/// </summary>
/// <remarks>
/// Every member is total: the implementation reports "cannot tell" instead of throwing, so the
/// policy never has to guess and never has to propagate a platform exception out of disposal.
/// </remarks>
internal interface IChildProcessHandle
{
    /// <summary>True once the child has terminated.</summary>
    bool HasExited { get; }

    /// <summary>Requests termination, best effort.</summary>
    void Kill();

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the child to terminate, returning whether it did.
    /// </summary>
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Bounded, best-effort termination of an owned child process.
/// </summary>
/// <remarks>
/// The Bridge owns the Codex App Server child, so it must not leak it. Disposal must not wait
/// forever, and it must not silently pretend the child was terminated: a child that survives is
/// reported through the diagnostic sink so the operator can end it, instead of being abandoned
/// without a trace.
/// </remarks>
internal static class ChildProcessTermination
{
    /// <summary>Reported when the child could not be terminated.</summary>
    internal const string SurvivedMessage =
        "The Codex App Server child process is still running after two termination attempts; "
        + "it may outlive the Bridge and should be ended manually.";

    /// <summary>
    /// Asks the child to terminate, waits a bounded amount for it to be reaped, makes one final
    /// best-effort attempt, and reports whether it is gone.
    /// </summary>
    internal static async Task<ChildTerminationOutcome> TerminateAsync(
        IChildProcessHandle handle,
        TimeSpan timeout,
        Action<string>? onDiagnostic,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.HasExited)
        {
            return ChildTerminationOutcome.Exited;
        }

        handle.Kill();

        if (await handle.WaitForExitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            return ChildTerminationOutcome.Exited;
        }

        // One final attempt: the first kill can lose a race with a child that is still starting up,
        // and the handle can be momentarily unusable.
        handle.Kill();

        if (await handle.WaitForExitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            return ChildTerminationOutcome.Exited;
        }

        onDiagnostic?.Invoke(SurvivedMessage);
        return ChildTerminationOutcome.StillRunning;
    }
}

/// <summary>
/// Adapts <see cref="System.Diagnostics.Process"/> to <see cref="IChildProcessHandle"/>, turning
/// every platform failure into "cannot confirm the child is gone" rather than an exception.
/// </summary>
internal sealed class ProcessTerminationHandle : IChildProcessHandle
{
    private readonly System.Diagnostics.Process _process;

    internal ProcessTerminationHandle(System.Diagnostics.Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _process = process;
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // No process is associated with this object any more, so there is nothing to end.
                return true;
            }
            catch (Exception exception) when (exception is Win32Exception or NotSupportedException)
            {
                // The state cannot be determined. Assume it is alive so termination is still
                // attempted instead of skipped.
                return false;
            }
        }
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or Win32Exception
                                              or NotSupportedException
                                              or AggregateException)
        {
            // Already exited, already terminating, or not permitted: nothing is left to kill, and
            // the remaining cleanup must still run.
        }
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);

        try
        {
            await _process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is OperationCanceledException
                                              or InvalidOperationException
                                              or Win32Exception)
        {
            // Still running, or the handle became unusable: either way the child is not confirmed
            // gone, which is what the caller needs to know.
            return false;
        }
    }
}
