# ADR 0002 — A bounded in-memory window, not SQLite

**Status:** Accepted · **Date:** 2026-09-23

## Context

The brief allows RAM or SQLite for "the latest transactions", and requires consistency under
concurrent reads and writes.

## Decision

A bounded in-memory window — `InMemoryTransactionStore`, default capacity 500 — behind
`ITransactionStore`. Concurrency is handled by a single lock over a `Dictionary` plus a
`LinkedList`.

## Why in memory

The phrase in the requirements is "the **latest** transactions". That is a ring buffer, not a
database. The dashboard's only query is "the most recent N, newest first, optionally filtered by
status", and it never asks about anything older than the window. SQLite would add a file, a
schema, migrations and a query layer to serve a question that a capped list answers in O(1), and
it would still need the same concurrency discipline on top.

The bound is what makes memory usage flat: a replica running for a month under sustained
ingestion holds 500 transactions, not a month of them.

`ITransactionStore` is three members wide, so swapping in SQLite or Postgres when the
requirements grow an "audit history" is a contained change.

## Why one lock, not `ConcurrentDictionary`

This is the part worth defending, because `ConcurrentDictionary` looks like the obvious answer.

`Apply` must do four things as one indivisible step:

1. read the currently stored transaction for the id,
2. run `TransactionMergePolicy` against it,
3. move the entry to the front of the recency order,
4. evict the tail if the window overflowed.

`ConcurrentDictionary` provides atomicity **per key**. The invariant that actually needs
protecting is that the map and the recency list agree with each other — a cross-structure
invariant it cannot express. Composing lock-free operations across two structures would require
a compare-and-retry loop that is strictly more complex, more likely to be subtly wrong, and no
faster in practice: the critical section here is a handful of pointer writes with no I/O, no
callbacks and no allocation beyond one list node.

Reads take the same lock and copy into a fresh array. Combined with `Transaction` being an
immutable record, a caller can hold and enumerate a snapshot indefinitely while writers carry on
— no torn reads, and no `InvalidOperationException` from a collection mutating mid-enumeration.
`TransactionsHub.OnConnectedAsync` relies on exactly this.

## Ordering

The window is ordered by **recency of update**, and eviction takes from the tail. So a long-lived
`Pending` transaction that is still receiving updates does not fall out of the window just
because it started a while ago (`Spares_a_transaction_from_eviction_when_it_is_updated`).

The dashboard does **not** re-sort on update: a row that changes status stays where it is. A row
that jumps to the top pulls the reader's place out from under them, and a stable position is also
what makes the status transition animation legible.

## Consequences

- Nothing survives a full cluster restart. Accepted: this is a live monitor, not the system of
  record. The upstream payment system holds the ledger.
- Each replica's window is a materialised view of the shared log rather than independent state —
  see [ADR 0003](0003-multi-replica-synchronisation.md).
- `GetLatest` is O(n) in the window size under the lock. At n ≤ 500 this is microseconds; it
  would need revisiting long before the capacity reached five figures.
