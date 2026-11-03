using System.Collections.Concurrent;

namespace CrmSync.Mocks;

/// <summary>
/// A stand-in for a CRM's contacts API: enforces a per-second rate limit with 429 and Retry-After,
/// like most real CRMs, and returns 503 at the rate set in <see cref="Chaos"/>.
/// </summary>
public sealed class CrmMock(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, CrmContact> _contacts = new();
    private readonly Lock _gate = new();
    private long _windowSecond;
    private int _windowCount;

    public int Count => _contacts.Count;

    public IReadOnlyList<CrmContact> Contacts => _contacts.Values.OrderBy(c => c.Company).ToList();

    private bool TryTakeRateLimitSlot(int perSecond)
    {
        lock (_gate)
        {
            var second = time.GetUtcNow().ToUnixTimeSeconds();
            if (second != _windowSecond)
            {
                _windowSecond = second;
                _windowCount = 0;
            }
            return ++_windowCount <= perSecond;
        }
    }

    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPut("/mock/crm/contacts/{externalId}", async (string externalId, CrmContact contact, CrmMock crm, Chaos chaos, HttpResponse response) =>
        {
            await Task.Delay(Random.Shared.Next(10, 80));
            if (!crm.TryTakeRateLimitSlot(chaos.CrmRequestsPerSecond))
            {
                response.Headers.RetryAfter = "1";
                return Results.StatusCode(429);
            }
            if (Random.Shared.NextDouble() < chaos.CrmFailureRate)
                return Results.StatusCode(503);
            if (contact.ExternalId != externalId)
                return Results.BadRequest("ExternalId in the body must match the URL.");

            crm._contacts[externalId] = contact;
            return Results.Ok(contact);
        }).ExcludeFromDescription();
    }
}
