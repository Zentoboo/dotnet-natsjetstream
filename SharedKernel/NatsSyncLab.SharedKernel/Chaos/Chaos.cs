namespace NatsSyncLab.SharedKernel.Chaos;

public static class Chaos
{
    public const string KillPointEnvironmentVariable = "CHAOS_KILLPOINT";

    public static void KillPoint(string name, bool failFast = false)
    {
        var armed = Environment.GetEnvironmentVariable(KillPointEnvironmentVariable);
        if (!string.Equals(armed, name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (failFast)
        {
            Environment.FailFast($"Chaos kill point '{name}' triggered.");
        }

        throw new ChaosKillPointException(name);
    }
}

public sealed class ChaosKillPointException(string killPoint)
    : Exception($"Chaos kill point '{killPoint}' triggered.");
