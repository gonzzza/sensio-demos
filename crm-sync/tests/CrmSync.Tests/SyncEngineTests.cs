using System.Net;
using CrmSync.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;

namespace CrmSync.Tests;

public class SyncEngineTests
{
    private readonly FakeBilling _billing = new(count: 30);
    private readonly FakeCrm _crm = new();
    private readonly FakeAlerts _alerts = new();
    private readonly SyncState _state = new();

    private SyncEngine CreateEngine(int deadLetterAlertThreshold = 5) => new(
        _billing, _crm, _state, _alerts, TimeProvider.System,
        Options.Create(new SyncOptions { DeadLetterAlertThreshold = deadLetterAlertThreshold }),
        NullLogger<SyncEngine>.Instance);

    [Fact]
    public async Task Second_run_reads_from_the_checkpoint_and_skips_what_the_crm_already_has()
    {
        var engine = CreateEngine();

        var first = await engine.RunAsync(CancellationToken.None);
        var second = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(("Completed", 30, 30), (first!.Status, first.Read, first.Upserted));
        // The checkpoint is inclusive, so the last record is read again and recognized as unchanged.
        Assert.Equal(("Completed", 1, 0, 1), (second!.Status, second.Read, second.Upserted, second.Skipped));
        Assert.Equal(30, _crm.Calls);
    }

    [Fact]
    public async Task Changed_customer_is_sent_again()
    {
        var engine = CreateEngine();
        await engine.RunAsync(CancellationToken.None);

        _billing.Change("cus_0007", c => c with { Status = "past_due" });
        var run = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(1, run!.Upserted);
        Assert.Equal("at-risk", _crm.Contacts["cus_0007"].Lifecycle);
    }

    [Fact]
    public async Task Failing_record_goes_to_dead_letter_queue_without_stopping_the_run_and_replay_recovers_it()
    {
        var engine = CreateEngine();
        _crm.FailingIds.Add("cus_0003");

        var run = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(("Completed", 29, 1), (run!.Status, run.Upserted, run.DeadLettered));
        Assert.Equal("cus_0003", Assert.Single(_state.DeadLetters).Customer.Id);
        Assert.Equal("HTTP 503", _state.DeadLetters[0].Reason);

        _crm.FailingIds.Clear();
        var replay = await engine.ReplayDeadLettersAsync(CancellationToken.None);

        Assert.Equal((1, 0), replay);
        Assert.Empty(_state.DeadLetters);
        Assert.Equal(30, _crm.Contacts.Count);
    }

    [Fact]
    public async Task Open_circuit_pauses_the_run_and_the_next_run_resumes_without_losing_records()
    {
        var engine = CreateEngine();
        _crm.OpenCircuitAfter = 10;

        var paused = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(("Paused", 10), (paused!.Status, paused.Upserted));
        Assert.Contains(_alerts.Raised, a => a.Key == "crm-circuit-open");
        Assert.Empty(_state.DeadLetters);

        _crm.OpenCircuitAfter = null;
        var resumed = await engine.RunAsync(CancellationToken.None);

        Assert.Equal("Completed", resumed!.Status);
        Assert.Equal(30, _crm.Contacts.Count);
    }

    [Fact]
    public async Task Unreachable_billing_api_fails_the_run_raises_an_alert_and_keeps_the_checkpoint()
    {
        var engine = CreateEngine();
        _billing.Unavailable = true;

        var run = await engine.RunAsync(CancellationToken.None);

        Assert.Equal("Failed", run!.Status);
        Assert.Equal(DateTimeOffset.MinValue, _state.Checkpoint);
        Assert.Contains(_alerts.Raised, a => a.Key == "billing-unavailable");
    }

    [Fact]
    public async Task Alert_is_raised_when_dead_letters_reach_the_threshold()
    {
        var engine = CreateEngine(deadLetterAlertThreshold: 2);
        _crm.FailingIds.UnionWith(["cus_0001", "cus_0002"]);

        await engine.RunAsync(CancellationToken.None);

        Assert.Contains(_alerts.Raised, a => a.Key == "dead-letters");
    }

    [Theory]
    [InlineData("active", "customer")]
    [InlineData("past_due", "at-risk")]
    [InlineData("canceled", "churned")]
    public void Billing_status_maps_to_crm_lifecycle(string status, string lifecycle)
    {
        var customer = new BillingCustomer("cus_1", "Cedar Labs", "billing@cedar-labs.example", "Growth", 499m, status, DateTimeOffset.UtcNow);

        Assert.Equal(lifecycle, ContactMapper.Map(customer).Lifecycle);
    }

    private sealed class FakeBilling : IBillingClient
    {
        private readonly Dictionary<string, BillingCustomer> _customers = new();
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow.AddHours(-1);

        public FakeBilling(int count)
        {
            for (var i = 1; i <= count; i++)
                _customers[$"cus_{i:D4}"] = new BillingCustomer($"cus_{i:D4}", $"Company {i}", $"billing@company-{i}.example", "Starter", 99m, "active", _start.AddSeconds(i));
        }

        public bool Unavailable { get; set; }

        public void Change(string id, Func<BillingCustomer, BillingCustomer> change) =>
            _customers[id] = change(_customers[id]) with { UpdatedAt = DateTimeOffset.UtcNow };

        public Task<BillingPage> GetChangesAsync(DateTimeOffset updatedSince, int page, CancellationToken ct)
        {
            if (Unavailable) throw new HttpRequestException("Service unavailable", null, HttpStatusCode.ServiceUnavailable);
            var changed = _customers.Values.Where(c => c.UpdatedAt >= updatedSince).OrderBy(c => c.UpdatedAt).ThenBy(c => c.Id).ToList();
            var items = changed.Skip((page - 1) * 20).Take(20).ToList();
            return Task.FromResult(new BillingPage(items, page * 20 < changed.Count ? page + 1 : null));
        }
    }

    private sealed class FakeCrm : ICrmClient
    {
        public Dictionary<string, CrmContact> Contacts { get; } = new();
        public HashSet<string> FailingIds { get; } = new();
        public int? OpenCircuitAfter { get; set; }
        public int Calls { get; private set; }

        public Task UpsertAsync(CrmContact contact, CancellationToken ct)
        {
            if (OpenCircuitAfter is { } limit && Contacts.Count >= limit) throw new BrokenCircuitException();
            Calls++;
            if (FailingIds.Contains(contact.ExternalId))
                throw new HttpRequestException("Service unavailable", null, HttpStatusCode.ServiceUnavailable);
            Contacts[contact.ExternalId] = contact;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAlerts : IAlerts
    {
        public List<(string Key, string Message)> Raised { get; } = new();

        public void Raise(string key, string message) => Raised.Add((key, message));
    }
}
