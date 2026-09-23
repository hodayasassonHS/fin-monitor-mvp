import type { TransactionEntry } from '../realtime/transactionStore.ts';
import { TransactionRow } from './TransactionRow.tsx';

interface TransactionTableProps {
  readonly entries: readonly TransactionEntry[];
  readonly emptyMessage: string;
}

export function TransactionTable({ entries, emptyMessage }: TransactionTableProps) {
  if (entries.length === 0) {
    return <p className="empty-state">{emptyMessage}</p>;
  }

  return (
    <div className="table-scroll">
      <table className="transaction-table">
        <caption className="visually-hidden">
          Live transactions, most recently updated first. Updates arrive automatically.
        </caption>
        <thead>
          <tr>
            <th scope="col">ID</th>
            <th scope="col">Amount</th>
            <th scope="col">Currency</th>
            <th scope="col">Status</th>
            <th scope="col">Time</th>
          </tr>
        </thead>
        {/*
          `aria-live` is deliberately absent. A screen reader announcing every arrival would be
          unusable under load — the count in the toolbar carries that information instead, at a
          pace a person can follow.
        */}
        <tbody>
          {entries.map((entry) => (
            <TransactionRow key={entry.transaction.transactionId} entry={entry} />
          ))}
        </tbody>
      </table>
    </div>
  );
}
