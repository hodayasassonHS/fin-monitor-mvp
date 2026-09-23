import { useCallback, useId, useState } from 'react';
import { fetchTransactions, postTransaction, type IngestOutcome } from '../api/transactionsApi.ts';
import { TRANSACTION_STATUSES, type TransactionStatus } from '../domain/transaction.ts';
import {
  randomTransaction,
  sendAll,
  settlementFor,
  summarise,
  type BurstProgress,
} from '../simulator/generator.ts';

interface LogLine {
  readonly id: string;
  readonly tone: 'ok' | 'warn' | 'error';
  readonly text: string;
}

const MAX_LOG_LINES = 12;

function toneFor(outcome: IngestOutcome): LogLine['tone'] {
  switch (outcome.kind) {
    case 'accepted':
      return 'ok';
    case 'duplicate':
    case 'conflict':
      return 'warn';
    default:
      return 'error';
  }
}

function describe(outcome: IngestOutcome): string {
  switch (outcome.kind) {
    case 'accepted':
      return `Accepted ${outcome.transaction.transactionId.slice(0, 8)} (${outcome.transaction.status}).`;
    case 'duplicate':
      return `Already recorded ${outcome.transaction.transactionId.slice(0, 8)}; no change.`;
    case 'invalid':
      return `Rejected: ${Object.entries(outcome.fieldErrors)
        .map(([field, messages]) => `${field} — ${messages.join(' ')}`)
        .join('; ')}`;
    case 'conflict':
      return `Conflict (${outcome.reason}): ${outcome.detail}`;
    case 'unavailable':
      return `Could not reach the server: ${outcome.detail}`;
  }
}

export function SimulatorRoute() {
  const formId = useId();
  const [amount, setAmount] = useState('1500.50');
  const [currency, setCurrency] = useState('USD');
  const [status, setStatus] = useState<TransactionStatus>('Pending');
  const [busy, setBusy] = useState(false);
  const [burstSize, setBurstSize] = useState('100');
  const [progress, setProgress] = useState<BurstProgress | null>(null);
  const [log, setLog] = useState<readonly LogLine[]>([]);

  const appendLog = useCallback((tone: LogLine['tone'], text: string) => {
    setLog((previous) =>
      [{ id: crypto.randomUUID(), tone, text }, ...previous].slice(0, MAX_LOG_LINES),
    );
  }, []);

  const sendOne = useCallback(
    async (event: React.FormEvent) => {
      event.preventDefault();

      const parsedAmount = Number(amount);

      // Caught here as well as server-side: a round trip to be told the number is unreadable is
      // a worse experience than being told immediately.
      if (!Number.isFinite(parsedAmount) || parsedAmount <= 0) {
        appendLog('error', 'Amount must be a number greater than zero.');
        return;
      }

      setBusy(true);

      try {
        const outcome = await postTransaction({
          ...randomTransaction(),
          amount: parsedAmount,
          currency: currency.toUpperCase(),
          status,
        });

        appendLog(toneFor(outcome), describe(outcome));
      } finally {
        setBusy(false);
      }
    },
    [amount, currency, status, appendLog],
  );

  const sendBurst = useCallback(async () => {
    const count = Number(burstSize);

    if (!Number.isInteger(count) || count < 1 || count > 1000) {
      appendLog('error', 'Burst size must be a whole number between 1 and 1000.');
      return;
    }

    setBusy(true);
    setProgress({ completed: 0, total: count });

    try {
      const requests = Array.from({ length: count }, randomTransaction);
      const started = performance.now();
      const outcomes = await sendAll(requests, postTransaction, setProgress);
      const elapsed = Math.round(performance.now() - started);

      appendLog('ok', `Sent ${count} transactions in ${elapsed} ms — ${summarise(outcomes)}.`);
    } finally {
      setBusy(false);
      setProgress(null);
    }
  }, [burstSize, appendLog]);

  const settlePending = useCallback(async () => {
    setBusy(true);

    try {
      const pending = await fetchTransactions({ status: 'Pending', limit: 50 });

      if (pending.length === 0) {
        appendLog('warn', 'Nothing is pending. Generate some transactions first.');
        return;
      }

      const outcomes = await sendAll(pending.map(settlementFor), postTransaction);
      appendLog('ok', `Settled ${pending.length} pending transactions — ${summarise(outcomes)}.`);
    } catch (error) {
      appendLog('error', error instanceof Error ? error.message : 'Could not load pending transactions.');
    } finally {
      setBusy(false);
    }
  }, [appendLog]);

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

          <button type="button" className="button" onClick={settlePending} disabled={busy}>
            Settle pending
          </button>
        </div>

        {progress ? (
          <p className="generator__progress" role="status">
            Sent {progress.completed} of {progress.total}
          </p>
        ) : null}
      </div>

      <div className="log">
        <h2 className="generator__title">Activity</h2>
        {log.length === 0 ? (
          <p className="empty-state">Nothing sent yet.</p>
        ) : (
          <ul className="log__list">
            {log.map((line) => (
              <li key={line.id} className={`log__line log__line--${line.tone}`}>
                {line.text}
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}
