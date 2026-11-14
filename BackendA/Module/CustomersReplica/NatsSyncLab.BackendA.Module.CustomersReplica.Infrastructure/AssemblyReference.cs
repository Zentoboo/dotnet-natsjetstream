using System.Reflection;

namespace NatsSyncLab.BackendA.Module.CustomersReplica.Infrastructure;

public static class AssemblyReference
{
    public static readonly Assembly Assembly = typeof(AssemblyReference).Assembly;
}
