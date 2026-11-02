import { useEffect, useRef, useState } from 'react';
import { Alert, Button, Snackbar } from '@mui/material';

const EVERY = 3 * 60_000;

async function running(): Promise<string | null> {
  try {
    const r = await fetch('/api/version', { cache: 'no-store', credentials: 'same-origin' });
    if (!r.ok) return null;
    return ((await r.json()) as { version?: string }).version ?? null;
  } catch { return null; }
}

/** Reloads the page past any cached copy of it; only ever run when the person clicks Refresh. */
export function refreshToLatest() {
  const u = new URL(window.location.href);
  u.searchParams.set('v', Date.now().toString(36));
  window.location.replace(u.toString());
}

/**
 * While someone is signed in, checks every few minutes and when the window gets focus whether a new version is live.
 * If it is, a banner offers a refresh. Nothing reloads by itself, so unsaved work is never lost; a dismissed banner comes back on a later check.
 */
export function UpdateBanner() {
  const loaded = useRef<string | null>(null);
  const [open, setOpen] = useState(false);

  useEffect(() => {
    let live = true;
    const check = async () => {
      const v = await running();
      if (!live || !v) return;
      if (loaded.current === null) { loaded.current = v; return; }
      if (v !== loaded.current) setOpen(true);
    };
    void check();
    const t = setInterval(check, EVERY);
    const onFocus = () => { void check(); };
    const onVisible = () => { if (document.visibilityState === 'visible') void check(); };
    window.addEventListener('focus', onFocus);
    document.addEventListener('visibilitychange', onVisible);
    return () => { live = false; clearInterval(t); window.removeEventListener('focus', onFocus); document.removeEventListener('visibilitychange', onVisible); };
  }, []);

  return (
    <Snackbar open={open} anchorOrigin={{ vertical: 'top', horizontal: 'center' }} sx={{ top: { xs: 64, sm: 72 }, '@media print': { display: 'none' } }}>
      <Alert severity="info" variant="filled" data-testid="update-banner" onClose={() => setOpen(false)}
        action={<><Button color="inherit" size="small" onClick={refreshToLatest} data-testid="update-refresh">Refresh</Button><Button color="inherit" size="small" onClick={() => setOpen(false)} data-testid="update-dismiss">Dismiss</Button></>}>
        A new version of Sequfloh is available. Refresh to get the latest update.
      </Alert>
    </Snackbar>
  );
}
