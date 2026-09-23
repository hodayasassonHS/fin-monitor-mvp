import { describe, expect, it, vi } from 'vitest';
import { aTransaction, createManualScheduler, settled } from '../test/builders.ts';
import { createTransactionStore } from './transactionStore.ts';

function storeWith(capacity = 100) {
  const manual = createManualScheduler();
  const store = createTransactionStore({ capacity, scheduler: manual.scheduler });
  return { store, manual };
}

const ids = (entries: readonly { transaction: { transactionId: string } }[]) =>
  entries.map((entry) => entry.transaction.transactionId);

describe('batching', () => {
  it('shows nothing until the frame runs', () => {
    const { store } = storeWith();

    store.push(aTransaction());

    // The whole performance argument rests on this: arrival does not imply render.
    expect(store.getSnapshot()).toHaveLength(0);
  });

  it('coalesces a burst of a hundred into a single notification', () => {
    const { store, manual } = storeWith(500);
    const listener = vi.fn();
    store.subscribe(listener);

    for (let i = 0; i < 100; i++) {
      store.push(aTransaction());
    }

    expect(manual.scheduledCount).toBe(1);

    manual.tick();

    // One render for a hundred transactions is the requirement that the UI stay responsive.
    expect(listener).toHaveBeenCalledTimes(1);
    expect(store.getSnapshot()).toHaveLength(100);
  });

  it('schedules again for transactions arriving after a flush', () => {
    const { store, manual } = storeWith();

    store.push(aTransaction());
    manual.tick();
    store.push(aTransaction());
    manual.tick();

    expect(store.getSnapshot()).toHaveLength(2);
  });

  it('does not notify when a flush changes nothing', () => {
    const { store, manual } = storeWith();
    const transaction = aTransaction();
    store.push(transaction);
    manual.tick();

    const listener = vi.fn();
    store.subscribe(listener);
    store.push(transaction);
    manual.tick();

    expect(listener).not.toHaveBeenCalled();
  });
});

describe('default scheduling', () => {
  it('flushes even when the tab is hidden and no animation frames arrive', () => {
    // Browsers stop delivering rAF to a hidden tab. Relying on it alone would let the buffer
    // grow for as long as the dashboard sat in a background tab, then dump the lot on refocus.
    vi.useFakeTimers();
    const requestAnimationFrame = vi
      .spyOn(globalThis, 'requestAnimationFrame')
      .mockImplementation(() => 0);
    vi.spyOn(globalThis, 'cancelAnimationFrame').mockImplementation(() => undefined);

    try {
      const store = createTransactionStore({ capacity: 10 });
      store.push(aTransaction());

      expect(requestAnimationFrame).toHaveBeenCalled();
      expect(store.getSnapshot()).toHaveLength(0);

      vi.advanceTimersByTime(250);

      expect(store.getSnapshot()).toHaveLength(1);
    } finally {
      vi.restoreAllMocks();
      vi.useRealTimers();
    }
  });

  it('lets the animation frame win on a visible tab', () => {
    vi.useFakeTimers();
    let frameCallback: FrameRequestCallback | null = null;
    vi.spyOn(globalThis, 'requestAnimationFrame').mockImplementation((callback) => {
      frameCallback = callback;
      return 1;
    });
    vi.spyOn(globalThis, 'cancelAnimationFrame').mockImplementation(() => undefined);

    try {
      const store = createTransactionStore({ capacity: 10 });
      store.push(aTransaction());

      frameCallback!(0);
      expect(store.getSnapshot()).toHaveLength(1);

      // The fallback timer must have been cancelled, or the batch would flush twice.
      const afterFrame = store.getSnapshot();
      vi.advanceTimersByTime(500);
      expect(store.getSnapshot()).toBe(afterFrame);
    } finally {
      vi.restoreAllMocks();
      vi.useRealTimers();
    }
  });
});

describe('snapshot identity', () => {
  it('returns the same array reference until something changes', () => {
    const { store, manual } = storeWith();
    store.push(aTransaction());
    manual.tick();

    const first = store.getSnapshot();

    // useSyncExternalStore re-renders whenever getSnapshot returns a new reference, so a stable
    // one is what stops an unrelated render from cascading through the whole table.
    expect(store.getSnapshot()).toBe(first);
  });

  it('keeps the entry object for rows that did not change', () => {
    const { store, manual } = storeWith();
    const untouched = aTransaction();
    const changing = aTransaction();
    store.push(untouched);
    store.push(changing);
    manual.tick();

    const before = store.getSnapshot();
    store.push(settled(changing, 'Completed'));
    manual.tick();
    const after = store.getSnapshot();

    // This is what makes React.memo on the row effective.
    const findUntouched = (entries: readonly { transaction: { transactionId: string } }[]) =>
      entries.find((entry) => entry.transaction.transactionId === untouched.transactionId);

    expect(findUntouched(after)).toBe(findUntouched(before));
  });
});

describe('ordering and updates', () => {
  it('puts newly arrived transactions at the top', () => {
    const { store, manual } = storeWith();
    const first = aTransaction();
    const second = aTransaction();

    store.push(first);
    manual.tick();
    store.push(second);
    manual.tick();

    expect(ids(store.getSnapshot())).toEqual([second.transactionId, first.transactionId]);
  });

  it('orders a batch newest-last-arrived first', () => {
    const { store, manual } = storeWith();
    const first = aTransaction();
    const second = aTransaction();

    store.push(first);
    store.push(second);
    manual.tick();

    expect(ids(store.getSnapshot())).toEqual([second.transactionId, first.transactionId]);
  });

  it('updates a row in place instead of moving it', () => {
    // A row that jumps to the top when it settles pulls the reader's place out from under them.
    const { store, manual } = storeWith();
    const older = aTransaction();
    const newer = aTransaction();
    store.push(older);
    store.push(newer);
    manual.tick();

    store.push(settled(older, 'Completed'));
    manual.tick();

    expect(ids(store.getSnapshot())).toEqual([newer.transactionId, older.transactionId]);
    expect(store.getSnapshot()[1]?.transaction.status).toBe('Completed');
  });

  it('marks an update so the row can animate the change', () => {
    const { store, manual } = storeWith();
    const transaction = aTransaction();
    store.push(transaction);
    manual.tick();

    store.push(settled(transaction, 'Failed'));
    manual.tick();

    const entry = store.getSnapshot()[0]!;
    expect(entry.change).toBe('updated');
    expect(entry.revision).toBe(1);
  });

  it('treats a row created and settled within one frame as a single arrival', () => {
    // Both events land in the same batch; the viewer only ever sees the end state, so it should
    // animate in once rather than appearing and immediately flashing.
    const { store, manual } = storeWith();
    const transaction = aTransaction();

    store.push(transaction);
    store.push(settled(transaction, 'Completed'));
    manual.tick();

    const entries = store.getSnapshot();
    expect(entries).toHaveLength(1);
    expect(entries[0]?.change).toBe('created');
    expect(entries[0]?.revision).toBe(0);
    expect(entries[0]?.transaction.status).toBe('Completed');
  });
});

describe('capacity', () => {
  it('drops the oldest rows past the cap', () => {
    const { store, manual } = storeWith(3);

    const transactions = Array.from({ length: 5 }, () => aTransaction());
    for (const transaction of transactions) {
      store.push(transaction);
    }
    manual.tick();

    expect(ids(store.getSnapshot())).toEqual([
      transactions[4]!.transactionId,
      transactions[3]!.transactionId,
      transactions[2]!.transactionId,
    ]);
  });

  it('refuses a capacity below one', () => {
    expect(() => createTransactionStore({ capacity: 0 })).toThrow(RangeError);
  });
});

describe('server snapshots', () => {
  it('adopts the server view without animating it', () => {
    // A snapshot is history, not news. Marking it 'restored' is what stops a reconnect from
    // looking like a flood of fresh activity.
    const { store } = storeWith();
    const transaction = aTransaction();

    store.applySnapshot([transaction]);

    expect(store.getSnapshot()[0]?.change).toBe('restored');
  });

  it('preserves rows that are already on screen unchanged', () => {
    const { store, manual } = storeWith();
    const transaction = aTransaction();
    store.push(transaction);
    manual.tick();
    const before = store.getSnapshot()[0];

    store.applySnapshot([transaction]);

    expect(store.getSnapshot()[0]).toBe(before);
  });

  it('does not notify when the snapshot matches what is displayed', () => {
    const { store, manual } = storeWith();
    const transaction = aTransaction();
    store.push(transaction);
    manual.tick();

    const listener = vi.fn();
    store.subscribe(listener);
    store.applySnapshot([transaction]);

    expect(listener).not.toHaveBeenCalled();
  });

  it('marks a row the snapshot has moved on as updated', () => {
    const { store, manual } = storeWith();
    const transaction = aTransaction();
    store.push(transaction);
    manual.tick();

    store.applySnapshot([settled(transaction, 'Failed')]);

    expect(store.getSnapshot()[0]?.change).toBe('updated');
  });

  it('discards buffered updates the snapshot supersedes', () => {
    const { store } = storeWith();
    const buffered = aTransaction();
    store.push(buffered);

    store.applySnapshot([]);

    expect(store.getSnapshot()).toHaveLength(0);
  });

  it('honours the capacity', () => {
    const { store } = storeWith(2);

    store.applySnapshot([aTransaction(), aTransaction(), aTransaction()]);

    expect(store.getSnapshot()).toHaveLength(2);
  });
});

describe('subscriptions', () => {
  it('stops notifying once unsubscribed', () => {
    const { store, manual } = storeWith();
    const listener = vi.fn();

    store.subscribe(listener)();
    store.push(aTransaction());
    manual.tick();

    expect(listener).not.toHaveBeenCalled();
  });

  it('clears everything on demand', () => {
    const { store, manual } = storeWith();
    store.push(aTransaction());
    manual.tick();

    store.clear();

    expect(store.getSnapshot()).toHaveLength(0);
  });
});
