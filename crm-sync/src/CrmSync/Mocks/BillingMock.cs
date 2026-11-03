namespace CrmSync.Mocks;

/// <summary>
/// A stand-in for a billing system's API: fictional customers that keep changing,
/// served with random latency and, at the rate set in <see cref="Chaos"/>, server errors.
/// </summary>
public sealed class BillingMock
{
    private static readonly string[] Prefixes = ["Blue", "North", "Bright", "Cedar", "Harbor", "Summit", "Pixel", "Atlas", "Copper", "Maple"];
    private static readonly string[] Suffixes = ["Labs", "Analytics", "Health", "Logistics", "Studio", "Systems"];
    private static readonly string[] Plans = ["Starter", "Growth", "Scale"];
    private static readonly string[] Statuses = ["active", "active", "active", "past_due", "canceled"];

    private readonly Lock _gate = new();
    private readonly Dictionary<string, BillingCustomer> _customers = new();
    private readonly Random _random = new(42);
    private readonly TimeProvider _time;

    public BillingMock(TimeProvider time)
    {
        _time = time;
        var now = time.GetUtcNow();
        var n = 0;
        foreach (var prefix in Prefixes)
        foreach (var suffix in Suffixes)
        {
            n++;
            var id = $"cus_{n:D4}";
            var company = $"{prefix} {suffix}";
            var slug = company.ToLowerInvariant().Replace(' ', '-');
            var plan = Plans[_random.Next(Plans.Length)];
            _customers[id] = new BillingCustomer(id, company, $"billing@{slug}.example", plan, PriceOf(plan), "active", now.AddMinutes(-n));
        }
    }

    public int Count { get { lock (_gate) return _customers.Count; } }

    /// <summary>Changes a few customers, the way upgrades, failed payments, and cancellations would.</summary>
    public void SimulateActivity()
    {
        lock (_gate)
        {
            var ids = _customers.Keys.ToArray();
            for (var i = 0; i < _random.Next(1, 4); i++)
            {
                var c = _customers[ids[_random.Next(ids.Length)]];
                var plan = _random.NextDouble() < 0.5 ? Plans[_random.Next(Plans.Length)] : c.Plan;
                var status = Statuses[_random.Next(Statuses.Length)];
                _customers[c.Id] = c with { Plan = plan, Mrr = status == "canceled" ? 0 : PriceOf(plan), Status = status, UpdatedAt = _time.GetUtcNow() };
            }
        }
    }

    public BillingPage GetChanges(DateTimeOffset updatedSince, int page, int pageSize)
    {
        lock (_gate)
        {
            var changed = _customers.Values
                .Where(c => c.UpdatedAt >= updatedSince)
                .OrderBy(c => c.UpdatedAt).ThenBy(c => c.Id)
                .ToList();
            var items = changed.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            int? next = page * pageSize < changed.Count ? page + 1 : null;
            return new BillingPage(items, next);
        }
    }

    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/mock/billing/customers", async (DateTimeOffset? updatedSince, int? page, int? pageSize, BillingMock billing, Chaos chaos) =>
        {
            await Task.Delay(Random.Shared.Next(20, 150));
            if (Random.Shared.NextDouble() < chaos.BillingFailureRate)
                return Results.StatusCode(Random.Shared.NextDouble() < 0.5 ? 500 : 503);

            return Results.Ok(billing.GetChanges(updatedSince ?? DateTimeOffset.MinValue, Math.Max(page ?? 1, 1), Math.Clamp(pageSize ?? 20, 1, 100)));
        }).ExcludeFromDescription();
    }

    private static decimal PriceOf(string plan) => plan switch { "Starter" => 99m, "Growth" => 499m, _ => 1499m };
}

/// <summary>Keeps the fictional billing data changing while the demo runs.</summary>
public sealed class BillingActivitySimulator(BillingMock billing) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            billing.SimulateActivity();
    }
}
