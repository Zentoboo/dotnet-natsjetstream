using NetArchTest.Rules;
using NatsSyncLab.SharedKernel;
using Xunit;

namespace NatsSyncLab.ArchitectureTests;

public sealed class LayerDependencyTests
{
    [Fact]
    public void Global_shared_kernel_does_not_depend_on_services()
    {
        var result = Types.InAssembly(typeof(DependencyInjection).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny("NatsSyncLab.BackendA", "NatsSyncLab.BackendB")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Service_shared_kernels_do_not_depend_on_module_internals()
    {
        var assemblies = new[]
        {
            typeof(NatsSyncLab.BackendA.SharedKernel.DependencyInjection).Assembly,
            typeof(NatsSyncLab.BackendB.SharedKernel.DependencyInjection).Assembly,
        };

        foreach (var assembly in assemblies)
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "NatsSyncLab.BackendA.Module.Products.Infrastructure",
                    "NatsSyncLab.BackendA.Module.CustomersReplica.Infrastructure",
                    "NatsSyncLab.BackendB.Module.Customers.Infrastructure",
                    "NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure")
                .GetResult();

            Assert.True(result.IsSuccessful, $"{assembly.GetName().Name}: {Describe(result)}");
        }
    }

    private static string Describe(TestResult result) =>
        result.FailingTypeNames is null
            ? "architecture rule failed"
            : string.Join(", ", result.FailingTypeNames);
}
