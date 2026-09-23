import { describe, expect, it, vi } from 'vitest';
import type { IngestOutcome, TransactionRequest } from '../api/transactionsApi.ts';
import { aTransaction } from '../test/builders.ts';
import { randomTransaction, sendAll, settlementFor, summarise } from './generator.ts';

const accepted = (): IngestOutcome => ({ kind: 'accepted', transaction: aTransaction() });

describe('randomTransaction', () => {
  it('produces a payload the API will accept', () => {
    const request = randomTransaction();

    expect(request.transactionId).toMatch(/^[0-9a-f-]{36}$/i);
    expect(request.amount).toBeGreaterThan(0);
    expect(request.currency).toMatch(/^[A-Z]{3}$/);
    expect(request.status).toBe('Pending');
    expect(Number.isNaN(Date.parse(request.timestamp))).toBe(false);
  });

  it('rounds the amount to whole cents', () => {
    // Fractions of a cent would be rejected by nobody but would render as noise; more
    // importantly, a resend has to match the recorded amount exactly or the server treats it as
    // an attempt to rewrite an immutable field.
    for (let i = 0; i < 200; i++) {
      const { amount } = randomTransaction();
      expect(Math.round(amount * 100)).toBeCloseTo(amount * 100, 6);
    }
  });

  it('gives every transaction a distinct id', () => {
    const ids = new Set(Array.from({ length: 500 }, () => randomTransaction().transactionId));

    expect(ids.size).toBe(500);
  });
});

describe('settlementFor', () => {
  it('keeps the amount and currency the server recorded', () => {
    // These are immutable server-side; changing them turns a settlement into a 409.
    const original = aTransaction({ amount: 42.42, currency: 'EUR' });

    const settlement = settlementFor(original);

    expect(settlement.transactionId).toBe(original.transactionId);
    expect(settlement.amount).toBe(42.42);
    expect(settlement.currency).toBe('EUR');
  });

  it('always moves to a terminal status', () => {
    const outcomes = new Set(
      Array.from({ length: 200 }, () => settlementFor(aTransaction()).status),
    );

    expect([...outcomes].sort()).toEqual(['Completed', 'Failed']);
  });
});

describe('sendAll', () => {
  it('sends every request', async () => {
    const send = vi.fn(async () => accepted());
    const requests = Array.from({ length: 50 }, randomTransaction);

    const outcomes = await sendAll(requests, send);

    expect(send).toHaveBeenCalledTimes(50);
    expect(outcomes).toHaveLength(50);
  });

  it('never runs more than the concurrency limit at once', async () => {
    let inFlight = 0;
    let peak = 0;

    const send = async (): Promise<IngestOutcome> => {
      inFlight++;
      peak = Math.max(peak, inFlight);
      await new Promise((resolve) => setTimeout(resolve, 1));
      inFlight--;
      return accepted();
    };

    await sendAll(Array.from({ length: 100 }, randomTransaction), send);

    expect(peak).toBeLessThanOrEqual(16);
    expect(peak).toBeGreaterThan(1);
  });

  it('reports progress as requests land', async () => {
    const progress: number[] = [];

    await sendAll(Array.from({ length: 10 }, randomTransaction), async () => accepted(), (update) =>
      progress.push(update.completed),
    );

    expect(progress).toHaveLength(10);
    expect(progress.at(-1)).toBe(10);
  });

  it('keeps going when a request throws', async () => {
    // A simulator that stops halfway leaves the dashboard in a state nobody can interpret.
    let call = 0;
    const send = async (_request: TransactionRequest): Promise<IngestOutcome> => {
      call++;
      if (call === 2) {
        throw new Error('network down');
      }
      return accepted();
    };

    const outcomes = await sendAll(Array.from({ length: 5 }, randomTransaction), send);

    expect(outcomes).toHaveLength(5);
    expect(outcomes.filter((outcome) => outcome.kind === 'unavailable')).toHaveLength(1);
  });

  it('handles an empty batch', async () => {
    expect(await sendAll([], async () => accepted())).toEqual([]);
  });
});

describe('summarise', () => {
  it('counts outcomes by kind', () => {
    const summary = summarise([
      accepted(),
      accepted(),
      { kind: 'conflict', reason: 'TerminalStatus', detail: 'already settled' },
    ]);

    expect(summary).toBe('2 accepted, 1 conflict');
  });
});
