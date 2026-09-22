namespace CodexQuota.Core.Quota;

/// <summary>
/// Thread-safe, in-memory <see cref="IQuotaStateStore"/>. Replacing the current
/// snapshot is a single synchronized operation so that the sequence number, the
/// previous snapshot and the returned update can never be observed out of step.
/// </summary>
public sealed class InMemoryQuotaStateStore : IQuotaStateStore
{
    private readonly object _gate = new();

    private QuotaSnapshot? _current;
    private long _sequence;

    public QuotaSnapshot? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public long Sequence
    {
        get
        {
            lock (_gate)
            {
                return _sequence;
            }
        }
    }

    public QuotaStateUpdate Replace(QuotaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            var previous = _current;

            _sequence++;
            _current = snapshot;

            return new QuotaStateUpdate(_sequence, previous, snapshot);
        }
    }
}
