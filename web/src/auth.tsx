import { createContext, useContext, useEffect, type ReactNode } from 'react';
import { clearTabs } from './tabs/model';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post } from './api';
import { setBusinessZone } from './components/Status';

export interface Grant { permission: string; siteId: string | null; departmentId: string | null }
export interface Me { id: string; email: string; displayName: string; mustChangePassword: boolean; grants: Grant[] }

interface AuthState {
  me: Me | null;
  needsSetup: boolean;
  loading: boolean;
  can: (permission: string) => boolean;
  login: (email: string, password: string, rememberMe?: boolean) => Promise<void>;
  refresh: () => Promise<void>;
  logout: () => Promise<void>;
}

const Ctx = createContext<AuthState>(null!);

// Closing the browser always signs out, even when the browser brings session cookies back ("Continue where you left off").
// Each open tab keeps a fresh mark in sessionStorage; a reload finds its own recent mark and a new tab asks the open ones.
const MARK = 'sqf.open';
const markOpen = () => sessionStorage.setItem(MARK, String(Date.now()));
const channel = typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel('sqf.open');
if (channel) channel.onmessage = (e) => { if (e.data === 'ask' && sessionStorage.getItem(MARK)) channel.postMessage('open'); };
setInterval(() => { if (sessionStorage.getItem(MARK)) markOpen(); }, 10_000);
window.addEventListener('pagehide', () => { if (sessionStorage.getItem(MARK)) markOpen(); });
let checked = false;
async function browserStillOpen(): Promise<boolean> {
  if (Date.now() - Number(sessionStorage.getItem(MARK) ?? 0) < 30_000) return true;
  if (!channel) return false;
  return new Promise((done) => {
    const t = setTimeout(() => { channel.removeEventListener('message', on); done(false); }, 400);
    const on = (e: MessageEvent) => { if (e.data === 'open') { clearTimeout(t); channel.removeEventListener('message', on); done(true); } };
    channel.addEventListener('message', on);
    channel.postMessage('ask');
  });
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const qc = useQueryClient();
  const q = useQuery({
    queryKey: ['me'],
    // signed out is a normal state: this endpoint answers 200 either way
    queryFn: async () => {
      if (!checked) {
        checked = true;
        if (await browserStillOpen()) markOpen();
        else { sessionStorage.removeItem(MARK); await post('/api/auth/logout').catch(() => {}); }
      }
      try { return await get<{ authenticated: boolean; needsSetup: boolean; user: Me | null }>('/api/auth/session'); } catch { return { authenticated: false, needsSetup: false, user: null }; } },
    staleTime: 60_000,
  });
  useEffect(() => {
    const out = () => qc.invalidateQueries({ queryKey: ['me'] });
    window.addEventListener('sqf:signed-out', out);
    return () => window.removeEventListener('sqf:signed-out', out);
  }, [qc]);
  const me = q.data?.user ?? null;
  const state: AuthState = {
    me,
    needsSetup: !!q.data?.needsSetup,
    loading: q.isLoading,
    can: (p) => !!me?.grants.some((g) => g.permission === p),
    login: async (email, password, rememberMe = false) => { await post('/api/auth/login', { email, password, rememberMe }); markOpen(); clearTabs(); await qc.invalidateQueries(); },
    refresh: async () => { await qc.invalidateQueries(); },
    logout: async () => {
      await post('/api/auth/logout');
      sessionStorage.removeItem(MARK);
      clearTabs();
      await qc.cancelQueries();
      qc.setQueryData(['me'], { authenticated: false, needsSetup: false, user: null });
      qc.removeQueries({ predicate: (x) => x.queryKey[0] !== 'me' });
    },
  };
  return <Ctx.Provider value={state}>{children}</Ctx.Provider>;
}

export const useAuth = () => useContext(Ctx);

export interface Branding { applicationName: string; organizationName: string; primaryColor: string | null; timeZone?: string }
export const useBranding = () =>
  useQuery({ queryKey: ['branding'], queryFn: async () => { const b = await get<Branding>('/api/organization/branding'); setBusinessZone(b.timeZone); return b; }, staleTime: 300_000 });
