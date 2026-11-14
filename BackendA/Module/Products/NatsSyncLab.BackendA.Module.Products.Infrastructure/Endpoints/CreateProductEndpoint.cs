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

internal sealed class CreateProductEndpoint : IEndpoint
{
    public void Configure(IEndpointRouteBuilder group)
    {
        group.MapPost(ModulePath.Products, HandleAsync)
            .WithName("CreateProduct")
            .WithTags("Products");
    }

    private static async Task<IResult> HandleAsync(
        CreateProductRequest request,
        ProductsDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = timeProvider.GetUtcNow();
            var product = Product.Create(request.Name, request.Price, now);

            context.Products.Add(product);
            context.Outbox.Add(ProductOutbox.Upsert(product, now));
            await context.SaveChangesAsync(cancellationToken);

            return Results.Ok(CustomApiResponse<ProductResponse>.Ok(
                ProductResponse.From(product),
                "Product created."));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(CustomApiResponse<ProductResponse>.Fail(exception.Message));
        }
    }

    internal sealed record CreateProductRequest(string Name, decimal Price);
}
