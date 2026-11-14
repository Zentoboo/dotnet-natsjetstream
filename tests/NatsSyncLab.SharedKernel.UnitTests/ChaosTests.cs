using ChaosKillPoint = NatsSyncLab.SharedKernel.Chaos.Chaos;
using Xunit;

namespace NatsSyncLab.SharedKernel.UnitTests;

public sealed class ChaosTests
{
    [Fact]
    public void KillPoint_is_a_no_op_when_not_armed()
    {
        ChaosKillPoint.KillPoint("unit-test");
    }

    [Fact]
    public void KillPoint_throws_when_armed()
    {
        var previous = Environment.GetEnvironmentVariable(ChaosKillPoint.KillPointEnvironmentVariable);
        Environment.SetEnvironmentVariable(ChaosKillPoint.KillPointEnvironmentVariable, "unit-test");

        try
        {
            Assert.Throws<NatsSyncLab.SharedKernel.Chaos.ChaosKillPointException>(
                () => ChaosKillPoint.KillPoint("unit-test"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ChaosKillPoint.KillPointEnvironmentVariable, previous);
        }
    }
}
