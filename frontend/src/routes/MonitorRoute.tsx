import { useMemo, useState } from 'react';
import { ConnectionBadge } from '../components/ConnectionBadge.tsx';
import { StatusFilter, type StatusFilterValue } from '../components/StatusFilter.tsx';
import { TransactionTable } from '../components/TransactionTable.tsx';
import { useTransactionFeed } from '../realtime/useTransactionFeed.ts';

const EMPTY_MESSAGES: Record<StatusFilterValue, string> = {
  All: 'Waiting for transactions. Open the Simulator to generate some.',
  Pending: 'Nothing pending right now.',
  Completed: 'Nothing has completed yet.',
  Failed: 'No errors. Everything is settling cleanly.',
};

export function MonitorRoute() {
  const { entries, connectionState } = useTransactionFeed();
  const [filter, setFilter] = useState<StatusFilterValue>('All');

  // Both derivations are memoised on `entries`, whose identity only changes when the store
  // actually flushes a change. A burst of a hundred transactions therefore costs one pass, not
  // one per transaction.
  const counts = useMemo(() => {
    const tally: Record<StatusFilterValue, number> = {
      All: entries.length,
      Pending: 0,
      Completed: 0,
      Failed: 0,
    };

    for (const entry of entries) {
      tally[entry.transaction.status]++;
    }

    return tally;
  }, [entries]);

  const visible = useMemo(
    () => (filter === 'All' ? entries : entries.filter((entry) => entry.transaction.status === filter)),
    [entries, filter],
  );

  return (
    <section className="panel">
      <header className="panel__header">
        <div>
          <h1 className="panel__title">Live Dashboard</h1>
          <p className="panel__subtitle">
            {counts.All === 0
              ? 'No transactions yet'
              : `Showing ${visible.length} of ${counts.All} transactions`}
          </p>
        </div>
        <ConnectionBadge state={connectionState} />
      </header>

      <StatusFilter value={filter} counts={counts} onChange={setFilter} />

      <TransactionTable entries={visible} emptyMessage={EMPTY_MESSAGES[filter]} />
    </section>
  );
}
