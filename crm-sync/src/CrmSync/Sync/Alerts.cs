using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace CrmSync.Sync;

public interface IAlerts
{
    void Raise(string key, string message);
}

/// <summary>
/// Raises alerts once per <see cref="AlertOptions.DedupeWindow"/>: logged, shown on the dashboard,
/// and posted to Slack when a webhook is configured.
/// </summary>
public sealed class AlertService(
    SyncState state,
    IHttpClientFactory httpClientFactory,
    IOptions<AlertOptions> options,
    TimeProvider time,
    ILogger<AlertService> logger) : IAlerts
{
    public const string HttpClientName = "alerts";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastRaised = new();

    public void Raise(string key, string message)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_lastRaised.TryGetValue(key, out var last) && now - last < options.Value.DedupeWindow)
                return;
            _lastRaised[key] = now;
        }

        logger.LogWarning("ALERT {AlertKey}: {AlertMessage}", key, message);
        state.RecordAlert(new Alert(now, key, message));

        if (!string.IsNullOrWhiteSpace(options.Value.SlackWebhookUrl))
            _ = PostToSlackAsync(message);
    }

    private async Task PostToSlackAsync(string message)
    {
        try
        {
            var http = httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.PostAsJsonAsync(options.Value.SlackWebhookUrl, new { text = $":rotating_light: CRM sync: {message}" });
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not post the alert to Slack");
        }
    }
}
