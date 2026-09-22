namespace CodexQuota.Codex.Process;

/// <summary>
/// Resolves the explicitly supported Codex App Server binary and its isolated home directory.
/// The Bridge owns this child process and never attaches to the Codex desktop application.
/// </summary>
public static class CodexRuntimeLocator
{
    /// <summary>Folder beside the desktop executable that holds the supported runtime.</summary>
    public const string RuntimeFolderName = "runtime";

    /// <summary>File name of the supported App Server binary.</summary>
    public const string AppServerFileName = "codex-app-server.exe";

    /// <summary>Bridge folder under the user's local application data.</summary>
    public const string BridgeFolderName = "CodexQuotaBridge";

    /// <summary>Leaf folder used as the Bridge's isolated <c>CODEX_HOME</c>.</summary>
    public const string CodexHomeFolderName = "codex-home";

    /// <summary>
    /// Returns the path of the supported App Server binary shipped beside the desktop executable.
    /// </summary>
    public static string ResolveAppServerPath(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        return Path.Combine(baseDirectory, RuntimeFolderName, AppServerFileName);
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
