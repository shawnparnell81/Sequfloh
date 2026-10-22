/** Open tabs: the pure parts (limits, storage, titles) kept apart from the React code so they stay easy to test. */
export interface WorkTab { id: string; url: string; title?: string; /** earlier pages of this tab, newest last */ back?: string[] }
export interface TabState { tabs: WorkTab[]; activeId: string }

export const HOME_ID = 'home';
export const MAX_TABS = 15;
/** The pinned first tab shows SEQUFLOH Home. */
export const HOME_URL = '/sequfloh';
export const homeTab = (): WorkTab => ({ id: HOME_ID, url: HOME_URL, title: 'Home' });

export const storageKey = (userId: string, org: string) => `sqf.tabs.v1.${org || 'org'}.${userId}`;
export const useTabsKey = (userId: string) => `sqf.useTabs.${userId}`;

let seq = 0;
export const newId = () => `t${Date.now().toString(36)}${(seq++).toString(36)}`;

export function loadState(key: string): TabState {
  try {
    const raw = JSON.parse(localStorage.getItem(key) ?? 'null') as TabState | null;
    if (raw && Array.isArray(raw.tabs)) {
      const rest = raw.tabs.filter((t) => t && t.id !== HOME_ID && typeof t.url === 'string' && t.url.startsWith('/')).slice(0, MAX_TABS - 1);
      const tabs = [homeTab(), ...rest];
      return { tabs, activeId: tabs.some((t) => t.id === raw.activeId) ? raw.activeId : HOME_ID };
    }
  } catch { /* unreadable storage starts fresh */ }
  return { tabs: [homeTab()], activeId: HOME_ID };
}

export function saveState(key: string, s: TabState) {
  try { localStorage.setItem(key, JSON.stringify(s)); } catch { /* storage full or blocked */ }
}

/** Same page, ignoring a trailing slash. */
export const samePage = (a: string, b: string) => a.replace(/\/(?=$|[?#])/, '') === b.replace(/\/(?=$|[?#])/, '');
/** The address of Home: SEQUFLOH Home, or the bare root that leads to it. */
export const isHome = (url: string) => url === '/' || samePage(url, HOME_URL);

/** A readable fallback title from the address, used until the page shows its own heading. */
export function titleFromUrl(url: string): string {
  const path = url.split(/[?#]/)[0];
  const parts = path.split('/').filter(Boolean);
  if (!parts.length || (parts.length === 1 && parts[0] === 'sequfloh')) return 'Home';
  const last = parts[parts.length - 1];
  const words = (s: string) => decodeURIComponent(s).replace(/[-_]/g, ' ').replace(/^\w/, (c) => c.toUpperCase());
  if (last === 'new') return `New ${words(parts[parts.length - 2] ?? 'record').replace(/s$/, '')}`;
  if (/^\d+$|^[0-9a-f-]{16,}$/i.test(last)) return `${words(parts[parts.length - 2] ?? 'Record')} ${last.length > 8 ? '' : `#${last}`}`.trim();
  return words(last);
}

/** Pages where typing is likely to be a draft worth keeping (new records and record pages, not list filters). */
export const holdsDrafts = (url: string) => {
  const path = url.split(/[?#]/)[0];
  return /\/new$/.test(path) || /\/(\d+|[0-9a-f-]{16,})(\/|$)/i.test(path) || /\/(settings|admin)\/[\w-]+$/.test(path) || /^\/account\//.test(path);
};

/**
 * Runs code that navigates (a row click handler, say) and returns the address it went to without moving the window there,
 * so the same handler can open its page in another tab.
 */
export function captureNavigation(run: () => void): string | null {
  const h = window.history;
  const push = h.pushState, replace = h.replaceState;
  let url: string | null = null;
  const grab = (_d: unknown, _t: string, u?: string | URL | null) => { if (u != null && url == null) { const x = new URL(String(u), window.location.href); url = x.pathname + x.search + x.hash; } };
  h.pushState = grab as typeof h.pushState;
  h.replaceState = grab as typeof h.replaceState;
  try { run(); } finally { h.pushState = push; h.replaceState = replace; }
  return url;
}
