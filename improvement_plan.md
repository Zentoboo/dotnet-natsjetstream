# Improvement Plan: Reliability and Operability

A concrete plan for closing the gaps found in [reading_summary.md](./reading_summary.md) against the current lab. The core sync flow is already correct; this plan focuses on failure visibility, bounded storage, and testability.

For the current behavior, see [README.md](./README.md). For setup and inspection commands, see [setup.md](./setup.md). For API and file maps, see [reference.md](./reference.md).

## Current state

Already correct and should not be redesigned:

- Product write and outbox row commit in one EF Core unit of work.
- The relay checks the publish ack before marking a row published.
- Messages carry `MsgId = EventId`; the outbox has a unique `event_id`.
- The consumer inserts into the inbox and applies the replica in one transaction, commits, then acks.
- The inbox primary key deduplicates redelivery.
- The replica upsert is version-guarded.
- The stream uses `File` storage and `Limits` retention, which fits an event log.
- `AckWait = 30s` and `MaxDeliver = 5` are reasonable for the current workload.
- The consumer uses an inline idempotent consumer. This is a valid alternative to a two-phase inbox processor.

Gaps addressed by this plan:

| Gap | Impact |
| --- | --- |
| No dead-letter handling | Poison messages disappear after `MaxDeliver`; failures are only in logs |
| No outbox/inbox retention | Tables grow forever and slow down over time |
| No metrics or lag visibility | Redelivery, backlog, and consumer lag are invisible |
| Chaos kill points unused | Crash-window scenarios cannot be tested deterministically |
| Fixed 2-second NAK delay | Wastes retries on permanent failures and does not back off under load |
| Outbox transaction held during publish | NATS outages can hold DB locks and connections |
| No in-progress ack | Long processing can trigger false redelivery |
| No payload/op semantic validation | Structurally valid but wrong events can corrupt the replica |

## Goals

- Never silently drop a product event after retries are exhausted.
- Keep outbox and inbox tables bounded.
- Make backlog, lag, redelivery, and failures observable.
- Make the documented failure scenarios testable.
- Keep the lab small enough to understand.

## Non-goals

- Exactly-once delivery.
- Multi-node NATS clustering.
- Authentication or production security.
- A full production job-queue redesign.
- A two-phase inbox processor unless the lab later needs batching or custom scheduling.

## Design decisions

### Dead letter: NATS stream, not only a database table

Use a dedicated JetStream stream, `B_DEAD_LETTER`, with subject `b.deadletter.>`. Store failure details and the original event there.

Why not a database-only dead-letter table:

- The main case that exhausts deliveries can be Backend B's database being down. A database DLQ would fail for exactly the failure it needs to capture.
- NATS is already running and is the transport. A NATS DLQ works when B's database is unavailable.
- It matches the source article's "publish to dead letter, then term" flow.

A database audit table can be added later if query convenience matters more than outage resilience.

### Retention windows

- Published outbox rows: keep **7 days**.
- Inbox rows: keep **8 days**.

Inbox retention must be longer than the stream's `MaxAge` (7 days). If an inbox row is deleted while the stream can still redeliver the event, the consumer loses its dedupe record. The version guard still makes product upserts harmless, but dedupe should not depend on that.

### Metrics: `System.Diagnostics.Metrics` first

Use the built-in `Meter` API. This needs no new NuGet package and works with `dotnet-counters`. Add an OpenTelemetry or Prometheus exporter only if the lab needs dashboards.

### Chaos hooks: environment-gated

Use the existing `Chaos.KillPoint` helper. It is already a no-op unless `CHAOS_KILLPOINT` matches. Add kill points only to the process being tested.

## Phase 1: Dead-letter handling and permanent-error classification

Highest value. Prevents silent loss.

### 1.1 Add a dead-letter stream

Extend `SharedKernel/NatsSyncLab.SharedKernel/Messaging/NatsOptions.cs`:

```csharp
public string StreamDeadLetter { get; set; } = "B_DEAD_LETTER";
public string DeadLetterSubjectPrefix { get; set; } = "b.deadletter";
```

Update `JetStreamInitializer` to ensure the stream exists:

- Name: `B_DEAD_LETTER`.
- Subject: `b.deadletter.>`.
- Storage: `File`.
- Retention: `Limits`.
- `MaxAge`: 30 days.
- Duplicate window: keep the existing `DuplicateWindow`.

Do not reuse `B_EVENTS` for this. It currently has event semantics and may be used by the reverse flow later.

### 1.2 Define the dead-letter payload

Add to the shared contracts:

```csharp
public sealed record DeadLetterEnvelope(
    Guid EventId,
    string OriginalSubject,
    string Reason,
    int DeliveryCount,
    DateTimeOffset FailedAt,
    string? RawPayload,
    string? Error);
```

Subject convention:

```text
b.deadletter.product.failed
```

Publish with `MsgId = EventId` so repeated failures do not create an unbounded number of DLQ entries within the dedupe window.

### 1.3 Classify errors in the consumer

Add a small exception type and classifier in the Backend B module:

```csharp
internal sealed class PermanentEventException(string message, string reason) : Exception(message)
{
    public string Reason { get; } = reason;
}
```

Treat these as permanent:

- invalid envelope;
- unsupported entity type;
- missing payload;
- payload ID/version mismatch;
- op/subject mismatch;
- delete op with `payload.IsDeleted = false`, or upsert op with an unexpected deleted payload if that rule is adopted.

Keep these as transient:

- Npgsql connection/command timeouts;
- database unavailable;
- NATS message-level errors;
- unknown exceptions that may succeed on retry.

Update `ProductsConsumerService.HandleAsync` so permanent failures throw `PermanentEventException` with a stable reason instead of generic `InvalidOperationException`.

### 1.4 Publish to the dead-letter stream, then terminate

Add a helper in `ProductsConsumerService`:

```csharp
private async Task DeadLetterAndTerminateAsync(
    INatsJSMsg<byte[]> message,
    string reason,
    Exception? exception,
    CancellationToken cancellationToken)
```

Behavior:

1. Build a `DeadLetterEnvelope` from the message and metadata.
2. Publish to `b.deadletter.product.failed` with `MsgId = EventId`.
3. Call `EnsureSuccess()` and throw on publish failure so the message is NAKed and retried.
4. Call `AckTerminateAsync` only after the DLQ publish succeeds.

This ordering is the same rule as the normal flow: persist the side effect, then acknowledge.

### 1.5 Handle exhausted deliveries

In the catch block around `HandleAsync`, read the delivery count from message metadata. On the final attempt:

```text
if (deliveryCount >= MaxDeliver)
    dead-letter the message, then terminate
else
    nak with backoff
```

For messages that fail permanently on the first attempt, dead-letter and terminate immediately. Do not consume all five attempts on a deterministic validation failure.

Use a named constant for `MaxDeliver` instead of repeating `5`.

### 1.6 Expose the DLQ for inspection

Update `setup.md` and `reference.md`:

```powershell
docker compose --profile tools run --rm nats-cli stream info B_DEAD_LETTER
docker compose --profile tools run --rm nats-cli stream view B_DEAD_LETTER --last 10
```

Update `README.md` limitations once the DLQ exists.

### Phase 1 files

| File | Change |
| --- | --- |
| `SharedKernel/.../Messaging/NatsOptions.cs` | Add dead-letter stream and subject settings |
| `SharedKernel/.../Messaging/JetStreamInitializer.cs` | Ensure `B_DEAD_LETTER` exists |
| `SharedKernel/.../Contracts/DeadLetterEnvelope.cs` | New contract |
| `BackendB/.../Messaging/PermanentEventException.cs` | New exception type |
| `BackendB/.../Messaging/ProductsConsumerService.cs` | Classify, publish DLQ, terminate, backoff |
| `setup.md`, `reference.md`, `README.md` | Document the stream and behavior |

### Phase 1 acceptance criteria

- An invalid envelope produces one DLQ message and the original message is terminated.
- A payload/envelope mismatch produces one DLQ message and is not retried five times.
- A database outage retries with backoff, then dead-letters after `MaxDeliver` if NATS is reachable.
- A successful event still produces no DLQ message.
- Duplicate redelivery of a DLQ'd event does not create duplicate DLQ entries within the dedupe window.

## Phase 2: Outbox and inbox retention

### 2.1 Add retention options

Backend A, `appsettings.json`:

```json
"Outbox": {
  "RetentionDays": 7,
  "CleanupIntervalMinutes": 60,
  "BatchSize": 5000
}
```

Backend B, `appsettings.json`:

```json
"Inbox": {
  "RetentionDays": 8,
  "CleanupIntervalMinutes": 60,
  "BatchSize": 5000
}
```

Add strongly typed options and validate:

- retention days must be greater than zero;
- inbox retention must be greater than the stream `MaxAge` in days, or log a startup warning;
- batch size must be greater than zero.

### 2.2 Outbox cleanup service

Add `OutboxCleanupService` to the Backend A Products infrastructure.

Behavior:

1. Wait for the configured interval.
2. Delete published rows older than the retention window in batches.
3. Repeat until a batch deletes fewer than `BatchSize` rows or the stopping token fires.

SQL shape:

```sql
DELETE FROM products.outbox
WHERE id IN (
    SELECT id
    FROM products.outbox
    WHERE published_at IS NOT NULL
      AND published_at < now() - @retention
    ORDER BY id
    LIMIT @batchSize
)
```

Use a short-lived context and no open transaction across the whole loop.

Register it in `AddProductsModuleInfrastructure` after the relay.

### 2.3 Inbox cleanup service

Add `InboxCleanupService` to the Backend B ProductsReplica infrastructure.

Use the same batching shape:

```sql
DELETE FROM products_replica.inbox
WHERE event_id IN (
    SELECT event_id
    FROM products_replica.inbox
    WHERE received_at < now() - @retention
    ORDER BY received_at
    LIMIT @batchSize
)
```

Keep retention above the stream's `MaxAge`. Do not delete unprocessed work that a future two-phase inbox processor might still need; if that design is adopted later, change the filter to require a processed marker.

### Phase 2 files

| File | Change |
| --- | --- |
| `BackendA/.../Messaging/OutboxCleanupService.cs` | New hosted service |
| `BackendA/.../Persistence/OutboxRetentionOptions.cs` | New options type |
| `BackendA/.../DependencyInjection.cs` | Register options and cleanup service |
| `BackendA/NatsSyncLab.BackendA.AppHost/appsettings.json` | Add `Outbox` section |
| `BackendB/.../Messaging/InboxCleanupService.cs` | New hosted service |
| `BackendB/.../Persistence/InboxRetentionOptions.cs` | New options type |
| `BackendB/.../DependencyInjection.cs` | Register options and cleanup service |
| `BackendB/NatsSyncLab.BackendB.AppHost/appsettings.json` | Add `Inbox` section |

No EF migration is required for retention; it only deletes existing rows.

### Phase 2 acceptance criteria

- Published outbox rows older than the window are removed.
- Unpublished outbox rows are never removed.
- Inbox rows older than the window are removed.
- A cleanup batch does not lock either table for the whole run.
- Startup fails or warns when inbox retention is shorter than stream retention.

## Phase 3: Observability

### 3.1 Add a shared metrics class

Add `SharedKernel/NatsSyncLab.SharedKernel/Diagnostics/SyncMetrics.cs` using `System.Diagnostics.Metrics`.

Meter name: `NatsSyncLab`.

Counters:

- `outbox.published`
- `outbox.publish_failures`
- `outbox.cleanup_deleted`
- `consumer.processed`
- `consumer.duplicates`
- `consumer.stale_versions`
- `consumer.nak`
- `consumer.dead_lettered`
- `consumer.terminated`
- `consumer.errors`

Histogram:

- `consumer.processing.duration_ms`

Observable gauges:

- `outbox.backlog`
- `inbox.lag_seconds`
- `consumer.last_delivery_count`

For gauges, keep a cached value updated by a lightweight background timer rather than querying the database inside the metric callback.

### 3.2 Instrument the relay

In `OutboxRelayService`:

- count published messages after the DB commit;
- count failures in the catch block;
- record batch duration;
- expose backlog from a periodic count query, or from the number of rows read in the last batch.

### 3.3 Instrument the consumer

In `ProductsConsumerService`:

- count processed, duplicates, stale version skips, NAKs, dead letters, terms, errors;
- record processing duration;
- log `eventId`, `entityId`, `version`, `streamSequence`, and `deliveryCount`;
- update `consumer.last_delivery_count`.

### 3.4 Local usage

No exporter is required for the lab:

```powershell
dotnet-counters monitor --process-id <pid> NatsSyncLab
```

If dashboards are wanted later, add `OpenTelemetry.Exporter.OpenTelemetryProtocol` or the Prometheus exporter in the AppHost.

### Phase 3 files

| File | Change |
| --- | --- |
| `SharedKernel/.../Diagnostics/SyncMetrics.cs` | New meter and instruments |
| `BackendA/.../Messaging/OutboxRelayService.cs` | Record metrics and enrich logs |
| `BackendB/.../Messaging/ProductsConsumerService.cs` | Record metrics and enrich logs |
| `BackendA/.../DependencyInjection.cs` | Register metrics if a singleton is needed |
| `BackendB/.../DependencyInjection.cs` | Register metrics if a singleton is needed |
| `README.md` / `setup.md` | Document `dotnet-counters` usage |

### Phase 3 acceptance criteria

- `dotnet-counters` shows the meter and counters while the system runs.
- Logs for each applied event include event ID, entity ID, version, stream sequence, and delivery count.
- Redelivery increments a visible counter.
- Duplicate skips and stale-version skips are distinguishable in metrics/logs.

## Phase 4: Chaos kill points and backoff

### 4.1 Add kill points

Use the existing `Chaos.KillPoint`.

Relay:

- after publish succeeds, before `SaveChangesAsync`;
- after `SaveChangesAsync`, before commit.

Consumer:

- after inbox insert;
- after replica upsert;
- after commit, before ack.

Example:

```csharp
Chaos.KillPoint("consumer-after-commit-before-ack");
```

Guard is already provided by the `CHAOS_KILLPOINT` environment variable.

### 4.2 Add exponential backoff

Replace the fixed `RetryDelay` NAK with a capped backoff:

```text
attempt 1: 1s
attempt 2: 5s
attempt 3: 15s
attempt 4: 30s
attempt 5: terminal handling
```

Compute from `NumDelivered`. Keep a small fixed delay for NATS message-level errors.

### Phase 4 files

| File | Change |
| --- | --- |
| `BackendA/.../Messaging/OutboxRelayService.cs` | Add relay kill points |
| `BackendB/.../Messaging/ProductsConsumerService.cs` | Add consumer kill points and backoff |
| `README.md` | Document how to trigger scenarios with `CHAOS_KILLPOINT` |

### Phase 4 acceptance criteria

- `CHAOS_KILLPOINT=consumer-after-commit-before-ack` causes redelivery and the inbox suppresses the duplicate.
- `CHAOS_KILLPOINT=relay-after-publish-before-save` causes a republish that is deduped by `MsgId` or the inbox.
- NAK delays increase with delivery count and cap at 30 seconds.

## Phase 5: Optional outbox transaction restructure

Do this only if multiple relay instances or long NATS outages are expected.

Current behavior: `OutboxRelayService` opens a transaction, selects up to 50 rows with `FOR UPDATE SKIP LOCKED`, publishes them over the network, then saves and commits. If NATS publish blocks on reconnect, the transaction and row locks stay open.

Options:

1. **Keep as-is and document.** Acceptable for the single-instance lab.
2. **Claim then publish.** Add `claimed_at` and `claim_token` columns; claim rows in a short transaction; publish outside the transaction; mark published with the token. Recover stale claims after a timeout.
3. **Reduce batch size.** Lowers the maximum lock duration but does not remove the underlying issue.

Recommended: option 1 for the lab, option 2 if relay scaling becomes real.

## Testing strategy

### Automated

- Unit tests for `PermanentEventException` classification.
- Unit tests for backoff computation.
- Unit tests for retention option validation.
- Unit tests for dead-letter envelope construction.
- Existing `dotnet test NatsSyncLab.slnx` must pass.

### Manual

Use the scenarios in `README.md` and the new chaos hooks:

1. Happy path create/update/delete.
2. B down, NATS up.
3. NATS down, A/B up.
4. B down, then NATS down.
5. Invalid payload -> DLQ.
6. DB down during consume -> retries, then DLQ after `MaxDeliver`.
7. Duplicate delivery -> inbox skip.
8. Old version -> version guard skip.
9. Retention cleanup -> old rows removed, new rows kept.

### Commands

```powershell
dotnet build NatsSyncLab.slnx
dotnet test NatsSyncLab.slnx
```

Run the lab with `setup.md`. Inspect the DLQ with the `nats-cli` commands added in Phase 1.

## Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| DLQ publish fails too | NAK and retry; on final failure, log full details and terminate rather than looping forever |
| Inbox retention creates a dedupe gap | Keep inbox retention longer than stream `MaxAge`; validate at startup |
| Cleanup deletes useful audit data | Keep configurable windows; default to 7/8 days; add a DB audit table later if needed |
| Metrics add overhead | Keep instruments low-cardinality; do not use event IDs as metric tags |
| Chaos hooks reach production | Already no-op without `CHAOS_KILLPOINT`; keep them out of hot paths when disabled |
| Relay restructure introduces duplicate publishing | `MsgId` and the inbox already make duplicates safe; add a claim token before scaling relays |

## Implementation order

1. Phase 1: dead-letter stream, classification, DLQ publish, tests.
2. Phase 2: retention options, cleanup services, startup validation.
3. Phase 3: metrics and log enrichment.
4. Phase 4: chaos kill points and exponential backoff.
5. Phase 5: only if needed.

Each phase should leave the lab runnable and the existing tests green.

## Open questions

- Should the DLQ also be mirrored into a Postgres table for easier SQL inspection, or is the NATS stream enough?
- Should `B_EVENTS` be repurposed or removed, since the product flow does not use it?
- Should the lab include a small `/health/sync` endpoint exposing outbox backlog and inbox lag, or are metrics enough?
- Should the dead-letter stream retention be 30 days or match the 7-day event stream?
