using System.Net.Http.Json;

namespace CrmSync.Sync;

public interface IBillingClient
{
    Task<BillingPage> GetChangesAsync(DateTimeOffset updatedSince, int page, CancellationToken ct);
}

public interface ICrmClient
{
    Task UpsertAsync(CrmContact contact, CancellationToken ct);
}

/// <summary>Billing API client. Retries, timeouts, and the circuit breaker live in its HttpClient pipeline.</summary>
public sealed class HttpBillingClient(HttpClient http) : IBillingClient
{
    public async Task<BillingPage> GetChangesAsync(DateTimeOffset updatedSince, int page, CancellationToken ct)
    {
        var url = $"customers?updatedSince={Uri.EscapeDataString(updatedSince.ToString("O"))}&page={page}&pageSize=20";
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BillingPage>(ct)
            ?? throw new InvalidOperationException("Billing API returned an empty page.");
    }
}

/// <summary>CRM client. Upserts by external id, so sending the same contact twice is harmless.</summary>
public sealed class HttpCrmClient(HttpClient http) : ICrmClient
{
    public async Task UpsertAsync(CrmContact contact, CancellationToken ct)
    {
        using var response = await http.PutAsJsonAsync($"contacts/{Uri.EscapeDataString(contact.ExternalId)}", contact, ct);
        response.EnsureSuccessStatusCode();
    }
}
