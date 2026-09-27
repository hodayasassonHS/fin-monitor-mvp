# ADR 0003 — Multi-replica synchronisation via a Redis Stream

**Status:** Accepted

## The problem

Five replicas behind one Service. A transaction is `POST`ed to replica A. A client connected to
replica B never sees it — and replica B's own store never learned about it either, so even a
reconnect won't fix it. Every replica's store needs to converge on the same state, including
replicas that just restarted or scaled up.

## Decision

A Redis Stream is the shared, ordered, capped log of transactions. Every replica publishes to it
and every replica consumes all of it, including its own writes.

```
                    POST /api/transactions
                              │
                    ┌─────────▼─────────┐
                    │  replica A        │   validate → merge check → publish
                    └─────────┬─────────┘   (never writes its own store directly)
                              │ XADD
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

Ingestion never writes to a replica's own store — it publishes, and a background subscriber
applies the event on every replica, including the one that took the request. Same code path
whether there's one replica or five. With no Redis configured, the same interface runs on an
in-process bus instead, as a single replica.

## Why Redis Streams, not Kafka

A durable partitioned log is the right shape here, and Kafka is the obvious option in that shape.
Rejected on cost: this is a 500-item window feeding one consumer, not multiple downstreams
needing independent replay — Kafka would be the most expensive thing in the deployment to hold
500 records for about a minute.

Redis Streams gives what's actually needed: durable and replayable (a starting replica rebuilds
its full window from the beginning of the stream), capped to match the store's own capacity, and
ordered — which matters because a transaction can't leave a final status once reached, and that
rule depends on all replicas seeing events in the same order.

Verified by running two replicas behind nginx, sharing only Redis: a transaction posted to one
was confirmed present on the other by querying it directly, not through the client.
