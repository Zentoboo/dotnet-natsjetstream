namespace NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Persistence;

public sealed class InboxRetentionOptions
{
    public const string SectionName = "Inbox";

    public int RetentionDays { get; set; } = 8;

    public int BatchSize { get; set; } = 5000;
}
