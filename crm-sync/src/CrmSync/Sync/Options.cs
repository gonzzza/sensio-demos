namespace CrmSync.Sync;

public sealed class SyncOptions
{
    public const string Section = "Sync";

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);
    public int DeadLetterAlertThreshold { get; set; } = 5;
    public string BillingBaseUrl { get; set; } = "http://localhost:5080/mock/billing/";
    public string CrmBaseUrl { get; set; } = "http://localhost:5080/mock/crm/";
}

public sealed class AlertOptions
{
    public const string Section = "Alerts";

    /// <summary>Slack incoming-webhook URL. Leave empty to only log alerts and show them on the dashboard.</summary>
    public string? SlackWebhookUrl { get; set; }

    /// <summary>The same alert is not repeated within this window, so a long outage doesn't flood the channel.</summary>
    public TimeSpan DedupeWindow { get; set; } = TimeSpan.FromMinutes(5);
}
