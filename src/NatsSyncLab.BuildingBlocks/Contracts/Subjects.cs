namespace NatsSyncLab.BuildingBlocks.Contracts;

public static class Subjects
{
    public const string SourceA = "a.events.>";
    public const string SourceB = "b.events.>";

    public static string Upserted(string source, string entityType) =>
        $"{source}.events.{entityType}.upserted";

    public static string Deleted(string source, string entityType) =>
        $"{source}.events.{entityType}.deleted";

    public static string ForOp(string source, string entityType, string op) => op switch
    {
        EventOps.Upsert => Upserted(source, entityType),
        EventOps.Delete => Deleted(source, entityType),
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown event operation."),
    };
}
