namespace CodexQuota.Codex.Protocol;

/// <summary>
/// Line-oriented transport to the Codex App Server: exactly one JSON object per line.
/// </summary>
public interface IJsonRpcTransport : IAsyncDisposable
{
    /// <summary>
    /// Writes one line. Implementations serialize concurrent writers.
    /// </summary>
    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>
    /// Reads inbound lines until the stream ends.
    /// </summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken);
}
