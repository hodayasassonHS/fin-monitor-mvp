import { memo } from 'react';
import { formatAmount, formatDateTime, formatTime } from '../domain/format.ts';
import { useChangeAnimation } from '../hooks/useChangeAnimation.ts';
import type { TransactionEntry } from '../realtime/transactionStore.ts';
import { StatusBadge } from './StatusBadge.tsx';

interface TransactionRowProps {
  readonly entry: TransactionEntry;
}

function TransactionRowImpl({ entry }: TransactionRowProps) {
  const { transaction, revision, change } = entry;
  const ref = useChangeAnimation<HTMLTableRowElement>(revision, change);

  return (
    <tr
      ref={ref}
      // Drives --status-tint, which the animation reads, and the row's resting styling.
      className={`transaction-row transaction-row--${transaction.status.toLowerCase()}`}
    >
      <td className="cell cell--id" title={transaction.transactionId}>
        <code>{transaction.transactionId.slice(0, 8)}</code>
      </td>
      <td className="cell cell--amount">{formatAmount(transaction.amount, transaction.currency)}</td>
      <td className="cell cell--currency">{transaction.currency}</td>
      <td className="cell cell--status">
        <StatusBadge status={transaction.status} />
      </td>
      <td className="cell cell--time">
        <time dateTime={transaction.timestamp} title={formatDateTime(transaction.timestamp)}>
          {formatTime(transaction.timestamp)}
        </time>
      </td>
    </tr>
  );
}

/**
 * Memoised on entry identity.
 *
 * This is the other half of the batching strategy. The store deliberately reuses the entry
 * object for every row that did not change, so when a flush replaces the array this comparison
 * lets React skip re-rendering all of them. Without it, one arriving transaction would re-render
 * every visible row and the batching in the store would buy far less than it looks like it does.
 */
export const TransactionRow = memo(TransactionRowImpl, (previous, next) => previous.entry === next.entry);
