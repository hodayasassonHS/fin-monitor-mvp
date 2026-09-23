import type { Transaction, TransactionStatus } from '../domain/transaction.ts';

/**
 * Empty by default: the dashboard is served from the same origin as the API, via the Vite proxy
 * in development and via nginx in the cluster. The override exists for the case where the two
 * are deployed apart, which then also needs the origin listed in the backend's CORS settings.
 */
const apiBaseUrl = import.meta.env['VITE_API_BASE_URL'] ?? '';

export interface TransactionRequest {
  readonly transactionId: string;
  readonly amount: number;
  readonly currency: string;
  readonly status: TransactionStatus;
  readonly timestamp: string;
}

export type IngestOutcome =
  /** Published to the cluster. */
  | { readonly kind: 'accepted'; readonly transaction: Transaction }
  /** Already recorded with this exact state — a safe retry, not a failure. */
  | { readonly kind: 'duplicate'; readonly transaction: Transaction }
  /** The payload was malformed, keyed by field. */
  | { readonly kind: 'invalid'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** Well-formed but contradicts what the server already holds. */
  | { readonly kind: 'conflict'; readonly reason: string; readonly detail: string }
  /** The request never got an answer: offline, DNS, server down. */
  | { readonly kind: 'unavailable'; readonly detail: string };

interface ProblemDetails {
  readonly title?: string;
  readonly detail?: string;
  readonly reason?: string;
  readonly errors?: Record<string, string[]>;
}

export async function postTransaction(request: TransactionRequest): Promise<IngestOutcome> {
  let response: Response;

  try {
    response = await fetch(`${apiBaseUrl}/api/transactions`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    });
  } catch (error) {
    // fetch only rejects when the request never completed; everything else is a status code.
    return { kind: 'unavailable', detail: describe(error) };
  }

  if (response.status === 202) {
    return { kind: 'accepted', transaction: (await response.json()) as Transaction };
  }

  if (response.status === 200) {
    return { kind: 'duplicate', transaction: (await response.json()) as Transaction };
  }

  const problem = await readProblem(response);

  if (response.status === 400 && problem?.errors) {
    return { kind: 'invalid', fieldErrors: problem.errors };
  }

  if (response.status === 409) {
    return {
      kind: 'conflict',
      reason: problem?.reason ?? 'Conflict',
      detail: problem?.detail ?? 'The update conflicts with the recorded transaction.',
    };
  }

  return { kind: 'unavailable', detail: problem?.detail ?? `Server responded ${response.status}.` };
}

export async function fetchTransactions(options?: {
  readonly status?: TransactionStatus;
  readonly limit?: number;
}): Promise<readonly Transaction[]> {
  const query = new URLSearchParams();

  if (options?.status) {
    query.set('status', options.status);
  }

  if (options?.limit !== undefined) {
    query.set('limit', String(options.limit));
  }

  const suffix = query.size > 0 ? `?${query}` : '';
  const response = await fetch(`${apiBaseUrl}/api/transactions${suffix}`);

  if (!response.ok) {
    throw new Error(`Could not load transactions (${response.status}).`);
  }

  return (await response.json()) as readonly Transaction[];
}

async function readProblem(response: Response): Promise<ProblemDetails | null> {
  try {
    return (await response.json()) as ProblemDetails;
  } catch {
    // A proxy or load balancer error page is not JSON; fall back to the status code.
    return null;
  }
}

function describe(error: unknown): string {
  return error instanceof Error ? error.message : 'The server could not be reached.';
}
