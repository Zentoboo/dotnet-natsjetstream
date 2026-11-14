# Replicating the MKA Modular Monolith

> A step-by-step guide to standing up a new solution with the same architecture.
> Read [ARCHITECTURE.md](./ARCHITECTURE.md) first for the *why*.

---

## 0. Decide the shape before writing code

Answer these three questions first:

1. **What are the services?** Each top-level folder becomes one deployable API. A service is justified when it can be deployed, scaled and versioned independently.
2. **What are the modules inside each service?** A module is justified when it **owns its own data** (schema + lifecycle) and has a clear business capability.
3. **Which style do you handle requests with?**
   - **Style A — Minimal API + handler** (fast, good for CRUD and small teams)
   - **Style B — MediatR vertical slices** (better for complex domains, enforced pipeline)
   Pick one per service and never mix.

> Rule of thumb: modules are cheap to add, services are expensive. Start with **one service, several modules**, then extract services if a real deployment boundary emerges.

---

## 1. Repository scaffolding

Create this at the root:

```
<Repo>/
├── .editorconfig
├── Directory.Build.props
├── Directory.Packages.props
├── <Suite>.slnx
├── <Service>Only.slnf              (one per service)
├── SharedKernel/
│   └── MKA.SharedKernel/
└── <Service>/
    ├── MKA.<Service>.AppHost/
    ├── SharedKernel/
    │   └── MKA.<Service>.SharedKernel/
    └── Module/
```

### `Directory.Build.props`

```xml
<Project>
  <PropertyGroup>
    <PlatformTarget>x64</PlatformTarget>
  </PropertyGroup>
</Project>
```

### `Directory.Packages.props`

Enable central package management and pin **every** version here. Individual `.csproj` files then use versionless `<PackageReference>`.

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.14" />
    <!-- ... one entry per package ... -->
  </ItemGroup>
</Project>
```

### Baseline package set

| Area           | Packages                                                                                                                                          |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------------------------- |
| Persistence    | `Microsoft.EntityFrameworkCore`, `.Relational`, `.Design`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Dapper`                                      |
| CQRS (Style B) | `MediatR`                                                                                                                                         |
| Validation     | `FluentValidation`, `FluentValidation.DependencyInjectionExtensions`                                                                              |
| API            | `Microsoft.AspNetCore.OpenApi`, `Scalar.AspNetCore`, `Asp.Versioning.Mvc`, `Asp.Versioning.Mvc.ApiExplorer`                                       |
| Auth           | `Microsoft.AspNetCore.Authentication.JwtBearer`                                                                                                   |
| Caching        | `ZiggyCreatures.FusionCache`, `.Backplane.StackExchangeRedis`, `.Serialization.SystemTextJson`, `Microsoft.Extensions.Caching.StackExchangeRedis` |
| Jobs           | `TickerQ`, `TickerQ.EntityFrameworkCore`, `TickerQ.Dashboard`                                                                                     |
| Logging        | `Serilog`, `Serilog.Sinks.MongoDB`, `Serilog.UI`, `Serilog.UI.MongoDbProvider`, `Serilog.Sinks.Map`, `Serilog.Exceptions.EntityFrameworkCore`     |
| Telemetry      | `OpenTelemetry.*`, `Npgsql.OpenTelemetry`, `Sentry.AspNetCore`, `Sentry.OpenTelemetry`                                                            |
| Domain helpers | `Ardalis.GuardClauses`, `Ardalis.SmartEnum`, `Ardalis.SmartEnum.EFCore`                                                                           |
| HTTP clients   | `Refit.HttpClientFactory`                                                                                                                         |

### Solution filters

For each service, add a `<Service>Only.slnf` listing only that service's projects plus the root SharedKernel — so the IDE loads one service at a time.

---

## 2. Create the two shared kernels

### 2.1 `MKA.SharedKernel` (global, product-agnostic)

Put **only** primitives that every service needs and that contain **no product knowledge**:

```
MKA.SharedKernel/
├── Abstractions/
│   ├── IEndpoint.cs
│   ├── IAuditableEntity.cs
│   ├── ISoftDelete.cs
│   ├── ICurrentUserService.cs
│   ├── IDbConnectionFactory.cs
│   └── I<External>Service.cs          (Refit contracts for cross-service calls)
├── Persistence/
│   ├── Entities/Base/BaseEntity.cs
│   ├── Entities/AuditTrail.cs
│   └── ConnectionFactory/NpgsqlConnectionFactory.cs
├── Infrastructure/
│   ├── Interceptors/AuditableEntityInterceptor.cs
│   ├── Interceptors/InterceptorContext.cs
│   └── ApiResponse/CustomApiResponse.cs
├── Exceptions/
│   ├── AppException.cs, NotFoundException.cs, ConflictException.cs, ...
│   └── Handlers/*ExceptionHandler.cs
├── CustomResult/Result.cs
├── Enums/                              (SmartEnums + converters)
├── Options/                            (typed configuration)
├── Request/                            (master-data sync DTOs)
└── DependencyInjection.cs
```

Key methods in `DependencyInjection.cs`:

- `AddSharedKernel()` — current user, feature management, exception handlers, problem details
- `AddEndpoints(Assembly)` — scans for `IEndpoint`
- `MapEndpoints(app, routeGroup)` — maps discovered endpoints
- `AddPostgresFactory(connectionString)` — Dapper connection factory
- `AddPostgres<T>(connectionString, isProduction, migrationTableName, schemaName)` — EF + shared `NpgsqlDataSource`

### 2.2 `MKA.<Service>.SharedKernel` (service platform)

Put **service-specific** concerns here:

```
MKA.<Service>.SharedKernel/
├── DependencyInjection.cs        (Add<Service>SharedKernel)
├── Auth/                         (claims transformation, permission requirement)
├── Constants/
│   ├── ModulePath.cs             (route prefixes)
│   └── CacheTag.cs
├── Behaviors/                    (Style B: ValidationBehavior, TransactionBehavior)
├── Messaging/                    (Style B: ICommand<>, IQuery<>)
├── Dto/                          (cross-module DTOs)
├── Enums/Permission.cs           (permission SmartEnum)
└── Utility/                      (Scalar transformer, Serilog module enricher)
```

Expose:

```csharp
public static IServiceCollection Add<Service>SharedKernel(
    this IServiceCollection services, IConfiguration config, List<ApiVersion> versions)
{
    services.AddApiVersioning(versions);
    services.AddAuthenticationAndAuthorization(config);
    services.AddTelemetry();
    services.AddSeriLog(config);
    return services;
}

public static IServiceCollection AddDatabaseContext<T>(
    this IServiceCollection services, IConfiguration config, bool isProduction,
    string migrationTableName, string schemaName) where T : DbContext
{
    var connectionString = config.GetConnectionString("DefaultConnection")!;
    services.AddPostgres<T>(connectionString, isProduction, migrationTableName, schemaName);
    services.AddPostgresFactory(connectionString);
    return services;
}
```

---

## 3. Create a module

For a module named `Order` in service `Sales`, create five projects:

```
Sales/Module/Order/
  MKA.Sales.Module.Order.Domain
  MKA.Sales.Module.Order.Application
  MKA.Sales.Module.Order.Infrastructure
  MKA.Sales.Module.Order.PublicApi
  MKA.Sales.Module.Order.IntegrationEvent
```

### Project references (copy exactly)

| Project             | `<ProjectReference>`                                                              |
| ------------------- | --------------------------------------------------------------------------------- |
| `.Domain`           | `MKA.SharedKernel` (+ `MKA.Sales.SharedKernel` if the domain needs service types) |
| `.Application`      | `.Domain`                                                                         |
| `.Infrastructure`   | `.Application`, `.Domain`, `MKA.SharedKernel`, `MKA.Sales.SharedKernel`           |
| `.PublicApi`        | `.Application`, `.Domain`, `.Infrastructure`                                      |
| `.IntegrationEvent` | `.Domain`                                                                         |

**Never** add a reference to another module's `Domain`, `Application` or `Infrastructure`.

### 3.1 Domain

```
Domain/
├── Entities/
│   └── Order/
│       ├── Order.cs
│       └── OrderId.cs
├── Constants/
└── DependencyInjection.cs        (can stay empty)
```

```csharp
public readonly record struct OrderId(Guid Value)
{
    internal static OrderId Create() => new(Guid.CreateVersion7());
    public static OrderId Of(Guid value) => new(value);
}

public sealed class Order : IAuditableEntity, ISoftDelete
{
    private Order() { }

    public OrderId Id { get; init; }
    public string Reference { get; private set; } = string.Empty;
    public OrderStatus Status { get; private set; } = OrderStatus.PENDING;
    public bool IsDeleted { get; set; }

    public string CreatedUsername { get; set; } = string.Empty;
    public DateTimeOffset CreatedOn { get; set; } = TimeProvider.System.GetUtcNow();
    public string? UpdatedUsername { get; set; }
    public DateTimeOffset? UpdatedOn { get; set; }

    public static Order Create(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return new Order { Id = OrderId.Create(), Reference = reference };
    }

    public void Approve() { /* guard + transition */ }
}
```

### 3.2 Application

**Style A:** almost empty — put the existing logic in `Infrastructure/Endpoints` and keep only shared abstractions here.

**Style B:**

```
Application/
├── Abstraction/IOrderDbContext.cs
├── Features/CreateOrder/
│   ├── CreateOrderCommand.cs
│   ├── CreateOrderHandler.cs
│   └── CreateOrderValidator.cs
├── Behaviors/
├── AssemblyReference.cs
└── DependencyInjection.cs        (AddOrderModuleApplication)
```

### 3.3 Infrastructure

```
Infrastructure/
├── Persistence/
│   ├── OrderDbContext.cs
│   └── Migrations/
├── Configuration/
│   └── OrderConfiguration.cs
├── Endpoints/
│   └── Order/CreateOrder.cs
├── Constants/DbConsts.cs
├── AssemblyReference.cs
└── DependencyInjection.cs        (AddOrderModuleInfrastructure)
```

`DbConsts`:

```csharp
public static class DbConsts
{
    public const string Schema = "order";
    public const string MigrationTableName = "migration_history_order";
}
```

`DbContext`:

```csharp
public sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders { get; set; } = default!;
    public DbSet<AuditTrail> AuditTrails { get; set; } = default!;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(DbConsts.Schema);
        builder.ApplyConfigurationsFromAssembly(typeof(OrderDbContext).Assembly);
    }
}
```

`DependencyInjection`:

```csharp
public static class DependencyInjection
{
    public static IServiceCollection AddOrderModuleInfrastructure(
        this IServiceCollection services, IConfiguration config, bool isProduction)
    {
        services.AddEndpointsApiExplorer();
        services.AddEndpoints(AssemblyReference.Assembly);
        services.AddSingleton(TimeProvider.System);

        services.AddDatabaseContext<OrderDbContext>(
            config, isProduction, DbConsts.MigrationTableName, DbConsts.Schema);

        services.AddHealthChecks().AddDbContextCheck<OrderDbContext>("order_db", tags: ["db", "order"]);
        services.AddValidatorsFromAssemblyContaining<AssemblyReference>(
            ServiceLifetime.Transient, includeInternalTypes: true);

        // Style B only:
        // services.AddOrderModuleApplication();
        // services.AddScoped<IOrderDbContext>(sp => sp.GetRequiredService<OrderDbContext>());

        return services;
    }
}
```

### 3.4 PublicApi

Only what other modules are allowed to call:

```csharp
// PublicApi/Orders/IOrderLookupService.cs
public interface IOrderLookupService
{
    Task<OrderSummaryDto?> GetAsync(Guid orderId, CancellationToken cancellationToken);
}
```

Implementation goes in `Infrastructure/Services/`, registration in the AppHost.

### 3.5 IntegrationEvent

Add event contracts only when you actually publish them. Otherwise leave the project empty (or omit it until needed — see "improvements" below).

---

## 4. Build the AppHost

```
<Service>/MKA.<Service>.AppHost/
├── Program.cs
├── DependencyInjection.cs        (Add<Service>Host)
├── DataSeed/
├── Migrations/                   (host-owned context, e.g. TickerQ)
├── appsettings.json
├── appsettings.Development.json
└── Utility/
```

### `Program.cs` template

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables();

var versions = new List<ApiVersion> { new(1) };

// Suite level
builder.Services.AddSharedKernel();

// Service level
builder.Services.AddSalesSharedKernel(builder.Configuration, versions);
builder.Services.AddSalesHost(builder.Configuration);

// Module level
builder.Services.AddOrderModuleInfrastructure(builder.Configuration, builder.Environment.IsProduction());
builder.Services.AddOrderModulePublicApi();
builder.Services.AddCustomerModuleInfrastructure(builder.Configuration, builder.Environment.IsProduction());
builder.Services.AddCustomerModulePublicApi();

// Cross-service clients
builder.Services.AddRefitGeneratedClient<IHrmService>()
    .ConfigureHttpClient((sp, client) =>
        client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<ApiEndpointsOption>>().Value.HRMS));

builder.Services.AddRouting(o => o.LowercaseUrls = true);
builder.Services.AddCors(...);

var app = builder.Build();

// Startup migrations (one per context)
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    await using var db = await sp.GetRequiredService<IDbContextFactory<OrderDbContext>>().CreateDbContextAsync();
    await db.Database.MigrateAsync();
    // ... every module context ...
}

app.UseExceptionHandler();
app.MapOpenApi();
app.UseAuthentication();
app.UseAuthorization();

var apiVersionSet = app.NewApiVersionSet().HasApiVersions(versions).ReportApiVersions().Build();
var group = app.MapGroup("api/v{version:apiVersion}").WithApiVersionSet(apiVersionSet);
app.MapEndpoints(group);

app.ConfigureAddScalar();
app.MapHealthChecks("/health");

await app.RunAsync();
```

### `Add<Service>Host`

Host-only concerns:

- Response compression / caching
- TickerQ scheduler + dashboard + Postgres operational store
- FusionCache (L1 + L2 Redis + backplane)
- Typed options binding + validation
- Sentry

---

## 5. Add a feature

### Style A — Minimal API

Create `Infrastructure/Endpoints/<Area>/<UseCase>.cs`:

```csharp
public sealed record CreateOrderRequest(string Reference, decimal Amount);

internal sealed class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    public CreateOrderRequestValidator()
    {
        RuleFor(x => x.Reference).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Amount).GreaterThan(0);
    }
}

public static class CreateOrder
{
    public sealed class EndPoint : IEndpoint
    {
        public void Configure(IEndpointRouteBuilder app)
        {
            app.MapPost(ModulePath.OrderModule, Handler)
                .WithTags("Order")
                .AddEndpointFilter<ValidationFilter<CreateOrderRequest>>()
                .RequireAuthorization(p => p.RequirePermission(Permission.OrderCreate.ToString()))
                .ProducesProblem(StatusCodes.Status400BadRequest);
        }

        private static async Task<Ok<CustomApiResponse<Guid>>> Handler(
            OrderDbContext context,
            CreateOrderRequest request,
            ICurrentUserService currentUser,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            var order = Order.Create(request.Reference);
            order.CreatedUsername = currentUser.Email ?? "-";
            order.CreatedOn = timeProvider.GetUtcNow();

            context.Orders.Add(order);
            await context.SaveChangesAsync(cancellationToken);

            return TypedResults.Ok(new CustomApiResponse<Guid>(order.Id.Value, "Order created."));
        }
    }
}
```

Add the route constant to `MKA.<Service>.SharedKernel/Constants/ModulePath.cs`:

```csharp
public const string OrderModule = "/sales/order";
```

### Style B — MediatR slice

```
Application/Features/CreateOrder/
├── CreateOrderCommand.cs
├── CreateOrderHandler.cs
└── CreateOrderValidator.cs
```

```csharp
public record CreateOrderCommand(string Reference, decimal Amount)
    : ICommand<CustomApiResponse<Guid>>;

public sealed class CreateOrderHandler(IOrderDbContext context)
    : IRequestHandler<CreateOrderCommand, CustomApiResponse<Guid>>
{
    public async Task<CustomApiResponse<Guid>> Handle(
        CreateOrderCommand request, CancellationToken cancellationToken)
    {
        var order = Order.Create(request.Reference);
        context.Orders.Add(order);
        await context.SaveChangesAsync(cancellationToken);
        return new CustomApiResponse<Guid>(order.Id.Value, "Order created.");
    }
}
```

Endpoint sends the command and maps the response.

---

## 6. Cross-module and cross-service communication

### In-process (same service)

1. Define the interface + DTOs in the owner module's `PublicApi`.
2. Implement it in the owner module's `Infrastructure`.
3. Register it in the AppHost (or the owner's module DI).
4. The consumer references **only** `.PublicApi`.

```csharp
// PublicApi
public interface ICustomerLookupService
{
    Task<CustomerSummary?> GetAsync(Guid customerId, CancellationToken ct);
}

// AppHost / owner DI
services.AddScoped<ICustomerLookupService, CustomerLookupService>();
```

If the implementation is registered as a singleton but needs scoped dependencies, create a scope inside (`IServiceScopeFactory`), as `MakerCheckerService` does.

### Cross-service

1. Put the Refit interface in `MKA.SharedKernel/Abstractions`.
2. Register it with `AddRefitGeneratedClient<T>()` and a base URL from `ApiEndpointsOption`.
3. Never reference another service's projects.

### Master data

If a service needs data owned by another service:

1. Add a `Sync<Entity>Request` DTO to `MKA.SharedKernel/Request`.
2. Expose a `Sync<Entity>` endpoint that upserts a **local copy** in the consuming module's schema.
3. Have the owning service (or a sync job) push updates.
4. Read only from the local copy at query time.

---

## 7. Definition of done for a new module

- [ ] Five projects exist with the correct reference graph.
- [ ] `DbConsts.Schema` and `DbConsts.MigrationTableName` are unique.
- [ ] `DbContext` calls `HasDefaultSchema` and `ApplyConfigurationsFromAssembly`.
- [ ] First migration generated and applied at startup.
- [ ] `Add<Module>ModuleInfrastructure` exists and is called from the AppHost.
- [ ] At least one `IEndpoint` discovered automatically.
- [ ] Route constant added to `ModulePath`.
- [ ] Permission added to the permission SmartEnum and enforced.
- [ ] Entities are `sealed`, with private setters and a static factory.
- [ ] Configurations are `internal sealed`.
- [ ] No reference to another module's non-`PublicApi` project.
- [ ] Health check registered.
- [ ] Logs appear under the right module collection.
- [ ] Cache keys/tags namespaced to the module.

---

## 8. Guardrails to enforce from day one

1. **One-way dependencies.** `Domain ← Application ← Infrastructure ← PublicApi`. Configure an architecture test (NetArchTest / ArchUnitNET) to fail the build on violations.
2. **Modules talk only through `PublicApi`.** No `ProjectReference` to another module's internals.
3. **One schema per module, no cross-schema FKs.** Store foreign identifiers as values.
4. **One request style per service.** Pick Minimal API or MediatR and stick to it.
5. **No EF Core in `Domain`.** Domain must compile without the persistence stack.
6. **Services talk HTTP, not project references.** Refit contracts live in the global SharedKernel.
7. **Central package management.** No versions in project files.
8. **Migrations are owned by the module that owns the table.**
9. **Every startup migration is idempotent and ordered.**
10. **Invalidate cache after `SaveChangesAsync`, never before.**

---

## 9. Improvements over the current implementation

If you're starting fresh, adopt these instead of copying the rough edges:

| Current state                                                     | Recommended                                                                                                                             |
| ----------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| Empty `Domain`/`Application`/`PublicApi`/`IntegrationEvent` stubs | Create only the projects a module actually needs; add layers when they earn their place.                                                |
| `PublicApi` references `Infrastructure`                           | `PublicApi` should reference `Application` only; keep implementations in `Infrastructure` and register them in the AppHost.             |
| Mixed `PublicApi` / `PublicAPI` casing                            | Standardise on `PublicApi`.                                                                                                             |
| Two handler styles in one suite                                   | One style per service (architecture-tested).                                                                                            |
| `Project.*` → `User.Domain` / `User.Infrastructure`               | Replace with `User.PublicApi` contracts.                                                                                                |
| No tests                                                          | Add `Domain.UnitTests`, `Infrastructure.IntegrationTests` (Testcontainers Postgres), `AppHost.FunctionalTests` (WebApplicationFactory). |
| Startup `MigrateAsync()` only                                     | Add a deployment-time migration job, or gate migrations behind a flag for zero-downtime.                                                |
| `IntegrationEvent` projects with no publisher                     | Introduce a real outbox + broker only when a use case demands it; otherwise delete the stubs.                                           |
| Host `DependencyInjection` doing everything                       | Split into `Add<Service>Host`, `Add<Service>Cache`, `Add<Service>Jobs` for readability.                                                 |

---

## 10. Minimal end-to-end checklist

```
[ ] Directory.Build.props + Directory.Packages.props + .editorconfig
[ ] MKA.SharedKernel (primitives + AddSharedKernel/AddEndpoints/MapEndpoints/AddPostgres)
[ ] MKA.<Service>.SharedKernel (auth, versioning, logging, telemetry, ModulePath, permissions)
[ ] Module: Domain / Application / Infrastructure / PublicApi
[ ] DbConsts + DbContext + configurations + first migration
[ ] Add<Module>ModuleInfrastructure
[ ] AppHost: Program.cs composition + startup migrations + middleware
[ ] First endpoint implementing IEndpoint
[ ] Health check + Scalar + /health verified
[ ] Cross-service Refit contract (if needed)
[ ] Master-data sync endpoint + DTO (if needed)
[ ] Architecture test enforcing reference rules
[ ] CI: build + tests + migration check
```

---

## Reference: full tree for a new service

```
<Repo>/
├── .editorconfig
├── Directory.Build.props
├── Directory.Packages.props
├── <Suite>.slnx
├── <Service>Only.slnf
├── SharedKernel/
│   └── MKA.SharedKernel/
│       ├── Abstractions/
│       ├── CustomResult/
│       ├── Enums/
│       ├── Exceptions/Handlers/
│       ├── Infrastructure/{ApiResponse,Interceptors}/
│       ├── Options/
│       ├── Persistence/{ConnectionFactory,Entities}/
│       ├── Request/
│       └── DependencyInjection.cs
└── <Service>/
    ├── MKA.<Service>.AppHost/
    │   ├── Program.cs
    │   ├── DependencyInjection.cs
    │   ├── DataSeed/
    │   ├── Migrations/            (host-owned context, e.g. TickerQ)
    │   ├── appsettings.json
    │   └── Utility/
    ├── SharedKernel/
    │   └── MKA.<Service>.SharedKernel/
    │       ├── Auth/
    │       ├── Behaviors/
    │       ├── Constants/{ModulePath,CacheTag}.cs
    │       ├── Dto/
    │       ├── Enums/Permission.cs
    │       ├── Messaging/
    │       ├── Utility/
    │       └── DependencyInjection.cs
    └── Module/
        └── Order/
            ├── MKA.<Service>.Module.Order.Domain/
            │   ├── Entities/Order/{Order,OrderId}.cs
            │   ├── Constants/
            │   └── DependencyInjection.cs
            ├── MKA.<Service>.Module.Order.Application/
            │   ├── Abstraction/IOrderDbContext.cs
            │   ├── Features/
            │   ├── Behaviors/
            │   ├── AssemblyReference.cs
            │   └── DependencyInjection.cs
            ├── MKA.<Service>.Module.Order.Infrastructure/
            │   ├── Configuration/OrderConfiguration.cs
            │   ├── Constants/DbConsts.cs
            │   ├── Endpoints/Order/
            │   ├── Persistence/{OrderDbContext.cs,Migrations/}
            │   ├── Services/
            │   ├── AssemblyReference.cs
            │   └── DependencyInjection.cs
            ├── MKA.<Service>.Module.Order.PublicApi/
            │   └── Orders/IOrderLookupService.cs
            └── MKA.<Service>.Module.Order.IntegrationEvent/
                └── OrderCreated.cs
```

---

## Related documents

- [ARCHITECTURE.md](./ARCHITECTURE.md) — how the existing system works.
- [../SharedKernel/GUIDE.md](../SharedKernel/GUIDE.md) — original in-repo backend guide.
