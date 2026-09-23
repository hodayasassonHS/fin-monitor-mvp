# ADR 0003 — Multi-replica synchronisation via a Redis Stream

**Status:** Accepted · **Date:** 2026-09-23

## The problem

Five replicas behind one Service. A transaction is `POST`ed to replica A. A support agent's
dashboard holds a WebSocket to replica B. The agent never sees it.

That is the visible half. The silent half is worse: replica B's in-memory window never learned
about the transaction either, so when the agent's connection drops and reconnects — to B, or to
C — the snapshot they are served is **missing data that the system has**. Two agents looking at
the same screen see different worlds, and neither has any way to tell.

So the requirement is not "deliver messages to all replicas". It is **every replica must converge
on the same state**, including replicas that were restarted, rescheduled, or scaled up five
minutes ago.

## Decision

A **Redis Stream** is the shared, ordered, capped log of transactions. Every replica appends to
it and every replica consumes all of it. A **pub/sub channel carries a contentless doorbell** so
consumers do not have to poll aggressively.

Each replica's in-memory store is a materialised view of that log, not independent state.

```
                    POST /api/transactions
                              │
                    ┌─────────▼─────────┐
                    │  replica A        │   validate → speculative merge → publish
                    └─────────┬─────────┘   (never writes its own store directly)
                              │ XADD (+ doorbell PUBLISH)
                    ┌─────────▼──────────────────────────┐
                    │   Redis Stream  MAXLEN ~ 500       │
                    └──┬──────────────┬──────────────┬───┘
                XREAD  │       XREAD  │       XREAD  │      every replica reads everything,
                       │              │              │      including its own writes
                 ┌─────▼────┐   ┌─────▼────┐   ┌─────▼────┐
                 │ replica A│   │ replica B│   │ replica C│
                 │  store   │   │  store   │   │  store   │  ← identical, by construction
                 │  + hub   │   │  + hub   │   │  + hub   │
                 └──────────┘   └──────────┘   └──────────┘
```

The single most important line in the implementation is the one that **isn't** there:
`TransactionIngestionService` never calls `ITransactionStore.Apply`. It publishes, and the
subscriber applies — on every replica, including the one that took the request. The local case
and the remote case are the same code path, which is why the distributed behaviour is exercised
by every test that posts a transaction, and why the two cannot drift apart.

## Alternatives considered

### Sticky sessions alone

Pin each client to a replica with `sessionAffinity: ClientIP`. This is not a solution — it is the
problem wearing a hat. The data is still siloed; sticky routing just makes each agent
consistently wrong instead of intermittently wrong. (Affinity *is* configured, for the unrelated
SignalR negotiate reason below.)

### SignalR's Redis backplane

`AddStackExchangeRedis()`. One line, and it is what most implementations reach for.

**Rejected, because it solves the visible half only.** The backplane fans out *hub messages*. It
does nothing about replica B's store, which still never saw the transaction. Live updates would
work, and then a reconnect would serve a snapshot with holes in it — a bug that appears only
after a disconnect, which is the hardest kind to catch in testing and the easiest to dismiss as
"the agent must have missed it".

It would also mean **two** delivery mechanisms: a backplane for the pushes, and something else to
replicate state. Two mechanisms means two failure modes and a consistency question between them.
Publishing domain events once, and deriving both the store and the pushes from that single
stream, is strictly simpler.

### Plain Redis Pub/Sub

Publish the transaction itself on a channel; each replica applies what it receives.

This is the answer the brief offers first, and it very nearly works. **Rejected because pub/sub
has no history.** It is fire-and-forget and at-most-once: a replica that is down, rolling, or
being scheduled receives nothing, and there is no way to ask what it missed. Its window is then
permanently thinner than its peers' — not stale, *wrong*, for the life of the pod. Since pods
restart constantly in Kubernetes, this is a routine occurrence, not an edge case.

### Kafka

Correct in shape — a durable partitioned log is exactly the right abstraction, and the design
here is a small Kafka in spirit.

**Rejected on cost, not on correctness.** Kafka earns its operational weight when you need long
retention, replay from arbitrary offsets, consumer groups with independent lag, exactly-once
semantics, or several unrelated downstreams. This is a support dashboard with a 500-item window
and one consumer shape. Kafka would be the largest and most expensive thing in the deployment by
an order of magnitude, to hold 500 records for about a minute.

If this grew a fraud engine, a reconciliation job and a data warehouse feed off the same stream,
that calculus flips — and the migration is contained, because `ITransactionEventBus` is the only
seam that would change.

### Redis Streams — chosen

`XADD` with `MAXLEN ~ 500`, and consumers reading from the start of the retained window.

- **Durable and replayable.** A starting replica reads from `0-0` and rebuilds its entire window
  before serving anyone. Restarts, rollouts and scale-ups converge automatically.
- **The retention bound is the right shape.** `MAXLEN` matches the store's capacity exactly. What
  a replica can recover and what a replica keeps are the same window, by construction — see the
  comment on `TransactionStoreOptions.Capacity`.
- **Ordered.** One stream gives a single total order, which matters for the one merge rule that
  is not commutative (see below).
- **Operationally trivial** next to Kafka, and Redis is already a reasonable thing to have.

**No consumer groups.** A consumer group hands each entry to exactly one member — right for
sharing work, exactly wrong for sharing state. Every replica needs its own complete copy.

### Why pub/sub is *also* present

StackExchange.Redis deliberately does not expose blocking `XREAD`; a blocking command would
occupy the shared multiplexer. Following a stream therefore means polling, and polling trades
latency against load — a 1 s poll makes a "real-time" dashboard feel broken, and a 20 ms poll is
five replicas hammering Redis forever.

So each `XADD` is followed by a `PUBLISH` of an **empty** message. The consumer sleeps on the
doorbell and drains the stream when woken, with a 1 s safety poll as a backstop. Because the
notification carries no data, pub/sub's at-most-once delivery costs nothing: a lost doorbell
delays a batch by up to one second, and correctness still rests entirely on the stream.

## What makes convergence actually work

The transport is only half of it. Replicas converge because `TransactionMergePolicy` is a **pure
function** with two properties, both of which are directly tested:

- **Idempotent.** Re-applying an already-applied event is rejected as `Duplicate`. This is what
  makes full replay on startup safe, and what stops a restarting replica flooding its clients
  with updates for things they already have (`TransactionProjectionService` suppresses the
  broadcast for any merge that was not accepted).
- **Monotonic.** State never moves backwards in time (`StaleTimestamp`, last-write-wins on the
  observation timestamp) and never leaves a terminal status (`TerminalStatus`). Late or reordered
  delivery cannot corrupt a view.

Last-write-wins handles reordering for most cases. The terminal-status rule is *not* commutative
— `Completed` then `Failed` lands somewhere different from `Failed` then `Completed` — and this
is precisely why the transport being a **single totally ordered stream** matters rather than
being incidental. All replicas apply the same events in the same order, so they reach the same
answer. Plain pub/sub would not guarantee that even if it had history.

The concurrency test `Settles_on_the_same_final_state_whichever_thread_wins` demonstrates the
commutative half directly: threads racing to apply `Pending@t0` and `Completed@t1` in arbitrary
order always converge on `Completed`.

## Consequences accepted

**Eventual consistency, stated honestly.** In distributed mode, ingestion returns `202 Accepted`
before the transaction is in any replica's store. A `GET` immediately afterwards may not find
it — typically for under a millisecond, but the guarantee is genuinely weaker. `202` rather than
`201` is the API saying so. In single-replica mode the in-process bus dispatches synchronously,
so read-your-writes does hold; the README does not pretend these are the same.

**The `409` conflict check is speculative.** `TransactionIngestionService` runs the merge policy
against the *local* view to reject a bad write early. Across replicas that view can be a
heartbeat stale, so a conflict another replica already recorded may be missed and answered `202`
— then rejected on apply, harmlessly. The alternative is a synchronous cluster-wide read on every
ingest, which would trade the system's main virtue for a rarely-observable improvement in
promptness. The cluster converges regardless; only the HTTP status is best-effort.

**Redis is a single point of failure for *synchronisation*, not for *availability*.** With Redis
down, each replica keeps serving its own snapshot and accepting its own ingestion; they simply
stop learning about each other. The readiness check reports **degraded, not unhealthy**, on
purpose: failing readiness would pull every replica out of the load balancer at once and turn a
partial outage into a total one.

**Stream retention bounds recovery.** A replica down longer than it takes 500 transactions to
flow will not recover the ones that were trimmed. It converges on the current window, which is
all the dashboard ever displays.

**One ordering caveat.** A cluster-wide restart loses the stream if Redis is not persisted (it is
not, by choice — see `k8s/redis.yaml`). The system of record is upstream; this is a live view.

## Verified

The single-replica path is covered end to end by the integration tests
(`TransactionHubTests`) — ingestion through the bus, into the store, out over a real SignalR
WebSocket to connected clients, including a 100-transaction burst and five simultaneous
dashboards.

The stream serialisation contract between replicas is covered by `TransactionStreamEntryTests`,
including culture-independence — a replica running under a `de-DE` host writing an amount its
peers cannot read is the classic way a distributed system quietly breaks.

`docker-compose.yml` runs the real two-replica topology against real Redis. **This has not been
executed** — the Docker daemon was unavailable in the environment where this was built — so the
cross-replica behaviour is verified by design and by unit coverage of its parts, not by having
watched it happen.
