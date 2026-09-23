export const TRANSACTION_STATUSES = ['Pending', 'Completed', 'Failed'] as const;

export type TransactionStatus = (typeof TRANSACTION_STATUSES)[number];

/**
 * A transaction exactly as the API sends it.
 *
 * `amount` is a JSON number, and the client never does arithmetic on it — it is formatted and
 * displayed, nothing more. The authoritative value is a .NET `decimal` on the server; doing sums
 * in IEEE-754 doubles here would reintroduce exactly the rounding error the backend avoids.
 */
export interface Transaction {
  readonly transactionId: string;
  readonly amount: number;
  readonly currency: string;
  readonly status: TransactionStatus;
  readonly timestamp: string;
}

export function isTerminal(status: TransactionStatus): boolean {
  return status === 'Completed' || status === 'Failed';
}

/**
 * True when two observations of a transaction represent the same state.
 *
 * Used to suppress work the user would not see: the server already filters out no-op updates,
 * but a reconnect re-delivers a snapshot that mostly repeats what is on screen, and re-rendering
 * or re-animating those rows would be visible churn for no information.
 */
export function isSameState(left: Transaction, right: Transaction): boolean {
  return (
    left.status === right.status &&
    left.amount === right.amount &&
    left.currency === right.currency &&
    left.timestamp === right.timestamp
  );
}
