namespace CodexQuota.Networking.Security;

/// <summary>
/// Supplies the Bridge's stable identity, creating it on first use.
/// </summary>
public interface IBridgeIdentityStore
{
    /// <summary>
    /// Returns the Bridge identity, generating and persisting one the first time it is called.
    /// Repeated calls — in this process or a later one — return the same identity.
    /// </summary>
    Task<BridgeIdentity> GetOrCreateAsync(CancellationToken cancellationToken);
}
