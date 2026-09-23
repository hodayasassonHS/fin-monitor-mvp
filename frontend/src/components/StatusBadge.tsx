import type { TransactionStatus } from '../domain/transaction.ts';

const LABELS: Record<TransactionStatus, string> = {
  Pending: 'Pending',
  Completed: 'Completed',
  Failed: 'Failed',
};

interface StatusBadgeProps {
  readonly status: TransactionStatus;
}

/**
 * The status pill.
 *
 * Colour is not the only signal: each status also carries a distinct glyph and its own text.
 * Roughly one man in twelve has some form of colour vision deficiency, and red/green is the
 * exact pair they struggle with — which here would be the difference between a payment that
 * settled and one that failed.
 *
 * The smooth colour morph on a status change is a plain CSS transition on the element rather
 * than an animation, so a Pending row turning Completed eases between the two states instead of
 * snapping.
 */
export function StatusBadge({ status }: StatusBadgeProps) {
  return (
    <span className={`status-badge status-badge--${status.toLowerCase()}`}>
      <span className="status-badge__dot" aria-hidden="true" />
      {LABELS[status]}
    </span>
  );
}
