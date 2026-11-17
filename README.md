# NATS JetStream Sync Lab

A local .NET 10 and React lab showing how a write-side service can replicate data to a read-side service through NATS JetStream.

Backend A owns products. Every product write commits to PostgreSQL together with an outbox event. A background relay publishes that event to JetStream, and Backend B applies it to its own replica database through an idempotent consumer.

For prerequisites, startup commands, inspection commands, and troubleshooting, see [setup.md](./setup.md).

```text
Browser
  |-- http://localhost:5173 -> Backend A UI (Vite) -> http://localhost:5001 -> PostgreSQL A
  |                                                       |
  |                                                       +-> products + outbox
  |                                                       +-> outbox relay -> NATS JetStream
  |                                                                              |
  |-- http://localhost:5174 -> Backend B UI (Vite) -> http://localhost:5002 <----+
  |                                                       |
  |                                                       +-> replica consumer
  |                                                       +-> PostgreSQL B
  |
  +-- http://localhost:4280 -> Natscope -> NATS
```

The lab demonstrates:

- Transactional outbox: the product change and its integration event commit atomically.
- At-least-once delivery through NATS JetStream.
- Inbox de-duplication in Backend B so a redelivered event does not duplicate side effects.
- Version-guarded replica upserts so an older event cannot overwrite a newer replica row.
- Soft deletion: a deleted product is retained in both databases and marked as deleted.
- Independent read models: Backend B is read-only and exposes its own HTTP API and UI.

## The mental model

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

## Contracts and routing

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

## Startup order

1. Start PostgreSQL and NATS.
2. Each AppHost runs its EF Core migrations, then `JetStreamInitializer` ensures both streams exist.
3. A starts `OutboxRelayService`, B starts `ProductsConsumerService`, and both begin listening on HTTP.

Start NATS before the backends. If NATS is unavailable during startup, the stream initializer can fail or stall. Once running, each NATS client reconnects after a later outage.

## Backend A: the write path

### HTTP endpoints

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

### Versioning and soft delete

`BackendA/Module/Products/NatsSyncLab.BackendA.Module.Products.Domain/Products/Product.cs` owns the version rules:

- New product: version `1`.
- Update: increments version.
- Delete: soft delete, increments version, sets `IsDeleted`.
- A deleted product cannot be updated again.

Because delete is just another versioned event (`isDeleted = true`), B applies it through the same upsert path as a create or update.

### Building the outbox event

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

### The outbox relay

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

## The NATS layer

NATS is configured in `infra/nats/nats-server.conf`:

- Client port: `4222`.
- Monitoring port: `8222`.
- JetStream storage directory: `/data`.
- File-store limit: `100Mb`.

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

## Backend B: the read path

`BackendB/Module/ProductsReplica/NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure/Messaging/ProductsConsumerService.cs` is the replication worker.

### Fetch loop

The service continuously:

1. Creates or updates the durable consumer.
2. Calls `FetchAsync` for up to 10 messages.
3. Handles each message.
4. Acknowledges after the database commit.
5. Repeats.

If the fetch or NATS connection fails, the outer loop waits **2 seconds** and retries (`:25`, `:37-43`).

### Envelope validation

B deserializes the event envelope and validates it. Invalid envelopes and unsupported entity types are terminated rather than retried forever. The product event must have matching entity ID and version between envelope and payload.

### Database transaction

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

### Read API and UI

B's read endpoints use Dapper:

- `BackendB/.../Endpoints/GetProductsEndpoint.cs`
- `BackendB/.../Endpoints/GetProductEndpoint.cs`

The B UI (`frontend/backend-b/src/App.tsx`) polls `/api/v1/products` every **3 seconds** while auto-refresh is enabled (`:91-101`). That UI polling is separate from the consumer's own fetch loop.

## Idempotency, ordering, and delivery guarantees

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

## Why recovery looks almost instant

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

## Reference

- [API reference](reference.md#api-reference)
- [Important files and responsibilities](reference.md#important-files-and-responsibilities)

## Example test scenarios

Run the system first using [setup.md](./setup.md). The scenarios below focus on what happens inside the pipeline and what to look for. Copy-paste inspection commands are in [setup.md](./setup.md) under **Inspect NATS** and **Inspect PostgreSQL**.

### Scenario 0: create, update, delete

Happy path with NATS, A, and B all running.

Sequence:

1. A commits a product row plus an outbox row.
2. The relay publishes the event to `A_EVENTS`.
3. B consumes it, writes the inbox row and replica, commits, and acknowledges.
4. The B UI's next refresh displays the product.

Using the UIs:

1. Open http://localhost:5173.
2. Create a product with a name and price.
3. Open http://localhost:5174 with **Auto refresh** enabled.
4. Within a few seconds, B should show the same product ID, name, price, and version.
5. Edit the product in A; B's version should increase by one and `syncedAt` should update.
6. Delete the product in A; both sides should retain the row and show it as deleted.

Using the API:

```powershell
$created = curl.exe -s -X POST http://localhost:5001/api/v1/products `
  -H "Content-Type: application/json" `
  --data-raw '{"name":"README Test","price":19.99}' | ConvertFrom-Json

$id = $created.data.id
$id

Start-Sleep -Seconds 2

curl.exe -s "http://localhost:5001/api/v1/products/$id"
curl.exe -s "http://localhost:5002/api/v1/products/$id"

curl.exe -s -X PUT "http://localhost:5001/api/v1/products/$id" `
  -H "Content-Type: application/json" `
  --data-raw '{"name":"README Test v2","price":24.50}'

Start-Sleep -Seconds 2

curl.exe -s "http://localhost:5002/api/v1/products/$id"

curl.exe -s -X DELETE "http://localhost:5001/api/v1/products/$id"
```

The write endpoint returns after the PostgreSQL commit. Backend B catches up asynchronously, so wait briefly before reading B.

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

## Failure boundaries and limitations

- **NATS volume deletion:** removing `nats-data` destroys stored stream messages and consumer state. Already-published outbox rows are marked published, so A will not automatically replay them.
- **Stream retention:** messages expire after 7 days by default. B must catch up within that window or the event is gone from the stream.
- **Max deliveries:** if B is running but cannot apply a message, it can be dropped after 5 attempts. There is no dead-letter queue.
- **In-flight redelivery:** if B crashes after fetch but before ack, redelivery can wait for `AckWait = 30 seconds`.
- **B database loss:** there is no snapshot/rebuild command in this lab. Resetting B's consumer would be required to replay retained stream messages.
- **Single NATS node:** the lab has one NATS server and one file store, not a clustered production deployment.
- **Unused `B_EVENTS`:** the stream exists but is not part of the current product sync path.
- **No authentication:** all local APIs and NATS connections are unsecured for learning purposes.
- **Only Products are synchronized:** the Customers modules are placeholders.
- **Backend B is read-only** for product data.
- **Local only:** NATS and PostgreSQL run as single local nodes; this setup is for local development and learning, not production deployment.

For running, inspecting, and troubleshooting the lab, see [setup.md](./setup.md).

## Further reading

- [Getting Started with NATS JetStream in .NET](https://milanjovanovic.tech/blog/getting-started-with-nats-jetstream-in-dotnet)
- [Implementing the Inbox Pattern for Reliable Message Consumption](https://milanjovanovic.tech/blog/implementing-the-inbox-pattern-for-reliable-message-consumption)
- [Outbox Pattern and the Dual-Write Problem](https://milanjovanovic.tech/blog/outbox-pattern-dual-write-problem)
- [NATS Message Acknowledgment](https://oneuptime.com/blog/post/2026-02-02-nats-message-acknowledgment/view)
