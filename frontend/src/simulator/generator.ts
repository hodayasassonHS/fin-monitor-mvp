import type { Transaction } from '../domain/transaction.ts';
import type { IngestOutcome, TransactionRequest } from '../api/transactionsApi.ts';

const CURRENCIES = ['USD', 'EUR', 'GBP', 'JPY', 'ILS'] as const;

/** Roughly a fifth of settled transactions fail, so the Errors filter has something to show. */
const FAILURE_RATE = 0.2;

/**
 * Requests in flight at once during a burst.
 *
 * Capped rather than firing every request simultaneously: HTTP/1.1 allows only about six
 * connections per origin, so a thousand parallel `fetch` calls would queue in the browser
 * anyway, while making progress reporting meaningless and starving the page of connections for
 * anything else. Sixteen keeps the pipe full without any of that.
 */
const MAX_CONCURRENCY = 16;

function pick<T>(values: readonly T[]): T {
  return values[Math.floor(Math.random() * values.length)]!;
}

function randomAmount(): number {
  // Log-uniform over roughly 1 – 25,000 so the feed looks like real traffic — mostly small
  // amounts with the occasional large one — rather than a flat spread.
  const magnitude = Math.exp(Math.random() * Math.log(25_000));
  return Math.round(magnitude * 100) / 100;
}

export function randomTransaction(): TransactionRequest {
  return {
    transactionId: crypto.randomUUID(),
    amount: randomAmount(),
    currency: pick(CURRENCIES),
    status: 'Pending',
    timestamp: new Date().toISOString(),
  };
}

/** Builds the follow-up request that moves a pending transaction to its final state. */
export function settlementFor(transaction: Transaction): TransactionRequest {
  return {
    transactionId: transaction.transactionId,
    // Amount and currency are immutable server-side; resending them unchanged is required.
    amount: transaction.amount,
    currency: transaction.currency,
    status: Math.random() < FAILURE_RATE ? 'Failed' : 'Completed',
    timestamp: new Date().toISOString(),
  };
}

export interface BurstProgress {
  readonly completed: number;
  readonly total: number;
}

/**
 * Sends many requests with bounded concurrency, reporting progress as they land.
 *
 * Never rejects: a simulator that aborts halfway through leaves the dashboard in a state nobody
 * can reason about, so every outcome — including failures — is returned for the caller to show.
 */
export async function sendAll(
  requests: readonly TransactionRequest[],
  send: (request: TransactionRequest) => Promise<IngestOutcome>,
  onProgress?: (progress: BurstProgress) => void,
): Promise<readonly IngestOutcome[]> {
  const outcomes = new Array<IngestOutcome>(requests.length);
  let next = 0;
  let completed = 0;

  async function worker(): Promise<void> {
    while (next < requests.length) {
      const index = next++;

      try {
        outcomes[index] = await send(requests[index]!);
      } catch (error) {
        outcomes[index] = {
          kind: 'unavailable',
          detail: error instanceof Error ? error.message : 'Request failed.',
        };
      }

      completed++;
      onProgress?.({ completed, total: requests.length });
    }
  }

  const workers = Array.from({ length: Math.min(MAX_CONCURRENCY, requests.length) }, worker);
  await Promise.all(workers);

  return outcomes;
}

export function summarise(outcomes: readonly IngestOutcome[]): string {
  const counts = new Map<IngestOutcome['kind'], number>();

  for (const outcome of outcomes) {
    counts.set(outcome.kind, (counts.get(outcome.kind) ?? 0) + 1);
  }

  return [...counts.entries()].map(([kind, count]) => `${count} ${kind}`).join(', ');
}
