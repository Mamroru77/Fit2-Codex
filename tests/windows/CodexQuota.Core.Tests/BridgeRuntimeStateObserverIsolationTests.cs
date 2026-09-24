using CodexQuota.Core.Quota;
using CodexQuota.Core.Runtime;
using Xunit;

namespace CodexQuota.Core.Tests;

/// <summary>
/// <see cref="BridgeRuntimeState.Changed"/> is the seam every future UI and logging subscriber
/// attaches to. A subscriber is third-party code from the producer's point of view, so it must not
/// be able to break the producer, misreport the source, or starve the subscribers behind it.
/// </summary>
public class BridgeRuntimeStateObserverIsolationTests
{
    [Fact]
    public void AThrowingObserverDoesNotEscapeTheUpdate()
    {
        var runtime = new BridgeRuntimeState();
        runtime.Changed += _ => throw new InvalidOperationException("observer failure");

        // The transition has already been applied when observers run, so it must be reported as
        // applied. Letting the exception escape would make callers believe the change failed.
        var exception = Record.Exception(() => runtime.SetPhase(BridgeRuntimePhase.Ready));

        Assert.Null(exception);
        Assert.Equal(BridgeRuntimePhase.Ready, runtime.Current.Phase);
    }

    [Fact]
    public void AThrowingObserverDoesNotPreventLaterObserversFromSeeingTheUpdate()
    {
        var runtime = new BridgeRuntimeState();
        var laterObserverSaw = new List<BridgeRuntimeSnapshot>();

        runtime.Changed += _ => throw new InvalidOperationException("first observer failure");
        runtime.Changed += laterObserverSaw.Add;

        runtime.SetPhase(BridgeRuntimePhase.Ready);

        var snapshot = Assert.Single(laterObserverSaw);
        Assert.Equal(BridgeRuntimePhase.Ready, snapshot.Phase);
    }

    [Fact]
    public void EveryObserverSeesTheSameUpdateEvenWhenOthersThrow()
    {
        var runtime = new BridgeRuntimeState();
        var seen = new List<BridgeRuntimeSnapshot>();

        runtime.Changed += _ => throw new InvalidOperationException("first observer failure");
        runtime.Changed += seen.Add;
        runtime.Changed += _ => throw new InvalidOperationException("third observer failure");
        runtime.Changed += seen.Add;

        runtime.SetSourceStatus(QuotaSourceStatus.SourceError);

        Assert.Equal(2, seen.Count);
        Assert.All(seen, snapshot => Assert.Equal(QuotaSourceStatus.SourceError, snapshot.SourceStatus));
    }

    [Fact]
    public void AThrowingObserverDoesNotBreakShutdown()
    {
        var runtime = new BridgeRuntimeState();
        runtime.Changed += _ => throw new InvalidOperationException("observer failure");

        // Shutdown is the last thing the runtime does. A subscriber that throws there must not be
        // able to stop the runtime from reporting that it stopped.
        var exception = Record.Exception(() => runtime.SetPhase(BridgeRuntimePhase.Stopped));

        Assert.Null(exception);
        Assert.Equal(BridgeRuntimePhase.Stopped, runtime.Current.Phase);
    }
}
