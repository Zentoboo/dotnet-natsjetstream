using Microsoft.AspNetCore.Routing;

namespace NatsSyncLab.SharedKernel.Abstractions;

public interface IEndpoint
{
    void Configure(IEndpointRouteBuilder app);
}
