import { createContext, useContext, useEffect, type ReactNode } from 'react';
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
  login: (email: string, password: string) => Promise<void>;
  refresh: () => Promise<void>;
  logout: () => Promise<void>;
}

const Ctx = createContext<AuthState>(null!);

export function AuthProvider({ children }: { children: ReactNode }) {
  const qc = useQueryClient();
  const q = useQuery({
    queryKey: ['me'],
    // signed out is a normal state: this endpoint answers 200 either way
    queryFn: async () => { try { return await get<{ authenticated: boolean; needsSetup: boolean; user: Me | null }>('/api/auth/session'); } catch { return { authenticated: false, needsSetup: false, user: null }; } },
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
    login: async (email, password) => { await post('/api/auth/login', { email, password }); await qc.invalidateQueries(); },
    refresh: async () => { await qc.invalidateQueries(); },
    logout: async () => {
      await post('/api/auth/logout');
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
