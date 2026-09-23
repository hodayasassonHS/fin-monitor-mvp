import { render, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { TransactionEntry } from '../realtime/transactionStore.ts';
import { aTransaction } from '../test/builders.ts';
import { TransactionTable } from './TransactionTable.tsx';

function entry(overrides: Partial<TransactionEntry> = {}): TransactionEntry {
  return {
    transaction: aTransaction(),
    revision: 0,
    change: 'restored',
    ...overrides,
  };
}

describe('TransactionTable', () => {
  it('explains itself when there is nothing to show', () => {
    render(<TransactionTable entries={[]} emptyMessage="No errors." />);

    expect(screen.getByText('No errors.')).toBeDefined();
  });

  it('renders one row per transaction', () => {
    render(<TransactionTable entries={[entry(), entry(), entry()]} emptyMessage="" />);

    expect(within(screen.getAllByRole('rowgroup')[1]!).getAllByRole('row')).toHaveLength(3);
  });

  it('shows the status as text, not only as a colour', () => {
    // Red and green are the exact pair that colour vision deficiency affects, and here they
    // separate a payment that settled from one that failed.
    render(
      <TransactionTable
        entries={[
          entry({ transaction: aTransaction({ status: 'Failed' }) }),
          entry({ transaction: aTransaction({ status: 'Completed' }) }),
        ]}
        emptyMessage=""
      />,
    );

    expect(screen.getByText('Failed')).toBeDefined();
    expect(screen.getByText('Completed')).toBeDefined();
  });

  it('formats the amount in its own currency', () => {
    render(
      <TransactionTable
        entries={[entry({ transaction: aTransaction({ amount: 1500.5, currency: 'EUR' }) })]}
        emptyMessage=""
      />,
    );

    // Locale decides the exact glyphs and separators, so assert on what must be there.
    const cell = screen.getByText(/1[,.\s]500[.,]50/);
    expect(cell.textContent).toMatch(/€|EUR/);
  });

  it('keeps the full id available even though the cell is truncated', () => {
    const transaction = aTransaction();
    render(<TransactionTable entries={[entry({ transaction })]} emptyMessage="" />);

    expect(screen.getByTitle(transaction.transactionId)).toBeDefined();
  });
});
