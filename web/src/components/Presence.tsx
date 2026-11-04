import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useAuth } from '../auth';

export interface PresenceInfo { online: boolean; lastSeenAt: string | null }

interface PresenceApi {
  data: Record<string, PresenceInfo>;
  watch: (id: string) => () => void;
  get: (id?: string | null) => PresenceInfo | undefined;
}

const empty: PresenceApi = { data: {}, watch: () => () => {}, get: () => undefined };
const Ctx = createContext<PresenceApi>(empty);

async function quiet(method: string, url: string) {
  const r = await fetch(url, { method, credentials: 'same-origin' });
  if (!r.ok) throw new Error(String(r.status));
  if (r.status === 204) return null;
  return r.json();
}

/** Keeps a short list of the people on screen and asks, every few seconds, whether they are still active. */
export function PresenceProvider({ children }: { children: ReactNode }) {
  const { me } = useAuth();
  const ids = useRef(new Map<string, number>());
  const [data, setData] = useState<Record<string, PresenceInfo>>({});
  const [rev, setRev] = useState(0);
  const watch = useCallback((id: string) => {
    ids.current.set(id, (ids.current.get(id) ?? 0) + 1);
    setRev((n) => n + 1);
    return () => {
      const left = (ids.current.get(id) ?? 1) - 1;
      if (left <= 0) ids.current.delete(id);
      else ids.current.set(id, left);
    };
  }, []);
  const get = useCallback((id?: string | null) => (id ? data[id] : undefined), [data]);

  useEffect(() => {
    if (!me) return undefined;
    let stop = false;
    const beat = () => quiet('POST', '/api/presence/heartbeat').then(() => {
      if (stop) return;
      setData((d) => ({ ...d, [me.id]: { online: true, lastSeenAt: new Date().toISOString() } }));
    }).catch(() => undefined);
    const poll = () => {
      const list = [...ids.current.keys()].slice(0, 80);
      if (list.length === 0) return;
      quiet('GET', `/api/presence?ids=${encodeURIComponent(list.join(','))}`).then((rows: { id: string; lastSeenAt: string | null; online: boolean }[] | null) => {
        if (stop || !rows) return;
        setData((d) => {
          const next = { ...d };
          for (const row of rows) next[row.id] = { online: row.online, lastSeenAt: row.lastSeenAt };
          return next;
        });
      }).catch(() => undefined);
    };
    beat();
    const soon = window.setTimeout(poll, 250);
    const hb = window.setInterval(beat, 45_000);
    const pl = window.setInterval(poll, 20_000);
    return () => { stop = true; window.clearTimeout(soon); window.clearInterval(hb); window.clearInterval(pl); };
  }, [me]);

  useEffect(() => {
    if (!me || rev === 0) return undefined;
    const t = window.setTimeout(() => {
      const list = [...ids.current.keys()].slice(0, 80);
      if (list.length === 0) return;
      quiet('GET', `/api/presence?ids=${encodeURIComponent(list.join(','))}`).then((rows: { id: string; lastSeenAt: string | null; online: boolean }[] | null) => {
        if (!rows) return;
        setData((d) => {
          const next = { ...d };
          for (const row of rows) next[row.id] = { online: row.online, lastSeenAt: row.lastSeenAt };
          return next;
        });
      }).catch(() => undefined);
    }, 200);
    return () => window.clearTimeout(t);
  }, [rev, me]);

  const value = useMemo(() => ({ data, watch, get }), [data, watch, get]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function usePresence() {
  return useContext(Ctx);
}
