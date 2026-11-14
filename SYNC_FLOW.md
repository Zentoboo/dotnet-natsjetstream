# How Backend A -> NATS JetStream -> Backend B Works

This document explains the runtime flow behind the product replication lab: which process does what, which file owns each responsibility, and why recovery is fast when NATS or Backend B is temporarily unavailable.

For startup commands and the basic test runbook, see [README.md](./README.md).

## 1. The mental model

Backend A is the write-side owner. Backend B is a read-side replica. They are connected by three separate durability boundaries:

| Boundary | What it protects | Where it lives |
| --- | --- | --- |
| Transactional outbox | A change committed in A but not yet published to NATS | PostgreSQL A |
| JetStream stream + durable consumer | A published event while Backend B is offline | NATS disk |
| Inbox + version guard | Duplicate, redelivered, or out-of-order events | PostgreSQL B |

The important consequence is that there is no single "sync button" and no direct HTTP call from A to B. The pipeline is:

```text
HTTP write
  -> A product row + outbox row committed in PostgreSQL
  -> A background relay reads unpublished outbox rows
  -> publishes an EventEnvelope to NATS JetStream
  -> NATS stores the message in A_EVENTS
  -> B's durable consumer fetches the message
  -> B writes an inbox row + upserts the replica
  -> B commits both, then acknowledges the NATS message
  -> B's HTTP API returns the updated replica
  -> B's React UI refreshes and displays it
```

## 2. Contracts and routing

The event envelope is defined in `SharedKernel/NatsSyncLab.SharedKernel/Contracts/EventEnvelope.cs`:

```json
{
  "eventId": "0199...",
  "entityType": "product",
  "entityId": "0199...",
  "version": 2,
  "op": "upsert",
  "occurredAt": "2026-11-11T12:00:00Z",
  "payload": {
    "id": "0199...",
    "name": "Mechanical Keyboard",
    "price": 129.99,
    "version": 2,
    "isDeleted": false,
    "updatedAt": "2026-11-11T12:00:00Z"
  }
}
```

The payload type is `ProductSyncPayload` in `SharedKernel/NatsSyncLab.SharedKernel/IntegrationEvents/ProductSyncPayload.cs`.

Subject constants are in `SharedKernel/NatsSyncLab.SharedKernel/Contracts/Subjects.cs`:

| Constant | Value |
| --- | --- |
| `SourceA` | `a.events.>` |
| `SourceB` | `b.events.>` |
| Product upsert | `a.events.product.upserted` |
| Product delete | `a.events.product.deleted` |

The streams are configured in `SharedKernel/NatsSyncLab.SharedKernel/Messaging/JetStreamInitializer.cs`:

| Stream | Subject | Used by current product flow |
| --- | --- | --- |
| `A_EVENTS` | `a.events.>` | Yes |
| `B_EVENTS` | `b.events.>` | No, not used by the current product flow |

The B consumer filters on `a.events.product.>` (`BackendB/Module/ProductsReplica/NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure/Messaging/ProductsConsumerService.cs:24`).

## 3. Startup order

The relevant startup sequence is:

1. PostgreSQL must be running.
2. Each AppHost runs its EF Core migrations before serving requests.
3. `JetStreamInitializer` ensures both streams exist.
4. A starts `OutboxRelayService`.
5. B starts `ProductsConsumerService`.
6. Each backend starts listening on its HTTP port.

A migrations are invoked from `BackendA/NatsSyncLab.BackendA.AppHost/Program.cs:28` through `DatabaseMigrator.MigrateAsync`.

B migrations are invoked from `BackendB/NatsSyncLab.BackendB.AppHost/Program.cs:28`.

NATS registration and the initializer are in `SharedKernel/NatsSyncLab.SharedKernel/Messaging/NatsServiceCollectionExtensions.cs:23-34`.

Important operational rule: **start NATS before the backends**. `JetStreamInitializer` is an `IHostedService` that calls `CreateOrUpdateStreamAsync` during startup. If NATS is unavailable at that moment, backend startup can fail or stall. Once a backend has started, its NATS client can reconnect after a later NATS outage.

## 4. Backend A: the write path

### 4.1 HTTP endpoints

Product writes are handled by:

- `BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.Infrastructure/Endpoints/CreateProductEndpoint.cs`
- `BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.Infrastructure/Endpoints/UpdateProductEndpoint.cs`
- `BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.Infrastructure/Endpoints/DeleteProductEndpoint.cs`

`POST /api/v1/products` creates a product, adds an outbox row, and saves both through the same `ProductsDbContext`:

```csharp
context.Products.Add(product);
context.Outbox.Add(ProductOutbox.Upsert(product, now));
await context.SaveChangesAsync(cancellationToken);
```

That is the transactional-outbox step. Both database changes belong to the same EF Core unit of work, so the API does not return success with a product row but no outbox row.

The HTTP response returns before B has processed anything. The API only guarantees that A committed its own data and outbox entry.

### 4.2 Versioning and soft delete

`BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.Domain/Products/Product.cs` owns the version rules:

- New product: version `1`.
- Update: increments version.
- Delete: soft delete, increments version, sets `IsDeleted`.
- A deleted product cannot be updated again.

Because delete is just another versioned event (`isDeleted = true`), B applies it through the same upsert path as a create or update.

### 4.3 Building the outbox event

`ProductOutbox` (`.../Messaging/ProductOutbox.cs`) converts the product change into a `ProductIntegrationEvent` and stores the serialized event envelope in the outbox.

`ProductIntegrationEvents` (`BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.IntegrationEvent/Products/ProductIntegrationEvents.cs`) builds:

- `EventId`: a new time-ordered GUID.
- `EntityType`: `product`.
- `EntityId`: the product ID.
- `Version`: the product version.
- `Op`: `upsert` or `delete`.
- `OccurredAt`: the current UTC time.
- `Payload`: the `ProductSyncPayload`.
- `Subject`: `a.events.product.upserted` or `a.events.product.deleted`.

The outbox record stores the event ID, subject, JSON payload, creation time, and a nullable published time. Its EF mapping is in `.../Persistence/Configuration/OutboxMessageConfiguration.cs`.

### 4.4 The outbox relay

`BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.Infrastructure/Messaging/OutboxRelayService.cs` is the process that moves data from PostgreSQL to NATS.

Key behavior:

- Poll interval: **500 ms** (`:17`).
- Failure delay: **2 seconds** (`:18`).
- Batch size: **50** (`:16`).
- Reads unpublished rows with `FOR UPDATE SKIP LOCKED` (`:58-60`).
- Publishes each payload with `MsgId = EventId` (`:73-77`).
- Marks the row published only after the publish attempt returns (`:85`).
- Saves the published timestamps and commits (`:88-89`).

The relay is the bridge between the two durability boundaries. If NATS is unavailable, the outbox row remains unpublished. If the process crashes after publishing but before committing the published timestamp, the same event can be published again; NATS message ID de-duplication and the B inbox are the safety nets.

## 5. The NATS layer

NATS is configured in `infra/nats/nats-server.conf`:

- Client port: `4222`.
- Monitoring port: `8222`.
- JetStream storage directory: `/data`.
- File-store limit: `1Gb`.

The Compose service maps the `nats-data` volume into `/data` (`docker-compose.yml`). That volume is why messages and consumer state survive a NATS container restart.

Stream configuration comes from `JetStreamInitializer`:

- Storage: `File`.
- Retention: `Limits`.
- Max age: **7 days** (`NatsOptions.MaxAge`).
- Duplicate window: **10 minutes** (`NatsOptions.DuplicateWindow`).

The B consumer is configured in `ProductsConsumerService`:

- Consumer name: `b-products-replica`.
- Ack policy: explicit.
- Ack wait: **30 seconds**.
- Max deliveries: **5**.
- Delivery policy: `All` when the consumer is first created.
- Filter subject: `a.events.product.>`.
- Fetch size: **10 messages**.
- Fetch expiry: **5 seconds**.

This is a durable pull consumer. When B is offline, NATS keeps the messages and the consumer position. When B returns, the consumer continues from its stored position.

## 6. Backend B: the read path

`BackendB/Module/ProductsReplica/NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure/Messaging/ProductsConsumerService.cs` is the replication worker.

### 6.1 Fetch loop

The service continuously:

1. Creates or updates the durable consumer.
2. Calls `FetchAsync` for up to 10 messages.
3. Handles each message.
4. Acknowledges after the database commit.
5. Repeats.

If the fetch or NATS connection fails, the outer loop waits **2 seconds** and retries (`:25`, `:37-43`).

### 6.2 Envelope validation

B deserializes the event envelope and validates it. Invalid envelopes and unsupported entity types are terminated rather than retried forever. The product event must have matching entity ID and version between envelope and payload.

### 6.3 Database transaction

For each valid message, B opens a transaction and performs two writes:

1. Insert the event ID into `products_replica.inbox`.
2. Upsert `products_replica.products_replica`.

The inbox insert uses:

```sql
INSERT INTO products_replica.inbox (...)
VALUES (...)
ON CONFLICT (event_id) DO NOTHING
```

If the insert affects zero rows, the event was already processed. B commits and acknowledges the message without applying the product again.

The replica upsert uses:

```sql
ON CONFLICT (id) DO UPDATE
SET ...
WHERE products_replica.products_replica.version < EXCLUDED.version
```

That version guard prevents an older event from overwriting a newer replica row.

The inbox record and replica change commit in the same transaction. Only after the commit does B acknowledge the NATS message (`:89`). This is the "ack after the side effect" rule.

The B tables are mapped in:

- `BackendB/.../Persistence/Configuration/ProductReplicaConfiguration.cs`
- `BackendB/.../Persistence/Configuration/InboxMessageConfiguration.cs`

### 6.4 Read API and UI

B's read endpoints use Dapper:

- `BackendB/.../Endpoints/GetProductsEndpoint.cs`
- `BackendB/.../Endpoints/GetProductEndpoint.cs`

The B UI (`frontend/backend-b/src/App.tsx`) polls `/api/v1/products` every **3 seconds** while auto-refresh is enabled (`:91-101`). That UI polling is separate from the consumer's own fetch loop.

## 7. Idempotency, ordering, and delivery guarantees

This system is at-least-once, not exactly-once delivery.

| Risk | Defense |
| --- | --- |
| NATS publish retry after an uncertain ack | `MsgId` set to the event ID + NATS duplicate window |
| B receives the same event twice | `inbox` primary key on `event_id` |
| Older event arrives after newer event | `WHERE replica.version < incoming.version` |
| B crashes after DB commit but before ack | Event is redelivered; inbox makes it a no-op |
| B crashes before DB commit | Nothing committed; redelivery is safe |
| Product deleted | Versioned `isDeleted = true` upsert |

The result is exactly-once **side effects** under at-least-once delivery.

## 8. Why recovery looks almost instant

The visible recovery time is a combination of several small intervals, not one long polling cycle.

| Mechanism | Interval/behavior | File |
| --- | --- | --- |
| A outbox poll | 500 ms | `OutboxRelayService.cs:17` |
| A outbox failure retry | 2 seconds | `OutboxRelayService.cs:18` |
| B consumer failure retry | 2 seconds | `ProductsConsumerService.cs:25` |
| B fetch expiry | 5 seconds | `ProductsConsumerService.cs:70` |
| B UI auto-refresh | 3 seconds | `frontend/backend-b/src/App.tsx:91-101` |
| NATS reconnect wait | 2 seconds minimum, 5 seconds maximum backoff, unlimited retries | NATS.Net defaults |
| NATS ack wait | 30 seconds | `ProductsConsumerService.cs:53` |
| Max deliveries | 5 | `ProductsConsumerService.cs:54` |
| Stream message retention | 7 days | `NatsOptions.cs` |

Two details matter for the "instant" effect:

1. **Pending messages do not wait for a poll.** B's consumer starts with an outstanding fetch. If messages are already pending when B starts, JetStream can return them immediately.
2. **NATS reconnects in the background.** When NATS returns, the NATS client reconnects automatically. The outbox relay either completes a publish that was waiting for reconnection or retries within its 2-second failure delay. B's consumer loop also reconnects within its 2-second retry delay.

NATS.Net's default behavior is also relevant: a publish on a disconnected client waits for reconnection rather than throwing immediately. That is why A can remain healthy and simply complete the pending publish after NATS returns, while the outbox row remains the durable fallback if A is stopped.

The fast path is therefore:

```text
NATS or B becomes available
  -> client reconnects or consumer loop retries
  -> pending message is fetched/published
  -> B commits inbox + replica
  -> B acknowledges
  -> B UI's next 3-second poll displays the change
```

If B had already fetched a message but had not acknowledged it before stopping, redelivery is bounded by `AckWait = 30 seconds`. That is the slower path.

## 9. Your three test scenarios

### Scenario 1: B is down, NATS stays up

Sequence:

1. A commits product + outbox row.
2. The relay publishes to `A_EVENTS`.
3. NATS stores the message.
4. The durable consumer has no active B process, so the message stays pending.
5. B restarts.
6. `ProductsConsumerService` starts fetching immediately.
7. B inserts inbox + upserts replica, commits, acknowledges.
8. The B UI refreshes and displays the product.

Evidence:

- A: `products.outbox.published_at` is non-null.
- NATS: `A_EVENTS` has a new message.
- NATS consumer: pending count rises while B is down, then returns to zero.
- B: replica row exists and `inbox` contains the event ID.

This scenario proves NATS retains messages for an offline consumer. No outbox replay is involved because the event was already published.

### Scenario 2: NATS is down, A and B stay up

Sequence:

1. A commits product + outbox row.
2. The relay tries to publish.
3. NATS is unavailable, so publication does not complete.
4. The outbox row remains with `published_at = NULL`.
5. B has nothing to consume and stays stale.
6. NATS restarts from its persistent volume.
7. A's NATS client reconnects or the relay retries.
8. The relay publishes; the outbox row is marked published.
9. B consumes and applies the event.

Evidence:

- A: product row exists.
- A: newest outbox row has `published_at = NULL` while NATS is down.
- B: no corresponding replica row yet.
- After NATS returns: `published_at` becomes non-null and B catches up.

This scenario proves the outbox protects the window between the A database commit and the NATS publish.

There is a timing branch: if NATS went down **after** the publish was already acknowledged, the message is in the stream instead, and the outbox row is already marked published. Either way, the event is durable, just in a different boundary.

### Scenario 3: B down, then NATS down

Sequence if the event reached NATS before NATS stopped:

1. B is down.
2. A upserts and publishes.
3. NATS stores the message; the durable consumer records it as pending.
4. NATS is stopped, but its data remains in the `nats-data` volume.
5. NATS is restarted and reloads the stream and consumer state.
6. B is restarted.
7. B fetches the pending message and applies it.

Sequence if NATS stopped before publication completed:

1. B is down.
2. A upserts and creates the outbox row.
3. NATS is stopped before the relay can publish.
4. The outbox row remains unpublished.
5. NATS starts again.
6. The relay publishes; the message enters `A_EVENTS`.
7. B starts and consumes it.

Both paths converge to the same final state. The restart order should be **NATS first, then B**, because B's startup initializer needs NATS.

This scenario proves the two durability boundaries cover each other:

- Outbox = before-publish outage.
- Stream + consumer = after-publish outage.

### Related caveat: B's database is down

Stopping only the B application is safe: no messages are being delivered, so they remain pending in NATS.

Stopping B's PostgreSQL while the consumer is running is different. B will fail to apply a message, NAK it, and retry after 2 seconds. After `MaxDeliver = 5`, the message can be dropped from the consumer. The current lab has no dead-letter queue or automatic replay for that case.

## 10. Inspection points

### Backend A

```powershell
docker compose exec postgres-a psql -U app -d backend_a -c "SELECT id, name, price, version, is_deleted, updated_at FROM products.products ORDER BY updated_at DESC;"
docker compose exec postgres-a psql -U app -d backend_a -c "SELECT id, event_id, subject, created_at, published_at FROM products.outbox ORDER BY id DESC LIMIT 10;"
```

### Backend B

```powershell
docker compose exec postgres-b psql -U app -d backend_b -c "SELECT id, name, price, version, is_deleted, updated_at, synced_at FROM products_replica.products_replica ORDER BY synced_at DESC;"
docker compose exec postgres-b psql -U app -d backend_b -c "SELECT event_id, stream_sequence, received_at FROM products_replica.inbox ORDER BY received_at DESC LIMIT 10;"
```

### NATS

```powershell
docker compose --profile tools run --rm nats-cli stream info A_EVENTS
docker compose --profile tools run --rm nats-cli consumer info A_EVENTS b-products-replica
docker compose --profile tools run --rm nats-cli stream view A_EVENTS --last 5
```

Natscope: http://localhost:4280, connect with `nats://nats:4222`.

## 11. Important files and responsibilities

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

## 12. Failure boundaries and limitations

- **NATS volume deletion:** removing `nats-data` destroys stored stream messages and consumer state. Already-published outbox rows are marked published, so A will not automatically replay them.
- **Stream retention:** messages expire after 7 days by default. B must catch up within that window or the event is gone from the stream.
- **Max deliveries:** if B is running but cannot apply a message, it can be dropped after 5 attempts. There is no dead-letter queue.
- **In-flight redelivery:** if B crashes after fetch but before ack, redelivery can wait for `AckWait = 30 seconds`.
- **B database loss:** there is no snapshot/rebuild command in this lab. Resetting B's consumer would be required to replay retained stream messages.
- **Single NATS node:** the lab has one NATS server and one file store, not a clustered production deployment.
- **Unused `B_EVENTS`:** the stream exists but is not part of the current product sync path.
- **No authentication:** all local APIs and NATS connections are unsecured for learning purposes.

## 13. Related documents

- [README.md](./README.md) - run the lab and execute manual tests.
- [nats.md](./nats.md) - NATS JetStream, acknowledgements, and idempotent-consumer concepts.
- [ARCHITECTURE.md](./ARCHITECTURE.md) - broader modular-monolith background.
- [REPLICATION.md](./REPLICATION.md) - architecture scaffolding guide.
- [nats-jetstream-sync-plan.md](./nats-jetstream-sync-plan.md) - original sync implementation plan.
