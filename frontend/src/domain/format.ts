/**
 * Formatters are built once and reused. `Intl.NumberFormat` is expensive to construct, and the
 * dashboard formats every visible row on every render — building one per cell was measurable.
 */
const currencyFormatters = new Map<string, Intl.NumberFormat>();

const timeFormatter = new Intl.DateTimeFormat(undefined, {
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hour12: false,
});

const dateTimeFormatter = new Intl.DateTimeFormat(undefined, {
  dateStyle: 'medium',
  timeStyle: 'medium',
});

export function formatAmount(amount: number, currency: string): string {
  let formatter = currencyFormatters.get(currency);

  if (!formatter) {
    try {
      formatter = new Intl.NumberFormat(undefined, {
        style: 'currency',
        currency,
        minimumFractionDigits: 2,
      });
    } catch {
      // An unrecognised ISO code must not take the dashboard down; show the bare number instead.
      formatter = new Intl.NumberFormat(undefined, { minimumFractionDigits: 2 });
    }

    currencyFormatters.set(currency, formatter);
  }

  return formatter.format(amount);
}

export function formatTime(isoTimestamp: string): string {
  const parsed = new Date(isoTimestamp);
  return Number.isNaN(parsed.getTime()) ? '—' : timeFormatter.format(parsed);
}

export function formatDateTime(isoTimestamp: string): string {
  const parsed = new Date(isoTimestamp);
  return Number.isNaN(parsed.getTime()) ? isoTimestamp : dateTimeFormatter.format(parsed);
}
