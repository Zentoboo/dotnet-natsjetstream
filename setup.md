# NATS JetStream Sync Lab - Setup

How to run the lab, inspect its state, and troubleshoot it. For how replication works and the example test scenarios, see [README.md](./README.md).

## Prerequisites

- Docker Desktop
- .NET 10 SDK
- Node.js 20.19+ or 22.12+ with npm

Check the tools:

```powershell
docker --version
dotnet --version
node --version
npm --version
```

## Services and ports

| Service | Address | Notes |
| --- | --- | --- |
| Backend A UI | http://localhost:5173 | Product create, update, delete |
| Backend B UI | http://localhost:5174 | Read-only replica with 3-second auto-refresh |
| Backend A API | http://localhost:5001 | Product owner API |
| Backend B API | http://localhost:5002 | Product replica API |
| NATS client | `nats://localhost:4222` | JetStream enabled |
| NATS monitoring | http://localhost:8222 | NATS HTTP monitoring |
| Natscope | http://localhost:4280 | Optional JetStream browser |
| PostgreSQL A | `localhost:5433` | Database `backend_a`, user `app`, password `app` |
| PostgreSQL B | `localhost:5434` | Database `backend_b`, user `app`, password `app` |

Local credentials are intentionally simple and are only for this lab.

## Start the system

Open separate terminals from the repository root.

### 1. Start infrastructure

```powershell
docker compose up -d
docker compose ps
```

This starts NATS, PostgreSQL A, and PostgreSQL B.

Start the optional Natscope UI as well:

```powershell
docker compose --profile tools up -d natscope
```

Natscope is a tooling service and is only started with the `tools` profile.

Important operational rule: **start NATS before the backends**. The JetStream initializer runs during backend startup and needs NATS to create the streams. Once a backend has started, its NATS client can reconnect after a later NATS outage.

### 2. Start Backend A

```powershell
dotnet run --project BackendA/NatsSyncLab.BackendA.AppHost --launch-profile http
```

### 3. Start Backend B

```powershell
dotnet run --project BackendB/NatsSyncLab.BackendB.AppHost --launch-profile http
```

Both backends apply their EF Core migrations at startup and ensure the JetStream streams exist. The streams are `A_EVENTS`, `B_EVENTS`, and `B_DEAD_LETTER`.

### 4. Start the Backend A UI

```powershell
cd frontend/backend-a
npm install
npm run dev
```

### 5. Start the Backend B UI

In another terminal:

```powershell
cd frontend/backend-b
npm install
npm run dev
```

If PowerShell blocks `npm.ps1`, use `npm.cmd` instead of `npm`, for example:

```powershell
npm.cmd install
npm.cmd run dev
```

The Vite dev server proxies `/api` and `/health` to the matching backend, so use `npm run dev` for this walkthrough. The Vite proxy is not configured for `npm run preview`.

## Quick health check

```powershell
curl.exe http://localhost:5001/health
curl.exe http://localhost:5002/health
curl.exe http://localhost:5001/api/v1/products
curl.exe http://localhost:5002/api/v1/products
```

PowerShell aliases `curl` to `Invoke-WebRequest`; use `curl.exe` on Windows. On macOS/Linux, `curl` works normally.

## Build, test, and lint

Backend build and tests:

```powershell
dotnet build NatsSyncLab.slnx
dotnet test NatsSyncLab.slnx
```

Frontend checks:

```powershell
cd frontend/backend-a
npm run lint
npm run build

cd ../backend-b
npm run lint
npm run build
```

## Inspect NATS

### Natscope

1. Open http://localhost:4280.
2. Add a NATS connection using:

   ```text
   nats://nats:4222
   ```

Natscope runs inside Docker, so it reaches NATS through the Compose service name. If you run Natscope directly on the host instead, use `nats://localhost:4222`.

In Natscope, check:

- Streams: `A_EVENTS`, `B_EVENTS`, `B_DEAD_LETTER`
- Consumer: `b-products-replica` on `A_EVENTS`
- Consumer state: delivered, acknowledged, pending, and redelivery counts
- Dead letters: failed product events on `B_DEAD_LETTER`

### NATS CLI

The `nats-cli` service uses the `nats-box` image and is started with the `tools` profile:

```powershell
docker compose --profile tools run --rm nats-cli stream ls
docker compose --profile tools run --rm nats-cli stream info A_EVENTS
docker compose --profile tools run --rm nats-cli stream info B_DEAD_LETTER
docker compose --profile tools run --rm nats-cli consumer ls A_EVENTS
docker compose --profile tools run --rm nats-cli consumer info A_EVENTS b-products-replica
docker compose --profile tools run --rm nats-cli stream view A_EVENTS --last 5
docker compose --profile tools run --rm nats-cli stream view B_DEAD_LETTER --last 10
```

Useful things to verify:

- Each product write adds a message to `A_EVENTS`.
- The subject is `a.events.product.upserted` or `a.events.product.deleted`.
- The consumer `b-products-replica` acknowledges messages after B commits.
- Pending messages drop as Backend B catches up.
- Invalid events and exhausted retries appear on `b.deadletter.product.failed` in `B_DEAD_LETTER`.
- The dead-letter payload includes the failure reason, delivery count, original stream sequence, raw payload, and exception details.

### NATS monitoring

```powershell
curl.exe "http://localhost:8222/jsz?streams=true&consumers=true"
```

This returns JetStream status as JSON, including stream and consumer state.

## Inspect PostgreSQL

### Backend A owner database

```powershell
docker compose exec postgres-a psql -U app -d backend_a -c "SELECT id, name, price, version, is_deleted, updated_at FROM products.products ORDER BY updated_at DESC;"
docker compose exec postgres-a psql -U app -d backend_a -c "SELECT id, event_id, subject, created_at, published_at FROM products.outbox ORDER BY id DESC LIMIT 10;"
```

An outbox row with `published_at` set to `NULL` has not been published yet.

### Backend B replica database

```powershell
docker compose exec postgres-b psql -U app -d backend_b -c "SELECT id, name, price, version, is_deleted, updated_at, synced_at FROM products_replica.products_replica ORDER BY synced_at DESC;"
docker compose exec postgres-b psql -U app -d backend_b -c "SELECT event_id, stream_sequence, received_at FROM products_replica.inbox ORDER BY received_at DESC LIMIT 10;"
```

The inbox table is the idempotency record. A duplicate event ID is ignored because the inbox primary key conflicts.

Each backend also runs cleanup once at startup:

- Backend A deletes published outbox rows older than 7 days.
- Backend B deletes inbox rows older than 8 days.
- Unpublished outbox rows are never deleted.

Cleanup is startup-only for this lab. Restart the backend to trigger it; production should use a timed job or periodic hosted service. Retention is configured under `Outbox` in Backend A's `appsettings.json` and `Inbox` in Backend B's `appsettings.json`.

## Inspect metrics

Both backends emit a meter named `NatsSyncLab`.

```powershell
dotnet-counters ps
dotnet-counters monitor --process-id <pid> NatsSyncLab
```

Key instruments:

- `outbox.published`, `outbox.publish_failures`, `outbox.cleanup_deleted`, `outbox.backlog`
- `consumer.processed`, `consumer.duplicates`, `consumer.stale_versions`, `consumer.nak`, `consumer.dead_lettered`, `consumer.terminated`, `consumer.errors`
- `consumer.processing.duration_ms`, `outbox.batch.duration_ms`, `consumer.last_delivery_count`, `inbox.lag_seconds`

No exporter is configured. The meter is visible only from inside the running process unless an OpenTelemetry or Prometheus exporter is added.

## Logs and shutdown

Run infrastructure logs:

```powershell
docker compose logs -f nats
docker compose logs -f postgres-a
docker compose logs -f postgres-b
```

Backend logs appear in the terminals where the backends are running.

Stop all Compose services, including optional tools:

```powershell
docker compose --profile tools down
```

This preserves the named volumes. To remove the databases, NATS data, and Natscope data as well:

```powershell
docker compose --profile tools down -v
```

The `-v` command is destructive. Use it only when you want a clean slate.

## Troubleshooting

### `npm.ps1` cannot be loaded

PowerShell may block the npm PowerShell script. Use `npm.cmd`:

```powershell
npm.cmd run dev
```

Alternatively, run the command through `cmd`:

```powershell
cmd /c npm run dev
```

### `curl` returns a PowerShell object instead of the response body

Use `curl.exe` explicitly on Windows.

### Frontend cannot reach the API

- Confirm Backend A is listening on port `5001`.
- Confirm Backend B is listening on port `5002`.
- Run the UI with `npm run dev` so the Vite proxy is active.
- Do not open the frontend as a `file://` URL.

### Natscope cannot connect

When Natscope runs in Compose, use `nats://nats:4222`. When it runs on the host, use `nats://localhost:4222`.

### A port is already in use

Check the port on Windows:

```powershell
netstat -ano | findstr :5001
netstat -ano | findstr :5173
```

Stop the process using the port or change the relevant launch profile or Vite configuration.

### Streams are missing

Start Backend A and Backend B. Both hosts ensure the JetStream streams, including `B_DEAD_LETTER`, at startup. If NATS data was deleted, restart the backends after NATS is healthy.

### Database migrations fail at startup

Check that PostgreSQL is healthy:

```powershell
docker compose ps
docker compose logs postgres-a
docker compose logs postgres-b
```

Also confirm the connection strings in `BackendA/NatsSyncLab.BackendA.AppHost/appsettings.json` and `BackendB/NatsSyncLab.BackendB.AppHost/appsettings.json` match the Compose ports.
