using Asp.Versioning;
using Asp.Versioning.Builder;
using NatsSyncLab.BackendA.AppHost;
using NatsSyncLab.BackendA.Module.CustomersReplica.Infrastructure;
using NatsSyncLab.BackendA.Module.Products.Infrastructure;
using NatsSyncLab.BackendA.SharedKernel;
using NatsSyncLab.SharedKernel;
using NatsSyncLab.SharedKernel.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSharedKernel();
builder.Services.AddBackendASharedKernel(builder.Configuration);
builder.Services.AddNatsJetStream(builder.Configuration);
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
    options.ApiVersionReader = new UrlSegmentApiVersionReader();
});

builder.Services.AddProductsModuleInfrastructure(builder.Configuration, builder.Environment.IsProduction());
builder.Services.AddCustomersReplicaModuleInfrastructure(builder.Configuration, builder.Environment.IsProduction());

var app = builder.Build();

await DatabaseMigrator.MigrateAsync(app.Services);

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "backend-a" }));

var versionSet = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1)).ReportApiVersions().Build();
var group = app.MapGroup("api/v{version:apiVersion}").WithApiVersionSet(versionSet);
app.MapEndpoints(group);

app.Run();
