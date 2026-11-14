# NATS JetStream Sync Lab

A local .NET 10 and React lab showing how a write-side service can replicate data to a read-side service through NATS JetStream.

Backend A owns products. Every product write commits to PostgreSQL together with an outbox event. A background relay publishes that event to JetStream, and Backend B applies it to its own replica database through an idempotent consumer.

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

## What this lab demonstrates

- Transactional outbox: the product change and its integration event commit atomically.
- At-least-once delivery through NATS JetStream.
- Inbox de-duplication in Backend B so a redelivered event does not duplicate side effects.
- Version-guarded replica upserts so an older event cannot overwrite a newer replica row.
- Soft deletion: a deleted product is retained in both databases and marked as deleted.
- Independent read models: Backend B is read-only and exposes its own HTTP API and UI.

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

### 2. Start Backend A

```powershell
dotnet run --project BackendA/NatsSyncLab.BackendA.AppHost --launch-profile http
```

### 3. Start Backend B

```powershell
dotnet run --project BackendB/NatsSyncLab.BackendB.AppHost --launch-profile http
```

Both backends apply their EF Core migrations at startup and ensure the JetStream streams exist. The streams are `A_EVENTS` and `B_EVENTS`.

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

## Manual test: create, update, delete

### 1. Create in Backend A

1. Open http://localhost:5173.
2. Enter a product name and price.
3. Select **Create product**.
4. Open http://localhost:5174.
5. Ensure **Auto refresh** is enabled.

Within a few seconds, Backend B should show the same product ID, name, price, and version. The **Updated in A** and **Synced to B** timestamps should be present.

### 2. Update in Backend A

1. In Backend A, select **Edit** for the product.
2. Change the name or price and save.
3. Watch Backend B.

The product's version should increase by one, and B's `syncedAt` time should update. For example, A version `1` becomes `2`.

### 3. Delete in Backend A

1. In Backend A, select **Delete** and confirm.
2. Watch Backend B.

The product is soft deleted. Both sides retain the row, but its status becomes `Deleted`.

### 4. Verify with the APIs

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

### 5. Optional: catch-up test

1. Stop Backend B with `Ctrl+C`.
2. Create or update a product in Backend A.
3. Restart Backend B:

   ```powershell
   dotnet run --project BackendB/NatsSyncLab.BackendB.AppHost --launch-profile http
   ```

4. Open Backend B and refresh.

The durable consumer resumes from its stored position and applies the missed event. The inbox prevents duplicate side effects if an event is delivered again.

## Inspect NATS

### Natscope

1. Open http://localhost:4280.
2. Add a NATS connection using:

   ```text
   nats://nats:4222
   ```

Natscope runs inside Docker, so it reaches NATS through the Compose service name. If you run Natscope directly on the host instead, use `nats://localhost:4222`.

In Natscope, check:

- Streams: `A_EVENTS`, `B_EVENTS`
- Consumer: `b-products-replica` on `A_EVENTS`
- Consumer state: delivered, acknowledged, pending, and redelivery counts

### NATS CLI

The `nats-cli` service uses the `nats-box` image and is started with the `tools` profile:

```powershell
docker compose --profile tools run --rm nats-cli stream ls
docker compose --profile tools run --rm nats-cli stream info A_EVENTS
docker compose --profile tools run --rm nats-cli consumer ls A_EVENTS
docker compose --profile tools run --rm nats-cli consumer info A_EVENTS b-products-replica
docker compose --profile tools run --rm nats-cli stream view A_EVENTS --last 5
```

Useful things to verify:

- Each product write adds a message to `A_EVENTS`.
- The subject is `a.events.product.upserted` or `a.events.product.deleted`.
- The consumer `b-products-replica` acknowledges messages after B commits.
- Pending messages drop as Backend B catches up.

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

## Optional resilience test: NATS outage

1. Stop NATS:

   ```powershell
   docker compose stop nats
   ```

2. Create a product in Backend A.

   The HTTP write should succeed because A writes to PostgreSQL first. The outbox relay will fail to publish while NATS is down and retry.

3. Confirm the pending outbox row:

   ```powershell
   docker compose exec postgres-a psql -U app -d backend_a -c "SELECT id, subject, published_at FROM products.outbox ORDER BY id DESC LIMIT 5;"
   ```

4. Start NATS again:

   ```powershell
   docker compose start nats
   ```

5. Wait a few seconds and rerun the outbox query.

`published_at` should become non-null. Backend B should receive the event once NATS is available again.

If you remove the NATS volume, restart both backends so the JetStream streams are recreated before continuing.

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

Start Backend A and Backend B. Both hosts ensure the JetStream streams at startup. If NATS data was deleted, restart the backends after NATS is healthy.

### Database migrations fail at startup

Check that PostgreSQL is healthy:

```powershell
docker compose ps
docker compose logs postgres-a
docker compose logs postgres-b
```

Also confirm the connection strings in `BackendA/NatsSyncLab.BackendA.AppHost/appsettings.json` and `BackendB/NatsSyncLab.BackendB.AppHost/appsettings.json` match the Compose ports.

## Project layout

```text
BackendA/                       Product owner service
BackendB/                       Product replica service
SharedKernel/                   Event envelope, outbox/inbox, NATS helpers
frontend/backend-a/             Backend A React UI
frontend/backend-b/             Backend B React UI
tests/                          Unit and architecture tests
infra/nats/                     NATS server configuration
docker-compose.yml              NATS, PostgreSQL, NATS CLI, Natscope
```

## Current scope and limitations

- Only Products are synchronized from Backend A to Backend B in this lab.
- The Customers modules are placeholders.
- Backend B is read-only for product data.
- There is no authentication or authorization.
- NATS and PostgreSQL run as single local nodes.
- This setup is for local development and learning, not production deployment.

## Further reading

- [ARCHITECTURE.md](./ARCHITECTURE.md) - broader modular-monolith architecture background
- [REPLICATION.md](./REPLICATION.md) - guide for scaffolding the architecture
- [nats.md](./nats.md) - NATS JetStream and idempotent-consumer notes
- [nats-jetstream-sync-plan.md](./nats-jetstream-sync-plan.md) - original sync implementation plan
