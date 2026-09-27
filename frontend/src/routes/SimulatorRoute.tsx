import { useCallback, useId, useState } from 'react';
import { postTransaction, type IngestOutcome } from '../api/transactionsApi.ts';
import { TRANSACTION_STATUSES, type TransactionStatus } from '../domain/transaction.ts';
import { randomTransaction, sendAll, type BurstProgress } from '../simulator/generator.ts';

/** Null for a successful send; a short human-readable reason otherwise. */
function describeFailure(outcome: IngestOutcome): string | null {
  switch (outcome.kind) {
    case 'accepted':
    case 'duplicate':
      return null;
    case 'invalid':
      return Object.entries(outcome.fieldErrors)
        .map(([field, messages]) => `${field} — ${messages.join(' ')}`)
        .join('; ');
    case 'conflict':
      return `${outcome.reason}: ${outcome.detail}`;
    case 'unavailable':
      return outcome.detail;
  }
}

export function SimulatorRoute() {
  const formId = useId();
  const [transactionId, setTransactionId] = useState('');
  const [amount, setAmount] = useState('1500.50');
  const [currency, setCurrency] = useState('USD');
  const [status, setStatus] = useState<TransactionStatus>('Pending');
  const [busy, setBusy] = useState(false);
  const [burstSize, setBurstSize] = useState('100');
  const [progress, setProgress] = useState<BurstProgress | null>(null);
  const [error, setError] = useState<string | null>(null);

  const sendOne = useCallback(
    async (event: React.FormEvent) => {
      event.preventDefault();
      setError(null);

      const parsedAmount = Number(amount);

      // Caught here as well as server-side: a round trip to be told the number is unreadable is
      // a worse experience than being told immediately.
      if (!Number.isFinite(parsedAmount) || parsedAmount <= 0) {
        setError('Amount must be a number greater than zero.');
        return;
      }

      setBusy(true);

      try {
        const outcome = await postTransaction({
          ...randomTransaction(),
          amount: parsedAmount,
          currency: currency.toUpperCase(),
          status,
          // Blank means "create new"; a pasted id targets an existing transaction, so this one
          // field covers both creation and manual status updates.
          ...(transactionId.trim() ? { transactionId: transactionId.trim() } : {}),
        });

        setError(describeFailure(outcome));
      } finally {
        setBusy(false);
      }
    },
    [transactionId, amount, currency, status],
  );

  const sendBurst = useCallback(async () => {
    setError(null);

    const count = Number(burstSize);

    if (!Number.isInteger(count) || count < 1 || count > 1000) {
      setError('Burst size must be a whole number between 1 and 1000.');
      return;
    }

    setBusy(true);
    setProgress({ completed: 0, total: count });

    try {
      const requests = Array.from({ length: count }, randomTransaction);
      await sendAll(requests, postTransaction, setProgress);
    } finally {
      setBusy(false);
      setProgress(null);
    }
  }, [burstSize]);

  return (
    <section className="panel">
      <header className="panel__header">
        <div>
          <h1 className="panel__title">Transaction Simulator</h1>
          <p className="panel__subtitle">
            Stands in for the upstream payment system. Everything here goes over plain HTTP POST
            to the ingestion API — open the dashboard in a second tab to watch it arrive.
          </p>
        </div>
      </header>

      <form className="form" onSubmit={sendOne}>
        <div className="field">
          <label htmlFor={`${formId}-id`}>Transaction ID (optional)</label>
          <input
            id={`${formId}-id`}
            value={transactionId}
            placeholder="Leave blank to create a new transaction"
            onChange={(event) => setTransactionId(event.target.value)}
          />
        </div>

        <div className="field">
          <label htmlFor={`${formId}-amount`}>Amount</label>
          <input
            id={`${formId}-amount`}
            inputMode="decimal"
            value={amount}
            onChange={(event) => setAmount(event.target.value)}
            required
          />
        </div>

        <div className="field">
          <label htmlFor={`${formId}-currency`}>Currency</label>
          <input
            id={`${formId}-currency`}
            value={currency}
            maxLength={3}
            onChange={(event) => setCurrency(event.target.value.toUpperCase())}
            required
          />
        </div>

        <div className="field">
          <label htmlFor={`${formId}-status`}>Status</label>
          <select
            id={`${formId}-status`}
            value={status}
            onChange={(event) => setStatus(event.target.value as TransactionStatus)}
          >
            {TRANSACTION_STATUSES.map((option) => (
              <option key={option} value={option}>
                {option}
              </option>
            ))}
          </select>
        </div>

        <button type="submit" className="button button--primary" disabled={busy}>
          Send transaction
        </button>

        {error ? (
          <p className="form__error" role="alert">
            {error}
          </p>
        ) : null}
      </form>

      <div className="generator">
        <h2 className="generator__title">Load generator</h2>
        <p className="generator__hint">
          The dashboard batches inbound updates into one render per animation frame, so a burst
          of a hundred is a single repaint rather than a hundred.
        </p>

        <div className="generator__controls">
          <div className="field field--inline">
            <label htmlFor={`${formId}-burst`}>Burst size</label>
            <input
              id={`${formId}-burst`}
              inputMode="numeric"
              value={burstSize}
              onChange={(event) => setBurstSize(event.target.value)}
            />
          </div>

          <button type="button" className="button" onClick={sendBurst} disabled={busy}>
            Generate burst
          </button>
        </div>

        {progress ? (
          <p className="generator__progress" role="status">
            Sent {progress.completed} of {progress.total}
          </p>
        ) : null}
      </div>
    </section>
  );
}
