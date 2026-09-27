import { memo, useCallback, useState } from 'react';
import { formatAmount, formatDateTime, formatTime } from '../domain/format.ts';
import { useChangeAnimation } from '../hooks/useChangeAnimation.ts';
import type { TransactionEntry } from '../realtime/transactionStore.ts';
import { StatusBadge } from './StatusBadge.tsx';

interface TransactionRowProps {
  readonly entry: TransactionEntry;
}

const COPIED_FLASH_MS = 1200;

function TransactionRowImpl({ entry }: TransactionRowProps) {
  const { transaction, revision, change } = entry;
  const ref = useChangeAnimation<HTMLTableRowElement>(revision, change);
  const [copied, setCopied] = useState(false);

  // The visible id is truncated for width; this is the only way to get the full one back out,
  // since the Simulator's update-by-id field needs it verbatim.
  const copyFullId = useCallback(() => {
    void navigator.clipboard.writeText(transaction.transactionId).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), COPIED_FLASH_MS);
    });
  }, [transaction.transactionId]);

  return (
    <tr
      ref={ref}
      // Drives --status-tint, which the animation reads, and the row's resting styling.
      className={`transaction-row transaction-row--${transaction.status.toLowerCase()}`}
    >
      <td className="cell cell--id">
        <button
          type="button"
          className="id-copy"
          onClick={copyFullId}
          title={transaction.transactionId}
          aria-label={`Copy full transaction id ${transaction.transactionId}`}
        >
          <code>{transaction.transactionId.slice(0, 8)}</code>
          {copied ? <span className="id-copy__flash">copied</span> : null}
        </button>
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
