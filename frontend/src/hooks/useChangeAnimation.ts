import { useEffect, useRef } from 'react';
import type { ChangeKind } from '../realtime/transactionStore.ts';
import { usePrefersReducedMotion } from './usePrefersReducedMotion.ts';

const ENTER_DURATION_MS = 420;
const PULSE_DURATION_MS = 900;

/**
 * Plays a row's arrival or status-change animation.
 *
 * **Why the Web Animations API and not a CSS class.** A CSS animation will not replay just
 * because a prop changed — retriggering it means removing the class, forcing a reflow, and
 * adding it back, which is both fragile and a guaranteed layout thrash on every update.
 * `element.animate()` starts a fresh animation on every call, so re-running the effect is all
 * that is needed, and the returned handle makes cancellation on unmount trivial.
 *
 * **What is animated, and what is not.** `opacity` and `transform` are composited, so they cost
 * nothing on the main thread. `background-color` is a paint-only property; it is cheap here
 * because only rows that actually changed animate, never the whole table. Nothing that triggers
 * layout — height, margin, top — is animated at all, which is what keeps a hundred simultaneous
 * arrivals from stalling the tab.
 */
export function useChangeAnimation<T extends HTMLElement>(
  revision: number,
  change: ChangeKind,
): React.RefObject<T | null> {
  const ref = useRef<T>(null);
  const prefersReducedMotion = usePrefersReducedMotion();

  useEffect(() => {
    const element = ref.current;

    // `restored` rows came from a snapshot: known history, not news. Animating them would make
    // every reconnect look like a flood of new activity.
    if (!element || change === 'restored' || prefersReducedMotion) {
      return;
    }

    // Absent under jsdom, so the component stays testable without stubbing the animation API.
    if (typeof element.animate !== 'function') {
      return;
    }

    // Read the tint from CSS rather than hard-coding it here, so status colours live in exactly
    // one place and the animation always matches the badge beside it.
    const tint = getComputedStyle(element).getPropertyValue('--status-tint').trim() || 'transparent';

    const animation =
      change === 'created'
        ? element.animate(
            [
              { opacity: 0, transform: 'translateY(-8px)', backgroundColor: tint },
              { opacity: 1, transform: 'none', backgroundColor: 'transparent' },
            ],
            { duration: ENTER_DURATION_MS, easing: 'cubic-bezier(0.22, 1, 0.36, 1)' },
          )
        : element.animate(
            [
              { backgroundColor: 'transparent' },
              { backgroundColor: tint, offset: 0.15 },
              { backgroundColor: tint, offset: 0.4 },
              { backgroundColor: 'transparent' },
            ],
            { duration: PULSE_DURATION_MS, easing: 'ease-out' },
          );

    return () => animation.cancel();
  }, [revision, change, prefersReducedMotion]);

  return ref;
}
