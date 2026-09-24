namespace CodexQuota.Networking.Discovery;

/// <summary>
/// The DNS-SD advertisement the Bridge publishes on the LAN.
/// </summary>
/// <param name="BridgeId">Non-personal, stable Bridge identifier.</param>
/// <param name="ApiVersion">API path version, so a client can decide whether it can talk to this Bridge.</param>
/// <param name="Port">TCP port. This travels in the SRV record, not in the TXT record.</param>
/// <param name="Tls">Whether the endpoint is HTTPS. Always true in V1.</param>
/// <remarks>
/// The TXT record is deliberately tiny. Discovery happens before any trust exists, so everything in
/// it is public: no account details, no Windows user name, no token, no key, no pairing secret.
/// </remarks>
public sealed record MdnsAdvertisement(string BridgeId, string ApiVersion, int Port, bool Tls)
{
    /// <summary>The DNS-SD service type the Bridge publishes.</summary>
    public const string ServiceType = "_codexquota._tcp";

    /// <summary>The fully qualified service type, as it appears on the wire.</summary>
    public const string QualifiedServiceType = ServiceType + ".local.";

    /// <summary>
    /// The TXT record. Its keys are exactly these three, and a test asserts that so a future field
    /// cannot be added without someone noticing it is public.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToTxtRecords()
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bridgeId"] = BridgeId,
            ["apiVersion"] = ApiVersion,
            ["tls"] = Tls ? "1" : "0",
        };
}

/// <summary>Publishes and withdraws the Bridge's LAN advertisement.</summary>
public interface IMdnsPublisher : IAsyncDisposable
{
    /// <summary>
    /// Publishes the advertisement, replacing any previous one. Replacing rather than adding is what
    /// keeps a stale address from staying visible after the PC's IP changes.
    /// </summary>
    Task PublishAsync(MdnsAdvertisement advertisement, CancellationToken cancellationToken);

    /// <summary>Withdraws the advertisement. Safe to call when nothing is published.</summary>
    Task UnpublishAsync(CancellationToken cancellationToken);
}
