namespace CrmSync;

/// <summary>A customer as the billing system reports it.</summary>
public sealed record BillingCustomer(
    string Id,
    string Company,
    string Email,
    string Plan,
    decimal Mrr,
    string Status,
    DateTimeOffset UpdatedAt);

public sealed record BillingPage(IReadOnlyList<BillingCustomer> Items, int? NextPage);

/// <summary>A contact as the CRM stores it, keyed by the billing customer id.</summary>
public sealed record CrmContact(
    string ExternalId,
    string Company,
    string Email,
    string Plan,
    decimal Mrr,
    string Lifecycle);

/// <summary>A record that kept failing after every retry, kept for inspection and replay.</summary>
public sealed record DeadLetter(
    BillingCustomer Customer,
    string Reason,
    int Failures,
    DateTimeOffset FirstFailedAt,
    DateTimeOffset LastFailedAt);

public sealed record SyncRun(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Status,
    int Read,
    int Upserted,
    int Skipped,
    int DeadLettered,
    string? Note);

public sealed record Alert(DateTimeOffset RaisedAt, string Key, string Message);
