using CrmSync.Mocks;
using CrmSync.Sync;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.Section));
builder.Services.Configure<AlertOptions>(builder.Configuration.GetSection(AlertOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);

// Simulated systems on the other side of the integration.
builder.Services.AddSingleton<Chaos>();
builder.Services.AddSingleton<BillingMock>();
builder.Services.AddSingleton<CrmMock>();
builder.Services.AddHostedService<BillingActivitySimulator>();

// The integration itself.
builder.Services.AddSingleton<SyncState>();
builder.Services.AddSingleton<IAlerts, AlertService>();
builder.Services.AddSingleton<SyncEngine>();
builder.Services.AddHostedService<SyncWorker>();
builder.Services.AddHttpClient(AlertService.HttpClientName);

builder.Services
    .AddHttpClient<IBillingClient, HttpBillingClient>((sp, http) =>
        http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<SyncOptions>>().Value.BillingBaseUrl))
    .AddStandardResilienceHandler()
    .Configure((options, sp) => ConfigureResilience(options, sp, "billing"));

builder.Services
    .AddHttpClient<ICrmClient, HttpCrmClient>((sp, http) =>
        http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<SyncOptions>>().Value.CrmBaseUrl))
    .AddStandardResilienceHandler()
    .Configure((options, sp) => ConfigureResilience(options, sp, "crm"));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

BillingMock.MapEndpoints(app);
CrmMock.MapEndpoints(app);

app.MapGet("/api/status", (SyncState state, BillingMock billing, CrmMock crm, Chaos chaos) => new
{
    CustomersInBilling = billing.Count,
    ContactsInCrm = crm.Count,
    RetriesAbsorbed = state.Retries,
    Checkpoint = state.Checkpoint == DateTimeOffset.MinValue ? (DateTimeOffset?)null : state.Checkpoint,
    state.DeadLetters,
    state.Runs,
    state.Alerts,
    Chaos = chaos.Snapshot(),
});

app.MapGet("/api/crm/contacts", (CrmMock crm) => crm.Contacts);

app.MapPost("/api/sync/run", async (SyncEngine engine, CancellationToken ct) =>
    await engine.RunAsync(ct) is { } run ? Results.Ok(run) : Results.Conflict("A sync is already running."));

app.MapPost("/api/dead-letters/replay", async (SyncEngine engine, CancellationToken ct) =>
    await engine.ReplayDeadLettersAsync(ct) is { } result
        ? Results.Ok(new { result.Replayed, result.StillFailing })
        : Results.Conflict("A sync is already running."));

app.MapPost("/api/chaos", (ChaosUpdate update, Chaos chaos) =>
{
    chaos.Update(update.BillingFailureRate, update.CrmFailureRate, update.CrmRequestsPerSecond);
    return chaos.Snapshot();
});

app.Run();

static void ConfigureResilience(HttpStandardResilienceOptions options, IServiceProvider sp, string dependency)
{
    var state = sp.GetRequiredService<SyncState>();
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CrmSync.Resilience");

    // Exponential backoff with jitter; 429 and 5xx are retried and a Retry-After header is honored.
    options.Retry.MaxRetryAttempts = 4;
    options.Retry.Delay = TimeSpan.FromMilliseconds(300);
    options.Retry.BackoffType = DelayBackoffType.Exponential;
    options.Retry.UseJitter = true;
    options.Retry.OnRetry = args =>
    {
        state.RecordRetry();
        var outcome = args.Outcome.Result is { } r ? $"HTTP {(int)r.StatusCode}" : args.Outcome.Exception?.GetType().Name;
        logger.LogInformation("Retrying {Dependency} call after {Outcome} (attempt {Attempt}, waiting {DelayMs} ms)",
            dependency, outcome, args.AttemptNumber + 1, (int)args.RetryDelay.TotalMilliseconds);
        return default;
    };

    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);

    // Stop calling a dependency that is failing most requests, and give it time to recover.
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
    options.CircuitBreaker.MinimumThroughput = 20;
    options.CircuitBreaker.FailureRatio = 0.5;
    options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);
    options.CircuitBreaker.OnOpened = args =>
    {
        logger.LogWarning("Circuit opened for {Dependency} for {BreakSeconds} s", dependency, (int)args.BreakDuration.TotalSeconds);
        return default;
    };
    options.CircuitBreaker.OnClosed = _ =>
    {
        logger.LogInformation("Circuit closed for {Dependency}", dependency);
        return default;
    };
}

record ChaosUpdate(double? BillingFailureRate, double? CrmFailureRate, int? CrmRequestsPerSecond);

public partial class Program;
