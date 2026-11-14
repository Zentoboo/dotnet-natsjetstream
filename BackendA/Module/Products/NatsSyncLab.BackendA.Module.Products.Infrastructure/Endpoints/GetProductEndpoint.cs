using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NatsSyncLab.BackendA.SharedKernel.Constants;
using NatsSyncLab.SharedKernel.Abstractions;
using NatsSyncLab.SharedKernel.Infrastructure.ApiResponse;
using NatsSyncLab.SharedKernel.Persistence;

namespace NatsSyncLab.BackendA.Module.Products.Infrastructure.Endpoints;

internal sealed class GetProductEndpoint : IEndpoint
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
        WHERE id = @Id
        """;

    public void Configure(IEndpointRouteBuilder group)
    {
        group.MapGet($"{ModulePath.Products}/{{id:guid}}", HandleAsync)
            .WithName("GetProduct")
            .WithTags("Products");
    }

    private static async Task<IResult> HandleAsync(
        Guid id,
        IDbConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var product = await connection.QuerySingleOrDefaultAsync<ProductReadResponse>(
            new CommandDefinition(Sql, new { Id = id }, cancellationToken: cancellationToken));

        return product is null
            ? Results.NotFound(CustomApiResponse<ProductReadResponse>.Fail("Product not found."))
            : Results.Ok(CustomApiResponse<ProductReadResponse>.Ok(product));
    }
}
