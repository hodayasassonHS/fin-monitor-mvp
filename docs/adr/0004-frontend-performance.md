# ADR 0004 — Frame-batched updates in the dashboard

**Status:** Accepted · **Date:** 2026-09-23

## Context

The brief: *"If 100 transactions arrive quickly, the browser should not freeze."*

A hundred transactions arrive as a hundred separate SignalR callbacks. The naive implementation
calls `setState` in each one, which asks React for a hundred renders of a list a human will see
exactly once. React 18+ batches state updates within a single event-loop turn, but these arrive
in *separate* turns — one per WebSocket frame — so automatic batching does not apply.

## Decision

Inbound transactions land in a buffer outside React. A flush is scheduled on the next animation
frame, and the store notifies subscribers once. React reads it through `useSyncExternalStore`.

## The three parts, which only work together

**1. Buffer and flush once per frame** (`createTransactionStore`). A burst of any size costs one
render, naturally paced to the display rather than to the network.

**2. Preserve object identity for unchanged rows.** On flush, the store reuses the existing entry
object for every row that did not change. This is what makes `React.memo` on `TransactionRow`
effective — it compares `previous.entry === next.entry`. Without it, one arriving transaction
would re-render all 500 visible rows and the batching would buy far less than it appears to.

**3. Never animate a layout property.** `opacity` and `transform` are composited and cost nothing
on the main thread. `background-color` is paint-only and cheap here because only changed rows
animate. Nothing that triggers layout — `height`, `margin`, `top` — is animated at all.

Dropping any one of these undoes most of the benefit of the others.

## Why `useSyncExternalStore` rather than `useState`

The batching has to happen *outside* React's control — the whole point is deciding when React is
allowed to hear about a change. `useSyncExternalStore` is the supported way to do that, and it
keeps the store as plain testable logic with no React in it. `transactionStore.test.ts` drives
batching with an injected scheduler and never renders a component.

## Why the Web Animations API rather than CSS classes

A CSS animation does not replay because a prop changed. Retriggering means removing the class,
forcing a reflow to flush style recalculation, and re-adding it — fragile, and a guaranteed
layout thrash on every update. `element.animate()` starts a fresh animation per call, so
re-running the effect is sufficient, and the returned handle makes cancel-on-unmount trivial.

The tint comes from `getComputedStyle(...).getPropertyValue('--status-tint')`, so status colours
live only in CSS and the flash always matches the badge beside it.

## The bug this found

During end-to-end verification the dashboard reported **Live** but rendered nothing. The cause
was not the feed: `document.hidden` was `true`, and **browsers stop delivering animation frames
to hidden tabs**, so the flush was never scheduled.

This is a real defect, not a test artefact. A dashboard spends most of its life in a background
tab on someone's second monitor, where `requestAnimationFrame` alone would let the buffer grow
unbounded for hours — a memory leak — and then dump the lot in one flush on refocus.

The scheduler now races `requestAnimationFrame` against a 250 ms `setTimeout`, first one wins.
A visible tab still gets frame-aligned batching because rAF always wins that race; a hidden tab
gets a bounded buffer. Covered by
`flushes even when the tab is hidden and no animation frames arrive` and
`lets the animation frame win on a visible tab`.

## Consequences

- Up to one frame (~16 ms) of added latency on a visible tab. Imperceptible, and the batching it
  buys is what keeps the tab responsive under load.
- Up to 250 ms when hidden, where nobody is looking.
- The store caps rows at the server's window (500). Beyond roughly a thousand rows the right
  answer is windowed virtualisation; a hard cap is the honest MVP answer and needs no dependency.
- Rows update in place and never re-sort, so a settling transaction does not move under the
  reader's cursor. See [ADR 0002](0002-storage.md).
