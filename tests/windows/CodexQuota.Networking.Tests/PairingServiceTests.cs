using System.Security.Cryptography;
using System.Text;
using CodexQuota.Networking.Auth;
using CodexQuota.Networking.Pairing;
using CodexQuota.Storage.Devices;

namespace CodexQuota.Networking.Tests;

/// <summary>
/// Pairing is the one moment trust is established, so its rules are the security boundary: a
/// session is one-time, it expires, and only a local Windows action can approve it.
/// </summary>
public class PairingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public void ALocallyCreatedQrSessionWaitsForThePhoneToClaimIt()
    {
        var harness = new PairingHarness();

        var session = harness.Service.CreateSession(PairingOrigin.QrCode, null, Now);

        Assert.Equal(PairingState.AwaitingClient, session.State);
        Assert.Equal(Now.Add(PairingService.SessionLifetime), session.ExpiresAt);
        Assert.Equal(6, session.VerificationCode.Length);
        Assert.True(int.TryParse(session.VerificationCode, out _), "The code must be six digits.");
    }

    [Fact]
    public void ADiscoveryRequestGoesStraightToLocalApproval()
    {
        var harness = new PairingHarness();

        var session = harness.Service.CreateSession(PairingOrigin.Discovery, "Find X8", Now);

        // Discovery never establishes trust on its own, so it can only ever ask.
        Assert.Equal(PairingState.AwaitingLocalApproval, session.State);
        Assert.Equal("Find X8", session.RequestedDisplayName);
    }

    [Fact]
    public void AQrSessionCanOnlyBeClaimedOnce()
    {
        var harness = new PairingHarness();
        var session = harness.Service.CreateSession(PairingOrigin.QrCode, null, Now);

        var claimed = harness.Service.Claim(session.PairingId, "Find X8", Now.AddSeconds(10));

        Assert.Equal(PairingState.AwaitingLocalApproval, claimed.State);
        Assert.Equal("Find X8", claimed.RequestedDisplayName);

        Assert.Throws<InvalidOperationException>(
            () => harness.Service.Claim(session.PairingId, "Someone Else", Now.AddSeconds(20)));
    }

    [Fact]
    public async Task AnUnapprovedSessionCannotComplete()
    {
        var harness = new PairingHarness();
        var session = harness.Service.CreateSession(PairingOrigin.Discovery, "Find X8", Now);

        var result = await harness.Service.CompleteAsync(session.PairingId, Now.AddSeconds(1), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("PAIRING_INVALID", result.ErrorCode);
        Assert.Null(result.Credential);
        Assert.Empty(harness.Repository.Devices);
    }

    [Fact]
    public async Task ALocalRejectPreventsCompletion()
    {
        var harness = new PairingHarness();
        var session = harness.Service.CreateSession(PairingOrigin.Discovery, "Find X8", Now);

        harness.Service.RejectLocally(session.PairingId, Now.AddSeconds(1));

        var result = await harness.Service.CompleteAsync(session.PairingId, Now.AddSeconds(2), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Credential);
        Assert.Empty(harness.Repository.Devices);
    }

    [Fact]
    public async Task LocalApprovalJustBeforeExpiryStillCompletes()
    {
        var harness = new PairingHarness();
        var session = harness.Service.CreateSession(PairingOrigin.Discovery, "Find X8", Now);

        harness.Service.ApproveLocally(session.PairingId, Now.AddSeconds(1));

        var result = await harness.Service.CompleteAsync(
            session.PairingId,
            session.ExpiresAt.AddSeconds(-1),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.NotNull(result.Credential);
        Assert.Single(harness.Repository.Devices);
    }

    [Fact]
    public async Task CompletionAtTheExpiryInstantIsExpired()
    {
        var harness = new PairingHarness();
        var session = harness.Service.CreateSession(PairingOrigin.Discovery, "Find X8", Now);

        harness.Service.ApproveLocally(session.PairingId, Now.AddSeconds(1));

        var result = await harness.Service.CompleteAsync(session.PairingId, session.ExpiresAt, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("PAIRING_EXPIRED", result.ErrorCode);
        Assert.Empty(harness.Repository.Devices);
    }

    [Fact]
    public async Task ASecondCompletionFailsAsUsed()
    {
        var harness = new PairingHarness();
        var session = harness.Service.CreateSession(PairingOrigin.Discovery, "Find X8", Now);

        harness.Service.ApproveLocally(session.PairingId, Now.AddSeconds(1));

        var first = await harness.Service.CompleteAsync(session.PairingId, Now.AddSeconds(2), CancellationToken.None);
        var second = await harness.Service.CompleteAsync(session.PairingId, Now.AddSeconds(3), CancellationToken.None);

        Assert.True(first.Succeeded, first.ErrorCode);
        Assert.False(second.Succeeded);
        Assert.Equal("PAIRING_INVALID", second.ErrorCode);

        // Exactly one device, and exactly one credential, for one pairing.
        Assert.Single(harness.Repository.Devices);
    }

    [Fact]
    public async Task ARejectedSessionCannotBeResurrectedByApproval()
    {
        var harness = new PairingHarness();
        var session = harness.Service.CreateSession(PairingOrigin.Discovery, "Find X8", Now);

        harness.Service.RejectLocally(session.PairingId, Now.AddSeconds(1));

        Assert.Throws<InvalidOperationException>(() => harness.Service.ApproveLocally(session.PairingId, Now.AddSeconds(2)));

        var result = await harness.Service.CompleteAsync(session.PairingId, Now.AddSeconds(3), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task TheVerificationCodeIsStableButIsNotAuthentication()
    {
        var harness = new PairingHarness();
        var session = harness.Service.CreateSession(PairingOrigin.Discovery, "Find X8", Now);

        var again = harness.Service.Find(session.PairingId, Now.AddSeconds(5));

        Assert.Equal(session.VerificationCode, again!.VerificationCode);

        // The six-digit code is for a human to compare, not a credential: presenting it as a bearer
        // token must authenticate nothing.
        Assert.Null(await harness.Tokens.ValidateAsync(session.VerificationCode, CancellationToken.None));
    }

    [Fact]
    public async Task AnExpiredSessionIsReportedAsExpiredAndCannotBeApproved()
    {
        var harness = new PairingHarness();
        var session = harness.Service.CreateSession(PairingOrigin.Discovery, "Find X8", Now);

        var expired = harness.Service.Find(session.PairingId, session.ExpiresAt.AddSeconds(1));

        Assert.Equal(PairingState.Expired, expired!.State);

        Assert.Throws<InvalidOperationException>(
            () => harness.Service.ApproveLocally(session.PairingId, session.ExpiresAt.AddSeconds(2)));

        var result = await harness.Service.CompleteAsync(
            session.PairingId,
            session.ExpiresAt.AddSeconds(3),
            CancellationToken.None);

        Assert.False(result.Succeeded);
    }
}

/// <summary>
/// Device credentials are the long-lived half of the trust relationship, so their entropy and their
/// storage both matter.
/// </summary>
public class DeviceTokenServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnIssuedCredentialDecodesToAtLeastThirtyTwoRandomBytes()
    {
        var harness = new PairingHarness();

        var credential = await harness.Tokens.IssueAsync("Find X8", Now, CancellationToken.None);

        var decoded = Base64UrlDecode(credential.Token);

        // The device id prefix plus a 256-bit secret.
        Assert.True(decoded.Length >= 32, $"The credential decoded to only {decoded.Length} bytes.");

        // And it is not a constant: two credentials must not share a secret.
        var second = await harness.Tokens.IssueAsync("Find X8", Now, CancellationToken.None);

        Assert.NotEqual(credential.Token, second.Token);
        Assert.NotEqual(credential.DeviceId, second.DeviceId);
    }

    [Fact]
    public async Task TheRepositoryNeverReceivesThePlaintextToken()
    {
        var harness = new PairingHarness();

        var credential = await harness.Tokens.IssueAsync("Find X8", Now, CancellationToken.None);

        var stored = Assert.Single(harness.Repository.Devices);

        Assert.NotEqual(credential.Token, stored.TokenHash);
        Assert.DoesNotContain(credential.Token, stored.TokenHash, StringComparison.Ordinal);
        Assert.False(
            string.IsNullOrWhiteSpace(stored.TokenHash),
            "The stored hash must not be empty.");
    }

    [Fact]
    public async Task AValidTokenResolvesToItsDevice()
    {
        var harness = new PairingHarness();
        var credential = await harness.Tokens.IssueAsync("Find X8", Now, CancellationToken.None);

        var principal = await harness.Tokens.ValidateAsync(credential.Token, CancellationToken.None);

        Assert.NotNull(principal);
        Assert.Equal(credential.DeviceId, principal!.DeviceId);
        Assert.Equal("Find X8", principal.DisplayName);
    }

    [Fact]
    public async Task ARevokedDeviceFailsValidationImmediately()
    {
        var harness = new PairingHarness();
        var credential = await harness.Tokens.IssueAsync("Find X8", Now, CancellationToken.None);

        Assert.NotNull(await harness.Tokens.ValidateAsync(credential.Token, CancellationToken.None));

        Assert.True(await harness.Tokens.RevokeAsync(credential.DeviceId, Now.AddMinutes(1), CancellationToken.None));

        Assert.Null(await harness.Tokens.ValidateAsync(credential.Token, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("AAAA")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task AMalformedTokenAuthenticatesNothing(string token)
    {
        var harness = new PairingHarness();

        Assert.Null(await harness.Tokens.ValidateAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task TwoWrongTokensFailIdenticallyRegardlessOfHowWrongTheyAre()
    {
        var harness = new PairingHarness();
        var credential = await harness.Tokens.IssueAsync("Find X8", Now, CancellationToken.None);

        var decoded = Base64UrlDecode(credential.Token);

        // One token shares the real device id but has a wrong secret; the other shares nothing.
        var sameDeviceWrongSecret = Base64UrlEncode(
            [.. decoded[..16], .. RandomNumberGenerator.GetBytes(decoded.Length - 16)]);
        var differentDevice = Base64UrlEncode(RandomNumberGenerator.GetBytes(decoded.Length));

        var first = await harness.Tokens.ValidateAsync(sameDeviceWrongSecret, CancellationToken.None);
        var second = await harness.Tokens.ValidateAsync(differentDevice, CancellationToken.None);

        // The logical outcome must not depend on how much of the token happened to be right.
        Assert.Null(first);
        Assert.Null(second);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded,
        };

        return Convert.FromBase64String(padded);
    }

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Wires the pairing service, the token service and an in-memory device store together.</summary>
internal sealed class PairingHarness
{
    internal PairingHarness()
    {
        Repository = new InMemoryPairedDeviceRepository();
        Tokens = new DeviceTokenService(Repository);
        Service = new PairingService(Tokens);
    }

    internal InMemoryPairedDeviceRepository Repository { get; }

    internal DeviceTokenService Tokens { get; }

    internal PairingService Service { get; }
}

/// <summary>An in-memory device store that records exactly what it was handed.</summary>
internal sealed class InMemoryPairedDeviceRepository : IPairedDeviceRepository
{
    private readonly List<PairedDevice> _devices = [];
    private readonly object _gate = new();

    internal IReadOnlyList<PairedDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices.ToArray();
            }
        }
    }

    public Task AppendAsync(PairedDevice device, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _devices.Add(device);
        }

        return Task.CompletedTask;
    }

    public Task<PairedDevice?> FindByDeviceIdAsync(string deviceId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_devices.FirstOrDefault(device => device.DeviceId == deviceId));
        }
    }

    public Task<IReadOnlyList<PairedDevice>> ListAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<PairedDevice>>(_devices.ToArray());
        }
    }

    public Task<bool> RevokeAsync(string deviceId, DateTimeOffset revokedAt, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var index = _devices.FindIndex(device => device.DeviceId == deviceId);

            if (index < 0)
            {
                return Task.FromResult(false);
            }

            _devices[index] = _devices[index] with { RevokedAt = revokedAt };
            return Task.FromResult(true);
        }
    }
}
