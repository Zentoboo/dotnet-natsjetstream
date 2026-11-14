using System.Reflection;

namespace NatsSyncLab.BackendA.Module.Products.Infrastructure;

public static class AssemblyReference
{
    public static readonly Assembly Assembly = typeof(AssemblyReference).Assembly;
}
