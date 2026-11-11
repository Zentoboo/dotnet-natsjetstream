using NatsSyncLab.BuildingBlocks.Chaos;
using Xunit;

namespace NatsSyncLab.Tests;

public sealed class ChaosTests
{
    [Fact]
    public void KillPoint_is_a_no_op_when_not_armed()
    {
        Chaos.KillPoint("unit-test");
    }

    [Fact]
    public void KillPoint_throws_when_armed()
    {
        var previous = Environment.GetEnvironmentVariable(Chaos.KillPointEnvironmentVariable);
        Environment.SetEnvironmentVariable(Chaos.KillPointEnvironmentVariable, "unit-test");

        try
        {
            Assert.Throws<ChaosKillPointException>(() => Chaos.KillPoint("unit-test"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Chaos.KillPointEnvironmentVariable, previous);
        }
    }
}
