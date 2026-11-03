using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace CrmSync.Sync;

/// <summary>
/// Copies changed billing customers into the CRM.
/// Retries, timeouts, and the circuit breaker are handled by the HttpClient pipelines; this class decides
/// what happens when a call still fails: the record goes to the dead-letter queue and the run carries on,
/// unless the CRM is down altogether, in which case the run stops and resumes from its checkpoint later.
/// </summary>
public sealed class SyncEngine(
    IBillingClient billing,
    ICrmClient crm,
    SyncState state,
    IAlerts alerts,
    TimeProvider time,
    IOptions<SyncOptions> options,
    ILogger<SyncEngine> logger)
{
    private readonly SemaphoreSlim _running = new(1, 1);

    /// <summary>Runs one sync. Returns null if another run or replay is already in progress.</summary>
    public async Task<SyncRun?> RunAsync(CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct)) return null;
        var id = Guid.NewGuid();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RunId"] = id });
        var startedAt = time.GetUtcNow();
        var since = state.Checkpoint;
        var checkpoint = since;
        int read = 0, upserted = 0, skipped = 0, deadLettered = 0;

        SyncRun Finish(string status, string? note = null)
        {
            var run = new SyncRun(id, startedAt, time.GetUtcNow(), status, read, upserted, skipped, deadLettered, note);
            state.RecordRun(run);
            logger.LogInformation(
                "Sync run {Status}: read {Read}, upserted {Upserted}, skipped {Skipped}, dead-lettered {DeadLettered}",
                status, read, upserted, skipped, deadLettered);
            return run;
        }

        try
        {
            for (int? page = 1; page is not null;)
            {
                BillingPage changes;
                try
                {
                    changes = await billing.GetChangesAsync(since, page.Value, ct);
                }
                catch (Exception ex) when (IsDependencyFailure(ex, ct))
                {
                    state.Checkpoint = checkpoint;
                    alerts.Raise("billing-unavailable", $"The billing API could not be read after retries ({Describe(ex)}). The sync will try again on its next run.");
                    return Finish("Failed", $"Billing API unavailable: {Describe(ex)}");
                }

                foreach (var customer in changes.Items)
                {
                    read++;
                    var contact = ContactMapper.Map(customer);
                    if (state.IsUnchanged(contact))
                    {
                        skipped++;
                    }
                    else
                    {
                        try
                        {
                            await crm.UpsertAsync(contact, ct);
                            state.MarkSynced(contact);
                            upserted++;
                        }
                        catch (BrokenCircuitException)
                        {
                            state.Checkpoint = checkpoint;
                            alerts.Raise("crm-circuit-open", "The CRM is failing most requests, so the sync paused instead of hammering it. It resumes from where it stopped on the next run.");
                            return Finish("Paused", "CRM circuit open; resuming from checkpoint on the next run");
                        }
                        catch (Exception ex) when (IsDependencyFailure(ex, ct))
                        {
                            state.AddDeadLetter(customer, Describe(ex), time.GetUtcNow());
                            deadLettered++;
                            logger.LogWarning("Customer {CustomerId} moved to the dead-letter queue: {Reason}", customer.Id, Describe(ex));
                        }
                    }

                    if (customer.UpdatedAt > checkpoint) checkpoint = customer.UpdatedAt;
                }

                page = changes.NextPage;
            }

            state.Checkpoint = checkpoint;
            if (state.DeadLetterCount >= options.Value.DeadLetterAlertThreshold)
                alerts.Raise("dead-letters", $"{state.DeadLetterCount} records failed after retries and are waiting in the dead-letter queue.");
            return Finish("Completed");
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>Sends every dead-lettered record to the CRM again. Returns null if a run is in progress.</summary>
    public async Task<(int Replayed, int StillFailing)?> ReplayDeadLettersAsync(CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct)) return null;
        try
        {
            int replayed = 0, failing = 0;
            foreach (var letter in state.DeadLetters)
            {
                var contact = ContactMapper.Map(letter.Customer);
                try
                {
                    await crm.UpsertAsync(contact, ct);
                    state.MarkSynced(contact);
                    replayed++;
                }
                catch (Exception ex) when (ex is BrokenCircuitException || IsDependencyFailure(ex, ct))
                {
                    state.AddDeadLetter(letter.Customer, Describe(ex), time.GetUtcNow());
                    failing++;
                    if (ex is BrokenCircuitException) break;
                }
            }
            logger.LogInformation("Dead-letter replay: {Replayed} sent, {StillFailing} still failing", replayed, failing);
            return (replayed, failing);
        }
        finally
        {
            _running.Release();
        }
    }

    private static bool IsDependencyFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
        || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code}",
        BrokenCircuitException => "circuit open",
        TimeoutRejectedException or TaskCanceledException => "timed out",
        _ => ex.GetType().Name,
    };
}

/// <summary>Runs the sync on a fixed interval.</summary>
public sealed class SyncWorker(SyncEngine engine, IOptions<SyncOptions> options, ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); // let the web server start listening first
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await engine.RunAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Sync run crashed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
