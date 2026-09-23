import { TRANSACTION_STATUSES, type TransactionStatus } from '../domain/transaction.ts';

export type StatusFilterValue = TransactionStatus | 'All';

export const STATUS_FILTER_VALUES: readonly StatusFilterValue[] = ['All', ...TRANSACTION_STATUSES];

interface StatusFilterProps {
  readonly value: StatusFilterValue;
  readonly counts: Readonly<Record<StatusFilterValue, number>>;
  readonly onChange: (value: StatusFilterValue) => void;
}

export function StatusFilter({ value, counts, onChange }: StatusFilterProps) {
  return (
    // A radiogroup rather than a row of buttons: these are mutually exclusive choices, and the
    // role is what gives keyboard users arrow-key navigation between them for free.
    <div className="filter" role="radiogroup" aria-label="Filter by status">
      {STATUS_FILTER_VALUES.map((option) => (
        <button
          key={option}
          type="button"
          role="radio"
          aria-checked={value === option}
          className={`filter__option filter__option--${option.toLowerCase()}`}
          data-active={value === option}
          onClick={() => onChange(option)}
        >
          {option === 'Failed' ? 'Errors' : option}
          <span className="filter__count">{counts[option]}</span>
        </button>
      ))}
    </div>
  );
}
