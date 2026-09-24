using System.Diagnostics;
using CodexQuota.Codex.Process;
using CodexQuota.Core.Logging;
using Xunit;

namespace CodexQuota.Codex.Tests;

/// <summary>
/// End-to-end coverage of the child's stderr path: a real process writes a credential to stderr,
/// the drain hands it to the sink, and the redactor removes it before anything could reach disk.
/// This is the path Stage A's composition attaches logging to.
/// </summary>
public class CodexAppServerProcessStderrTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task ARealChildsStderrReachesTheSinkAndIsRedacted()
    {
        var lines = new List<string>();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("echo Authorization: Bearer abcdef123 1>&2");

        var process = CodexAppServerProcess.StartFrom(
            startInfo,
            line =>
            {
                lock (lines)
                {
                    lines.Add(line);
                }

                received.TrySetResult();
            });

        try
        {
            await received.Task.WaitAsync(Timeout);
        }
        finally
        {
            await process.DisposeAsync();
        }

        string captured;

        lock (lines)
        {
            captured = Assert.Single(lines);
        }

        // The line really came from the child's stderr...
        Assert.Contains("abcdef123", captured, StringComparison.Ordinal);

        // ...and the mandatory redaction pass removes the credential from it.
        var redacted = SensitiveDataRedactor.Redact(captured);

        Assert.DoesNotContain("abcdef123", redacted, StringComparison.Ordinal);
        Assert.Contains(SensitiveDataRedactor.Placeholder, redacted, StringComparison.Ordinal);
    }
}
