namespace CodexQuota.Codex.Process;

/// <summary>
/// The supported Codex App Server runtime: the executable to launch and the arguments that start
/// its stdio transport.
/// </summary>
/// <param name="ExecutablePath">Full path of the supported executable.</param>
/// <param name="Arguments">The arguments that start the App Server over stdio.</param>
public sealed record CodexRuntime(string ExecutablePath, IReadOnlyList<string> Arguments);

/// <summary>
/// Resolves the explicitly supported Codex App Server runtime and its isolated home directory.
/// The Bridge owns this child process and never attaches to the Codex desktop application.
/// </summary>
/// <remarks>
/// The runtime dependency is explicit and lives in a fixed place beside the desktop executable, so
/// the Bridge never depends on the Codex desktop application being installed, open, or configured.
/// </remarks>
public static class CodexRuntimeLocator
{
    /// <summary>Folder beside the desktop executable that holds the supported runtime.</summary>
    public const string RuntimeFolderName = "runtime";

    /// <summary>File name of a purpose-built standalone App Server binary, when one is packaged.</summary>
    public const string AppServerFileName = "codex-app-server.exe";

    /// <summary>File name of the Codex CLI, which hosts the App Server as a subcommand.</summary>
    public const string CodexCliFileName = "codex.exe";

    /// <summary>Subcommand that starts the App Server when the Codex CLI is the runtime.</summary>
    public const string AppServerSubcommand = "app-server";

    /// <summary>Transport selector accepted by both runtime shapes.</summary>
    public const string ListenArgument = "--listen";

    /// <summary>Line-oriented JSON-RPC over the child's standard streams.</summary>
    public const string StdioTransport = "stdio://";

    /// <summary>Bridge folder under the user's local application data.</summary>
    public const string BridgeFolderName = "CodexQuotaBridge";

    /// <summary>Leaf folder used as the Bridge's isolated <c>CODEX_HOME</c>.</summary>
    public const string CodexHomeFolderName = "codex-home";

    /// <summary>
    /// Resolves the supported runtime shipped beside the desktop executable.
    /// </summary>
    /// <remarks>
    /// Two shapes are supported, in this order:
    /// <list type="number">
    /// <item>a purpose-built standalone <c>codex-app-server.exe</c>, started with
    /// <c>--listen stdio://</c>;</item>
    /// <item>the Codex CLI <c>codex.exe</c>, whose App Server is the <c>app-server</c> subcommand,
    /// so it is started with <c>app-server --listen stdio://</c>.</item>
    /// </list>
    /// The second shape is what the official distribution actually installs: there is no standalone
    /// <c>codex-app-server</c> executable, the App Server is a subcommand of the CLI. Both are
    /// supported so packaging may vendor either, and the transport flag is identical in both cases.
    /// </remarks>
    public static CodexRuntime ResolveRuntime(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var standalone = ResolveAppServerPath(baseDirectory);

        return File.Exists(standalone)
            ? new CodexRuntime(standalone, [ListenArgument, StdioTransport])
            : new CodexRuntime(ResolveCodexCliPath(baseDirectory), [AppServerSubcommand, ListenArgument, StdioTransport]);
    }

    /// <summary>Path of the packaged standalone App Server binary beside the desktop executable.</summary>
    public static string ResolveAppServerPath(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        return Path.Combine(baseDirectory, RuntimeFolderName, AppServerFileName);
    }

    /// <summary>Path of the packaged Codex CLI beside the desktop executable.</summary>
    public static string ResolveCodexCliPath(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        return Path.Combine(baseDirectory, RuntimeFolderName, CodexCliFileName);
    }

    /// <summary>
    /// Returns the isolated <c>CODEX_HOME</c> so Bridge authentication and configuration never
    /// mix with Codex Desktop state.
    /// </summary>
    public static string ResolveCodexHome(string localAppDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataDirectory);

        return Path.Combine(localAppDataDirectory, BridgeFolderName, CodexHomeFolderName);
    }
}
