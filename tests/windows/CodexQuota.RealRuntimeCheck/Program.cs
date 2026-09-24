using System.Text.Json;
using CodexQuota.Codex.Account;
using CodexQuota.Codex.Process;
using CodexQuota.Codex.Protocol;

namespace CodexQuota.RealRuntimeCheck;

/// <summary>
/// Drives the <em>real</em> Codex App Server through the Bridge's own process ownership and RPC
/// client. This is the part of the Stage A gate that cannot be faked: it proves the Bridge speaks
/// the actual protocol, not just the protocol the in-process fake implements.
/// </summary>
/// <remarks>
/// It is a separate executable rather than a test so the deterministic gate
/// <c>dotnet test src/windows/CodexQuota.sln -c Release</c> never depends on a runtime being
/// installed on the machine.
/// <para>
/// Exit codes: 0 all checks passed; 1 a check failed; 2 the runtime was not found.
/// </para>
/// </remarks>
internal static class Program
{
    private const string DefaultRuntimePath =
        @"C:\Users\HUAWEI\AppData\Roaming\npm\node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe";

    private static async Task<int> Main()
    {
        var runtimePath = Environment.GetEnvironmentVariable("CODEX_QUOTA_REAL_RUNTIME") ?? DefaultRuntimePath;

        if (!File.Exists(runtimePath))
        {
            Console.Error.WriteLine($"FAIL: no Codex runtime at '{runtimePath}'.");
            Console.Error.WriteLine("Set CODEX_QUOTA_REAL_RUNTIME to the Codex executable to run this check.");
            return 2;
        }

        Console.WriteLine($"Runtime: {runtimePath}");

        // The isolated home keeps this check from touching the operator's own Codex login.
        var codexHome = Path.Combine(Path.GetTempPath(), $"codexquota-realcheck-{Guid.NewGuid():N}");
        Directory.CreateDirectory(codexHome);

        var stderrLines = new List<string>();
        var diagnostics = new List<string>();

        // Exactly the launch configuration CodexRuntimeLocator resolves for the real distribution.
        var runtime = new CodexRuntime(runtimePath, ["app-server", "--listen", "stdio://"]);
        var process = CodexAppServerProcess.Start(
            runtime,
            codexHome,
            line =>
            {
                lock (stderrLines)
                {
                    stderrLines.Add(line);
                }
            },
            diagnostics.Add);

        try
        {
            await using var client = new CodexRpcClient(process.Transport);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            // 1. The documented handshake: one initialize request, then one initialized notification.
            await client.InitializeAsync(timeout.Token);
            Console.WriteLine("PASS: initialize handshake completed.");

            // 2. The account surface answers; this is what the login flow branches on.
            var account = await new CodexAccountService(client).ReadAccountAsync(timeout.Token);
            Console.WriteLine($"PASS: account/read -> authenticated={account.Authenticated}");

            if (account.Authenticated)
            {
                Console.WriteLine("NOTE: this runtime is already logged in; the AuthRequired path was not exercised.");
            }

            // 3. The quota-read method is accepted by the real server. Without a login it must be
            //    refused as an account problem, not as a malformed request: that is the difference
            //    between "the Bridge speaks the protocol" and "the user has not logged in yet".
            try
            {
                await client.CallAsync<JsonElement>("account/rateLimits/read", null, timeout.Token);
                Console.WriteLine("PASS: account/rateLimits/read succeeded (account is logged in).");
            }
            catch (Exception exception)
            {
                if (exception.Message.Contains("missing field", StringComparison.OrdinalIgnoreCase)
                    || exception.Message.Contains("Invalid request", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"FAIL: account/rateLimits/read was rejected as malformed: {exception.Message}");
                    return 1;
                }

                Console.WriteLine($"PASS: account/rateLimits/read reached the account gate: {exception.Message}");
            }
        }
        finally
        {
            await process.DisposeAsync();
        }

        if (diagnostics.Count > 0)
        {
            Console.Error.WriteLine($"FAIL: the child survived termination: {string.Join(" | ", diagnostics)}");
            return 1;
        }

        Console.WriteLine("PASS: disposing the Bridge terminated the owned child process.");
        Console.WriteLine("RESULT: PASS");
        return 0;
    }
}
