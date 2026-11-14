using System.Reflection;

namespace NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure;

public static class AssemblyReference
{
    public static readonly Assembly Assembly = typeof(AssemblyReference).Assembly;
}
