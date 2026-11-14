using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Constants;
using NatsSyncLab.BackendB.SharedKernel.Constants;
using NatsSyncLab.SharedKernel.Abstractions;
using NatsSyncLab.SharedKernel.Infrastructure.ApiResponse;
using NatsSyncLab.SharedKernel.Persistence;

namespace NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Endpoints;

internal sealed class GetProductsEndpoint : IEndpoint
{
    private const string Sql =
        """
        SELECT id AS "Id",
               name AS "Name",
               price AS "Price",
               version AS "Version",
               is_deleted AS "IsDeleted",
               updated_at AS "UpdatedAt",
               synced_at AS "SyncedAt"
        FROM products_replica.products_replica
        ORDER BY name
        """;

    public void Configure(IEndpointRouteBuilder group)
    {
        group.MapGet(ModulePath.ProductsReplica, HandleAsync)
            .WithName("GetReplicatedProducts")
            .WithTags("ProductsReplica");
    }

    private static async Task<IResult> HandleAsync(
        IDbConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var products = await connection.QueryAsync<ProductReplicaResponse>(
            new CommandDefinition(Sql, cancellationToken: cancellationToken));

        return Results.Ok(CustomApiResponse<IReadOnlyList<ProductReplicaResponse>>.Ok(
            products.AsList()));
    }
}
