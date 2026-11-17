# Reading Summary: NATS JetStream, Outbox, Inbox, and Acknowledgments

A digest of the four reference articles linked from [README.md](./README.md). It captures the source material, a comparison checklist, and the results of applying that checklist to this lab.

The resulting implementation plan is in [improvement_plan.md](./improvement_plan.md).

## Sources

| # | Article | Source | Focus |
| --- | --- | --- | --- |
| 1 | [Getting Started With NATS JetStream in .NET](https://milanjovanovic.tech/blog/getting-started-with-nats-jetstream-in-dotnet) | Milan Jovanović | NATS/JetStream basics, publish/consume, retention, ack-after-side-effect |
| 2 | [Implementing the Inbox Pattern for Reliable Message Consumption](https://milanjovanovic.tech/blog/implementing-the-inbox-pattern-for-reliable-message-consumption) | Milan Jovanović | Consumer-side deduplication, inbox table, background processor |
| 3 | [Why the Outbox Pattern Solves the Dual-Write Problem](https://milanjovanovic.tech/blog/outbox-pattern-dual-write-problem) | Milan Jovanović | Transactional outbox, dual-write failure modes, EF Core interceptor |
| 4 | [How to Implement Message Acknowledgment in NATS](https://oneuptime.com/blog/post/2026-02-02-nats-message-acknowledgment/view) | OneUptime (Nawaz Dhandala) | Ack, nak, in-progress, term, error classification, monitoring |

## Cross-cutting principles

- **At-least-once is the default.** Brokers guarantee delivery, not single delivery. Duplicates are normal, not exceptional.
- **Ack after the side effect, never before.** Acking first means a crash can lose work with no redelivery.
- **Outbox solves the publish side.** Business data and the event commit in one database transaction; a separate processor publishes later.
- **Inbox or an idempotent consumer solves the consume side.** Record the message ID in the same transaction as the side effect, or deduplicate on insert.
- **Exactly-once delivery is not the goal.** Exactly-once *side effects* under at-least-once delivery is the goal.
- **Ordering is not automatic.** Concurrent processors, redelivery, and multiple relays can reorder events. Per-entity version checks or explicit coordination are needed.
- **Bound retries and handle poison messages.** `MaxDeliver` plus `term()`/dead-letter handling prevents infinite loops.
- **AckWait must exceed real processing time.** For long tasks, extend the lease with an in-progress ack instead of raising the timeout blindly.

## 1. Getting Started With NATS JetStream in .NET

### Key ideas

- **Core NATS** is fire-and-forget pub/sub. If no subscriber is connected, the message is gone.
- **JetStream** persists messages to a stream on disk. Consumers can read them later, including after a restart, giving durable at-least-once delivery.
- NATS is a single Go binary, small to run, fast, and configures storage/retention per stream.
- In .NET: publish from an endpoint, consume in a `BackgroundService`.

### Important details

- Server flags: `-js` enables JetStream, `-sd /data` sets the storage directory so streams survive restarts.
- Packages: `NATS.Net` and `NATS.Extensions.Microsoft.DependencyInjection`.
- Register one multiplexed, self-reconnecting connection with `AddNatsClient`, then expose `INatsJSContext` via `CreateJetStreamContext()`.
- Publishing returns a `PubAckResponse`; call `EnsureSuccess()` so an unstored message does not pass silently.
- The worker creates/updates a stream and a durable consumer, then fetches messages in a loop.
- Every worker instance sharing the same durable consumer competes for messages: each message runs on one worker.
- Workers are singletons, so resolve scoped dependencies such as `DbContext` through `IServiceScopeFactory`.

### Stream settings

- `Storage`: `File` (survives restart) or `Memory` (faster, lost on restart).
- `Retention`:
  - `Limits` - keep until an age, size, or count limit; replayable log; reading does not remove messages.
  - `Workqueue` - remove a message as soon as a consumer acks it; the stream is the queue; FIFO.
  - `Interest` - keep a message only while a consumer still needs it, then drop it after all interested consumers ack.
- Job-queue recipe: `Workqueue` + `File`.

### Consumer settings

- `AckPolicy.Explicit` - the consumer must ack manually.
- `AckWait` (`30s` in the example) must exceed worst-case processing time.
- `MaxDeliver` (`5` in the example) caps retries for poison messages.

### Caveats

- Acking before the side effect is finished can lose the job permanently if the worker crashes.
- Redelivery can duplicate work, so the handler must be idempotent.
- `Workqueue` is for one-consumer jobs; event replication across independent readers is a different shape (closer to `Limits`).
- The article focuses on the happy path and consumer settings; it does not cover outbox/inbox implementation details.

### Relevance to this lab (to compare later)

- The lab uses `Limits` retention rather than `Workqueue`, which fits event replication rather than a job queue.
- The lab's consumer also uses explicit ack, `AckWait = 30s`, and `MaxDeliver = 5`.
- Check whether the lab's consumer has any equivalent of in-progress ack for long operations.

## 2. Implementing the Inbox Pattern

### Key ideas

- The Outbox ensures reliable **publishing**; the Inbox ensures reliable **consumption**.
- The broker can deliver the same message twice when an ack is lost. Processing twice can duplicate side effects.
- Inbox approach:
  1. Receive the message.
  2. Insert it into an inbox table keyed by message ID.
  3. Ignore duplicates on insert.
  4. A background processor reads unprocessed rows and handles them.
- This separates reception from processing and makes retries an application concern.

### Important details

- Inbox table columns: `id`, `type`, `content` (JSONB), `received_on_utc`, `processed_on_utc`, `error`.
- Deduplication relies on `id` as the primary key plus `ON CONFLICT (id) DO NOTHING`.
- A filtered index on unprocessed rows keeps the index small.
- The consumer only writes the inbox row and returns/acks; it does not run business logic inline.
- The processor:
  - reads a batch ordered by `received_on_utc`;
  - uses `FOR UPDATE SKIP LOCKED` so multiple processors can run concurrently;
  - deserializes using the stored type;
  - dispatches each message;
  - records success or the error;
  - writes results back in one round-trip using `UNNEST` and commits.
- If the processor crashes mid-batch, the transaction rolls back and the messages are picked up next run.

### Caveats

- **Table growth:** delete processed rows after a retention period, partition by time, or archive.
- **Poison messages:** track errors, cap retries, dead-letter and alert.
- **Ordering:** `ORDER BY received_on_utc` is only rough arrival order. With `SKIP LOCKED` and multiple processors, strict or per-aggregate ordering is not guaranteed.
- **Lag:** monitor the gap between `received_on_utc` and `processed_on_utc`; scale batch size, polling, or processors if it grows.
- The consumer can ack before actual processing, because the inbox row is the durable handoff.

### Inbox vs. idempotent consumer

- **Idempotent consumer:** processes inline, checks a dedup record, does the work and records the dedup entry in the same transaction. The broker controls retries/backoff.
- **Inbox:** separates reception and processing. The consumer acks immediately after persisting; retries, backoff, batching, and scaling are application-controlled.
- Use an idempotent consumer when side effects are transactional and broker retries are sufficient. Use an inbox when you need batching, custom retry policy, or horizontal processor scaling.

### Relevance to this lab (to compare later)

- The lab's consumer writes an inbox row and applies the replica in the same transaction, then acks. That is closer to the inline idempotent-consumer shape than the article's two-phase inbox processor.
- The lab has no separate inbox processor, no batch drain, and no `UNNEST` update.
- Check whether the lab's inbox table records processing errors or only receipt, and whether it has retention/cleanup.

## 3. Why the Outbox Pattern Solves the Dual-Write Problem

### Key ideas

- **Dual-write problem:** writing to a database and a broker without a shared transaction. One can succeed while the other fails.
- Failure directions:
  - DB commits, broker publish fails: data exists but downstream never hears about it.
  - Broker publish succeeds, DB fails: downstream reacts to data that was never saved.
- Retries do not fix it because the process can crash between commit and publish.
- Two-phase commit is poorly supported by many brokers, adds latency, can block on coordinator failure, and couples infrastructure.
- Publish-first-then-save is worse: it publishes events for data that may not exist.
- Outbox solution: save business data and the event in the **same database transaction**, then let a background processor publish and mark the event processed.

### The guarantee

- The database transaction is atomic: either both rows commit or neither does.
- Publishing happens later and asynchronously, adding a small delay.
- The outbox processor may publish and then crash before marking the row processed, so it can publish the same event again.
- Therefore the Outbox guarantees **at-least-once**, not exactly-once. Consumers must be idempotent.

### Minimal EF Core example

- `OutboxMessage` entity: `Id`, `Type`, `Content`, `OccurredOnUtc`, `ProcessedOnUtc`.
- A `SaveChangesInterceptor` collects domain events from tracked aggregates, clears them, serializes them, and adds outbox rows to the same `DbContext`.
- Everything commits together on `SaveChangesAsync`.
- The `Type` column should be resolvable later: store `AssemblyQualifiedName` or resolve with `Assembly.GetType` on the assembly that defines the events. `Type.GetType` only searches the calling assembly and core library.
- Register the interceptor on the `DbContext` options.
- The background publisher is a separate concern; the outbox insert is what solves the dual-write problem.

### When to use and when to skip

- Use when losing an event has business consequences, when modules/services communicate through events, or when data/event consistency is non-negotiable.
- Skip for analytics pings, debug logs, metrics, and fire-and-forget notifications where occasional loss is acceptable.
- Change Data Capture (CDC) is an alternative that captures changes from the database log instead of an outbox table.

### Relevance to this lab (to compare later)

- The lab matches this pattern: the write endpoint inserts the product and an outbox row in one `SaveChangesAsync`; the relay publishes later.
- Check whether the lab marks rows published only after a successful publish, and whether it can republish after a crash-between-publish-and-mark.
- Check whether the lab's outbox payload is the full event envelope versus a domain event plus type metadata.

## 4. NATS Message Acknowledgment

### Key ideas

- Acknowledgment types control what happens after a delivery:
  - `ack()` - processing succeeded; remove the message from pending delivery.
  - `nak(delay)` - temporary failure; redeliver after a delay.
  - `working()` / in-progress - long processing; reset the ack wait timer.
  - `term()` - permanent failure; stop all future redelivery.
- Classify errors correctly. Permanent failures should not be retried forever; transient failures should be retried with backoff.

### Stream and consumer example settings

- Stream: `Limits` retention, `File` storage, `max_bytes` 100 MB, `max_age` 24 hours, duplicate window 60 seconds, 3 replicas in the example.
- Consumer: durable name, `AckPolicy.Explicit`, `ack_wait` 30 seconds, `max_deliver` 5, `DeliverPolicy.All`, `max_ack_pending` 1000.

### Error handling flow

- Parse/validation failure: permanent; `term()` and optionally publish to a dead-letter subject.
- Success: `ack()`.
- Temporary failure: `nak(delay)`, often exponential backoff capped at a maximum.
- Rate limiting: `nak()` with the `Retry-After` delay when available.
- Retry limit reached or permanent error: publish to a dead-letter queue, then `term()`.
- Long-running task: send `working()` on a heartbeat interval shorter than `ack_wait`.

### Consumer configuration guidance

| Option | Meaning | Typical guidance |
| --- | --- | --- |
| `ack_wait` | Time before an unacked message is redelivered | 30–60 seconds, greater than worst-case processing |
| `max_deliver` | Maximum delivery attempts | 3–5 |
| `max_ack_pending` | Unacked messages before backpressure | 1000–10000, lower for long tasks |
| `ack_policy` | Whether manual ack is required | explicit |

### Monitoring

- Track ack counts by type (`ack`, `nak`, `term`).
- Track processing duration and outcome.
- Track redelivery counts; delivery count > 1 indicates redelivery.
- High `nak` rates suggest upstream/transient problems; frequent `term` suggests bad message quality.

### Caveats

- Without acknowledgment, a consumer crash can lose the message (with Core NATS or non-durable consumption).
- `working()` only extends the lease; it does not confirm completion.
- `term()` is final: the message is not redelivered, so pair it with dead-letter publishing if the message must be preserved.
- Setting `AckWait` too low causes redelivery while processing is still healthy.
- The example uses JavaScript and Go clients; the concepts map to NATS.Net, but type/method names differ.

### Relevance to this lab (to compare later)

- The lab uses `ack` after DB commit. Check whether it uses `nak` with delay, `term`, or in-progress acks anywhere.
- The lab's `AckWait = 30s` and `MaxDeliver = 5` match the article's common guidance.
- The lab has no dead-letter queue; after `MaxDeliver`, messages can be dropped.
- Check whether the lab monitors redelivery/delivery counts or only database state.

## Comparison checklist for later

Use these questions when reviewing this lab against the sources:

- Does every consumer ack only after the durable side effect commits?
- Is the outbox row marked published only after the broker confirms storage (`EnsureSuccess`)?
- Can a crash between publish and mark cause a duplicate publish, and is the consumer idempotent for it?
- Is deduplication keyed by a stable message/event ID with a unique constraint?
- Is the inbox write in the same transaction as the business side effect, or is it a two-phase inbox processor?
- Is there a version guard to reject out-of-order or older events?
- Is the stream retention mode correct for the workload (`Limits` for event logs, `Workqueue` for jobs)?
- Are `AckWait` and `MaxDeliver` sized for the real processing time?
- Are transient errors retried with `nak`/backoff, and permanent errors terminated?
- Is there dead-letter handling, or can poison messages disappear after `MaxDeliver`?
- Is long-running work protected with in-progress acks?
- Is redelivery/ack/term/processing lag observable?
- Are inbox/outbox tables bounded by retention, partitioning, or cleanup?
- Is ordering handled explicitly where it matters?
- Would the lab benefit from any source recommendation it does not yet implement?

## Lab comparison results

The checklist above was applied to the current code. Results are based on the code as it exists now.

| # | Checklist item | Result | Evidence / notes |
| --- | --- | --- | --- |
| 1 | Ack only after the durable side effect commits | **Pass** | Inbox + replica commit at `ProductsConsumerService.cs:184`, ack at `:89`; invalid events terminate without DB writes. |
| 2 | Outbox marked published only after the broker confirms storage | **Pass** | `OutboxRelayService.cs:73-86` checks `ack.Error` before `MarkPublished`; save/commit at `:88-89`. This is equivalent to `EnsureSuccess()`. |
| 3 | Crash between publish and mark causes a duplicate publish, and the consumer handles it | **Pass with caveat** | `MsgId = EventId` at `:76`; 10-minute dedupe window (`NatsOptions.cs:13`); the inbox primary key dedupes beyond that window. |
| 4 | Deduplication keyed by a stable message/event ID with a unique constraint | **Pass** | Outbox unique `event_id` (Backend A migration `:54-58`); inbox primary key `event_id` (Backend B migration `:26-29`). |
| 5 | Inbox write in the same transaction as the side effect, or a two-phase inbox processor | **Pass by design** | This lab is an inline idempotent consumer, not the article's two-phase inbox processor. The article says this is valid when side effects are transactional. |
| 6 | Version guard rejects stale events | **Pass** | `WHERE products_replica.version < EXCLUDED.version` at `ProductsConsumerService.cs:180`. |
| 7 | Stream retention mode correct for the workload | **Pass** | `Limits` + `File` (`JetStreamInitializer.cs:34-37`), with a 7-day `MaxAge` (`NatsOptions.cs:15`). Fits a replayable event log rather than a job queue. |
| 8 | `AckWait` and `MaxDeliver` sized for real processing time | **Pass with caveat** | 30 seconds / 5 attempts (`ProductsConsumerService.cs:53-54`). Adequate for a quick DB upsert; no in-progress ack if work exceeds 30 seconds. |
| 9 | Transient errors retried, permanent errors terminated | **Partial** | Exceptions NAK with a fixed 2-second delay (`:94`); invalid envelopes and unsupported entity types terminate (`:111`, `:123`). Payload mismatch and missing payload throw and are retried as transient (`:130`, `:134`) even though they are likely permanent. |
| 10 | Dead-letter handling | **Gap** | None. Terminated messages are only logged; messages exhausting `MaxDeliver` are dropped. |
| 11 | Long-running work protected with in-progress acks | **Gap (low priority)** | Not used. Acceptable for the current millisecond-scale workload. |
| 12 | Redelivery / ack / term / processing lag observable | **Gap** | Only logs. No metrics, no delivery-count logging, no outbox backlog, inbox lag, or redelivery counters. |
| 13 | Inbox/outbox bounded by retention, partitioning, or cleanup | **Gap** | Both tables grow indefinitely; published outbox rows are never purged. |
| 14 | Ordering handled explicitly where it matters | **Pass with caveat** | A single relay with `ORDER BY id`, plus the version guard, makes reordering harmless. If Backend B is scaled to multiple consumer instances, ordering is not preserved. |
| 15 | Lab benefits from missing source recommendations | **Identified** | Missing dead-letter handling, retention/cleanup, metrics, exponential backoff, permanent-error classification, and in-progress ack. |

### Additional findings

- **Outbox transaction holds locks during network I/O.** `OutboxRelayService.cs:56` opens the transaction, publishing happens at `:73`, and commit happens at `:89`. If NATS is down and `PublishAsync` waits for reconnection, the transaction and `FOR UPDATE SKIP LOCKED` row locks stay open. This is acceptable for the single-instance lab but risky for production.
- **One timestamp for the whole batch.** `publishedAt` is computed once at `OutboxRelayService.cs:69`, so every row in a batch shares the same published time.
- **Chaos kill points exist but are unused.** `SharedKernel/Chaos/Chaos.cs` is referenced only by its unit test. The relay and consumer do not call kill points, so crash-window scenarios such as after-commit-before-ack cannot be triggered with it.
- **The lab matches the outbox article closely.** The inbox article's two-phase processor is a different valid design, not a defect in this lab.

### Priority

1. Add a dead-letter table or subject, and terminate deterministic payload errors instead of retrying them.
2. Add outbox/inbox retention or cleanup.
3. Add basic observability: outbox backlog, inbox lag, delivery count, redelivery count, and ack/nak/term counts.
4. Wire chaos kill points into the relay and consumer if failure scenarios are to be tested.
5. Consider exponential backoff for NAK/relay retries and in-progress acks if processing time grows.

## Glossary

- **Subject:** the name messages are published to (for example, `a.events.product.upserted`).
- **Stream:** JetStream's persisted store of messages for one or more subjects.
- **Consumer:** a cursor over a stream that delivers messages and tracks acknowledgments.
- **Durable consumer:** a named consumer whose state survives restarts; instances sharing it compete for messages.
- **Pull consumer:** the client asks for messages instead of the server pushing them.
- **Ack / AckWait:** confirmation that a message was handled / the window before an unacked message is redelivered.
- **Nak:** negative acknowledgment; asks for redelivery, optionally after a delay.
- **In-progress / working:** resets the ack timer for long-running processing.
- **Term:** permanent failure; stops redelivery.
- **Redelivery:** the same message delivered again because it was not acked in time.
- **Poison message:** a message that reproducibly fails or crashes its handler.
- **Dead-letter queue:** a destination for messages that cannot be processed.
- **Outbox:** a table storing events in the same transaction as business data so they can be published later.
- **Inbox:** a table storing received message IDs so duplicate deliveries are ignored.
- **Idempotent consumer:** a consumer that can apply the same message more than once without duplicate side effects.
- **Dual-write problem:** one write to the database and one to a broker can diverge without a shared transaction.
- **At-least-once:** a message is delivered one or more times, never silently dropped, but may be duplicated.
