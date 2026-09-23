# ADR 0001 — SignalR rather than raw WebSockets

**Status:** Accepted · **Date:** 2026-09-23

## Context

The dashboard needs server-pushed updates, and the brief allows either
`Microsoft.AspNetCore.WebSockets` directly or a library such as SignalR. It also sets a hard
requirement: the implementation must handle many concurrent connections thread-safely.

## Decision

Use SignalR, with a strongly typed hub (`Hub<ITransactionsClient>`).

## Why

**Raw WebSockets means re-implementing a list of solved problems.** A production WebSocket
endpoint is not `AcceptWebSocketAsync` and a loop. It is a connection registry that is safe under
concurrent add, remove and broadcast; per-connection send serialisation, because concurrent writes
to one `WebSocket` throw; keep-alive pings and timeout detection; close-handshake handling; and a
client-side reconnect with backoff. Each of those is a place to introduce exactly the race the
brief asks us to avoid. SignalR's `HubLifetimeManager` already does all of it and is explicitly
documented as safe to invoke from any thread — which is why `TransactionProjectionService` can
broadcast from the event bus with no locking of its own.

**The typed hub removes a class of silent failure.** `Clients.All.TransactionUpserted(dto)`
against `ITransactionsClient` is compiler-checked. The stringly-typed alternative,
`Clients.All.SendAsync("TransactionUpserted", dto)`, fails by simply not arriving when someone
renames one side.

**Snapshot-on-connect needs message ordering.** A new dashboard needs current state before the
live stream. Fetching a snapshot over REST and then subscribing leaves a gap in which updates are
lost. SignalR guarantees ordering within a connection, so sending the snapshot from
`OnConnectedAsync` closes that gap for free (`ITransactionsClient.Snapshot`).

## Trade-offs accepted

- **Protocol lock-in.** Consumers must speak the SignalR protocol. Acceptable: the only consumer
  is our own dashboard. A future third-party integration would get a plain REST or webhook
  contract rather than a hub.
- **Framing overhead.** SignalR's envelope is larger than a bare JSON frame. Irrelevant at the
  scale of a support-desk dashboard.
- **A negotiate round trip by default,** which interacts badly with load balancing. Addressed
  in [ADR 0003](0003-multi-replica-synchronisation.md) and in `k8s/service.yaml`.

## What was explicitly not used

**The SignalR Redis backplane** (`AddStackExchangeRedis`). This is the obvious answer to
multi-replica fan-out and it solves only half the problem — see
[ADR 0003](0003-multi-replica-synchronisation.md), which is the more interesting decision.
