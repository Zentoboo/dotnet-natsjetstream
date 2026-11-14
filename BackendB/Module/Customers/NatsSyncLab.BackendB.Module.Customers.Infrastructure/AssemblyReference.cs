using System.Reflection;

namespace NatsSyncLab.BackendB.Module.Customers.Infrastructure;

public static class AssemblyReference
{
    public static readonly Assembly Assembly = typeof(AssemblyReference).Assembly;
}
