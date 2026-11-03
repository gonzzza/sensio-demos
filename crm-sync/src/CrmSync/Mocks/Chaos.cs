namespace CrmSync.Mocks;

/// <summary>
/// Knobs that make the simulated billing API and CRM misbehave, adjustable live from the dashboard.
/// </summary>
public sealed class Chaos
{
    private readonly Lock _gate = new();
    private double _billingFailureRate = 0.10;
    private double _crmFailureRate = 0.10;
    private int _crmRequestsPerSecond = 8;

    public double BillingFailureRate { get { lock (_gate) return _billingFailureRate; } }
    public double CrmFailureRate { get { lock (_gate) return _crmFailureRate; } }
    public int CrmRequestsPerSecond { get { lock (_gate) return _crmRequestsPerSecond; } }

    public void Update(double? billingFailureRate, double? crmFailureRate, int? crmRequestsPerSecond)
    {
        lock (_gate)
        {
            if (billingFailureRate is { } b) _billingFailureRate = Math.Clamp(b, 0, 1);
            if (crmFailureRate is { } c) _crmFailureRate = Math.Clamp(c, 0, 1);
            if (crmRequestsPerSecond is { } r) _crmRequestsPerSecond = Math.Clamp(r, 1, 100);
        }
    }

    public object Snapshot() => new { BillingFailureRate, CrmFailureRate, CrmRequestsPerSecond };
}
