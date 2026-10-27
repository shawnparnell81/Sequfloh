/**
 * Scanner detection. USB and Bluetooth scanners type like a keyboard: the code arrives as a fast burst of keys ending with Enter.
 * Pure logic so it can be checked on its own; the app wires it to window key events.
 */
export interface ScanOptions { minLength?: number; maxGapMs?: number }
export const SCAN_MIN_LENGTH = 6;
export const SCAN_MAX_GAP_MS = 50;

export interface KeyLike { key: string; ctrlKey?: boolean; altKey?: boolean; metaKey?: boolean }

/** Feed key events with their time; returns the scanned code when a burst ends with Enter, otherwise null. */
export function createScanDetector(opts: ScanOptions = {}) {
  const min = opts.minLength ?? SCAN_MIN_LENGTH, gap = opts.maxGapMs ?? SCAN_MAX_GAP_MS;
  let buf = '', last = 0, fast = true;
  return {
    feed(e: KeyLike, at: number): string | null {
      if (e.ctrlKey || e.altKey || e.metaKey) { buf = ''; return null; }
      if (e.key === 'Enter') {
        const code = buf.trim(), ok = fast && code.length >= min && at - last <= gap * 4;
        buf = ''; fast = true;
        return ok ? code : null;
      }
      if (e.key.length !== 1) return null;
      if (buf && at - last > gap) { buf = ''; fast = true; }
      buf += e.key; last = at;
      return null;
    },
    /** Characters collected so far (to tell whether Enter belongs to a burst). */
    get pending() { return buf; },
    reset() { buf = ''; },
  };
}

/** Whether keys typed here belong to a field (so a global scan must not take them). */
export function isTypingTarget(el: EventTarget | null): boolean {
  if (!(el instanceof HTMLElement)) return false;
  const tag = el.tagName;
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable;
}

/** Placeholder values that never identify anything. Prefix or suffix stripping for the label layout goes here once known. */
const PLACEHOLDERS = new Set(['/', '-', '--', 'n/a', 'na', 'none', 'tbd', '?', '0']);
export function normalizeScan(raw: string): string | null {
  const c = raw.trim();
  return !c || PLACEHOLDERS.has(c.toLowerCase()) ? null : c;
}
