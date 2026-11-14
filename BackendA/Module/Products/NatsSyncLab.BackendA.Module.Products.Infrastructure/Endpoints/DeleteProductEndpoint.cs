using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NatsSyncLab.BackendA.Module.Products.Domain.Products;
using NatsSyncLab.BackendA.Module.Products.Infrastructure.Messaging;
using NatsSyncLab.BackendA.Module.Products.Infrastructure.Persistence;
using NatsSyncLab.BackendA.SharedKernel.Constants;
using NatsSyncLab.SharedKernel.Abstractions;
using NatsSyncLab.SharedKernel.Infrastructure.ApiResponse;

namespace NatsSyncLab.BackendA.Module.Products.Infrastructure.Endpoints;

internal sealed class DeleteProductEndpoint : IEndpoint
{
    public void Configure(IEndpointRouteBuilder group)
    {
        group.MapDelete($"{ModulePath.Products}/{{id:guid}}", HandleAsync)
            .WithName("DeleteProduct")
            .WithTags("Products");
    }

    private static async Task<IResult> HandleAsync(
        Guid id,
        ProductsDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var product = await context.Products
            .SingleOrDefaultAsync(item => item.Id == ProductId.Of(id), cancellationToken);

        if (product is null)
        {
            return Results.NotFound(CustomApiResponse<ProductResponse>.Fail("Product not found."));
        }

        var now = timeProvider.GetUtcNow();
        if (product.Delete(now))
        {
            context.Outbox.Add(ProductOutbox.Delete(product, now));
            await context.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(CustomApiResponse<ProductResponse>.Ok(
            ProductResponse.From(product),
            "Product deleted."));
    }
}
