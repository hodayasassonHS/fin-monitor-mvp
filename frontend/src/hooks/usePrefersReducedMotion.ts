import { useEffect, useState } from 'react';

const QUERY = '(prefers-reduced-motion: reduce)';

function matches(): boolean {
  // `matchMedia` is absent under jsdom and in any non-browser host; assume motion is fine.
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia(QUERY).matches
    : false;
}

/**
 * Tracks the viewer's motion preference, and keeps tracking it.
 *
 * Read live rather than once at mount because the setting can change while the dashboard is
 * open — this is a screen people leave running all day, so a one-shot read would go stale.
 */
export function usePrefersReducedMotion(): boolean {
  const [prefersReducedMotion, setPrefersReducedMotion] = useState(matches);

  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
      return;
    }

    const query = window.matchMedia(QUERY);
    const onChange = (event: MediaQueryListEvent) => setPrefersReducedMotion(event.matches);

    query.addEventListener('change', onChange);
    return () => query.removeEventListener('change', onChange);
  }, []);

  return prefersReducedMotion;
}
