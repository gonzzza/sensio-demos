namespace CrmSync.Sync;

/// <summary>Translates billing data into the CRM's model, including the lifecycle stage sales teams act on.</summary>
public static class ContactMapper
{
    public static CrmContact Map(BillingCustomer customer) => new(
        ExternalId: customer.Id,
        Company: customer.Company,
        Email: customer.Email,
        Plan: customer.Plan,
        Mrr: customer.Mrr,
        Lifecycle: customer.Status switch
        {
            "active" => "customer",
            "past_due" => "at-risk",
            "canceled" => "churned",
            _ => "unknown",
        });
}
