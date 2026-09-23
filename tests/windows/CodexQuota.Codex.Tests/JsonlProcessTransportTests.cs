using CodexQuota.Codex.Protocol;
using Xunit;

namespace CodexQuota.Codex.Tests;

public class JsonlProcessTransportTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DisposeAsyncCompletesWhileTheReaderIsBlockedWaitingForInput()
    {
        using var standardInput = new MemoryStream();
        var standardOutput = new BlockingStream();
        var transport = new JsonlProcessTransport(standardInput, standardOutput);

        // Wait until the reader loop is genuinely blocked inside ReadLineAsync. This is the exact
        // state in which "complete the channel -> await the reader loop -> dispose the reader"
        // can never finish, because the reader loop is what would have to dispose the reader.
        await standardOutput.ReadStarted.WaitAsync(TestTimeout);

        await transport.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    }

    /// <summary>
    /// A stdout that never produces data: reads block until the stream is disposed, which is what a
    /// child process that is alive but silent looks like to the transport.
    /// </summary>
    private sealed class BlockingStream : Stream
    {
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task ReadStarted => _readStarted.Task;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadCoreAsync(cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => new(ReadCoreAsync(cancellationToken));

        protected override void Dispose(bool disposing)
        {
            _released.TrySetResult();
            base.Dispose(disposing);
        }

        private async Task<int> ReadCoreAsync(CancellationToken cancellationToken)
        {
            _readStarted.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(BlockingStream));
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
