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

internal sealed class UpdateProductEndpoint : IEndpoint
{
    public void Configure(IEndpointRouteBuilder group)
    {
        group.MapPut($"{ModulePath.Products}/{{id:guid}}", HandleAsync)
            .WithName("UpdateProduct")
            .WithTags("Products");
    }

    private static async Task<IResult> HandleAsync(
        Guid id,
        UpdateProductRequest request,
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

        try
        {
            var now = timeProvider.GetUtcNow();
            product.Update(request.Name, request.Price, now);

            context.Outbox.Add(ProductOutbox.Upsert(product, now));
            await context.SaveChangesAsync(cancellationToken);

            return Results.Ok(CustomApiResponse<ProductResponse>.Ok(
                ProductResponse.From(product),
                "Product updated."));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(CustomApiResponse<ProductResponse>.Fail(exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(CustomApiResponse<ProductResponse>.Fail(exception.Message));
        }
    }

    internal sealed record UpdateProductRequest(string Name, decimal Price);
}
