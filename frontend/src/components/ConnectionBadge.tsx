import type { ConnectionState } from '../realtime/useTransactionFeed.ts';

const LABELS: Record<ConnectionState, string> = {
  connecting: 'Connecting',
  live: 'Live',
  reconnecting: 'Reconnecting',
  offline: 'Offline',
};

const DESCRIPTIONS: Record<ConnectionState, string> = {
  connecting: 'Opening the live feed.',
  live: 'Receiving transactions in real time.',
  reconnecting: 'Connection lost. Retrying; the feed will catch up automatically.',
  offline: 'Not connected. The data below may be out of date.',
};

interface ConnectionBadgeProps {
  readonly state: ConnectionState;
}

/**
 * Shows whether the feed is actually live.
 *
 * A dashboard that silently stops updating is worse than one that is visibly broken: a quiet
 * screen reads as "nothing is happening" when it may mean "nothing is getting through". The
 * state is announced politely so the change is not missed.
 */
export function ConnectionBadge({ state }: ConnectionBadgeProps) {
  return (
    <span className={`connection connection--${state}`} role="status" title={DESCRIPTIONS[state]}>
      <span className="connection__dot" aria-hidden="true" />
      {LABELS[state]}
      <span className="visually-hidden">. {DESCRIPTIONS[state]}</span>
    </span>
  );
}
