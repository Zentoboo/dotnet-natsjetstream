# Reference

API details and a map of the files that implement the product sync flow. For how it works and the example test scenarios, see [../README.md](../README.md). For setup and inspection commands, see [../setup.md](../setup.md).

## API reference

All successful responses use the wrapper:

```json
{
  "success": true,
  "message": "Product created.",
  "data": {
    "id": "0199...",
    "name": "Mechanical Keyboard",
    "price": 129.99,
    "version": 1,
    "isDeleted": false,
    "updatedAt": "2026-11-11T12:00:00Z"
  }
}
```

| Method | Path | Service | Notes |
| --- | --- | --- | --- |
| GET | `/api/v1/products` | A and B | List products/replicas |
| GET | `/api/v1/products/{id}` | A and B | Read one product/replica |
| POST | `/api/v1/products` | A only | Body: `{"name":"...","price":12.34}` |
| PUT | `/api/v1/products/{id}` | A only | Body: `{"name":"...","price":12.34}` |
| DELETE | `/api/v1/products/{id}` | A only | Soft delete; replica receives a delete event |

Backend B's replica responses also include `syncedAt`, the time B applied the event.

## Important files and responsibilities

### Composition and infrastructure

| Responsibility | File |
| --- | --- |
| Backend A startup, migrations, API versioning, health | `BackendA/NatsSyncLab.BackendA.AppHost/Program.cs` |
| Backend A EF migration runner | `BackendA/NatsSyncLab.BackendA.AppHost/DatabaseMigrator.cs` |
| Backend B startup, migrations, API versioning, health | `BackendB/NatsSyncLab.BackendB.AppHost/Program.cs` |
| Backend B EF migration runner | `BackendB/NatsSyncLab.BackendB.AppHost/DatabaseMigrator.cs` |
| NATS client registration, reconnect configuration source, stream initializer registration | `SharedKernel/NatsSyncLab.SharedKernel/Messaging/NatsServiceCollectionExtensions.cs` |
| Stream creation and stream retention settings | `SharedKernel/NatsSyncLab.SharedKernel/Messaging/JetStreamInitializer.cs` |
| NATS URL, stream names, retention, duplicate window | `SharedKernel/NatsSyncLab.SharedKernel/Messaging/NatsOptions.cs` |
| NATS container, ports, and persistent volume | `docker-compose.yml` |
| NATS server JetStream storage and monitoring | `infra/nats/nats-server.conf` |

### Backend A: write side

| Responsibility | File |
| --- | --- |
| Create product + outbox row | `BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.Infrastructure/Endpoints/CreateProductEndpoint.cs` |
| Update product + outbox row | `BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.Infrastructure/Endpoints/UpdateProductEndpoint.cs` |
| Soft delete + outbox row | `BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.Infrastructure/Endpoints/DeleteProductEndpoint.cs` |
| Product version/update/delete rules | `BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.Domain/Products/Product.cs` |
| Build outbox record from product event | `BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.Infrastructure/Messaging/ProductOutbox.cs` |
| Build envelope, subject, payload | `BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.IntegrationEvent/Products/ProductIntegrationEvents.cs` |
| Poll outbox and publish to NATS | `BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.Infrastructure/Messaging/OutboxRelayService.cs` |
| Outbox entity | `SharedKernel/NatsSyncLab.SharedKernel/Persistence/OutboxMessage.cs` |
| Outbox table mapping | `BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.Infrastructure/Persistence/Configuration/OutboxMessageConfiguration.cs` |
| Product table mapping | `BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.Infrastructure/Persistence/Configuration/ProductConfiguration.cs` |
| A read endpoints | `BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.Infrastructure/Endpoints/GetProductsEndpoint.cs` and `GetProductEndpoint.cs` |
| A database context | `BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.Infrastructure/Persistence/ProductsDbContext.cs` |

### Shared contracts

| Responsibility | File |
| --- | --- |
| Event envelope and JSON helpers | `SharedKernel/NatsSyncLab.SharedKernel/Contracts/EventEnvelope.cs` |
| Product event payload | `SharedKernel/NatsSyncLab.SharedKernel/IntegrationEvents/ProductSyncPayload.cs` |
| Subjects | `SharedKernel/NatsSyncLab.SharedKernel/Contracts/Subjects.cs` |
| Entity types | `SharedKernel/NatsSyncLab.SharedKernel/Contracts/EntityTypes.cs` |
| Operations | `SharedKernel/NatsSyncLab.SharedKernel/Contracts/EventOps.cs` |
| Source names | `SharedKernel/NatsSyncLab.SharedKernel/Contracts/Sources.cs` |
| Inbox entity | `SharedKernel/NatsSyncLab.SharedKernel/Persistence/InboxMessage.cs` |

### Backend B: read side

| Responsibility | File |
| --- | --- |
| Fetch, deduplicate, upsert, acknowledge | `BackendB/Module/ProductsReplica/NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure/Messaging/ProductsConsumerService.cs` |
| Replica entity | `BackendB/Module/ProductsReplica/NatsSyncLab.BackendB.Module.ProductsReplica.Domain/Products/ProductReplica.cs` |
| Replica database context | `BackendB/Module/ProductsReplica/NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure/Persistence/ProductsReplicaDbContext.cs` |
| Replica table mapping | `BackendB/Module/ProductsReplica/NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure/Persistence/Configuration/ProductReplicaConfiguration.cs` |
| Inbox table mapping | `BackendB/Module/ProductsReplica/NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure/Persistence/Configuration/InboxMessageConfiguration.cs` |
| B read endpoints | `BackendB/Module/ProductsReplica/NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure/Endpoints/GetProductsEndpoint.cs` and `GetProductEndpoint.cs` |
| B read DTO | `BackendB/Module/ProductsReplica/NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure/Endpoints/ProductReplicaResponse.cs` |

### Frontends

| Responsibility | File |
| --- | --- |
| A create/update/delete UI | `frontend/backend-a/src/App.tsx` |
| B replica UI and 3-second refresh | `frontend/backend-b/src/App.tsx` |
| A API proxy | `frontend/backend-a/vite.config.ts` |
| B API proxy | `frontend/backend-b/vite.config.ts` |

### Tests

| Responsibility | File |
| --- | --- |
| Product domain behavior | `tests/NatsSyncLab.BackendA.Module.Products.UnitTests/ProductTests.cs` |
| Event envelope validation | `tests/NatsSyncLab.SharedKernel.UnitTests/EventEnvelopeTests.cs` |
| Subject construction | `tests/NatsSyncLab.SharedKernel.UnitTests/SubjectsTests.cs` |
| Chaos/edge behavior | `tests/NatsSyncLab.SharedKernel.UnitTests/ChaosTests.cs` |
| Architecture rules | `tests/NatsSyncLab.ArchitectureTests` |
