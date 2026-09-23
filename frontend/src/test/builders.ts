import type { Transaction, TransactionStatus } from '../domain/transaction.ts';
import type { Scheduler } from '../realtime/transactionStore.ts';

let sequence = 0;

export function aTransaction(overrides: Partial<Transaction> = {}): Transaction {
  sequence++;

  return {
    transactionId: `00000000-0000-0000-0000-${String(sequence).padStart(12, '0')}`,
    amount: 1500.5,
    currency: 'USD',
    status: 'Pending',
    timestamp: '2024-01-15T10:00:00.000Z',
    ...overrides,
  };
}

export function settled(transaction: Transaction, status: TransactionStatus): Transaction {
  return { ...transaction, status, timestamp: '2024-01-15T10:00:30.000Z' };
}

export interface ManualScheduler {
  readonly scheduler: Scheduler;
  /** Runs the pending callback, standing in for the next animation frame. */
  tick(): void;
  readonly scheduledCount: number;
}

/**
 * A scheduler the test drives by hand.
 *
 * Batching is the whole point of the store, and it is only observable in the gap between "a
 * transaction arrived" and "the next frame ran". Real `requestAnimationFrame` gives no control
 * over that gap; this makes it explicit, so the tests can assert on what happens inside it.
 */
export function createManualScheduler(): ManualScheduler {
  let pending: (() => void) | null = null;
  let scheduledCount = 0;

  return {
    scheduler: (run) => {
      pending = run;
      scheduledCount++;
      return () => {
        pending = null;
      };
    },
    tick() {
      const run = pending;
      pending = null;
      run?.();
    },
    get scheduledCount() {
      return scheduledCount;
    },
  };
}
