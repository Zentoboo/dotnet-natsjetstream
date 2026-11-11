# MKA Suite — Architecture Overview

> How the Modular Monolith is structured, and the rules it follows.
> For a step-by-step "build a new one" guide, see [REPLICATION.md](./REPLICATION.md).
>
> Last reviewed: 2026-11-10

---

## Table of contents

1. [Topology: a suite of modular monoliths](#1-topology-a-suite-of-modular-monoliths)
2. [Physical layout & naming](#2-physical-layout--naming)
3. [Layers and dependency rules](#3-layers-and-dependency-rules)
4. [Composition root](#4-composition-root)
5. [Persistence: one context + one schema per module](#5-persistence-one-context--one-schema-per-module)
6. [Domain conventions](#6-domain-conventions)
7. [Request handling](#7-request-handling)
8. [Cross-module and cross-service communication](#8-cross-module-and-cross-service-communication)
9. [Cross-cutting platform](#9-cross-cutting-platform)
10. [Async, jobs and events](#10-async-jobs-and-events)
11. [Known irregularities / technical debt](#11-known-irregularities--technical-debt)

---

## 1. Topology: a suite of modular monoliths

The repository is a **suite of independently deployable services**. Each top-level product folder is one ASP.NET Core process; inside each process, functionality is split into **modules** that run in-process.

```
MKA.Suite (repository)
│
├── SharedKernel/
│   └── MKA.SharedKernel                  ← global, product-agnostic primitives
│
├── Staff/                                ← DEPLOYABLE SERVICE
│   ├── MKA.Staff.AppHost                 ← the only executable (WebApi)
│   ├── SharedKernel/
│   │   └── MKA.Staff.SharedKernel        ← service-specific platform
│   └── Module/
│       ├── Staff/
│       ├── User/
│       ├── Trip/
│       └── Meeting/
│
├── ProjectManagement/                    ← DEPLOYABLE SERVICE
│   ├── MKA.ProjectManagement.AppHost
│   ├── SharedKernel/MKA.ProjectManagement.SharedKernel
│   └── Module/{Project,User,Attachment,Forum}/
│
├── HRMS/        ← DEPLOYABLE SERVICE
├── Finance/     ← DEPLOYABLE SERVICE
├── Contract/    ← DEPLOYABLE SERVICE
├── Hermes/      ← DEPLOYABLE SERVICE
├── SupplyChain/ ← DEPLOYABLE SERVICE
└── Ticketing/   ← DEPLOYABLE SERVICE
```

### Key characteristics

| Characteristic | Detail |
| --- | --- |
| **Deployment unit** | One `*.AppHost` per product. |
| **In-process modules** | Modules in the same service call each other through interfaces — no network hop. |
| **Own database objects** | Each module owns a Postgres schema and its own migration history. |
| **One physical database** | All modules in a service share one Postgres DB and one `NpgsqlDataSource`. |
| **No cross-schema FKs** | Other modules' identifiers are stored as plain values (usually GUID). |
| **Shared identity provider** | Keycloak (JWT bearer) across all services. |

> **`User` is not a shared library.** Almost every service has its own `User` module — a local identity slice (users, roles, permissions, departments, countries…) plus a local copy of master data. Copies are kept current via HTTP **sync endpoints** and shared request contracts. The suite is a set of bounded contexts that share a login provider, not a set of projects sharing code.

### Solution organisation

- `MKA.Suite.slnx` — the whole suite.
- `<Service>Only.slnf` — solution filters (`ProjectManagementOnly.slnf`, `SupplyChainOnly.slnf`) so an IDE only loads one service.
- `Directory.Build.props` — sets `PlatformTarget=x64`.
- `Directory.Packages.props` — **central package management** (no versions in individual `.csproj` files).
- `.editorconfig` — formatting/analyzer rules.

---

## 2. Physical layout & naming

Every module is **five projects** with a fixed naming pattern:

```
<Service>/Module/<Module>/
  MKA.<Service>.Module.<Module>.Domain
  MKA.<Service>.Module.<Module>.Application
  MKA.<Service>.Module.<Module>.Infrastructure
  MKA.<Service>.Module.<Module>.PublicApi
  MKA.<Service>.Module.<Module>.IntegrationEvent
```

Plus, once per service:

```
<Service>/SharedKernel/MKA.<Service>.SharedKernel
<Service>/MKA.<Service>.AppHost
```

And once per suite:

```
SharedKernel/MKA.SharedKernel
```

### Contents per project

| Project | Typical contents |
| --- | --- |
| `Domain` | Entities, strongly-typed IDs, domain constants, SmartEnums, domain logic, domain events |
| `Application` | Use cases / features, handlers, validators, `I<Module>DbContext` abstraction, pipeline behaviors |
| `Infrastructure` | `DbContext`, `IEntityTypeConfiguration` classes, migrations, minimal-API endpoints, background jobs, external service implementations |
| `PublicApi` | The **only** surface other modules may reference: interfaces + DTOs |
| `IntegrationEvent` | Event contracts intended for cross-module / cross-service publication |
| `AppHost` | `Program.cs`, middleware pipeline, startup migrations, data seeding, scheduler, cache, auth |
| `*.SharedKernel` | Service-level platform: auth, versioning, logging, telemetry, route constants, shared DTOs |

> **Naming note:** casing is inconsistent in the wild (`PublicApi` vs `PublicAPI`, one `DepedencyInjection` typo). Standardise on `PublicApi` for new work.

---

## 3. Layers and dependency rules

The dependency direction is strictly one-way:

```
                 ┌──────────────────────┐
                 │   MKA.SharedKernel   │  (no dependencies)
                 └──────────┬───────────┘
        ┌───────────────────┴────────────────────┐
        │                                        │
┌───────▼──────────────┐              ┌──────────▼─────────────┐
│ MKA.<Svc>.SharedKernel│              │  Module.Domain          │
└───────┬──────────────┘              └──────────┬─────────────┘
        │                                        │
        │                             ┌──────────▼─────────────┐
        │                             │  Module.Application    │
        │                             └──────────┬─────────────┘
        │                                        │
        │                             ┌──────────▼─────────────┐
        └────────────────────────────►│  Module.Infrastructure │
                                      └──────────┬─────────────┘
                                                 │
                                      ┌──────────▼─────────────┐
                                      │  Module.PublicApi      │
                                      └────────────────────────┘

Module.IntegrationEvent ──► Module.Domain (and sometimes Application)
MKA.<Svc>.AppHost       ──► every layer of every module in that service
```

Verified project-reference rules:

| Project | References |
| --- | --- |
| `Domain` | `MKA.SharedKernel` (+ `MKA.<Service>.SharedKernel` in most services) |
| `Application` | `Domain` (+ service SharedKernel in some services) |
| `Infrastructure` | `Application`, `Domain`, `MKA.SharedKernel`, `MKA.<Service>.SharedKernel` |
| `PublicApi` | `Application`, `Domain`, `Infrastructure` (+ SharedKernels) |
| `IntegrationEvent` | `Domain` (sometimes `Application`) |
| `AppHost` | All layer projects of all modules it hosts + both SharedKernels |

### Module isolation

**Modules do not reference each other's internal layers.** A module may reference another module's `.PublicApi` only.

Examples:

- `MKA.Staff.Module.Meeting.Infrastructure` → `MKA.Staff.Module.User.PublicApi`
- `MKA.Staff.Module.Staff.Infrastructure` → `MKA.Staff.Module.User.PublicApi`

The interface lives in the owner's `PublicApi`; the implementation lives in the owner's `Infrastructure`; the AppHost wires them together.

---

## 4. Composition root

Every layer exposes a static `DependencyInjection` class with an `Add...` extension method. The AppHost reads as a **manifest** of what the service is composed of.

`Staff/MKA.Staff.AppHost/Program.cs`:

```csharp
var versions = new List<ApiVersion> { new(1) };

builder.Services.AddSharedKernel();                                     // suite primitives
builder.Services.AddStaffSharedKernel(builder.Configuration, versions); // auth, logging, telemetry, API versioning
builder.Services.AddStaffHost(builder.Configuration);                   // TickerQ, FusionCache, options, compression

builder.Services.AddStaffModuleInfrastructure(builder.Configuration, builder.Environment.IsProduction());
builder.Services.AddTripModuleInfrastructure(builder.Configuration, builder.Environment.IsProduction());
builder.Services.AddMeetingModuleInfrastructure(builder.Configuration, builder.Environment.IsProduction());
builder.Services.AddUserModuleInfrastructure(builder.Configuration, builder.Environment.IsProduction());

// Cross-service HTTP clients
builder.Services.AddRefitGeneratedClient<IHRMSService>()
    .ConfigureHttpClient((sp, client) =>
        client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<ApiEndpointsOption>>().Value.HRMS));
```

### Endpoint discovery

Endpoints are **not registered by hand**. `MKA.SharedKernel.DependencyInjection` scans assemblies for `IEndpoint` implementations:

```csharp
public static IServiceCollection AddEndpoints(this IServiceCollection services, Assembly assembly)
{
    ServiceDescriptor[] descriptors = [.. assembly.DefinedTypes
        .Where(t => t is { IsAbstract: false, IsInterface: false } && t.IsAssignableTo(typeof(IEndpoint)))
        .Select(t => ServiceDescriptor.Transient(typeof(IEndpoint), t))];
    services.TryAddEnumerable(descriptors);
    return services;
}
```

And maps them all under the versioned route group:

```csharp
RouteGroupBuilder versionedGroup = app.MapGroup("api/v{version:apiVersion}").WithApiVersionSet(apiVersionSet);
app.MapEndpoints(versionedGroup);
```

```csharp
public interface IEndpoint
{
    void Configure(IEndpointRouteBuilder app);
}
```

### Service-level DI

`MKA.<Service>.SharedKernel/DependencyInjection.cs` provides the platform:

- `AddApiVersioning(...)` — query string / header / URL segment / media type
- `AddAuthenticationAndAuthorization(...)` — Keycloak JWT, claims transformation, audit interceptor
- `AddTelemetry()` — OpenTelemetry + Sentry + OTLP
- `AddSeriLog(...)` — Serilog → MongoDB + Serilog UI
- `AddDatabaseContext<T>(...)` — wraps `AddPostgres<T>` + `AddPostgresFactory`

---

## 5. Persistence: one context + one schema per module

This is the heart of the "modular" part.

Each module owns:

| Concern | Where it lives |
| --- | --- |
| `DbContext` | `Infrastructure/Persistence/<Module>DbContext.cs` |
| Postgres schema | `Infrastructure/Constants/DbConsts.Schema` (e.g. `"trip"`) |
| Migrations history table | `Infrastructure/Constants/DbConsts.MigrationTableName` (e.g. `"migration_history_trip"`) |
| Entity configurations | `Infrastructure/Configuration/*Configuration.cs` (`internal sealed`) |
| Migrations | `Infrastructure/Persistence/Migrations/` or `Infrastructure/Migrations/` |

Example `DbConsts`:

```csharp
public static class DbConsts
{
    public const string Schema = "trip";
    public const string MigrationTableName = "migration_history_trip";
}
```

Example `DbContext`:

```csharp
public sealed class TripDbContext(DbContextOptions<TripDbContext> options) : DbContext(options)
{
    public DbSet<TripDetails> TripDetails { get; set; } = default!;
    public DbSet<Participant> Participants { get; set; } = default!;
    public DbSet<AuditTrail> AuditTrails { get; set; } = default!;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(DbConsts.Schema);
        builder.HasSequence<long>("trip_reference_seq", DbConsts.Schema);
        builder.ApplyConfigurationsFromAssembly(typeof(TripDbContext).Assembly);
    }
}
```

### One physical database, many logical schemas

`MKA.SharedKernel.DependencyInjection.AddPostgres<T>` registers:

1. a **single** `NpgsqlDataSource` (singleton) — one connection pool for the whole service
2. a scoped `IDbContextFactory<T>`
3. a scoped `T` created from that factory

```csharp
services.TryAddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).EnableDynamicJson().Build());

void Configure(IServiceProvider sp, DbContextOptionsBuilder options)
{
    options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql =>
    {
        npgsql.MigrationsAssembly(typeof(T).Assembly.FullName)
              .MigrationsHistoryTable(migrationTableName, schemaName)
              .EnableRetryOnFailure(5, TimeSpan.FromSeconds(30), null);
    })
    .AddInterceptors(sp.GetRequiredService<AuditableEntityInterceptor>());
}

services.AddDbContextFactory<T>(Configure, ServiceLifetime.Scoped);
services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<T>>().CreateDbContext());
```

### Startup migration

The AppHost migrates **every** context on boot, then seeds (`Program.cs`):

```csharp
var tripContext = await services.GetRequiredService<IDbContextFactory<TripDbContext>>().CreateDbContextAsync();
await tripContext.Database.MigrateAsync(cancellationToken);
...
await StaffUserSeedService.SeedAllAsync(services, userContext, meetingContext);
await StaffDataSeedService.SeedAllAsync(staffContext);
```

Consequences to keep in mind:

- Migrations are applied at process start — deploys are not zero-downtime by default.
- Each module's migration history is isolated, so modules can evolve independently.
- Cross-module integrity is **application-enforced**, not database-enforced.

---

## 6. Domain conventions

### Entities

`SharedKernel/GUIDE.md` + actual code:

```csharp
public sealed class TripDetails : IAuditableEntity
{
    private TripDetails() { }                        // EF only
    public TripDetailsId Id { get; init; }           // identity
    public string TripName { get; private set; }     // state: private set
    public TripStatus Status { get; private set; }

    public static TripDetails Create(...)            // factory + guard clauses
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(...);
        return new TripDetails { Id = ..., Status = TripStatus.PENDING };
    }

    public void Approve() { EnsurePending(); Status = TripStatus.APPROVED; }
}
```

Rules:

- `public sealed class`
- `private` parameterless constructor for EF
- `private set` for mutable state, `init` for identity
- Static factory `Create(...)` with guard clauses
- Behaviour on the entity (`Approve`, `StartApprovalFlow`), not in services
- `IAuditableEntity` for audit fields
- `ISoftDelete` for soft deletion

### Strongly-typed IDs

```csharp
public readonly record struct TripDetailsId(Guid Value)
{
    internal static TripDetailsId Create() => new(Guid.CreateVersion7());
    public static TripDetailsId Of(Guid value) => new(value);
}
```

Using `readonly record struct` keeps IDs on the stack and immutable. IDs are generated with `Guid.CreateVersion7()` (time-ordered, index-friendly).

### EF configuration

`internal sealed`, table in `snake_case`, conversions for IDs and SmartEnums:

```csharp
internal sealed class TripDetailsConfiguration : IEntityTypeConfiguration<TripDetails>
{
    private const string TableName = "trip_details";

    public void Configure(EntityTypeBuilder<TripDetails> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("CK_trip_details_travel_dates", "\"TravelEndDate\" >= \"TravelStartDate\"");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id)
            .HasConversion(id => id.Value, value => TripDetailsId.Of(value))
            .ValueGeneratedNever();

        builder.Property(x => x.Status)
            .HasConversion<TripStatusConverter>()
            .HasMaxLength(20);

        builder.HasQueryFilter(x => !x.IsDeleted);   // when ISoftDelete
    }
}
```

### Auditing

`AuditableEntityInterceptor` runs on `SaveChangesAsync`:

- fills `CreatedOn` / `CreatedUsername` on add, `UpdatedOn` / `UpdatedUsername` on modify
- writes an `AuditTrail` row per changed entity with old/new values
- uses `ICurrentUserService.Email` as the actor

### Enumerations

Prefer **Ardalis SmartEnum** + a converter (`TripStatus`, `Currency`, `VehicleType`, …) over C# enums. They carry behaviour and serialize predictably.

### API envelope

All successful responses use:

```csharp
public record CustomApiResponse<T>
{
    public string Message { get; set; }
    public T? Data { get; set; }
    public long SentDate { get; set; }
    public Pagination? Pagination { get; set; }
}
```

---

## 7. Request handling

Two styles coexist. Pick **one** per new service.

### Style A — Minimal API + handler (Staff, HRMS, most services)

Endpoint, request, validator and handler live in one vertical file:

```
Infrastructure/Endpoints/Travel/CreateTravel.cs
  ├── CreateTravelRequest                     (public sealed record)
  ├── CreateTravelRequestValidator            (AbstractValidator<...>)
  └── CreateTravel.EndPoint : IEndpoint
```

```csharp
public sealed class EndPoint : IEndpoint
{
    public void Configure(IEndpointRouteBuilder app)
    {
        app.MapPost(ModulePath.TravelModule, Handler)
            .WithTags("Travel")
            .AddEndpointFilter<ValidationFilter<CreateTravelRequest>>()
            .RequireAuthorization(p => p.RequirePermission(PermissionSmartEnum.TravelCreate.ToString()))
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static async Task<Results<Ok<CustomApiResponse<TravelIdResponse>>, /* ... */>> Handler(
        TripDbContext context,
        [FromBody] CreateTravelRequest request,
        ICurrentUserService currentUser,
        IHRMSService hrms,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) { ... }
}
```

Handler injects the module `DbContext` and cross-service Refit clients directly. Concurrency is handled with `SerializableTransactionRetry` and explicit `BeginTransactionAsync(IsolationLevel.Serializable)`.

### Style B — MediatR vertical slices (ProjectManagement)

```
Application/Features/Customization/DeleteMetadataFieldTemplate/
  ├── DeleteMetadataFieldTemplateCommand   : ICommand<CustomApiResponse<string>>
  ├── DeleteMetadataFieldTemplateHandler   : IRequestHandler<...>
  └── DeleteMetadataFieldTemplateValidator
```

```csharp
public record DeleteMetadataFieldTemplateCommand(Guid TemplateId)
    : ICommand<CustomApiResponse<string>>;

public sealed class DeleteMetadataFieldTemplateHandler(IProjectDbContext context)
    : IRequestHandler<DeleteMetadataFieldTemplateCommand, CustomApiResponse<string>>
{
    public async Task<CustomApiResponse<string>> Handle(
        DeleteMetadataFieldTemplateCommand request, CancellationToken cancellationToken)
    {
        var template = await context.MetadataTemplates
            .FirstOrDefaultAsync(x => x.Id == MetadataTemplateId.Of(request.TemplateId), cancellationToken);
        if (template is null) return new CustomApiResponse<string>(ResponseMessage.NotFound("Template"));

        template.SoftDelete();
        await context.SaveChangesAsync(cancellationToken);
        return new CustomApiResponse<string>("Template deleted successfully");
    }
}
```

Handlers depend on an **abstraction** defined in `Application` and implemented by `Infrastructure`:

```csharp
// Application/Abstraction/IProjectDbContext.cs
public interface IProjectDbContext
{
    DbSet<Project> Projects { get; }
    DatabaseFacade Database { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

// Infrastructure DI
services.AddScoped<IProjectDbContext>(sp => sp.GetRequiredService<ProjectDbContext>());
```

Pipeline behaviors (registered in `Add<Module>ModuleApplication`):

```csharp
services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(AssemblyReference.Assembly);
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
    cfg.AddOpenBehavior(typeof(ProjectTransactionBehavior<,>));
});
services.AddValidatorsFromAssembly(AssemblyReference.Assembly, includeInternalTypes: true);
```

`ValidationBehavior` either returns a `ValidationFailedResponse<T>` or throws `CustomeValidationException`, keeping the `CustomApiResponse<T>` contract.

### HTTP conventions

| Concern | Convention |
| --- | --- |
| Route prefix | `api/v{version:apiVersion}` set in AppHost |
| Module path | constants in `MKA.<Service>.SharedKernel/Constants/ModulePath.cs` |
| Auth | `RequirePermission("<Permission>")` |
| Validation | Style A: `ValidationFilter<T>`; Style B: `ValidationBehavior` |
| Errors | `IExceptionHandler` chain → `ProblemDetails` |
| Success shape | `CustomApiResponse<T>` |
| Docs | Scalar (`/scalar/v1`) + OpenAPI |
| Health | `/health` |

---

## 8. Cross-module and cross-service communication

### 8.1 In-process (same service): `PublicApi`

Contract in the owning module's `PublicApi`:

```csharp
// Staff/Module/User/MKA.Staff.Module.User.PublicApi/MakerChecker/IMakerCheckerService.cs
public interface IMakerCheckerService
{
    Task<bool> HasPendingAsync(string entityId, string entityType, CancellationToken ct);
    Task<Guid> StageAsync(MakerCheckerStageRequest request, CancellationToken ct);
    Task<MakerCheckerApplicationDto?> GetAsync(Guid id, CancellationToken ct);
}
```

Implementation in the owning module's `Infrastructure`, registered by the AppHost:

```csharp
// Staff/Module/User/.../Infrastructure/Services/MakerCheckerService.cs
public sealed class MakerCheckerService(IServiceScopeFactory factory) : IMakerCheckerService
{
    // creates its own scope because callers may be singletons/hosted services
}
```

Consumer references only `...User.PublicApi`:

```xml
<!-- Meeting.Infrastructure.csproj -->
<ProjectReference Include="..\..\User\MKA.Staff.Module.User.PublicApi\MKA.Staff.Module.User.PublicApi.csproj" />
```

### 8.2 Cross-service: Refit contracts

Interfaces live in the global `MKA.SharedKernel/Abstractions`:

- `IHRMSService`
- `IFinanceService`
- `IHermesMsGraphService`
- `IKeycloakService`
- `IMasterDataService`, `IAttributesService`, `IPermissionService`, …

AppHost binds each to a base URL from configuration:

```csharp
builder.Services.AddRefitGeneratedClient<IFinanceService>()
    .ConfigureHttpClient((sp, client) =>
        client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<ApiEndpointsOption>>().Value.Finance));
```

### 8.3 Master-data sync

Because each service has its own copy of users/master data, upstream data is pushed through **sync endpoints**:

```
SharedKernel/MKA.SharedKernel/Request/
  SyncCompanyRequest.cs
  SyncDepartmentRequest.cs
  SyncCountryRequest.cs      (via SyncLocation...)
  SyncDefaultScheduleRequest.cs
  SyncMeetingTypeRequest.cs
  ...
```

Each service exposes endpoints such as `Staff/Module/User/.../Endpoints/Company/SyncCompany.cs` that upsert the local copy. This is the mechanism behind "every service has a User module".

### 8.4 Anti-pattern seen in the codebase

`Project.Domain` → `User.Domain` and `Project.Infrastructure` → `User.Infrastructure` / `Attachment.Infrastructure`. This is convenience-driven coupling that bypasses `PublicApi`. Do not replicate it.

---

## 9. Cross-cutting platform

Registered **once per service** in `MKA.<Service>.SharedKernel` and/or the AppHost.

| Concern | Implementation |
| --- | --- |
| **Authentication** | Keycloak JWT bearer; `PermissionClaimsTransformation`; `RequirePermission()` endpoint extension |
| **API versioning** | `Asp.Versioning` — query/header/URL/media-type readers |
| **OpenAPI docs** | `Microsoft.AspNetCore.OpenApi` + Scalar UI, bearer scheme transformer |
| **Error handling** | `IExceptionHandler`: `ValidationExceptionHandler`, `NotFoundExceptionHandler`, `ForbiddenExceptionHandler`, `JsonDeserializationExceptionHandler`, `GlobalExceptionHandler` |
| **Persistence** | Shared `NpgsqlDataSource`, `IDbConnectionFactory` (Dapper), `AuditableEntityInterceptor` |
| **Caching** | FusionCache: L1 memory + L2 Redis + Redis backplane, fail-safe, eager refresh, jitter |
| **Scheduling** | TickerQ: Postgres operational store (`tq_scheduler` schema), dashboard `/admin/tickerq`, module jobs under `Infrastructure/Jobs/` |
| **Logging** | Serilog → MongoDB (collection per module via `ModuleEnricher`), Serilog UI at `/{service}-logs` |
| **Telemetry** | OpenTelemetry (EF Core, ASP.NET, Npgsql, HttpClient, Redis) → OTLP; Sentry |
| **Host** | Response compression (Brotli), response caching, CORS, static files, `/health` |

### Cache rules

- FusionCache only; never raw `IDistributedCache` in handlers.
- Use **keys** for values, **tags** for grouping/invalidation.
- Invalidate **after** `SaveChangesAsync()` commits.
- `CacheKeyPrefix` is per service (`CachePrefix.Staff`, `CachePrefix.ProjectManagement`).

### Configuration

Strongly-typed options bound and validated at startup (`AddOptions<T>().BindConfiguration(...).ValidateDataAnnotations().ValidateOnStart()`):

`KeycloakSettingOption`, `KeycloakSecretOption`, `SMTPSettingOption`, `SMTPSecretOption`, `FrontendUrlOption`, `ApiEndpointsOption`, `PortalOption`, `BNMUrlOption`, `SerilogUIOption`, `TelegramBotOption`.

---

## 10. Async, jobs and events

- **Jobs** live in `Infrastructure/Jobs/<JobName>/` and are driven by TickerQ cron scheduling. A job has a `...Request` and a `...Job`; exceptions go through `JobExceptionHandler`.
- **Domain events** — `BaseEntity` can raise `IDomainEvent`; `DomainEventsDispatcher`/`IDomainEventHandler` exist in `MKA.SharedKernel`.
- **Integration events** — `*.IntegrationEvent` projects exist, and `MassTransit`/`MassTransit.RabbitMQ` are referenced centrally, but the only RabbitMQ consumer is commented out. Treat eventing as **scaffolding**, not an active part of the architecture.

---

## 11. Known irregularities / technical debt

Things to be aware of when reading or copying this codebase:

1. **`PublicApi` casing** varies (`PublicApi` vs `PublicAPI`). Standardise.
2. **Empty placeholder projects** — `Trip.PublicApi`, `Trip.IntegrationEvent`, `Trip.Application`, `Trip.Domain` each contain an empty `DependencyInjection` class. The 5-project shape is enforced even when a layer has no code.
3. **Layer leakage** — `Project.*` references `User.Domain`/`User.Infrastructure` and `Attachment.Infrastructure` directly.
4. **Two handler styles** — Staff uses minimal-API handlers; ProjectManagement uses MediatR. Both are valid, but mixing them in one service hurts consistency.
5. **No test projects** — there are no unit/integration/functional test projects anywhere in the solution.
6. **Startup migrations** — `Database.MigrateAsync()` at boot; no rolling/zero-downtime strategy.
7. **Dead folders** — `B2B/` and `DealerSurvey/` exist outside the solution; `Configuration/Unused/` and `Endpoints/Unused/` contain dead code.
8. **Inconsistent SharedKernel responsibilities** — e.g. DB registration helper `AddDatabaseContext<T>` lives in the service SharedKernel, while `AddPostgres<T>` lives in the global one.

---

## Related documents

- [REPLICATION.md](./REPLICATION.md) — step-by-step guide to stand up a new modular monolith with the same architecture.
- [../SharedKernel/GUIDE.md](../SharedKernel/GUIDE.md) — original in-repo backend guide (entity/DTO/cache/Dapper/OTel notes).
