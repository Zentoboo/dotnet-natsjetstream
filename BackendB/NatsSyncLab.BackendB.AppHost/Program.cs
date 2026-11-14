using Asp.Versioning;
using Asp.Versioning.Builder;
using NatsSyncLab.BackendB.AppHost;
using NatsSyncLab.BackendB.Module.Customers.Infrastructure;
using NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure;
using NatsSyncLab.BackendB.SharedKernel;
using NatsSyncLab.SharedKernel;
using NatsSyncLab.SharedKernel.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSharedKernel();
builder.Services.AddBackendBSharedKernel(builder.Configuration);
builder.Services.AddNatsJetStream(builder.Configuration);
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
    options.ApiVersionReader = new UrlSegmentApiVersionReader();
});

builder.Services.AddCustomersModuleInfrastructure(builder.Configuration, builder.Environment.IsProduction());
builder.Services.AddProductsReplicaModuleInfrastructure(builder.Configuration, builder.Environment.IsProduction());

var app = builder.Build();

await DatabaseMigrator.MigrateAsync(app.Services);

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "backend-b" }));

var versionSet = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1)).ReportApiVersions().Build();
var group = app.MapGroup("api/v{version:apiVersion}").WithApiVersionSet(versionSet);
app.MapEndpoints(group);

app.Run();
