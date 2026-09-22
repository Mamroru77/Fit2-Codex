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
/// The production <see cref="ICodexProcess"/>: an owned <c>codex-app-server.exe</c> child process
/// started over stdio. Disposing this instance terminates the child and disposes its transport.
/// </summary>
public sealed class CodexAppServerProcess : ICodexProcess
{
    // Fully qualified because this file's namespace ends in ".Process", which would otherwise
    // shadow the System.Diagnostics.Process type.
    private readonly System.Diagnostics.Process _process;

    private CodexAppServerProcess(System.Diagnostics.Process process, IJsonRpcTransport transport)
    {
        _process = process;
        Transport = transport;
    }

    public IJsonRpcTransport Transport { get; }

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
    /// Starts the App Server and binds a JSONL transport to its stdio.
    /// </summary>
    public static CodexAppServerProcess Start(string appServerPath, string codexHomePath)
    {
        var startInfo = CreateStartInfo(appServerPath, codexHomePath);

        var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{startInfo.FileName}'.");

        var transport = new JsonlProcessTransport(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream);

        return new CodexAppServerProcess(process, transport);
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        // Terminate the child first: the transport's reader loop only ends at end of stream.
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        await _process.WaitForExitAsync().ConfigureAwait(false);
        await Transport.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
    }
}
