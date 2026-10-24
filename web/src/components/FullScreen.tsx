import { useCallback, useEffect, useRef, useState } from 'react';
import { Button, DialogTitle, Typography } from '@mui/material';
import FullscreenOutlined from '@mui/icons-material/FullscreenOutlined';
import FullscreenExitOutlined from '@mui/icons-material/FullscreenExitOutlined';

/** What the toggle enlarges: the dialog it sits in, else the page's main area (title and toolbar included, menu left out), else the active tab. */
function rootFor(btn: HTMLElement): HTMLElement {
  return (btn.closest('.MuiDialog-paper') ?? btn.closest('[data-fullscreen-root]') ?? btn.closest('[data-testid^="tab-view-"]') ?? document.body) as HTMLElement;
}
function pageBackground() {
  for (const n of [document.querySelector('main'), document.getElementById('root'), document.body, document.documentElement]) {
    const c = n ? getComputedStyle(n).backgroundColor : '';
    if (c && c !== 'transparent' && !/rgba\(.*,\s*0\)$/.test(c)) return c;
  }
  return '#fff';
}
const SAVE = ['position', 'inset', 'zIndex', 'overflow', 'background', 'width', 'height', 'maxWidth', 'maxHeight', 'margin', 'padding'] as const;

/**
 * Full screen for a form or document. Uses the browser's full screen mode where it is allowed, otherwise fills the window with CSS. The same page
 * stays on screen, so the toolbar, unsaved edits and everything typed stay as they were; Exit full screen or Esc puts the layout and scroll
 * position back exactly.
 */
export function FullScreenButton({ size = 'medium' }: { size?: 'small' | 'medium' }) {
  const btn = useRef<HTMLButtonElement>(null);
  const saved = useRef<{ el: HTMLElement; style: Partial<Record<(typeof SAVE)[number], string>>; winY: number; elY: number; native: boolean; lifted: [HTMLElement, string][] } | null>(null);
  const [on, setOn] = useState(false);

  const restore = useCallback(() => {
    const s = saved.current; if (!s) return;
    saved.current = null;
    for (const k of SAVE) s.el.style[k] = s.style[k] ?? '';
    s.el.removeAttribute('data-fullscreen');
    for (const [p, z] of s.lifted) p.style.zIndex = z;
    setOn(false);
    // the browser settles the layout over a few frames after leaving full screen; put the scroll back until it sticks
    const put = () => { s.el.scrollTop = s.elY; window.scrollTo(0, s.winY); };
    requestAnimationFrame(put); for (const ms of [50, 150, 300, 600]) setTimeout(put, ms);
  }, []);

  const enter = async () => {
    const el = rootFor(btn.current!);
    const style: Partial<Record<(typeof SAVE)[number], string>> = {};
    for (const k of SAVE) style[k] = el.style[k];
    // a parent with its own stacking order would keep the sidebar and top bar painted above the page; lift those parents while full screen
    const lifted: [HTMLElement, string][] = [];
    for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
      const c = getComputedStyle(p);
      if (c.zIndex !== 'auto' || c.isolation === 'isolate') { lifted.push([p, p.style.zIndex]); p.style.zIndex = '1400'; }
    }
    saved.current = { el, style, winY: window.scrollY, elY: el.scrollTop, native: false, lifted };
    el.style.overflow = 'auto'; el.style.background = pageBackground();
    let native = false;
    if (document.fullscreenEnabled && el.requestFullscreen) { try { await el.requestFullscreen(); native = true; } catch { native = false; } }
    // the same full-window layout in both modes, so the page looks the same whether or not the browser allowed real full screen
    Object.assign(el.style, { position: 'fixed', inset: '0', zIndex: '1400', width: '100vw', height: '100vh', maxWidth: 'none', maxHeight: 'none', margin: '0', padding: '16px' });
    saved.current.native = native;
    el.setAttribute('data-fullscreen', native ? 'native' : 'css');
    setOn(true);
  };
  const exit = () => { if (saved.current?.native && document.fullscreenElement) void document.exitFullscreen(); else restore(); };

  useEffect(() => {
    if (!on) return;
    const onChange = () => { if (saved.current?.native && !document.fullscreenElement) restore(); };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape' && saved.current && !saved.current.native) { e.stopPropagation(); restore(); } };
    document.addEventListener('fullscreenchange', onChange);
    document.addEventListener('keydown', onKey, true);
    return () => { document.removeEventListener('fullscreenchange', onChange); document.removeEventListener('keydown', onKey, true); };
  }, [on, restore]);
  useEffect(() => () => { if (saved.current) { if (saved.current.native && document.fullscreenElement) void document.exitFullscreen(); restore(); } }, [restore]);

  return <Button ref={btn} size={size} data-action="fullscreen" data-testid="fullscreen-toggle" aria-pressed={on}
    startIcon={on ? <FullscreenExitOutlined /> : <FullscreenOutlined />} onClick={() => (on ? exit() : void enter())}>{on ? 'Exit full screen' : 'Full screen'}</Button>;
}

/** Title row for every form dialog: the title, then Full screen. e2e/record-actions.spec.ts fails if a dialog uses a plain DialogTitle. */
export function FormDialogTitle({ children }: { children: React.ReactNode }) {
  return <DialogTitle component="div" sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
    <Typography variant="h6" component="h2" sx={{ flex: 1 }}>{children}</Typography><FullScreenButton size="small" />
  </DialogTitle>;
}
