namespace NatsSyncLab.BackendA.Module.Products.Infrastructure.Persistence;

public sealed class OutboxRetentionOptions
{
    public const string SectionName = "Outbox";

    public int RetentionDays { get; set; } = 7;

    public int BatchSize { get; set; } = 5000;
}
