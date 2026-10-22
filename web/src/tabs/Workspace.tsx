import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useLocation, useNavigate, type Location } from 'react-router-dom';
import { useTheme } from '@mui/material/styles';
import { Box, Button, Dialog, DialogActions, DialogContent, DialogContentText, DialogTitle, IconButton, ListItemIcon, ListItemText, Menu, MenuItem, Snackbar, Tooltip, Typography, useMediaQuery } from '@mui/material';
import Close from '@mui/icons-material/Close';
import HomeOutlined from '@mui/icons-material/HomeOutlined';
import ArrowBack from '@mui/icons-material/ArrowBack';
import ExpandMore from '@mui/icons-material/ExpandMore';
import TabOutlined from '@mui/icons-material/TabOutlined';
import OpenInNew from '@mui/icons-material/OpenInNew';
import ArticleOutlined from '@mui/icons-material/ArticleOutlined';
import { useAuth, useBranding } from '../auth';
import { modules, sectionOf } from '../modules/registry';
import { HOME_ID, HOME_URL, MAX_TABS, captureNavigation, holdsDrafts, isHome, loadState, newId, samePage, saveState, storageKey, titleFromUrl, useTabsKey, type TabState, type WorkTab } from './model';

interface TabsApi {
  enabled: boolean;
  /** Opens an address in another tab; stays on the current one unless `show` is set. */
  openInTab: (url: string, show?: boolean) => void;
  /** Runs a navigating handler and opens where it went in another tab. */
  openHandlerInTab: (run: () => void) => void;
  /** Shows the "open in new tab" menu for an address or handler at the pointer. */
  showOpenMenu: (e: React.MouseEvent, target: string | (() => void)) => void;
  setTitle: (tabId: string, title: string) => void;
  goBack: () => void;
  goHome: () => void;
  canGoBack: boolean;
}

const Ctx = createContext<TabsApi | null>(null);
const TabIdCtx = createContext<string | null>(null);
export const useTabs = () => useContext(Ctx);

/** Pages call this with their heading so their tab reads e.g. "VWI-2026-0004 rev A — Seal leak validation". */
export function useTabTitle(title: string | undefined) {
  const api = useContext(Ctx), id = useContext(TabIdCtx);
  useEffect(() => { if (api && id && title) api.setTitle(id, title); }, [api, id, title]);
}

/** Whether tabs are on for this person on this device (Your account › Preferences). On unless switched off. */
export function useTabsSetting(): [boolean, (on: boolean) => void] {
  const { me } = useAuth();
  const key = useTabsKey(me?.id ?? 'anon');
  const [on, setOn] = useState(() => localStorage.getItem(key) !== '0');
  useEffect(() => {
    const h = () => setOn(localStorage.getItem(key) !== '0');
    window.addEventListener('sqf:tabs-setting', h); window.addEventListener('storage', h);
    return () => { window.removeEventListener('sqf:tabs-setting', h); window.removeEventListener('storage', h); };
  }, [key]);
  return [on, (v: boolean) => { localStorage.setItem(key, v ? '1' : '0'); window.dispatchEvent(new Event('sqf:tabs-setting')); }];
}

const moduleFor = (url: string) => {
  const path = url.split(/[?#]/)[0];
  let best: (typeof modules)[number] | undefined;
  for (const m of modules) if ((path === m.basePath || path.startsWith(m.basePath + '/')) && (!best || m.basePath.length > best.basePath.length)) best = m;
  return best;
};
/** The area of the application an address belongs to: its menu section (Work Instructions, Tracking, Reports...), else its first path parts. */
const areaOf = (url: string) => sectionOf(url)?.label ?? url.split(/[?#]/)[0].split('/').filter(Boolean).slice(0, 2).join('/');
const iconFor = (t: WorkTab) => t.id === HOME_ID ? <HomeOutlined sx={{ fontSize: 16 }} /> : sectionOf(t.url)?.icon ?? moduleFor(t.url)?.icon ?? <ArticleOutlined sx={{ fontSize: 16 }} />;
const navLabels = new Map<string, string>();
const collect = (items: { label: string; path: string; children?: { label: string; path: string }[] }[]) => items.forEach((i) => { if (!navLabels.has(i.path)) navLabels.set(i.path, i.label); if (i.children) collect(i.children); });
modules.forEach((m) => { navLabels.set(m.basePath, m.label); collect(m.sidebar); });
/** The page's own heading once shown; until then the menu name of the address, else one made from the address. */
const labelFor = (t: WorkTab) => t.title || navLabels.get(t.url.split(/[?#]/)[0]) || titleFromUrl(t.url);
const toLocation = (url: string, key: string): Location => {
  const u = new URL(url, 'http://x');
  return { pathname: u.pathname, search: u.search, hash: u.hash, state: null, key };
};

/**
 * Keeps several pages open as tabs under the top bar. Every tab stays mounted (hidden when not shown), so typing in
 * a half-filled form survives switching. The window address always shows the active tab; each tab keeps its own back history.
 */
export function TabbedWorkspace({ render }: { render: (location: Location | undefined) => ReactNode }) {
  const [on] = useTabsSetting();
  const { data: brand, isPending } = useBranding();
  if (!on) return <>{render(undefined)}</>;
  // the saved tabs belong to one user in one organization, so wait for the organization before reading them
  if (isPending) return null;
  const org = `${window.location.host}.${brand?.organizationName ?? ''}`;
  return <Tabbed key={org} org={org} render={render} />;
}

function Tabbed({ render, org }: { render: (location: Location) => ReactNode; org: string }) {
  const { me } = useAuth();
  const key = storageKey(me!.id, org);
  const loc = useLocation();
  const nav = useNavigate();
  const here = loc.pathname + loc.search + loc.hash;
  const [state, setState] = useState<TabState>(() => {
    const s = loadState(key);
    // a link opened from outside (bookmark, e-mail) lands in its own tab, or the tab already showing it
    const hit = s.tabs.find((t) => samePage(t.url, here));
    if (hit) return { ...s, activeId: hit.id };
    if (isHome(here)) return { ...s, activeId: HOME_ID };
    if (s.tabs.length >= MAX_TABS) { const tabs = s.tabs.map((t) => t.id === s.activeId && t.id !== HOME_ID ? { ...t, url: here, title: undefined, back: [] } : t); return { tabs, activeId: s.activeId === HOME_ID ? tabs[tabs.length - 1].id : s.activeId }; }
    const t: WorkTab = { id: newId(), url: here };
    return { tabs: [...s.tabs, t], activeId: t.id };
  });
  const [dirty, setDirty] = useState<Set<string>>(new Set());
  // tabs restored after a reload or opened in the background load when first shown
  const [mounted, setMounted] = useState<Set<string>>(() => new Set([state.activeId]));
  useEffect(() => { if (!mounted.has(state.activeId)) setMounted((m) => new Set(m).add(state.activeId)); }, [state.activeId, mounted]);
  const [notice, setNotice] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<WorkTab | null>(null);
  const [menu, setMenu] = useState<{ x: number; y: number; target: string | (() => void) } | null>(null);
  const goingBack = useRef(false);
  const stateRef = useRef(state); stateRef.current = state;
  // quick successive moves read the newest tabs, not the last rendered ones
  const commit = useCallback((n: TabState) => { stateRef.current = n; setState(n); }, []);
  const update = useCallback((fn: (s: TabState) => TabState) => { const n = fn(stateRef.current); if (n !== stateRef.current) commit(n); }, [commit]);

  useEffect(() => saveState(key, state), [key, state]);
  const active = state.tabs.find((t) => t.id === state.activeId) ?? state.tabs[0];

  // the window address moved (a link, a menu, the browser's own back/forward): the active tab follows it
  useEffect(() => {
    const s = stateRef.current;
    const cur = s.tabs.find((t) => t.id === s.activeId)!;
    const back = goingBack.current; goingBack.current = false;
    if (samePage(cur.url, here)) return;
    const hit = s.tabs.find((t) => samePage(t.url, here) && t.id !== cur.id);
    if (isHome(here) || hit && cur.id === HOME_ID) { commit({ ...s, activeId: hit?.id ?? HOME_ID }); return; }
    if (cur.id === HOME_ID) {
      if (s.tabs.length >= MAX_TABS) { setNotice(`You can have up to ${MAX_TABS} tabs open. This page opened in your last tab; close a tab to open more.`); const last = s.tabs[s.tabs.length - 1]; commit({ tabs: s.tabs.map((t) => t.id === last.id ? { ...t, url: here, title: undefined, back: [...(t.back ?? []), t.url].slice(-50) } : t), activeId: last.id }); return; }
      const t: WorkTab = { id: newId(), url: here, back: [] };
      commit({ tabs: [...s.tabs, t], activeId: t.id }); return;
    }
    // browser back to the page before: pop it instead of piling it on
    const stack = cur.back ?? [];
    const popping = back || (stack.length > 0 && samePage(stack[stack.length - 1], here));
    // going to another area of the application (top bar, Home tiles, search, account menu) opens it in a tab of its own,
    // the way it does from Home, so the page being worked on stays open; moving within an area stays in the tab
    if (!popping && areaOf(here) !== areaOf(cur.url)) {
      if (hit) { commit({ ...s, activeId: hit.id }); return; }
      if (s.tabs.length < MAX_TABS) {
        const at = s.tabs.findIndex((t) => t.id === cur.id) + 1;
        const t: WorkTab = { id: newId(), url: here, back: [] };
        const tabs = [...s.tabs]; tabs.splice(at, 0, t);
        commit({ tabs, activeId: t.id }); return;
      }
      setNotice(`You can have up to ${MAX_TABS} tabs open. This page opened in the current tab; close a tab to open more.`);
    }
    const nextBack = popping ? stack.slice(0, -1) : [...stack, cur.url].slice(-50);
    commit({ ...s, tabs: s.tabs.map((t) => t.id === cur.id ? { ...t, url: here, title: undefined, back: nextBack } : t) });
    setDirty((d) => { if (!d.has(cur.id)) return d; const n = new Set(d); n.delete(cur.id); return n; });
  }, [here]);

  const activate = useCallback((t: WorkTab) => { if (t.id !== stateRef.current.activeId) { commit({ ...stateRef.current, activeId: t.id }); nav(t.url); } }, [nav]);

  const openInTab = useCallback((url: string, show = false) => {
    const s = stateRef.current;
    // every open adds a tab, even for a page that is already open somewhere (a second blank form, say)
    if (s.tabs.length >= MAX_TABS) { setNotice(`You can have up to ${MAX_TABS} tabs open. Close one to open another.`); return; }
    const insertAt = Math.max(s.tabs.findIndex((t) => t.id === s.activeId) + 1, 1);
    const t: WorkTab = { id: newId(), url };
    const tabs = [...s.tabs]; tabs.splice(insertAt, 0, t);
    commit({ tabs, activeId: show ? t.id : s.activeId });
    if (show) nav(url);
  }, [activate, nav]);
  const openHandlerInTab = useCallback((run: () => void) => { const url = captureNavigation(run); if (url) openInTab(url); }, [openInTab]);

  const reallyClose = useCallback((tab: WorkTab) => {
    const s = stateRef.current;
    const i = s.tabs.findIndex((t) => t.id === tab.id);
    const tabs = s.tabs.filter((t) => t.id !== tab.id);
    setDirty((d) => { const n = new Set(d); n.delete(tab.id); return n; });
    setMounted((m) => { const n = new Set(m); n.delete(tab.id); return n; });
    if (s.activeId === tab.id) { const next = tabs[Math.min(i, tabs.length - 1)] ?? tabs[0]; commit({ tabs, activeId: next.id }); nav(next.url); }
    else commit({ ...s, tabs });
  }, [nav]);
  const close = useCallback((tab: WorkTab) => { if (tab.id === HOME_ID) return; if (dirty.has(tab.id)) setConfirm(tab); else reallyClose(tab); }, [dirty, reallyClose]);

  const goBack = useCallback(() => {
    const cur = stateRef.current.tabs.find((t) => t.id === stateRef.current.activeId)!;
    const prev = (cur.back ?? []).slice(-1)[0];
    if (!prev) return;
    goingBack.current = true;
    nav(prev);
  }, [nav]);
  const goHome = useCallback(() => { activate(stateRef.current.tabs[0]); }, [activate]);
  const setTitle = useCallback((id: string, title: string) => update((s) => s.tabs.some((t) => t.id === id && t.title !== title) ? { ...s, tabs: s.tabs.map((t) => t.id === id ? { ...t, title } : t) } : s), []);
  const showOpenMenu = useCallback((e: React.MouseEvent, target: string | (() => void)) => { e.preventDefault(); setMenu({ x: e.clientX, y: e.clientY, target }); }, []);

  // successful saves clear the "unsaved changes" mark of the tab being worked in
  useEffect(() => {
    const h = () => setDirty((d) => { const id = stateRef.current.activeId; if (!d.has(id)) return d; const n = new Set(d); n.delete(id); return n; });
    window.addEventListener('sqf:saved', h); return () => window.removeEventListener('sqf:saved', h);
  }, []);
  // leaving the application with unsaved typing in any tab asks first
  useEffect(() => {
    if (!dirty.size) return;
    const h = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = ''; };
    window.addEventListener('beforeunload', h); return () => window.removeEventListener('beforeunload', h);
  }, [dirty.size]);

  // Ctrl/Cmd-click and middle-click on links open them in another tab; Alt+W closes the tab, Alt+[ and Alt+] move between tabs
  useEffect(() => {
    const linkOf = (e: MouseEvent) => {
      const a = (e.target as HTMLElement | null)?.closest?.('a[href]') as HTMLAnchorElement | null;
      if (!a || a.target === '_blank' || a.hasAttribute('download') || a.closest('[data-tab-strip]')) return null;
      const u = new URL(a.href, window.location.href);
      if (u.origin !== window.location.origin || u.pathname.startsWith('/api/')) return null;
      return u.pathname + u.search + u.hash;
    };
    const click = (e: MouseEvent) => { if (!(e.ctrlKey || e.metaKey) || e.button !== 0) return; const url = linkOf(e); if (url) { e.preventDefault(); e.stopPropagation(); openInTab(url); } };
    const aux = (e: MouseEvent) => { if (e.button !== 1) return; const url = linkOf(e); if (url) { e.preventDefault(); e.stopPropagation(); openInTab(url); } };
    const down = (e: MouseEvent) => { if (e.button === 1 && linkOf(e)) e.preventDefault(); };
    const ctx = (e: MouseEvent) => { if (e.shiftKey) return; const url = linkOf(e); if (url) { e.preventDefault(); setMenu({ x: e.clientX, y: e.clientY, target: url }); } };
    const keys = (e: KeyboardEvent) => {
      if (!e.altKey || e.ctrlKey || e.metaKey) return;
      const s = stateRef.current, i = s.tabs.findIndex((t) => t.id === s.activeId);
      if (e.key.toLowerCase() === 'w' || e.code === 'KeyW') { e.preventDefault(); close(s.tabs[i]); }
      else if (e.key === ']' || e.code === 'BracketRight') { e.preventDefault(); activate(s.tabs[(i + 1) % s.tabs.length]); }
      else if (e.key === '[' || e.code === 'BracketLeft') { e.preventDefault(); activate(s.tabs[(i - 1 + s.tabs.length) % s.tabs.length]); }
    };
    document.addEventListener('click', click, true); document.addEventListener('auxclick', aux, true); document.addEventListener('mousedown', down, true);
    document.addEventListener('contextmenu', ctx); window.addEventListener('keydown', keys);
    return () => { document.removeEventListener('click', click, true); document.removeEventListener('auxclick', aux, true); document.removeEventListener('mousedown', down, true); document.removeEventListener('contextmenu', ctx); window.removeEventListener('keydown', keys); };
  }, [openInTab, close, activate]);

  const api = useMemo<TabsApi>(() => ({ enabled: true, openInTab, openHandlerInTab, showOpenMenu, setTitle, goBack, goHome, canGoBack: (active.back ?? []).length > 0 }),
    [openInTab, openHandlerInTab, showOpenMenu, setTitle, goBack, goHome, active.back]);

  const markDirty = (tab: WorkTab) => (e: React.FormEvent) => {
    const el = e.target as HTMLInputElement;
    if (!holdsDrafts(tab.url) || el.type === 'search' || /search|filter/i.test(el.placeholder ?? '') || /search|filter/i.test(el.getAttribute?.('aria-label') ?? '')) return;
    if (!dirty.has(tab.id)) setDirty((d) => new Set(d).add(tab.id));
  };

  return (
    <Ctx.Provider value={api}>
      <TabStrip tabs={state.tabs} activeId={state.activeId} dirty={dirty} onActivate={activate} onClose={close}
        onReorder={(from, to) => update((s) => { const tabs = [...s.tabs]; const [m] = tabs.splice(from, 1); tabs.splice(Math.max(1, to), 0, m); return { ...s, tabs }; })} />
      {state.tabs.filter((t) => mounted.has(t.id) || t.id === state.activeId).map((t) => (
        <TabIdCtx.Provider key={t.id} value={t.id}>
          <Box data-testid={`tab-view-${t.id}`} data-active={t.id === state.activeId} hidden={t.id !== state.activeId} onInput={markDirty(t)} onChange={markDirty(t)}
            sx={{ display: t.id === state.activeId ? 'block' : 'none' }}>
            <TitleWatcher tab={t} />
            {render(t.id === state.activeId && samePage(t.url, here) ? { ...toLocation(t.url, t.id), state: loc.state, key: loc.key } : toLocation(t.url, t.id))}
          </Box>
        </TabIdCtx.Provider>
      ))}
      <Menu open={!!menu} onClose={() => setMenu(null)} anchorReference="anchorPosition" anchorPosition={menu ? { top: menu.y, left: menu.x } : undefined}>
        <MenuItem data-testid="open-in-new-tab" onClick={() => { const t = menu!.target; setMenu(null); typeof t === 'string' ? openInTab(t) : openHandlerInTab(t); }}>
          <ListItemIcon><TabOutlined fontSize="small" /></ListItemIcon><ListItemText>Open in new tab</ListItemText>
        </MenuItem>
        <MenuItem onClick={() => { const t = menu!.target; setMenu(null); const url = typeof t === 'string' ? t : captureNavigation(t); if (url) window.open(url, '_blank', 'noopener'); }}>
          <ListItemIcon><OpenInNew fontSize="small" /></ListItemIcon><ListItemText>Open in new browser window</ListItemText>
        </MenuItem>
      </Menu>
      <Dialog open={!!confirm} onClose={() => setConfirm(null)} data-testid="unsaved-dialog">
        <DialogTitle>Close {confirm ? labelFor(confirm) : ''}?</DialogTitle>
        <DialogContent><DialogContentText>You have changes in this tab that haven't been saved. Closing the tab throws them away.</DialogContentText></DialogContent>
        <DialogActions>
          <Button onClick={() => setConfirm(null)} variant="contained" autoFocus>Keep it open</Button>
          <Button color="error" onClick={() => { const t = confirm!; setConfirm(null); reallyClose(t); }}>Close without saving</Button>
        </DialogActions>
      </Dialog>
      <Snackbar open={!!notice} autoHideDuration={5000} onClose={() => setNotice(null)} message={notice} anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }} data-testid="tabs-notice" />
    </Ctx.Provider>
  );
}

/** Pages without their own title call fall back to the first heading on the page. */
function TitleWatcher({ tab }: { tab: WorkTab }) {
  const api = useContext(Ctx);
  const ref = useRef<HTMLSpanElement>(null);
  useEffect(() => {
    const view = ref.current?.parentElement; if (!view || !api) return;
    let timer = 0;
    const read = () => { const h = view.querySelector('h1, h2, h3'); const text = h?.textContent?.trim(); if (text && !tab.title) api.setTitle(tab.id, text.slice(0, 80)); };
    const obs = new MutationObserver(() => { clearTimeout(timer); timer = window.setTimeout(read, 250); });
    obs.observe(view, { childList: true, subtree: true });
    read();
    return () => { obs.disconnect(); clearTimeout(timer); };
  }, [api, tab.id, tab.url, tab.title]);
  return <span ref={ref} hidden />;
}

function TabStrip({ tabs, activeId, dirty, onActivate, onClose, onReorder }: {
  tabs: WorkTab[]; activeId: string; dirty: Set<string>; onActivate: (t: WorkTab) => void; onClose: (t: WorkTab) => void; onReorder: (from: number, to: number) => void;
}) {
  const tk = useTheme().tk;
  const narrow = useMediaQuery('(max-width:599.95px)');
  const scroller = useRef<HTMLDivElement>(null);
  const [overflow, setOverflow] = useState(false);
  const [listEl, setListEl] = useState<HTMLElement | null>(null);
  const [drag, setDrag] = useState<number | null>(null);
  const active = tabs.find((t) => t.id === activeId) ?? tabs[0];
  useEffect(() => {
    const el = scroller.current; if (!el) return;
    const check = () => setOverflow(el.scrollWidth > el.clientWidth + 2);
    check(); const ro = new ResizeObserver(check); ro.observe(el); return () => ro.disconnect();
  }, [tabs.length, narrow]);
  useEffect(() => { scroller.current?.querySelector<HTMLElement>('[data-active="true"]')?.scrollIntoView({ block: 'nearest', inline: 'nearest' }); }, [activeId]);

  const list = (
    <Menu anchorEl={listEl} open={!!listEl} onClose={() => setListEl(null)} slotProps={{ paper: { sx: { maxHeight: 440, minWidth: 260 } } }} data-testid="tabs-menu">
      {tabs.map((t) => (
        <MenuItem key={t.id} selected={t.id === activeId} onClick={() => { setListEl(null); onActivate(t); }} sx={{ gap: 1 }}>
          <ListItemIcon sx={{ minWidth: 0, color: 'primary.main' }}>{iconFor(t)}</ListItemIcon>
          <ListItemText primaryTypographyProps={{ noWrap: true, fontSize: 13, maxWidth: 260 }}>{labelFor(t)}{dirty.has(t.id) ? ' •' : ''}</ListItemText>
          {t.id !== HOME_ID && <IconButton size="small" aria-label={`Remove ${labelFor(t)} tab`} onClick={(e) => { e.stopPropagation(); onClose(t); }}><Close sx={{ fontSize: 14 }} /></IconButton>}
        </MenuItem>
      ))}
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', px: 2, pt: 1 }}>{tabs.length} of {MAX_TABS} tabs</Typography>
    </Menu>
  );

  const shell = { position: 'sticky' as const, top: 56, zIndex: 1090, display: 'flex', alignItems: 'flex-end', gap: 0.5, px: { xs: 1, sm: 2 }, pt: 0.75, minHeight: 40,
    background: tk.glass.side, backdropFilter: 'blur(16px) saturate(150%)', borderBottom: 1, borderColor: 'divider', '@media print': { display: 'none' } };

  if (narrow) return (
    <Box data-tab-strip data-testid="tab-strip" sx={{ ...shell, alignItems: 'center', pb: 0.5 }}>
      <Button data-testid="tabs-switcher" onClick={(e) => setListEl(e.currentTarget)} endIcon={<ExpandMore />} startIcon={iconFor(active)}
        sx={{ textTransform: 'none', flex: 1, justifyContent: 'flex-start', minWidth: 0, color: 'text.primary', borderRadius: 999, bgcolor: tk.glass.field, border: 1, borderColor: 'divider', px: 1.5 }}>
        <Box component="span" sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', flex: 1, textAlign: 'left' }}>{labelFor(active)}</Box>
        <Box component="span" sx={{ ml: 1, px: 0.75, borderRadius: 999, fontSize: 11, fontWeight: 700, color: '#fff', background: 'linear-gradient(90deg, #1257C4, #0E8550)' }}>{tabs.length}</Box>
      </Button>
      {active.id !== HOME_ID && <IconButton aria-label="Remove tab" onClick={() => onClose(active)}><Close fontSize="small" /></IconButton>}
      {list}
    </Box>
  );

  return (
    <Box component="nav" data-tab-strip data-testid="tab-strip" aria-label="Open tabs" sx={shell}>
      <Box ref={scroller} sx={{ display: 'flex', alignItems: 'flex-end', gap: 0.5, overflowX: 'auto', overflowY: 'hidden', flex: 1, minWidth: 0, scrollbarWidth: 'none', '&::-webkit-scrollbar': { display: 'none' } }}>
        {tabs.map((t, i) => {
          const on = t.id === activeId, pinned = t.id === HOME_ID;
          return (
            <Box key={t.id} aria-current={on ? 'page' : undefined} aria-selected={on} data-active={on} data-testid={`tab-${pinned ? 'home' : i}`} data-tab-url={t.url} tabIndex={0}
              title={labelFor(t)} draggable={!pinned}
              onClick={() => onActivate(t)} onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onActivate(t); } else if (e.key === 'Delete') { e.preventDefault(); onClose(t); } }}
              onAuxClick={(e) => { if (e.button === 1) { e.preventDefault(); onClose(t); } }} onMouseDown={(e) => { if (e.button === 1) e.preventDefault(); }}
              onDragStart={(e) => { setDrag(i); e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', String(i)); }}
              onDragOver={(e) => { if (drag != null) e.preventDefault(); }} onDrop={(e) => { e.preventDefault(); if (drag != null && drag !== i) onReorder(drag, i); setDrag(null); }} onDragEnd={() => setDrag(null)}
              sx={{ position: 'relative', display: 'flex', alignItems: 'center', gap: 0.75, flexShrink: 0, cursor: 'pointer', userSelect: 'none',
                width: pinned ? 'auto' : { sm: 150, md: 190 }, px: pinned ? 1.25 : 1.25, pr: pinned ? 1.25 : 0.5, height: 32, fontSize: 12.5, fontWeight: on ? 700 : 500,
                borderRadius: '10px 10px 0 0', border: 1, borderBottom: 0, borderColor: on ? 'divider' : 'transparent',
                bgcolor: on ? 'background.paper' : 'transparent', color: on ? 'text.primary' : 'text.secondary', opacity: drag === i ? 0.4 : 1, mb: '-1px',
                '&:hover': { bgcolor: on ? 'background.paper' : tk.glass.field, color: 'text.primary' },
                '&:focus-visible': { outline: '2px solid #1257C4', outlineOffset: -2 },
                '&::before': on ? { content: '""', position: 'absolute', left: 8, right: 8, top: 0, height: 3, borderRadius: '0 0 3px 3px', background: 'linear-gradient(90deg, #1257C4, #0E8550)' } : {} }}>
              <Box sx={{ display: 'flex', color: on ? 'primary.main' : 'inherit' }}>{iconFor(t)}</Box>
              {!pinned && <Box component="span" sx={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{labelFor(t)}</Box>}
              {pinned && <Box component="span" sx={{ display: { xs: 'none', md: 'inline' } }}>Home</Box>}
              {!pinned && (dirty.has(t.id)
                ? <Tooltip title="Unsaved changes — click to close"><Box component="span" aria-hidden data-testid="tab-close" onClick={(e) => { e.stopPropagation(); onClose(t); }}
                    sx={{ width: 20, height: 20, border: 0, borderRadius: 999, bgcolor: 'transparent', cursor: 'pointer', display: 'grid', placeItems: 'center', '& .dot': { width: 8, height: 8, borderRadius: 999, bgcolor: '#0E8550' }, '& svg': { display: 'none' }, '&:hover .dot': { display: 'none' }, '&:hover svg': { display: 'block' } }}>
                    <span className="dot" /><Close sx={{ fontSize: 14 }} /></Box></Tooltip>
                : <Box component="span" aria-hidden data-testid="tab-close" onClick={(e) => { e.stopPropagation(); onClose(t); }}
                    sx={{ width: 20, height: 20, border: 0, borderRadius: 999, bgcolor: 'transparent', color: 'inherit', cursor: 'pointer', display: 'grid', placeItems: 'center', opacity: on ? 0.8 : 0.5, '&:hover': { opacity: 1, bgcolor: tk.glass.field } }}>
                    <Close sx={{ fontSize: 14 }} /></Box>)}
            </Box>
          );
        })}
      </Box>
      {(overflow || tabs.length > 6) && (
        <Tooltip title="All open tabs"><Button data-testid="tabs-overflow" size="small" onClick={(e) => setListEl(e.currentTarget)} endIcon={<ExpandMore />}
          sx={{ mb: 0.5, flexShrink: 0, textTransform: 'none', borderRadius: 999, minWidth: 0, px: 1.25, fontSize: 12, color: 'text.secondary' }}>{tabs.length}</Button></Tooltip>
      )}
      {list}
    </Box>
  );
}

/**
 * Back and Home for the page being viewed. Back walks this tab's own history, so switching tabs switches what Back does;
 * Home brings up the pinned Home tab. Without tabs, Back follows the browser history of the window.
 */
export function PageNav() {
  const tabs = useTabs();
  const nav = useNavigate();
  const tk = useTheme().tk;
  const canBack = tabs ? tabs.canGoBack : window.history.length > 1;
  const btn = { width: 30, height: 30, border: 1, borderColor: 'divider', bgcolor: tk.glass.field, color: 'primary.main', backdropFilter: 'blur(8px)',
    '&:hover': { bgcolor: 'background.paper', borderColor: 'primary.main' }, '&.Mui-disabled': { opacity: 0.4 } };
  return (
    <Box data-testid="page-nav" sx={{ display: 'inline-flex', gap: 0.75, alignItems: 'center', mr: 1.5, '@media print': { display: 'none' } }}>
      <Tooltip title={canBack ? 'Back' : 'Nothing to go back to in this tab'}><span>
        <IconButton size="small" aria-label="Back" data-testid="page-back" disabled={!canBack} onClick={() => tabs ? tabs.goBack() : nav(-1)} sx={btn}><ArrowBack sx={{ fontSize: 17 }} /></IconButton>
      </span></Tooltip>
      <Tooltip title="Home"><IconButton size="small" aria-label="Home" data-testid="page-home" onClick={() => tabs ? tabs.goHome() : nav(HOME_URL)} sx={btn}><HomeOutlined sx={{ fontSize: 17 }} /></IconButton></Tooltip>
    </Box>
  );
}
