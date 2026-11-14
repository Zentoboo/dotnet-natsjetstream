using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NatsSyncLab.BackendA.Module.Products.Infrastructure.Constants;
using NatsSyncLab.BackendA.SharedKernel.Constants;
using NatsSyncLab.SharedKernel.Abstractions;
using NatsSyncLab.SharedKernel.Infrastructure.ApiResponse;
using NatsSyncLab.SharedKernel.Persistence;

namespace NatsSyncLab.BackendA.Module.Products.Infrastructure.Endpoints;

internal sealed class GetProductsEndpoint : IEndpoint
{
    private const string Sql =
        """
        SELECT id AS "Id",
               name AS "Name",
               price AS "Price",
               version AS "Version",
               is_deleted AS "IsDeleted",
               updated_at AS "UpdatedAt"
        FROM products.products
        ORDER BY name
        """;

    public void Configure(IEndpointRouteBuilder group)
    {
        group.MapGet(ModulePath.Products, HandleAsync)
            .WithName("GetProducts")
            .WithTags("Products");
    }

    private static async Task<IResult> HandleAsync(
        IDbConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var products = await connection.QueryAsync<ProductReadResponse>(
            new CommandDefinition(Sql, cancellationToken: cancellationToken));

        return Results.Ok(CustomApiResponse<IReadOnlyList<ProductReadResponse>>.Ok(
            products.AsList()));
    }
}
