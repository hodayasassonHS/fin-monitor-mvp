import {
  HttpTransportType,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr';
import { useEffect, useMemo, useState, useSyncExternalStore } from 'react';
import type { Transaction } from '../domain/transaction.ts';
import { createTransactionStore, type TransactionEntry } from './transactionStore.ts';

export type ConnectionState = 'connecting' | 'live' | 'reconnecting' | 'offline';

/** Matches the server's retention window; the dashboard never needs to hold more than it serves. */
const DEFAULT_CAPACITY = 500;

/** Backoff between reconnect attempts, then every 30s. Deliberately quick at first: a rolling
 *  deployment drops every connection at once and they should all come back promptly. */
const RECONNECT_DELAYS_MS = [0, 1_000, 3_000, 5_000, 10_000, 30_000];

const hubUrl = `${import.meta.env['VITE_API_BASE_URL'] ?? ''}/hubs/transactions`;

function createConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(hubUrl, {
      // Going straight to the WebSocket skips SignalR's negotiate round trip. Behind a
      // multi-replica Service that also removes a real failure mode: negotiate and connect are
      // separate HTTP requests and can be routed to different replicas, which breaks the
      // handshake. See the Kubernetes manifests, which additionally pin session affinity.
      skipNegotiation: true,
      transport: HttpTransportType.WebSockets,
    })
    .withAutomaticReconnect([...RECONNECT_DELAYS_MS])
    .configureLogging(LogLevel.Warning)
    .build();
}

export interface TransactionFeed {
  readonly entries: readonly TransactionEntry[];
  readonly connectionState: ConnectionState;
}

/**
 * Subscribes to the live transaction feed and exposes it as React state.
 *
 * The store sits behind `useSyncExternalStore` rather than `useState` so that inbound
 * transactions can be coalesced outside React's control and handed over one batch per frame.
 * See {@link createTransactionStore} for why that matters under load.
 */
export function useTransactionFeed(capacity: number = DEFAULT_CAPACITY): TransactionFeed {
  const store = useMemo(() => createTransactionStore({ capacity }), [capacity]);
  const [connectionState, setConnectionState] = useState<ConnectionState>('connecting');

  useEffect(() => {
    const connection = createConnection();
    let disposed = false;

    // Handlers are attached before start() so the snapshot the server sends on connect cannot
    // arrive before there is anything listening for it.
    connection.on('Snapshot', (transactions: readonly Transaction[]) => {
      store.applySnapshot(transactions);
    });

    connection.on('TransactionUpserted', (transaction: Transaction) => {
      store.push(transaction);
    });

    connection.onreconnecting(() => setConnectionState('reconnecting'));

    // The server re-sends a full snapshot on every new connection, so reconnecting also repairs
    // whatever was missed while the socket was down. No catch-up request is needed here.
    connection.onreconnected(() => setConnectionState('live'));

    connection.onclose(() => setConnectionState('offline'));

    const started = connection.start().then(
      () => {
        if (!disposed) {
          setConnectionState('live');
        }
      },
      () => {
        if (!disposed) {
          setConnectionState('offline');
        }
      },
    );

    return () => {
      disposed = true;

      // Let start() settle before stopping. StrictMode mounts, unmounts and remounts every
      // effect in development, so this cleanup fires while the handshake is still in flight;
      // calling stop() at that moment is supported but logs an error on every page load, which
      // trains people to ignore the console. Waiting costs nothing and keeps it clean.
      void started
        .then(() =>
          connection.state === HubConnectionState.Disconnected ? undefined : connection.stop(),
        )
        .catch(() => undefined);
    };
  }, [store]);

  const entries = useSyncExternalStore(store.subscribe, store.getSnapshot, store.getSnapshot);

  return { entries, connectionState };
}
