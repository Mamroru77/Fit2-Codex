// System.IO is explicit on purpose: enabling UseWindowsForms replaces the SDK's default
// implicit-usings set with a Windows-specific one that does not include it.
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using CodexQuota.Networking.Discovery;
using CodexQuota.Networking.Pairing;
using CodexQuota.Networking.Security;
using QRCoder.Core.Generators;
using QRCoder.Core.Renderers;

namespace CodexQuota.Desktop.ViewModels;

/// <summary>
/// Drives the Pair Device window: the QR code, the Bridge identity code, and the Allow/Reject
/// decision that only this window can make.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place in the product that can approve a pairing, and it does so only in response
/// to a click. Nothing here is reachable from the network.
/// </para>
/// <para>
/// The window shows two codes and they are different things. The Bridge identity code is derived from
/// the pinned fingerprint and never changes; it is what the phone compares to decide whether it is
/// talking to the right Bridge. The session code belongs to one pairing session and is what the user
/// compares to decide whether this is the phone they just picked up.
/// </para>
/// </remarks>
public sealed class PairingViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PairingService _pairing;
    private readonly BridgeIdentity _identity;
    private readonly Func<PairingQrPayload?> _payloadFactory;

    private PairingSession? _pending;
    private string _pairingJson = string.Empty;
    private BitmapSource? _qrImage;
    private bool _disposed;

    /// <param name="pairing">The pairing service, used for creating, approving and rejecting sessions.</param>
    /// <param name="identity">The Bridge identity, for the code the phone compares against.</param>
    /// <param name="payloadFactory">
    /// Builds the QR payload for a session. It returns <c>null</c> when there is no LAN endpoint yet,
    /// which is a real state: a Bridge with no eligible interface cannot be paired with.
    /// </param>
    public PairingViewModel(
        PairingService pairing,
        BridgeIdentity identity,
        Func<PairingQrPayload?> payloadFactory)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(payloadFactory);

        _pairing = pairing;
        _identity = identity;
        _payloadFactory = payloadFactory;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The stable Bridge identity code, e.g. <c>A1B2-C3D4-E5F6-7890</c>.</summary>
    public string BridgeVerificationCode => _identity.VerificationCode;

    /// <summary>The QR payload currently displayed, or empty when pairing cannot start yet.</summary>
    public string PairingJson
    {
        get => _pairingJson;
        private set => Set(ref _pairingJson, value);
    }

    /// <summary>The rendered QR code, or <c>null</c>.</summary>
    public BitmapSource? QrImage
    {
        get => _qrImage;
        private set => Set(ref _qrImage, value);
    }

    /// <summary>True when the Bridge has a LAN endpoint and a QR code can be shown.</summary>
    public bool CanPair => _qrImage is not null;

    /// <summary>True when a phone is waiting for this window's decision.</summary>
    public bool HasPendingRequest => _pending is not null;

    /// <summary>The phone waiting for approval, and the code the user compares.</summary>
    public string PendingRequestText => _pending is null
        ? "No phone is waiting."
        : $"{_pending.RequestedDisplayName ?? "A phone"} wants to pair. Code {_pending.VerificationCode}.";

    /// <summary>Starts a new pairing session and renders its QR code.</summary>
    public void StartPairing()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var session = _pairing.CreateSession(PairingOrigin.QrCode, null, DateTimeOffset.UtcNow);
        var payload = _payloadFactory();

        if (payload is null)
        {
            // No endpoint means no usable QR code. Showing one would send the phone to an address
            // nothing is listening on.
            _pending = null;
            PairingJson = string.Empty;
            QrImage = null;
            Raise(nameof(CanPair));
            Raise(nameof(HasPendingRequest));
            Raise(nameof(PendingRequestText));
            return;
        }

        var json = (payload with { PairingId = session.PairingId }).ToJson();

        _pending = session;
        PairingJson = json;
        QrImage = RenderQr(json);

        Raise(nameof(CanPair));
        Raise(nameof(HasPendingRequest));
        Raise(nameof(PendingRequestText));
    }

    /// <summary>Re-reads the session so a claim made by the phone becomes visible here.</summary>
    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pending is null)
        {
            return;
        }

        var current = _pairing.Find(_pending.PairingId, DateTimeOffset.UtcNow);

        if (current is not null)
        {
            _pending = current;
        }

        Raise(nameof(HasPendingRequest));
        Raise(nameof(PendingRequestText));
    }

    /// <summary>
    /// Approves the waiting phone. This is the only transition in the product that can lead to a
    /// device credential, and it happens only here.
    /// </summary>
    public void Allow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pending is null)
        {
            return;
        }

        _pending = _pairing.ApproveLocally(_pending.PairingId, DateTimeOffset.UtcNow);

        Raise(nameof(HasPendingRequest));
        Raise(nameof(PendingRequestText));
    }

    /// <summary>Refuses the waiting phone.</summary>
    public void Reject()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pending is null)
        {
            return;
        }

        _pending = _pairing.RejectLocally(_pending.PairingId, DateTimeOffset.UtcNow);

        Raise(nameof(HasPendingRequest));
        Raise(nameof(PendingRequestText));
    }

    public void Dispose() => _disposed = true;

    /// <summary>Renders the payload as a PNG-backed bitmap. The payload itself is never logged.</summary>
    private static BitmapSource RenderQr(string json)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(json, QRCodeGenerator.ECCLevel.M);

        var png = new PngByteQRCode(data).GetGraphic(20);

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();

        return image;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Raise(propertyName);
    }

    private void Raise(string? propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
