namespace CrmSync.Sync;

/// <summary>
/// Everything the sync remembers between runs: the checkpoint, what it last sent to the CRM,
/// the dead-letter queue, and recent runs and alerts. Kept in memory so the demo needs no database;
/// a production version would persist it.
/// </summary>
public sealed class SyncState
{
    private const int HistorySize = 25;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, CrmContact> _lastSynced = new();
    private readonly Dictionary<string, DeadLetter> _deadLetters = new();
    private readonly LinkedList<SyncRun> _runs = new();
    private readonly LinkedList<Alert> _alerts = new();
    private DateTimeOffset _checkpoint = DateTimeOffset.MinValue;
    private long _retries;

    public DateTimeOffset Checkpoint
    {
        get { lock (_gate) return _checkpoint; }
        set { lock (_gate) _checkpoint = value; }
    }

    public long Retries => Interlocked.Read(ref _retries);

    public void RecordRetry() => Interlocked.Increment(ref _retries);

    /// <summary>True when the CRM already has exactly this contact, so sending it again would be wasted work.</summary>
    public bool IsUnchanged(CrmContact contact)
    {
        lock (_gate) return _lastSynced.TryGetValue(contact.ExternalId, out var last) && last == contact;
    }

    public void MarkSynced(CrmContact contact)
    {
        lock (_gate)
        {
            _lastSynced[contact.ExternalId] = contact;
            _deadLetters.Remove(contact.ExternalId);
        }
    }

    public void AddDeadLetter(BillingCustomer customer, string reason, DateTimeOffset now)
    {
        lock (_gate)
        {
            _deadLetters[customer.Id] = _deadLetters.TryGetValue(customer.Id, out var existing)
                ? existing with { Customer = customer, Reason = reason, Failures = existing.Failures + 1, LastFailedAt = now }
                : new DeadLetter(customer, reason, 1, now, now);
        }
    }

    public IReadOnlyList<DeadLetter> DeadLetters
    {
        get { lock (_gate) return _deadLetters.Values.OrderBy(d => d.FirstFailedAt).ToList(); }
    }

    public int DeadLetterCount
    {
        get { lock (_gate) return _deadLetters.Count; }
    }

    public void RecordRun(SyncRun run)
    {
        lock (_gate)
        {
            _runs.AddFirst(run);
            if (_runs.Count > HistorySize) _runs.RemoveLast();
        }
    }

    public IReadOnlyList<SyncRun> Runs
    {
        get { lock (_gate) return _runs.ToList(); }
    }

    public void RecordAlert(Alert alert)
    {
        lock (_gate)
        {
            _alerts.AddFirst(alert);
            if (_alerts.Count > HistorySize) _alerts.RemoveLast();
        }
    }

    public IReadOnlyList<Alert> Alerts
    {
        get { lock (_gate) return _alerts.ToList(); }
    }
}
