using NatsSyncLab.SharedKernel.Contracts;
using Xunit;

namespace NatsSyncLab.SharedKernel.UnitTests;

public sealed class SubjectsTests
{
    [Theory]
    [InlineData(Sources.A, EntityTypes.Product, EventOps.Upsert, "a.events.product.upserted")]
    [InlineData(Sources.A, EntityTypes.Product, EventOps.Delete, "a.events.product.deleted")]
    [InlineData(Sources.B, EntityTypes.Customer, EventOps.Upsert, "b.events.customer.upserted")]
    [InlineData(Sources.B, EntityTypes.Customer, EventOps.Delete, "b.events.customer.deleted")]
    public void ForOp_builds_expected_subject(
        string source,
        string entityType,
        string op,
        string expected)
    {
        Assert.Equal(expected, Subjects.ForOp(source, entityType, op));
    }

    [Fact]
    public void ForOp_rejects_unknown_operation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Subjects.ForOp(Sources.A, EntityTypes.Product, "exploded"));
    }
}
