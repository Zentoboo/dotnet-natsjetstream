namespace NatsSyncLab.BuildingBlocks.Contracts;

public static class EventOps
{
    public const string Upsert = "upsert";
    public const string Delete = "delete";

    public static bool IsKnown(string op) => op is Upsert or Delete;
}
