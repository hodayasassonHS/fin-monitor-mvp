# Real-Time Financial Monitor

An MVP that ingests transactions over HTTP and pushes them to a live support dashboard, built to
run correctly on one replica or on five.

- **Backend** — .NET 9, minimal APIs, SignalR
- **Frontend** — React 19, TypeScript, Vite
- **Distribution** — Redis Streams as a shared, replayable event log
- **Tests** — 126 backend, 39 frontend, all passing

---

## Running it

### Locally, with no infrastructure at all

```bash
# terminal 1 — API on http://localhost:5080
dotnet run --project backend/src/FinMonitor.Api

# terminal 2 — dashboard on http://localhost:5173
npm --prefix frontend install
npm --prefix frontend run dev
```

Open **http://localhost:5173/add** to generate traffic and **http://localhost:5173/monitor** to
watch it arrive. Two browser tabs side by side is the best way to see it work.

No Redis, no Docker, no configuration. With no Redis connection string the service uses an
in-process event bus over the identical code path — see
[ADR 0003](docs/adr/0003-multi-replica-synchronisation.md).

### The multi-replica demonstration

```bash
docker compose up --build     # http://localhost:8080
```

Two independent API replicas behind nginx, sharing nothing but Redis. Post from the simulator —
it lands on either replica — and watch it appear on a dashboard whose WebSocket is held by the
other one. Check the `X-Instance` response header to see which replica served you. Stop a
replica, generate traffic, start it again: it replays the retained stream and catches up.

Delete `Redis__ConnectionString` from one replica and it silently stops seeing the other's
traffic. That is the failure this architecture exists to prevent.

### Tests

```bash
dotnet test --project backend                # 126 tests
npm --prefix frontend run test               # 39 tests
npm --prefix frontend run typecheck
```

---

## The API

| Method | Path | Behaviour |
| --- | --- | --- |
| `POST` | `/api/transactions` | Ingest, or update the status of a known transaction |
| `GET` | `/api/transactions?status=&limit=` | Latest transactions, newest first |
| `GET` | `/api/transactions/{id}` | One transaction |
| `GET` | `/health/live` `/health/ready` | Kubernetes probes |
| WS | `/hubs/transactions` | Live feed |

```jsonc
// POST /api/transactions
{
  "transactionId": "6f1d8b6a-6b0f-4f27-8f6f-2a3c4d5e6f70",
  "amount": 1500.50,
  "currency": "USD",
  "status": "Pending",        // Pending | Completed | Failed
  "timestamp": "2024-01-15T10:00:00Z"
}
```

**Status codes carry meaning:**

| Code | Meaning |
| --- | --- |
| `202 Accepted` | Published to the cluster. Not `201` — see *Eventual consistency* below |
| `200 OK` | Already recorded with exactly this state. A retry is a success, not an error |
| `400 Bad Request` | Malformed. Reports **every** bad field at once, not just the first |
| `409 Conflict` | Contradicts what is recorded, with a machine-readable `reason` |

The hub sends `Snapshot` once on connect, then `TransactionUpserted` per change. The snapshot
travels over the hub rather than a separate REST call so that nothing can slip into the gap
between fetching state and subscribing to it.

---

## Architecture

```
  Simulator  ──POST──▶  Ingestion  ──▶  validate  ──▶  speculative merge (409 fast path)
   (/add)                                                        │
                                                                 ▼
                                                    ITransactionEventBus
                                              in-process  │  Redis Stream
                                                          ▼
                                             TransactionProjectionService
                                              (the ONLY writer to the store)
                                                    │           │
                                              store.Apply   hub broadcast
                                                                ▼
  Dashboard  ◀────────────── WebSocket ─────────────────  connected clients
   (/monitor)
```

**The decision that shapes everything else:** ingestion never writes to the store. It publishes,
and a subscriber running on *every* replica applies the event. The replica that took the request
is not special-cased, so the local path and the distributed path are the same code — which is why
the distributed design is exercised by every test that posts a transaction.

### Project layout

```
backend/src/FinMonitor.Core     domain, store, merge policy, ingestion — no framework deps
backend/src/FinMonitor.Redis    the distributed event bus
backend/src/FinMonitor.Api      endpoints, SignalR hub, projection, health
frontend/src/realtime           the batching store and the feed hook
frontend/src/routes             /add and /monitor
docs/adr                        the four decisions worth arguing about
k8s                             manifests
```

### The four decisions

| ADR | Decision | The short version |
| --- | --- | --- |
| [0001](docs/adr/0001-realtime-transport.md) | SignalR over raw WebSockets | Connection registry, per-connection send serialisation, keep-alives and reconnect are solved problems |
| [0002](docs/adr/0002-storage.md) | Bounded memory, one lock | "Latest N" is a ring buffer, not a database; the invariant spans two structures, which `ConcurrentDictionary` cannot express |
| [0003](docs/adr/0003-multi-replica-synchronisation.md) | **Redis Streams, not the SignalR backplane** | The backplane fixes live pushes and leaves every replica's *store* wrong |
| [0004](docs/adr/0004-frontend-performance.md) | Frame-batched rendering | 100 arrivals cost one render, not a hundred |

---

## Multi-replica synchronisation — the short version

Full reasoning in [ADR 0003](docs/adr/0003-multi-replica-synchronisation.md). The essentials:

**The problem is not message delivery, it is state convergence.** If a client connected to
replica B never sees a transaction ingested by replica A, that is the visible half. The silent
half is that B's *store* never saw it either — so a reconnect serves a snapshot with holes in it,
and two agents looking at the same screen see different worlds.

**This is why the SignalR Redis backplane was rejected.** It fans out hub messages and does
nothing for the store, producing a bug that only appears after a disconnect.

**Plain Redis Pub/Sub was rejected too**, for the reason the backplane fails in a different way:
it has no history. A replica that restarts — routine in Kubernetes — misses everything it was
down for, permanently.

**Kafka was rejected on operational cost, not correctness.** It is the right shape, and it would
be the largest thing in the deployment by an order of magnitude to hold 500 records for a minute.
If a fraud engine and a warehouse feed appear, that flips — and `ITransactionEventBus` is the only
seam that changes.

**Redis Streams**: durable, ordered, capped at exactly the store's retention window, replayable
from `0-0` so a starting replica rebuilds its view before serving anyone. A contentless pub/sub
"doorbell" wakes consumers so following the stream does not mean aggressive polling; losing a
doorbell costs latency, never data.

**Convergence comes from the merge policy being pure, idempotent and monotonic** — replay is
safe, stale events lose, terminal statuses are final. The stream's total order handles the one
rule that is not commutative.

---

## Things I decided, that you may want to decide differently

These were ambiguous in the brief. Each is a deliberate call, not an oversight.

1. **`transactionId` is client-supplied** and used as the idempotency key. The sample payload
   includes it, so the client owns identity.
2. **`POST` is an upsert.** "Smooth transitions between status changes" implies a transaction is
   updated after ingestion, so re-posting a known id is a status update.
3. **Amount and currency are immutable** once recorded. A payload disagreeing with a recorded
   financial figure is a bug or an attack, not an update — `409`.
4. **`Completed` and `Failed` are terminal.** Moving out of them is `409`.
5. **Amount must be positive.** Refunds and chargebacks belong in a real ledger as their own
   transactions, not as negative amounts.
6. **`decimal`, never `double`.** It is money.
7. **No authentication.** Out of scope for an MVP, and the first thing to add — see below.

## Eventual consistency, stated plainly

On a single replica the in-process bus dispatches synchronously, so a `GET` after a `POST`
returns always sees the transaction.

**In distributed mode it does not.** `202` returns once the transaction is durably in the stream,
before any replica has applied it — typically under a millisecond, but the guarantee is genuinely
weaker. `202 Accepted` rather than `201 Created` is the API being honest about this.

Relatedly, the `409` conflict check is **speculative**: it runs the merge policy against the
local view, which can be a heartbeat stale. A conflict another replica already recorded may be
answered `202` and then rejected harmlessly on apply. The cluster converges either way; only the
status code is best-effort. The alternative — a synchronous cluster-wide read per ingest — would
trade away the system's main virtue.

---

## Deployment

```bash
kubectl apply -f k8s/
```

`namespace` · `configmap` (+ Secret) · `deployment` (5 replicas) · `service` (+ PodDisruptionBudget) ·
`redis` · `dashboard` · `ingress` · `hpa`

Details worth pointing at:

- **`sessionAffinity: ClientIP` on the Service.** SignalR's default handshake is two HTTP
  requests — `/negotiate` then the transport — and a round-robin Service can route them to
  different replicas, breaking the handshake intermittently and unreproducibly. The dashboard
  avoids this entirely with `skipNegotiation: true` over WebSockets; affinity is kept as a second
  line of defence. **It is not how consistency is achieved** — that is the stream's job.
- **`terminationGracePeriodSeconds: 60` and a `preStop` sleep.** Every pod termination severs
  live WebSockets; the pause lets endpoint removal propagate before the container stops, avoiding
  a burst of failed requests on every rollout.
- **`maxUnavailable: 0`.** A rollout is already a reconnect storm; going under-provisioned at the
  same time piles it onto fewer pods.
- **Liveness does not check Redis.** A replica that lost the stream is stale, not broken.
  Restarting it discards warm state and fixes nothing. Readiness reports *degraded*, not
  unhealthy, for the same reason — failing readiness would pull every replica out of the load
  balancer and turn a partial outage into a total one.
- **No CPU limit.** Throttling at a quota boundary causes exactly the stutter a real-time
  dashboard exists to avoid. The request reserves a floor; the memory limit protects the node.
- **`readOnlyRootFilesystem`** with an `emptyDir` at `/tmp`, non-root, all capabilities dropped,
  and the namespace labelled `pod-security: restricted` so a future manifest cannot regress it.

### Images

Multi-stage, layer-cached on the manifests so a source edit does not re-restore packages.
Non-root. Alpine runtime: roughly **110 MB** for the API against ~220 MB for the Debian default,
and ~50 MB for the dashboard.

Trimming and Native AOT were considered and rejected: SignalR's hub dispatch and
`System.Text.Json`'s reflection-based serialisation are not reliably trim-safe without moving the
contract onto source generators. That is worthwhile work and the wrong thing to have silently in
an MVP — the failure mode is a missing member at runtime, in production, rather than a build
error.

---

## Testing

**Backend — 126 tests.** Written against the domain rules before the API layer existed.

- `TransactionMergePolicyTests` — every transition rule, plus the convergence property the
  distributed design rests on
- `InMemoryTransactionStoreConcurrencyTests` — barrier-released threads hammering the store:
  exactly one writer wins for a contended id, capacity is never exceeded, snapshots stay
  internally consistent while writes are in flight, and last-write-wins settles on the same
  final state whichever thread wins
- `TransactionApiTests` / `TransactionHubTests` — the real application under
  `WebApplicationFactory`, with a real SignalR client over the test server's WebSocket
  transport: snapshot on connect, live push, status change, five simultaneous dashboards, a
  100-transaction burst, and *silence* where silence is correct (retries and rejected updates
  must not produce a broadcast)
- `TransactionStreamEntryTests` — the cross-replica wire format, including that a replica under
  a `de-DE` locale writes amounts its peers can read

**Frontend — 39 tests.** The batching store driven by an injected scheduler, the hidden-tab
flush, object-identity preservation, the load generator's concurrency bound, and component
rendering.

### What was verified by running it, and what was not

The full stack was exercised end to end against a live backend and browser: snapshot on connect,
live push, in-place status transition, `409` on an illegal transition, a 100-transaction burst
(**221 ms**, all accepted), bulk settlement of 50 pending transactions, and status filtering —
all confirmed by inspecting the rendered DOM.

**Not executed:** `docker build` and `docker compose up` — the Docker daemon was unavailable in
this environment. The Kubernetes manifests parse and produce the expected resources, but have not
been applied to a cluster. Treat both as reviewed-but-unrun.

---

## Where I would go next

In order:

1. **Authentication and authorisation** on both the ingestion endpoint and the hub. The single
   largest gap — right now anyone who can reach the service can write transactions.
2. **Scale on connections, not CPU.** This workload is bounded by concurrent sockets; a replica
   can hold thousands of idle connections at negligible CPU. The HPA's CPU target is a
   placeholder for a custom metric exported from SignalR.
3. **Rate limiting** on ingestion. `AddRateLimiter` with a per-client partition.
4. **OpenTelemetry** traces spanning ingest → stream → apply → broadcast. Debugging a
   convergence problem across five replicas without distributed tracing is guesswork.
5. **Windowed virtualisation** in the table, when the window grows past ~1000 rows.
6. **A `System.Text.Json` source-generated context**, which would unlock trimming and roughly
   halve the image again.
