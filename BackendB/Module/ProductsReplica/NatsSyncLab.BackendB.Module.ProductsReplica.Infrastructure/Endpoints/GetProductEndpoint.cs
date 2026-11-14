using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NatsSyncLab.BackendB.SharedKernel.Constants;
using NatsSyncLab.SharedKernel.Abstractions;
using NatsSyncLab.SharedKernel.Infrastructure.ApiResponse;
using NatsSyncLab.SharedKernel.Persistence;

namespace NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Endpoints;

internal sealed class GetProductEndpoint : IEndpoint
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
        WHERE id = @Id
        """;

    public void Configure(IEndpointRouteBuilder group)
    {
        group.MapGet($"{ModulePath.ProductsReplica}/{{id:guid}}", HandleAsync)
            .WithName("GetReplicatedProduct")
            .WithTags("ProductsReplica");
    }

    private static async Task<IResult> HandleAsync(
        Guid id,
        IDbConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var product = await connection.QuerySingleOrDefaultAsync<ProductReplicaResponse>(
            new CommandDefinition(Sql, new { Id = id }, cancellationToken: cancellationToken));

        return product is null
            ? Results.NotFound(CustomApiResponse<ProductReplicaResponse>.Fail("Product not found."))
            : Results.Ok(CustomApiResponse<ProductReplicaResponse>.Ok(product));
    }
}
