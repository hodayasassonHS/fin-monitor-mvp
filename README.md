# Real-Time Financial Monitor

MVP that accepts transactions over HTTP and pushes them to a live dashboard.

- Backend: .NET 9, minimal APIs, SignalR
- Frontend: React 19, TypeScript, Vite
- Storage: in-memory
- Tests: 126 backend

## Running it

```bash
# terminal 1 — API on http://localhost:5080
dotnet run --project backend/src/FinMonitor.Api

# terminal 2 — dashboard on http://localhost:5173
npm --prefix frontend install
npm --prefix frontend run dev
```

Open `/add` to send transactions and `/monitor` to watch them arrive live.

```bash
dotnet test --project backend
```

## API

| Method | Path | Behaviour |
| --- | --- | --- |
| `POST` | `/api/transactions` | Create a transaction, or update the status of an existing one |
| `GET` | `/api/transactions?status=&limit=` | Latest transactions |
| `GET` | `/api/transactions/{id}` | One transaction |
| WS | `/hubs/transactions` | Live feed — snapshot on connect, then one push per change |

```jsonc
{
  "transactionId": "6f1d8b6a-6b0f-4f27-8f6f-2a3c4d5e6f70",
  "amount": 1500.50,
  "currency": "USD",
  "status": "Pending",
  "timestamp": "2024-01-15T10:00:00Z"
}
```

`202` = accepted. `200` = already recorded, unchanged. `400` = validation failed. `409` =
conflicts with the recorded transaction (amount/currency changed, or it's already in a final
state).

## Multi-replica

Running behind several replicas, a client connected to one pod won't see a transaction sent to
another unless the pods share state. Here, ingestion never writes to a replica's own store
directly — it publishes to a Redis Stream, and every replica (including the one that received
the request) applies events from that stream the same way. With no Redis configured, it falls
back to an in-process bus and runs as a single replica.

Reasoning and alternatives considered: [ADR 0003](docs/adr/0003-multi-replica-synchronisation.md).

To see two replicas actually syncing through Redis:

```bash
docker compose up --build     # http://localhost:8080
```

## Deployment

Dockerfile for both backend and frontend, and Kubernetes manifests in `k8s/`
(`deployment.yaml`, `service.yaml`, plus supporting resources).

```bash
kubectl apply -f k8s/
```
