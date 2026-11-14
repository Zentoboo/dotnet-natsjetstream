# NATS JetStream + .NET + Postgres: Idempotent Sync Test Project

A small lab project to verify that two independent .NET modular-monolith backends can keep master data in sync through NATS JetStream, and to find out exactly what happens when things fail (a backend down, NATS down, duplicates, reordering, and so on).

---

## 1. Goals and non-goals

**Goals**

- Two separate backends (A and B), each with its own Postgres database, run locally via Docker.
- Each backend **owns** some master data and replicates it to the other through JetStream.
- Sync is **idempotent**: duplicates, redelivery and replays never corrupt state.
- Sync is **convergent**: after any failure and recovery, both sides agree.
- A repeatable test suite for failure scenarios, each ending in an objective reconciliation check.

**Non-goals**

- Production hardening, auth, UI, multi-node NATS clustering (can be a stretch goal).
- Conflict resolution for concurrent writes to the same entity. The ownership rule avoids it.

---

## 2. Core design

### 2.1 The ownership rule

Every dataset has **exactly one owner**. Only the owner writes it and publishes events. The other backend holds a read-only replica.

| Dataset | Owner | Replica on |
|---|---|---|
| `Products` | Backend A | Backend B |
| `Customers` | Backend B | Backend A |

(Entities are suggestions. Swap in whatever fits.)

### 2.2 Architecture

```
 +----------------------+        +-------------------+        +----------------------+
 | Backend A + Postgres |        | NATS JetStream    |        | Backend B + Postgres |
 |                      |        |                   |        |                      |
 |  products (owned)    |        |                   |        |  customers (owned)   |
 |  outbox + relay  ----+------> |  A_EVENTS stream -+------> |  inbox + apply       |
 |                      |        |                   |        |   -> products_replica|
 |  inbox + apply   <---+------- |  B_EVENTS stream <+------- |  outbox + relay      |
 |   -> customers_replica        |                   |        |                      |
 +----------------------+        +-------------------+        +----------------------+
```

### 2.3 Guarantees and where they come from

JetStream gives **at-least-once** delivery. Exactly-once *effect* is built in your code:

| Problem | Mechanism |
|---|---|
| DB commit succeeds but publish fails | **Transactional outbox** (event row written in the same tx as the data) |
| Publisher retries create duplicates | `Nats-Msg-Id` header = outbox event ID (JetStream dedupe window) |
| Redelivery after a missed ack | **Inbox table** with unique `event_id` |
| Late or out-of-order events overwrite newer data | Per-entity **version** check on apply |
| Poison messages block progress | `MaxDeliver` + backoff + dead-letter handling |
| Consumer offline longer than retention | **Snapshot/resync** endpoint on the owner |

### 2.4 Event envelope

```json
{
  "eventId": "0f8fad5b-d9cb-469f-a165-70867728950e",
  "entityType": "product",
  "entityId": "b5a1...",
  "version": 7,
  "op": "upsert",
  "occurredAt": "2026-11-10T08:00:00Z",
  "payload": { "name": "Widget", "price": 12.5 }
}
```

- `op` is `upsert` or `delete` (tombstone).
- `version` is a per-entity monotonically increasing number assigned by the owner.
- Subject convention: `a.events.product.upserted`, `a.events.product.deleted`, `b.events.customer.upserted`, and so on.

---

## 3. Prerequisites

- .NET 8 SDK (or newer)
- Docker Desktop and Docker Compose
- A Postgres client (psql, DBeaver)
- NATS CLI (`nats`) for inspecting streams and consumers
- NuGet packages: `NATS.Net`, `Npgsql`, plus your choice of data access (Dapper or EF Core)

---

## 4. Repository layout

```
nats-sync-lab/
├─ docker-compose.yml
├─ docs/
│  └─ scenarios.md
├─ scripts/
│  ├─ chaos/            # stop/start/pause scripts per scenario
│  └─ reconcile.sh
├─ src/
│  ├─ BuildingBlocks/   # shared: envelope, NATS setup, outbox/inbox helpers
│  ├─ BackendA/
│  │  ├─ Modules/
│  │  │  ├─ Products/       # owned
│  │  │  └─ CustomersReplica/ # read-only
│  │  └─ Host/              # Web API, relay, consumers
│  └─ BackendB/
│     ├─ Modules/
│     │  ├─ Customers/        # owned
│     │  └─ ProductsReplica/  # read-only
│     └─ Host/
└─ tests/
   └─ FailureScenarios/
```

Keep the shared building blocks small. Both backends should treat each other as external systems, with only the event contract in common.

---

## 5. Phase 0: Infrastructure with Docker Compose

### Step 0.1: Compose file

```yaml
services:
  nats:
    image: nats:2.10-alpine
    command: ["-js", "-sd", "/data", "-m", "8222"]
    ports:
      - "4222:4222"   # client
      - "8222:8222"   # monitoring
    volumes:
      - nats-data:/data
    healthcheck:
      test: ["CMD", "wget", "-qO-", "http://localhost:8222/healthz"]
      interval: 5s
      timeout: 3s
      retries: 10

  postgres-a:
    image: postgres:16
    environment:
      POSTGRES_USER: app
      POSTGRES_PASSWORD: app
      POSTGRES_DB: backend_a
    ports: ["5433:5432"]
    volumes: [pg-a-data:/var/lib/postgresql/data]

  postgres-b:
    image: postgres:16
    environment:
      POSTGRES_USER: app
      POSTGRES_PASSWORD: app
      POSTGRES_DB: backend_b
    ports: ["5434:5432"]
    volumes: [pg-b-data:/var/lib/postgresql/data]

  backend-a:
    build: ./src/BackendA
    depends_on:
      nats: { condition: service_healthy }
      postgres-a: { condition: service_started }
    environment:
      ConnectionStrings__Db: Host=postgres-a;Database=backend_a;Username=app;Password=app
      Nats__Url: nats://nats:4222
      ASPNETCORE_URLS: http://+:8080
    ports: ["5001:8080"]

  backend-b:
    build: ./src/BackendB
    depends_on:
      nats: { condition: service_healthy }
      postgres-b: { condition: service_started }
    environment:
      ConnectionStrings__Db: Host=postgres-b;Database=backend_b;Username=app;Password=app
      Nats__Url: nats://nats:4222
      ASPNETCORE_URLS: http://+:8080
    ports: ["5002:8080"]

volumes:
  nats-data:
  pg-a-data:
  pg-b-data:
```

### Step 0.2: Verify the infrastructure

```bash
docker compose up -d nats postgres-a postgres-b
nats --server localhost:4222 server info
curl http://localhost:8222/jsz
```

You should see JetStream enabled with the file store.

### Step 0.3 (optional, later): Toxiproxy

Add a `toxiproxy` service and point both backends at the proxy instead of NATS directly. This lets you inject latency, timeouts and partitions without stopping containers.

---

## 6. Phase 1: Scaffold the solution

1. Create the solution and projects: `BuildingBlocks`, `BackendA.Host`, `BackendB.Host`, and one class library per module.
2. Enforce module boundaries (a module exposes a public interface, and its internals stay `internal`).
3. Add health endpoints (`/health`) and structured logging with correlation of `eventId` in every log line. You will rely on this heavily when debugging scenarios.
4. Add configuration for the NATS URL, stream names and consumer names.

---

## 7. Phase 2: Database schemas

Run these on **each** backend's database (adjust table names per owner).

### 7.1 Owned table (example: A's products)

```sql
CREATE TABLE products (
  id          uuid PRIMARY KEY,
  name        text        NOT NULL,
  price       numeric     NOT NULL,
  version     bigint      NOT NULL DEFAULT 1,
  is_deleted  boolean     NOT NULL DEFAULT false,
  updated_at  timestamptz NOT NULL DEFAULT now()
);
```

### 7.2 Outbox

```sql
CREATE TABLE outbox (
  id            bigserial   PRIMARY KEY,
  event_id      uuid        NOT NULL UNIQUE,
  subject       text        NOT NULL,
  payload       jsonb       NOT NULL,
  created_at    timestamptz NOT NULL DEFAULT now(),
  published_at  timestamptz NULL
);
CREATE INDEX ix_outbox_unpublished ON outbox (id) WHERE published_at IS NULL;
```

### 7.3 Inbox

```sql
CREATE TABLE inbox (
  event_id     uuid        PRIMARY KEY,
  stream_seq   bigint      NULL,
  received_at  timestamptz NOT NULL DEFAULT now()
);
```

### 7.4 Replica table (example: B's copy of products)

```sql
CREATE TABLE products_replica (
  id          uuid PRIMARY KEY,
  name        text        NOT NULL,
  price       numeric     NOT NULL,
  version     bigint      NOT NULL,
  is_deleted  boolean     NOT NULL DEFAULT false,
  synced_at   timestamptz NOT NULL DEFAULT now()
);
```

---

## 8. Phase 3: NATS streams and consumers

### 8.1 Streams (created at startup, idempotent)

| Stream | Subjects | Storage | Retention | Dedupe window |
|---|---|---|---|---|
| `A_EVENTS` | `a.events.>` | File | Limits (e.g. 7 days) | 10 min |
| `B_EVENTS` | `b.events.>` | File | Limits (e.g. 7 days) | 10 min |

```csharp
await js.CreateOrUpdateStreamAsync(new StreamConfig("A_EVENTS", new[] { "a.events.>" })
{
    Storage = StreamConfigStorage.File,
    Retention = StreamConfigRetention.Limits,
    MaxAge = TimeSpan.FromDays(7),
    DuplicateWindow = TimeSpan.FromMinutes(10),
});
```

### 8.2 Consumers

One **durable pull consumer** per direction:

| Consumer | Stream | Owner side |
|---|---|---|
| `b-from-a` | `A_EVENTS` | Backend B |
| `a-from-b` | `B_EVENTS` | Backend A |

Settings: explicit ack, `AckWait` 30s, `MaxDeliver` 5, backoff such as 1s, 5s, 15s, 30s.

> Note: NATS.Net type and property names change between versions, so check them against the version you install.

---

## 9. Phase 4: Publisher (outbox relay)

### Step 4.1: Write path (inside the owner)

In **one DB transaction**: update the entity (bump `version`) and insert an outbox row. The request never talks to NATS directly.

### Step 4.2: Relay loop (background service)

```sql
SELECT id, event_id, subject, payload
FROM outbox
WHERE published_at IS NULL
ORDER BY id
LIMIT 100
FOR UPDATE SKIP LOCKED;
```

For each row, in order:

1. Publish to JetStream with `MsgId = event_id`.
2. Check the `PubAck` (`EnsureSuccess`). A duplicate flag is also fine.
3. `UPDATE outbox SET published_at = now() WHERE id = @id`.

```csharp
var ack = await js.PublishAsync(
    subject: row.Subject,
    data: row.Payload,
    opts: new NatsJSPubOpts { MsgId = row.EventId.ToString() });
ack.EnsureSuccess();
```

Rules:

- Run a **single relay instance** per backend at first, to preserve order. Scaling out later needs per-entity ordering thought.
- If publishing fails, stop the batch, back off, retry. Never skip a row.
- Periodically clean up old published rows.

---

## 10. Phase 5: Consumer (inbox plus idempotent apply)

For each message, in **one DB transaction**:

```sql
-- 1. Dedupe
INSERT INTO inbox (event_id, stream_seq) VALUES (@eventId, @seq)
ON CONFLICT (event_id) DO NOTHING;
-- rows affected = 0 -> duplicate: skip apply, still ack

-- 2. Version-guarded upsert
INSERT INTO products_replica (id, name, price, version, is_deleted)
VALUES (@id, @name, @price, @version, @isDeleted)
ON CONFLICT (id) DO UPDATE
SET name = EXCLUDED.name,
    price = EXCLUDED.price,
    version = EXCLUDED.version,
    is_deleted = EXCLUDED.is_deleted,
    synced_at = now()
WHERE products_replica.version < EXCLUDED.version;
```

Then **commit, then ack**. Never ack before commit.

Error handling:

| Situation | Action |
|---|---|
| Transient error (DB down, timeout) | Do not ack (or `Nak` with delay). Redelivery handles it. |
| Permanent error (bad payload) | Log, send to a dead-letter table or subject, then ack/term |
| Reaching `MaxDeliver` | Listen for the max-deliveries advisory and record the message |

---

## 11. Phase 6: Reverse direction

Repeat phases 2 to 5 mirrored: B owns `customers` and publishes to `B_EVENTS`, A consumes into `customers_replica`. Do not start this until the one-way flow passes its tests.

---

## 12. Phase 7: Reconciliation tooling

Add a `GET /sync/reconcile` endpoint on each backend, returning for each replicated dataset:

- Row count and count of non-deleted rows
- Max version
- A checksum, for example `md5(string_agg(id || ':' || version, ',' ORDER BY id))`

`scripts/reconcile.sh` calls the owner's endpoint and the replica's endpoint and diffs them. **Every failure scenario ends by running this.** Converged means identical.

Also expose the outbox backlog (`COUNT(*) WHERE published_at IS NULL`) and the consumer lag (from `nats consumer info`).

---

## 13. Phase 8: Failure scenario suite

For each scenario: **setup, action, recover, wait for drain, reconcile**. Record the expected and the actual result.

### 13.1 Consumer side

| # | Scenario | How to trigger | Expected result |
|---|---|---|---|
| C1 | B down while A writes | `docker compose stop backend-b`; create/update 100 products on A; start B | B catches up, reconcile passes |
| C2 | B crashes after DB commit, before ack | Chaos kill-point (`CHAOS=after-commit-before-ack`) | Redelivery, inbox dedupes, no double apply |
| C3 | B crashes mid-transaction | Kill-point before commit | Rollback, redelivery, final state correct |
| C4 | B's Postgres down | `docker compose stop postgres-b` | Messages stay unacked or nak'd, resume on recovery |
| C5 | B slow (ack timeout) | Add artificial delay > `AckWait` | Redelivery while the first is still running, idempotency holds |

### 13.2 Publisher side

| # | Scenario | How to trigger | Expected result |
|---|---|---|---|
| P1 | A crashes after commit, before publish | Kill-point after tx commit | Outbox row remains, relay publishes on restart |
| P2 | A crashes after publish, before marking sent | Kill-point after `PubAck` | Republish deduped by `Nats-Msg-Id` (or inbox if outside window) |
| P3 | A's Postgres down | Stop `postgres-a` | No writes accepted, nothing published, clean recovery |

### 13.3 Broker side

| # | Scenario | How to trigger | Expected result |
|---|---|---|---|
| N1 | NATS down while A writes | `docker compose stop nats` | Writes succeed, outbox grows |
| N2 | NATS returns | `docker compose start nats` | Outbox drains, B converges |
| N3 | NATS restart with file storage | Restart container, keep volume | Streams and consumers intact |
| N4 | NATS restart with memory storage | Temporarily switch stream to memory | Stream lost, expose the gap, trigger resync |
| N5 | Network partition | Toxiproxy `timeout`/`down` toxic | Reconnect, no lost state |
| N6 | NATS down during consume | Stop mid-batch | Unacked messages redelivered after return |

### 13.4 Data correctness

| # | Scenario | How to trigger | Expected result |
|---|---|---|---|
| D1 | Duplicate event | Publish same `eventId` twice (bypass the dedupe window) | Applied once |
| D2 | Out-of-order versions | Publish v5 then v4 manually | v4 ignored |
| D3 | Rapid updates to one entity | 1000 updates in a loop | Replica ends at the latest version |
| D4 | Delete | Delete on owner | Replica shows tombstone, later upserts with lower version ignored |
| D5 | Poison message | Publish malformed payload | Retries up to `MaxDeliver`, dead-lettered, stream continues |
| D6 | Parent/child ordering | Event for a child arrives before its parent | Defined behavior (retry or park) |

### 13.5 Recovery

| # | Scenario | How to trigger | Expected result |
|---|---|---|---|
| R1 | Consumer offline longer than retention | Shorten `MaxAge`, keep B down, wait | Gap detected, resync from snapshot |
| R2 | Brand-new replica | Wipe B's replica tables and consumer | Snapshot plus stream catch-up converges |
| R3 | Consumer deleted | `nats consumer rm` | Recreated, decide start point (snapshot, then new events) |

---

## 14. Phase 9: Snapshot / resync

JetStream is not a permanent log, so a replica must be able to rebuild from the owner:

1. Owner exposes `GET /sync/snapshot?since=<version or time>` (paged, ordered by id).
2. Replica pulls the snapshot and applies it with the same version-guarded upsert.
3. Replica then resumes its durable consumer. Overlap is safe because of the version guard.

Trigger a resync when: a gap is detected, the consumer was deleted, or the stream sequence has moved past the consumer's position.

---

## 15. Observability checklist

- Log `eventId`, `entityId`, `version`, `stream_seq`, `deliveryCount` for every message handled.
- Metrics (even simple counters): outbox backlog, publish failures, redeliveries, duplicates skipped, stale versions skipped, DLQ count.
- NATS monitoring at `http://localhost:8222` and `nats consumer info <stream> <consumer>` for lag.

---

## 16. Suggested timeline

| Step | Deliverable |
|---|---|
| 1 | Compose up, streams created, `nats` CLI verified |
| 2 | A writes product, outbox relay publishes, B applies (happy path) |
| 3 | Reconciliation endpoints and script |
| 4 | Scenarios C1 to C5, P1 to P3 |
| 5 | Scenarios N1 to N6 (add Toxiproxy) |
| 6 | Scenarios D1 to D6 |
| 7 | Reverse direction (B owns customers) |
| 8 | Snapshot/resync and R1 to R3 |
| 9 | Write up findings in `docs/scenarios.md` |

---

## 17. Common pitfalls to watch for

- **Acking before commit** loses data on a crash.
- **Relying on the dedupe window alone** fails for retries after the window. The inbox is the real guard.
- **Skipping the version check** lets redeliveries and replays overwrite newer data.
- **Multiple relay instances** can reorder events for one entity.
- **Memory storage** silently loses streams on restart.
- **Hard deletes** without tombstones make replicas diverge permanently.
- **Treating the stream as the source of truth.** The owner's database is. The stream is transport.

---

## 18. Definition of done

- [ ] Both directions sync through Docker Compose from a clean start
- [ ] Every scenario in section 13 has a recorded result
- [ ] Reconciliation passes after every scenario
- [ ] Resync from snapshot works for a wiped replica
- [ ] Findings and surprises written up
