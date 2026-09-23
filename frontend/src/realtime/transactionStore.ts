import { isSameState, type Transaction } from '../domain/transaction.ts';

/**
 * Why a row looks the way it does, which is what the animation layer keys off.
 *
 * - `restored` — arrived in a snapshot. Already-known history, so it appears without fanfare.
 * - `created`  — arrived live and is new. Worth announcing.
 * - `updated`  — arrived live and changed an existing row. Worth drawing the eye to.
 */
export type ChangeKind = 'restored' | 'created' | 'updated';

export interface TransactionEntry {
  readonly transaction: Transaction;
  /** Increments on every change. Bumping it is what re-triggers the row's animation. */
  readonly revision: number;
  readonly change: ChangeKind;
}

/** Defers work to a later turn; returns a cancel function. Injected so tests can drive it. */
export type Scheduler = (run: () => void) => () => void;

export interface TransactionStoreOptions {
  /** Hard cap on rendered rows, mirroring the server's own retention window. */
  readonly capacity: number;
  readonly scheduler?: Scheduler;
}

export interface TransactionStore {
  subscribe(listener: () => void): () => void;
  getSnapshot(): readonly TransactionEntry[];
  /** Replaces state from a server snapshot, preserving rows that have not changed. */
  applySnapshot(transactions: readonly Transaction[]): void;
  /** Queues a live update. Nothing renders until the next flush. */
  push(transaction: Transaction): void;
  /** Applies queued updates immediately. Called by the scheduler; exposed for tests. */
  flush(): void;
  clear(): void;
}

/**
 * Longest the buffer may sit unflushed when animation frames are not being delivered.
 * Far longer than a frame, so it never competes with rAF on a visible tab.
 */
const HIDDEN_FLUSH_INTERVAL_MS = 250;

/**
 * Flushes on the next animation frame, or after a short delay — whichever comes first.
 *
 * The timer is not redundant. Browsers stop delivering animation frames to a hidden tab, and a
 * dashboard spends most of its life in a background tab on someone's second monitor. With rAF
 * alone the buffer would grow for as long as the tab stayed hidden — an unbounded leak on a busy
 * feed — and then dump hours of accumulated transactions in one flush on refocus. The timeout
 * bounds the buffer no matter what the tab is doing, while a visible tab still gets frame-
 * aligned batching because rAF always wins the race.
 */
const animationFrameScheduler: Scheduler = (run) => {
  let settled = false;

  const finish = () => {
    if (settled) {
      return;
    }

    settled = true;
    cancelAnimationFrame(frame);
    clearTimeout(timer);
    run();
  };

  const frame = requestAnimationFrame(finish);
  const timer = setTimeout(finish, HIDDEN_FLUSH_INTERVAL_MS);

  return () => {
    settled = true;
    cancelAnimationFrame(frame);
    clearTimeout(timer);
  };
};

/**
 * Holds the dashboard's transactions and decides when React is allowed to hear about them.
 *
 * **The performance problem this solves.** A burst of a hundred transactions arrives as a
 * hundred separate SignalR callbacks. Setting state in each one asks React for a hundred
 * renders of a list that a human will see exactly once, and the tab stops responding to input
 * while it works through them. Instead, inbound transactions land in a buffer and a single flush
 * is scheduled on the next animation frame: a burst of any size costs one render, and the flush
 * is naturally paced to the display rather than to the network.
 *
 * **Why rows do not move.** Updates replace a row in place rather than lifting it to the top.
 * A support agent reading a row must not have it slide away because its status changed, and
 * stable positions are also what make the status transition animation legible.
 *
 * **Object identity is load-bearing.** A flush reuses the existing entry object for every row
 * that did not change, so `React.memo` on the row component can reject re-renders by reference.
 * Rebuilding the entries wholesale would defeat memoisation and undo the batching gains.
 *
 * Kept free of React on purpose: it is plain state plus a subscription, driven through
 * `useSyncExternalStore`, which makes the batching logic testable without rendering anything.
 */
export function createTransactionStore({
  capacity,
  scheduler = animationFrameScheduler,
}: TransactionStoreOptions): TransactionStore {
  if (capacity < 1) {
    throw new RangeError(`capacity must be at least 1, got ${capacity}.`);
  }

  let entries: readonly TransactionEntry[] = [];
  let buffer: Transaction[] = [];
  let cancelScheduled: (() => void) | null = null;
  const listeners = new Set<() => void>();

  function notify(): void {
    for (const listener of listeners) {
      listener();
    }
  }

  function cancelPendingFlush(): void {
    cancelScheduled?.();
    cancelScheduled = null;
  }

  function scheduleFlush(): void {
    if (cancelScheduled) {
      return;
    }

    cancelScheduled = scheduler(() => {
      cancelScheduled = null;
      flush();
    });
  }

  function flush(): void {
    cancelPendingFlush();

    if (buffer.length === 0) {
      return;
    }

    const batch = buffer;
    buffer = [];

    const index = new Map<string, TransactionEntry>();
    for (const entry of entries) {
      index.set(entry.transaction.transactionId, entry);
    }

    // Ids added during this batch, oldest first, so they can be prepended newest-first below.
    const addedIds: string[] = [];
    const addedIdSet = new Set<string>();
    let changed = false;

    for (const transaction of batch) {
      const existing = index.get(transaction.transactionId);

      if (!existing) {
        index.set(transaction.transactionId, { transaction, revision: 0, change: 'created' });
        addedIds.push(transaction.transactionId);
        addedIdSet.add(transaction.transactionId);
        changed = true;
        continue;
      }

      if (isSameState(existing.transaction, transaction)) {
        continue;
      }

      // A row created and then updated inside the same frame is still new to the viewer: it
      // should animate in once, not animate in and immediately pulse.
      index.set(transaction.transactionId, {
        transaction,
        revision: addedIdSet.has(transaction.transactionId) ? 0 : existing.revision + 1,
        change: addedIdSet.has(transaction.transactionId) ? 'created' : 'updated',
      });
      changed = true;
    }

    if (!changed) {
      return;
    }

    const next: TransactionEntry[] = [];

    for (let i = addedIds.length - 1; i >= 0; i--) {
      next.push(index.get(addedIds[i]!)!);
    }

    for (const entry of entries) {
      next.push(index.get(entry.transaction.transactionId)!);
    }

    entries = next.length > capacity ? next.slice(0, capacity) : next;
    notify();
  }

  function applySnapshot(transactions: readonly Transaction[]): void {
    // Anything buffered is either already in this snapshot or older than it; the server's view
    // is authoritative, so the buffer is dropped rather than replayed on top.
    cancelPendingFlush();
    buffer = [];

    const previous = new Map<string, TransactionEntry>();
    for (const entry of entries) {
      previous.set(entry.transaction.transactionId, entry);
    }

    const next: TransactionEntry[] = [];
    let changed = transactions.length !== entries.length;

    for (const transaction of transactions.slice(0, capacity)) {
      const existing = previous.get(transaction.transactionId);

      if (existing && isSameState(existing.transaction, transaction)) {
        // Identity preserved, so a reconnect does not re-render or re-animate rows that are
        // already on screen and unchanged.
        next.push(existing);
        continue;
      }

      changed = true;
      next.push({
        transaction,
        revision: existing ? existing.revision + 1 : 0,
        change: existing ? 'updated' : 'restored',
      });
    }

    if (!changed && next.every((entry, i) => entry === entries[i])) {
      return;
    }

    entries = next;
    notify();
  }

  function push(transaction: Transaction): void {
    buffer.push(transaction);
    scheduleFlush();
  }

  function clear(): void {
    cancelPendingFlush();
    buffer = [];

    if (entries.length === 0) {
      return;
    }

    entries = [];
    notify();
  }

  return {
    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    getSnapshot: () => entries,
    applySnapshot,
    push,
    flush,
    clear,
  };
}
