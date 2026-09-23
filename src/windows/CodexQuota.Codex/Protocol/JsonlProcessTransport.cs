using System.Text;
using System.Threading.Channels;

namespace CodexQuota.Codex.Protocol;

/// <summary>
/// JSONL transport over a child process's redirected stdin/stdout. Writes are serialized behind a
/// single lock, and exactly one background reader drains stdout into a channel so the child is
/// never blocked by a slow consumer.
/// </summary>
/// <remarks>
/// The reader loop ends at end of stream, and also immediately when this transport is disposed:
/// disposal aborts a read that is blocked waiting for input rather than waiting for the child to
/// close stdout. Callers therefore never have to terminate the process first in order to dispose.
/// </remarks>
public sealed class JsonlProcessTransport : IJsonRpcTransport
{
    private readonly Stream _standardOutput;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly Task _readerLoop;

    public JsonlProcessTransport(Stream standardInput, Stream standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(standardOutput);

        _standardOutput = standardOutput;

        _writer = new StreamWriter(standardInput, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        _reader = new StreamReader(standardOutput, Encoding.UTF8);

        _readerLoop = Task.Run(ReadLoopAsync);
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken)
        => _lines.Reader.ReadAllAsync(cancellationToken);

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync().ConfigureAwait(false);

                if (line is null)
                {
                    break;
                }

                await _lines.Writer.WriteAsync(line).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The child's stdout was closed, or this transport was disposed underneath the reader:
            // either way no further line can arrive, so the loop ends instead of hanging.
        }
        finally
        {
            _lines.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Disposing the stdout stream first is what makes shutdown deterministic: it aborts a
        // ReadLineAsync that is blocked waiting for input, so the reader loop can never outlive
        // disposal even while the child process is alive and silent.
        await _standardOutput.DisposeAsync().ConfigureAwait(false);

        await _readerLoop.ConfigureAwait(false);
        _lines.Writer.TryComplete();

        await _writer.DisposeAsync().ConfigureAwait(false);
        _reader.Dispose();
        _writeLock.Dispose();
    }
}
