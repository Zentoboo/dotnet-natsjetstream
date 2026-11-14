---
id: 5c0gcztv7tzrj2yhqtaxwsb
title: Nats
desc: ''
updated: 1793333782419
created: 1793330279376
---

# NATS JetStream & the Idempotent Consumer Pattern

**References**

- [Getting Started with NATS JetStream in .NET](https://milanjovanovic.tech/blog/getting-started-with-nats-jetstream-in-dotnet)
- [The Idempotent Consumer Pattern in .NET](https://milanjovanovic.tech/blog/the-idempotent-consumer-pattern-in-dotnet-and-why-you-need-it)
- [How I Use NATS JetStream as a Job Queue in .NET](https://milanjovanovic.tech/blog/nats-jetstream-job-queue-dotnet)

## TL;DR

- NATS is a messaging system that runs as a single Go binary. **JetStream** is its persistence layer, which turns a subject into a durable queue with **at-least-once** delivery.
- In .NET: add `NATS.Net`, publish from an endpoint, consume in a `BackgroundService`.
- **Ack after the side effect, never before.**
- Redelivery can cause duplicates, so the handler must be **idempotent**.
- Idempotent Consumer pattern: record each processed message ID in a DB table, in the **same transaction** as the side effect.
- Production shape: **two streams** (jobs + results), **one shared durable pull consumer** for competing workers, **publish the result before acking the job**, `AckWait` + `MaxDeliver` to bound crashes and poison jobs.

### Reading map

| Part | Sections | What you get |
| --- | --- | --- |
| A. Fundamentals | 1-7 | What NATS/JetStream is, setup, publish, consume, retention, ack rule |
| B. Idempotency | 8-13 | Why duplicates happen, the Idempotent Consumer pattern, its limits |
| C. Production design | 14-19 | Real job queue: two streams, ordering rule, poison jobs, limits |
| D. Reference | 20-24 | Cheat sheet, decision guide, glossary, checklist, further reading |

---

# Part A. Fundamentals

## 1. Core NATS vs JetStream

| | Core NATS | JetStream |
| --- | --- | --- |
| Model | Fire-and-forget pub/sub | Persisted stream on top of Core |
| No subscriber online | Message is **gone** | Message is **stored**, delivered later |
| Survives restart | No | Yes (with `File` storage) |
| Good for | Live notifications | Work queues, durable jobs |

- JetStream captures messages published to a subject into a **stream** on disk. Consumers can read them later, even after a restart.

- ![NATS&JetStream](assets/images/NATSandJetStream.png)

## 2. Why NATS is worth a look

- **Tiny**: server image is about 18 MB, a single Go binary with no ZooKeeper or Erlang.
- **Fast**: Core NATS does millions of msgs/sec per node. JetStream does hundreds of thousands/sec.
- **Cheap to run**: idles in tens of MB of RAM, so it can run right next to your app.
- **Flexible per stream**: each stream sets its own storage and retention, so one server can host a cache and a strict work queue side by side.
- Clusters with Raft-based replication when you need more scale.
- Operationally friendly: easy to run on a single VPS, and it plugs into a Prometheus + Grafana monitoring stack.

## 3. Setup in .NET

**Server (docker-compose)**: `-js` enables JetStream, `-sd` sets the storage dir so streams survive restarts.

```yaml
nats:
  image: nats:2.14-alpine
  command: ['-js', '-sd', '/data']
  ports: ['4222:4222']
  volumes:
    - nats-data:/data
  restart: unless-stopped
```

**Packages**

```bash
dotnet add package NATS.Net
dotnet add package NATS.Extensions.Microsoft.DependencyInjection
```

**Program.cs**: one multiplexed, self-reconnecting connection, plus a JetStream context to inject.

```csharp
builder.Services.AddNatsClient(nats =>
    nats.ConfigureOptions(opts => opts with { Url = "nats://localhost:4222" }));

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<INatsConnection>().CreateJetStreamContext());
```

## 4. Publishing a job

```csharp
app.MapPost("/jobs", async (CreateJob request, INatsJSContext js, CancellationToken ct) =>
{
    var job = new Job(Guid.NewGuid(), request.Payload);

    PubAckResponse ack = await js.PublishAsync("jobs.work", job, cancellationToken: ct);
    ack.EnsureSuccess(); // throws if the stream didn't store it

    return Results.Accepted($"/jobs/{job.Id}");
});
```

- `NATS.Net` serializes the record to JSON automatically.
- `PublishAsync` on the JetStream context returns a **publish ack** from the server. Always call `EnsureSuccess()` so a message the stream failed to store doesn't pass silently.

## 5. Consuming in a worker

```csharp
public class JobWorker(INatsJSContext js) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await js.CreateStreamAsync(new StreamConfig("JOBS", ["jobs.work"])
        {
            Retention = StreamConfigRetention.Workqueue, // acked messages are removed
            Storage   = StreamConfigStorage.File         // survives restart
        }, ct);

        var consumer = await js.CreateOrUpdateConsumerAsync("JOBS", new ConsumerConfig("workers")
        {
            AckPolicy  = ConsumerConfigAckPolicy.Explicit,
            AckWait    = TimeSpan.FromSeconds(30), // must exceed worst-case processing time
            MaxDeliver = 5                         // drop poison message after 5 tries
        }, ct);

        await foreach (var msg in consumer.ConsumeAsync<Job>(cancellationToken: ct))
        {
            await ProcessAsync(msg.Data, ct);          // side effect FIRST
            await msg.AckAsync(cancellationToken: ct); // THEN ack
        }
    }
}
```

- Register with `builder.Services.AddHostedService<JobWorker>()`.
- The worker is a singleton, so resolve scoped dependencies (e.g. `DbContext`) via `IServiceScopeFactory`.
- All running instances share the `workers` durable consumer. They **compete** for jobs, so each job runs on one worker.
- Key consumer settings:
  - `AckPolicy.Explicit`: you must ack manually.
  - `AckWait`: how long before JetStream assumes the worker died and redelivers. **Must exceed your worst-case processing time**, otherwise a slow-but-healthy job gets redelivered while it's still running.
  - `MaxDeliver`: caps retries so a poison message doesn't loop forever.

## 6. Two stream settings to know

**`Storage`**

- `File`: on disk, survives restarts
- `Memory`: faster, gone on restart

**`Retention`**: when a message leaves the stream

- `Limits` (default): keeps messages until an age, size or count limit. It's a replayable log, and reading doesn't remove anything.
- `Workqueue`: drops a message as soon as a consumer acks it. The stream *is* the queue, delivered FIFO (oldest first).
- `Interest`: keeps a message only while a consumer still needs it, then drops it once every interested consumer acks.

> **Job queue recipe: `Workqueue` + `File`.**

## 7. Acknowledge after the side effect

- ![AcknowledgeAftertheSideEffect](assets/images/AcknowledgeAftertheSideEffect.png)

- JetStream gives **at-least-once** delivery. If a worker crashes before acking, the message is **redelivered**.
- Ack *before* the work finishes, and a crash leaves the job marked done with nothing to show for it. **The job is lost.**
- **Rule: ack after the side effect, never before.**
- The trade-off is that a job can run more than once, so the handler must be idempotent.
- **At-least-once delivery only holds up when the handler reading the stream is idempotent.**

---

# Part B. Idempotency

## 8. Idempotency basics

- **Idempotent operation**: can be applied multiple times without changing the result beyond the first application.
  - Non-idempotent: "charge credit card $100". Applied twice, it charges $200.
  - Idempotent: "set account status to ACTIVE".

## 9. Why duplicates happen (the key challenge)

- Distributed systems are unreliable by nature.
- **Exactly-once delivery** is theoretically impossible in most systems. Messages can be:
  - out of order
  - duplicated
  - delayed
- Example of a duplicate on the **producer** side:
  1. Producer publishes, the broker stores it and sends an ACK.
  2. Network glitch: the ACK never reaches the producer.
  3. Producer times out and **retries**.
  4. The broker now holds **two** copies of the event.
- Other sources of duplicates: broker redeliveries, and consumer crash + retry.
- If you design assuming every message is processed exactly once, you set yourself up for subtle data corruption.
- Goal: not exactly-once *delivery*, but exactly-once *side effects*, using the **Idempotent Consumer** pattern.

## 10. Two layers of defence

**Layer 1: Producer-side (let the broker dedupe)**

- Give each message a **stable unique ID** (a new `Guid`, or the entity ID).
- Brokers like Azure Service Bus and SQS drop re-publishes with the same ID within a dedup window.
- Fixes: network retries, duplicate publishes.
- Doesn't fix: consumer retries or crashes mid-processing.

**Layer 2: Consumer-side (Idempotent Consumer)**

- Brokers can dedupe on the producer side, but **only the consumer can defend against redeliveries.**

## 11. Implementing the Idempotent Consumer

**Pattern**: record each processed message ID in a table and check it before handling. Commit the side effects and that record in **one transaction**.

```csharp
public async Task ConsumeAsync(ConsumeContext<NoteCreated> context)
{
    // 1. Already processed? Short-circuit.
    if (await dbContext.MessageConsumers.AnyAsync(c =>
            c.MessageId == context.MessageId &&
            c.ConsumerName == nameof(NoteCreatedConsumer)))
        return;

    using var transaction = await dbContext.Database.BeginTransactionAsync();

    // 2. Do the work
    dbContext.Tags.AddRange(tagEntities);

    // 3. Record that this message was processed
    dbContext.MessageConsumers.Add(new MessageConsumer
    {
        MessageId = context.MessageId,
        ConsumerName = nameof(NoteCreatedConsumer),
        ConsumedAtUtc = DateTime.UtcNow
    });

    await dbContext.SaveChangesAsync();
    await transaction.CommitAsync(); // both succeed or both fail
}
```

**Three details that matter**

1. **The idempotency key = `(MessageId, ConsumerName)`**
   - `ConsumerName` lets multiple different consumers each process the same message independently.
   - Add a **unique constraint** on `(MessageId, ConsumerName)`. Without it, two concurrent deliveries can both pass the `AnyAsync` check (a race condition). With it, only one insert wins.
2. **Atomic side effect + idempotency record**
   - Processing fails: no `MessageConsumers` row, so it's safe to retry.
   - Processing succeeds: both rows commit together.
   - You never get "work done but not marked" or "marked but work not done".
3. **Handles at-least-once delivery**
   - Consume, ACK fails, broker redelivers, code runs again, hits the table, returns early, **no duplicate side effects**.

> Core idea: **do this work exactly once per message ID, even under retries.**

## 12. The catch: non-deterministic handlers

Problem: external calls (email API, payment gateway, another queue) sit **outside your DB transaction**. The DB may commit, but a network hiccup means you can't tell if the external action happened. A retry could send a second email or charge the card twice.

**Fix A: Pass an idempotency key to the external call** (simplest, most reliable)

```csharp
await emailService.SendAsync(new SendEmailRequest
{
    To = user.Email,
    Subject = "Welcome!",
    Body = "Thanks for signing up.",
    IdempotencyKey = context.MessageId // same key, provider runs it once
});
```

- Works if the provider supports it (most payment and email platforms do).

**Fix B: Store the intent locally (outbox-style)**

- Write a record of the intended action (e.g. a `PendingEmails` table) in your DB first.
- A background process reads the pending records and performs the action once.
- Makes it deterministic, but adds extra tables and workers. Often overengineering unless the action is critical or irreversible (payments, account provisioning).

## 13. When you *don't* need the pattern

Skip it if the operation is **naturally idempotent** (it overwrites state rather than appending):

- updating a projection / rebuilding a read model
- setting a status flag ("set status = Active")
- refreshing a cache

Or if a simple **precondition check** is enough: "if the entity is already in the desired state, return early".

> Apply the pattern where duplicates cause **real harm** (financial or data inconsistency). Otherwise, simpler is better.

*Note: Part C is a real example of this. The production job queue needs no `MessageConsumers` table because grading is idempotent and keyed by job id.*

---

# Part C. A production job queue on JetStream

*Case study from the author's own system (Katabench): the API accepts a code submission, a worker compiles and runs it in a sandbox, and the user waits for the verdict. Hard requirement: **a user always gets their result or a retriable error, never a silent loss.***

## 14. Why not a Redis list?

The first version was the classic minimal queue: the API does `RPUSH`, workers poll with `LPOP`, and results go into a per-job key with a TTL. It could lose a job in two structural ways:

| Problem | What happens |
| --- | --- |
| `LPOP` removes the job with **no acknowledgment** | A worker that crashes mid-job takes the job with it. Nothing redelivers it. |
| **No persistence** | An in-memory Redis restart or redeploy wipes the queue and every in-flight result. |

- You *can* patch around both (`LMOVE` to a processing list, AOF persistence, a sweeper for stuck jobs), but then you're hand-building acknowledgments and durability. That is exactly the feature set of a real broker.
- Why JetStream over RabbitMQ here: a single lightweight binary that is trivial to run on a VPS, and it plugs straight into the existing Prometheus + Grafana stack.

| Capability | Redis list | JetStream |
| --- | --- | --- |
| Acknowledgment | None (`LPOP` is destructive) | Per-message ack, unacked jobs are redelivered |
| Survives broker restart | No (in-memory) | Yes (file-backed streams) |
| Crashed worker | Job lost | Redelivered after `AckWait` |
| Poison job | Not handled | Bounded by `MaxDeliver` |
| Competing workers | Manual | Built in (shared durable consumer) |

## 15. The shape: two streams

Everything is built on two **file-backed** streams.

```csharp
var js = new NatsJSContext(connection);

// The job queue: a message is removed once a worker acks it.
await js.CreateStreamAsync(new StreamConfig("GRADING_JOBS", ["grading.jobs"])
{
    Retention = StreamConfigRetention.Workqueue,
    Storage = StreamConfigStorage.File
});

// Results: short retention, one subject per job id.
await js.CreateStreamAsync(new StreamConfig("GRADING_RESULTS", ["grading.results.>"])
{
    Retention = StreamConfigRetention.Limits,
    MaxAge = TimeSpan.FromMinutes(5),
    Storage = StreamConfigStorage.File
});
```

| | Jobs stream | Results stream |
| --- | --- | --- |
| Name / subject | `GRADING_JOBS` / `grading.jobs` | `GRADING_RESULTS` / `grading.results.>` |
| Retention | `Workqueue` (acked = deleted) | `Limits` with `MaxAge` = 5 min |
| Storage | `File` | `File` |
| Behaves like | A queue | A short-lived mailbox |
| Producer | The API | The worker |
| Consumer | Pool of workers on **one shared durable pull consumer** | The API, via an **ephemeral ordered consumer** filtered to one job id |

**Jobs stream**

- Work-queue retention: JetStream deletes a message once a consumer acks it, so the stream behaves like a queue instead of a log.
- **One shared durable pull consumer** gives you the *competing consumers* pattern. Adding a worker is just starting another process, with zero configuration.

**Results stream**

- The worker publishes each outcome to `grading.results.<jobId>`.
- The API awaits it with an ephemeral ordered consumer filtered to that one subject (the `>` wildcard matches every job id; the filter narrows it to one).
- Because the stream **retains messages for a few minutes**, a result published *before* the API started waiting is still delivered. That closes a whole class of races that a fire-and-forget reply channel has.

## 16. The one ordering rule that matters

```csharp
var consumer = await js.CreateOrUpdateConsumerAsync("GRADING_JOBS",
    new ConsumerConfig("workers")
    {
        AckWait = TimeSpan.FromSeconds(60), // redeliver if no ack in time
        MaxDeliver = 4                      // bounded poison retry
    });

await foreach (var msg in consumer.ConsumeAsync<GradingJob>(cancellationToken: ct))
{
    GradingJobOutcome outcome = await grader.GradeAsync(msg.Data, ct);

    // Publish the result FIRST, then ack the job.
    await js.PublishAsync($"grading.results.{msg.Data.JobId}", outcome, cancellationToken: ct);
    await msg.AckAsync(cancellationToken: ct);
}
```

> **Publish the result before you ack the job.**

Walk every failure window:

| Where the worker crashes | State | What happens | Outcome |
| --- | --- | --- | --- |
| **Before** the publish | Job un-acked | Redelivered after `AckWait`, another worker grades it | Nothing lost |
| **Between** publish and ack | Result published, job un-acked | Job redelivered and graded again, a **second identical result** is published | Harmless: grading is idempotent, keyed by job id |
| (If you acked first) after ack, before publish | Job gone, no result | Nothing redelivers it | **Result lost, unrecoverable** (the Redis hole again) |

- This is Section 7's "ack after the side effect" rule applied to a two-step workflow. Here the "side effect" is *publishing the result*.
- Idempotency here comes from the **job id**, not a `MessageConsumers` table. See the note after Section 13.

## 17. Poison jobs and `MaxDeliver`

- A submission that **reproducibly crashes its worker** (the system runs untrusted code, so this is normal) would be redelivered forever.
- `MaxDeliver` bounds it: redelivered a limited number of times, then **dropped**.
- JetStream emits a **`MAX_DELIVERIES` advisory** when that happens. Wire monitoring/alerts to it.
- Without `MaxDeliver`, one poison job **crash-loops the whole worker pool**.
- The waiting client gets a timeout instead of an infinite loop.
- `AckWait` and `MaxDeliver` do different jobs:
  - `AckWait` bounds a **crashed worker** (the job moves to another worker).
  - `MaxDeliver` bounds a **poison job** (the job stops being retried).

## 18. Hiding it all behind a synchronous API

- From the user's view nothing is asynchronous: the API **holds the HTTP request open**, awaits the result subject up to a **dispatch budget**, and returns a retriable **`503`** on timeout.
- Every failure mode above degrades to **"try again"**, never to a missing result.

**End-to-end flow**

1. API receives a submission and publishes the job to `grading.jobs` (jobs stream).
2. A worker from the pool pulls it through the shared durable consumer.
3. Worker runs the job (compile + sandbox execute).
4. Worker **publishes** the outcome to `grading.results.<jobId>` (results stream).
5. Worker **acks** the job, which deletes it from the work queue.
6. API, awaiting `grading.results.<jobId>`, receives the outcome and replies to the user.
7. If the budget runs out first, the API returns a retriable `503`.

## 19. Limitations to know before copying this design

- **A single NATS node is a single point of failure.** Jobs survive a restart (file-backed streams), but **not a dead disk**. Fix: clustered JetStream with Raft replicas once you outgrow one box.
- **The results stream is retention-bounded** (minutes). If a client should fetch a result hours later, the queue is the wrong home. Persist results to Postgres and treat the stream purely as **transport**.
- Delivery is still **at-least-once**. Even with work-queue retention, a job not acked in time is delivered again, so the handler must stay idempotent.

---

# Part D. Reference

## 20. Cheat sheet: settings and what they do

| Setting | Where | Meaning | Typical value / advice |
| --- | --- | --- | --- |
| `Retention = Workqueue` | Stream | Acked message is deleted; stream *is* the queue; FIFO | Job queues |
| `Retention = Limits` | Stream | Keep until age/size/count limit; replayable log | Results mailbox, event log |
| `Retention = Interest` | Stream | Keep only while a consumer still needs it | Fan-out where all consumers must see it |
| `Storage = File` | Stream | On disk, survives restart | Default for anything durable |
| `Storage = Memory` | Stream | Faster, lost on restart | Cache-like data |
| `MaxAge` | Stream | Auto-expire messages after a duration | e.g. 5 min for results |
| `AckPolicy = Explicit` | Consumer | You must ack each message | Required for reliable processing |
| `AckWait` | Consumer | Time before an un-acked message is redelivered | **Greater than** worst-case processing time |
| `MaxDeliver` | Consumer | Max delivery attempts before dropping | Small bounded number (e.g. 4-5) |
| Shared durable name (e.g. `workers`) | Consumer | All instances compete for messages | Scale by starting another process |

## 21. Decision guide

| Question | Answer |
| --- | --- |
| Is it a live notification where missing it is fine? | **Core NATS** |
| Must it survive a restart / crash? | **JetStream**, `File` storage |
| Is it a job each worker should process once? | `Workqueue` retention + shared durable consumer |
| Do I need to replay history? | `Limits` retention |
| Handler writes to my DB? | **Idempotent Consumer** (message-id table + same transaction + unique constraint) |
| Handler calls email/payment API? | Pass an **idempotency key**, or store intent locally |
| Handler just overwrites state? | Likely **naturally idempotent**, skip the pattern |
| Should the result outlive minutes/hours? | Persist to a DB; use the stream only as transport |
| Jobs vs facts? | **Jobs** (do this once) fit a queue; **facts** (something happened, many readers, replay) fit a log. RabbitMQ deletes on consume, Kafka keeps and moves a cursor. |

## 22. Glossary

- **Subject**: the name messages are published to (e.g. `jobs.work`). Wildcards such as `grading.results.>` match many subjects.
- **Stream**: JetStream's persisted store of messages for one or more subjects.
- **Consumer**: a view/cursor over a stream that delivers messages and tracks acks.
- **Durable consumer**: a named consumer whose state survives restarts. Instances sharing it compete for messages.
- **Ephemeral consumer**: a temporary consumer that goes away when the client does.
- **Pull consumer**: the client asks for messages (as `ConsumeAsync` does), rather than the server pushing.
- **Ack / AckWait**: confirmation that a message was handled / the window before it is redelivered.
- **Redelivery**: the same message delivered again because it wasn't acked in time.
- **Poison message**: a message that reproducibly fails or crashes its handler.
- **Advisory** (`MAX_DELIVERIES`): a system event JetStream emits, e.g. when a message exhausts its delivery attempts.
- **Competing consumers**: several workers pulling from one queue so each message is handled by one of them.
- **Idempotent**: safe to apply more than once with the same end result.
- **Idempotency key**: a stable identifier (here the `MessageId`/job id) used to recognise duplicates.
- **At-least-once**: a message is delivered one or more times, never silently dropped, but may be duplicated.

## 23. Final checklist

**Stream and consumer setup**

- [ ] Job stream uses `Workqueue` + `File`
- [ ] Results (if any) go to a separate short-retention `Limits` stream, one subject per job id
- [ ] Consumer uses `Explicit` ack
- [ ] `AckWait` is longer than worst-case processing time
- [ ] `MaxDeliver` is set, and an alert is wired to the `MAX_DELIVERIES` advisory
- [ ] Workers share one durable consumer so they compete

**Ordering and idempotency**

- [ ] **Ack after** the side effect
- [ ] When a worker produces a result, **publish the result before acking the job**
- [ ] Publisher sets a stable message ID (broker-level dedup)
- [ ] Consumer checks `(MessageId, ConsumerName)` before processing, or the work is naturally idempotent (e.g. keyed by job id)
- [ ] Unique constraint on `(MessageId, ConsumerName)`
- [ ] Side effect + idempotency record in **one transaction**
- [ ] External calls get an idempotency key, or use a local intent record

**Resilience**

- [ ] `PublishAsync(...).EnsureSuccess()` so unstored publishes don't pass silently
- [ ] API returns a retriable error (e.g. `503`) on timeout, never a silent loss
- [ ] Results needed later are persisted in a database, not left in the stream
- [ ] Plan for a single-node failure (clustered JetStream with Raft replicas) when load or risk grows

## 24. Further reading

- [How I Use NATS JetStream as a Job Queue in .NET](https://milanjovanovic.tech/blog/nats-jetstream-job-queue-dotnet) (source for Part C)
- [RabbitMQ vs Kafka for .NET Applications](https://milanjovanovic.tech/blog/rabbitmq-vs-kafka-dotnet): when your messages are jobs vs facts
- [Idempotent Consumer: Handling Duplicate Messages](https://milanjovanovic.tech/blog/idempotent-consumer-handling-duplicate-messages)
- [NATS JetStream docs](https://docs.nats.io/nats-concepts/jetstream) and [NATS.Net client](https://github.com/nats-io/nats.net)
