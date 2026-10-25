import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { Alert, Box, Button, Checkbox, Chip, CircularProgress, Dialog, DialogActions, DialogContent, DialogTitle, IconButton, MenuItem, Paper, Stack, Table, TableBody, TableCell, TableContainer,
  TableHead, TablePagination, TableRow, TableSortLabel, TextField, Tooltip, Typography, alpha } from '@mui/material';
import PushPinOutlined from '@mui/icons-material/PushPinOutlined';
import PushPin from '@mui/icons-material/PushPin';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post } from '../api';
import { useAuth } from '../auth';
import { modules } from '../modules/registry';
import { useTabs } from '../tabs/Workspace';
import { ModuleWorkspace, type Crumb } from '../layout/ModuleWorkspace';
import { ListToolbar } from '../layout/PageHeader';
import { mono } from '../theme';

export const S = '/api/sequfloh';
export const base = '/sequfloh';
export const mod = () => modules.find((m) => m.key === 'sequfloh')!;
export const nul = (v?: string | null) => (v === undefined || v === null || String(v).trim() === '' ? null : String(v).trim());
export const numOrNull = (v?: string | number | null) => (v === '' || v === undefined || v === null || Number.isNaN(Number(v)) ? null : Number(v));
export const Mono = ({ children }: { children: ReactNode }) => <span style={{ fontFamily: mono, whiteSpace: 'nowrap' }}>{children}</span>;
export const fmtMinutes = (m?: number | null) => (m === null || m === undefined ? '—' : `${Math.floor(m / 60)}h ${String(Math.round(m % 60)).padStart(2, '0')}m`);
export const fmtSeconds = (s?: number | null) => {
  if (s === null || s === undefined) return '—';
  const v = Math.max(0, Math.floor(s));
  return `${String(Math.floor(v / 3600)).padStart(2, '0')}:${String(Math.floor((v % 3600) / 60)).padStart(2, '0')}:${String(v % 60).padStart(2, '0')}`;
};
/** Splits a PascalCase status into words (InDevelopment → In Development). */
export const words = (s?: string | null) => (s ? s.replace(/([a-z])([A-Z])/g, '$1 $2') : '—');

export interface SqfContext {
  user: { id: string; displayName: string; email: string; departmentId: string | null };
  roles: string[]; sites: { id: string; code: string; name: string }[]; permissions: string[]; organization: string | null; application: string | null;
  module: { key: string; displayName: string; subtitle: string; description: string; moduleEnabled: boolean; recordsPerPage: number; favoritesEnabled: boolean; defaultLandingPage: string };
}
export const useSqf = () => useQuery({ queryKey: ['sqf-context'], queryFn: () => get<SqfContext>(`${S}/context`), staleTime: 60_000 });
export const useOptions = (list: string) => useQuery({ queryKey: ['sqf-options', list], queryFn: () => get<string[]>(`${S}/reference/${list}/options`), staleTime: 60_000 });

const statusColor: Record<string, 'default' | 'primary' | 'success' | 'warning' | 'error' | 'info'> = {
  Draft: 'default', Released: 'success', Superseded: 'default',
  Obsolete: 'default', NotStarted: 'default', Assigned: 'info', InProgress: 'primary', Paused: 'warning', OnHold: 'warning',
  Completed: 'success', Failed: 'error', Canceled: 'default', Pass: 'success', Fail: 'error', Conditional: 'warning', Pending: 'default', Active: 'success', Inactive: 'default',
  NotApplicable: 'default', Accepted: 'success',
};
/** Status chip in the shared chip style; colours follow the SEQUFLOH display settings mapping. */
export const SqfStatus = ({ status, colors }: { status?: string | null; colors?: Record<string, string> }) =>
  status ? <Chip size="small" variant={status === 'Released' ? 'filled' : 'outlined'} color={((colors?.[status] as typeof statusColor[string]) ?? statusColor[status] ?? 'default')} label={words(status)} /> : <span>—</span>;

/** A SEQUFLOH page inside the app shell: module menu, breadcrumbs, title, description, organization and user context, pin, recent tracking. */
export function SqfPage({ title, description, crumbs, actions, tabs, tab, onTab, children, permission, recent }: {
  title: ReactNode; description?: string; crumbs: Crumb[]; actions?: ReactNode; tabs?: { label: string; value: string }[]; tab?: string; onTab?: (v: string) => void; children?: ReactNode;
  permission?: string; recent?: { label: string; itemType: string; recordId?: string | null } | null;
}) {
  const { can } = useAuth();
  const ctx = useSqf();
  const loc = useLocation();
  const qc = useQueryClient();
  const path = loc.pathname + loc.search;
  const items = useQuery({ queryKey: ['sqf-my-items'], queryFn: () => get<{ recent: Item[]; favorites: Item[] }>(`${S}/my-items`), enabled: can('sequfloh.access') });
  const pinned = items.data?.favorites.find((f) => f.path === path);
  const label = recent?.label ?? (typeof title === 'string' ? title : crumbs[crumbs.length - 1]?.label ?? 'SEQUFLOH');
  const touched = useRef('');
  useEffect(() => {
    if (!can('sequfloh.access') || touched.current === path + label || (permission && !can(permission))) return;
    touched.current = path + label;
    post(`${S}/my-items`, { kind: 'recent', itemType: recent?.itemType ?? 'Page', recordId: recent?.recordId ?? null, path, label }).then(() => qc.invalidateQueries({ queryKey: ['sqf-my-items'] })).catch(() => undefined);
  }, [path, label]); // eslint-disable-line react-hooks/exhaustive-deps
  const pin = async () => {
    if (pinned) await del(`${S}/my-items/favorites/${pinned.id}`);
    else await post(`${S}/my-items`, { kind: 'favorite', itemType: recent?.itemType ?? 'Page', recordId: recent?.recordId ?? null, path, label });
    qc.invalidateQueries({ queryKey: ['sqf-my-items'] });
  };
  const denied = !can('sequfloh.access') || (permission && !can(permission));
  const c = ctx.data;
  return (
    <ModuleWorkspace module={mod()} crumbs={crumbs} title={title}
      actions={<Stack direction="row" gap={1} alignItems="center" flexWrap="wrap" justifyContent="flex-end" sx={{ displayPrint: 'none' }}>{actions}
        {!denied && (c?.module.favoritesEnabled ?? true) && <Tooltip title={pinned ? 'Unpin from SEQUFLOH Home' : 'Pin to SEQUFLOH Home'}><IconButton aria-label={pinned ? 'Unpin page' : 'Pin page'} onClick={pin}>{pinned ? <PushPin color="primary" /> : <PushPinOutlined />}</IconButton></Tooltip>}</Stack>}
      subtitle={description}
      meta={c && <>
        <Chip size="small" color="primary" variant="outlined" label={`${c.module.displayName} · ${c.module.subtitle}`} />
        {c.organization && <Chip size="small" variant="outlined" label={c.organization} />}
        {c.sites.length > 0 && <Chip size="small" variant="outlined" label={c.sites.map((s) => s.code).join(', ')} />}
        <Chip size="small" variant="outlined" label={`${c.user.displayName}${c.roles.length ? ' · ' + c.roles.join(', ') : ''}`} />
        {import.meta.env.DEV && <Chip size="small" color="warning" variant="outlined" label="Development" />}
      </>}
      tabs={tabs} tab={tab} onTab={onTab}>
      {denied ? <Alert severity="warning">You do not have permission to open this SEQUFLOH page. Ask a SEQUFLOH administrator for module access.</Alert>
        : c && !c.module.moduleEnabled ? <><Alert severity="info" sx={{ mb: 2 }}>SEQUFLOH is switched off for this organization. Only SEQUFLOH settings administrators can open it.</Alert>{children}</>
        : children}
    </ModuleWorkspace>
  );
}
interface Item { id: string; itemType: string; recordId: string | null; path: string; label: string; lastOpenedAt: string }

export const Loading = () => <Stack alignItems="center" sx={{ py: 6 }}><CircularProgress size={28} aria-label="Loading" /></Stack>;
export const ErrorState = ({ error, retry }: { error: unknown; retry?: () => void }) =>
  <Alert severity="error" action={retry && <Button color="inherit" size="small" onClick={retry}>Retry</Button>}>{(error as Error)?.message ?? 'Something went wrong.'}</Alert>;
export const Empty = ({ text, action }: { text: string; action?: ReactNode }) =>
  <Paper variant="outlined" sx={{ p: 4, textAlign: 'center' }}><Typography color="text.secondary" sx={{ mb: action ? 2 : 0 }}>{text}</Typography>{action}</Paper>;

/** Confirmation dialog; when a reason is required the confirm button stays disabled until one is entered. */
export function Confirm({ open, title, text, confirm, danger, reason, reasonLabel, reasonOptions, onClose, onConfirm }: {
  open: boolean; title: string; text: string; confirm: string; danger?: boolean; reason?: 'required' | 'optional'; reasonLabel?: string; reasonOptions?: string[];
  onClose: () => void; onConfirm: (reason: string) => Promise<unknown>;
}) {
  const [r, setR] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} fullWidth maxWidth="sm" TransitionProps={{ onEnter: () => { setR(''); setError(null); } }}>
      <DialogTitle>{title}</DialogTitle>
      <DialogContent>
        <Typography variant="body2" sx={{ mb: 2 }}>{text}</Typography>
        {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
        {reason && reasonOptions && reasonOptions.length > 0 && <TextField select fullWidth label={reasonLabel ?? 'Reason'} value={reasonOptions.includes(r.split(': ')[0]) ? r.split(': ')[0] : ''} sx={{ mb: 2 }}
          onChange={(e) => setR(e.target.value)}>{reasonOptions.map((o) => <MenuItem key={o} value={o}>{o}</MenuItem>)}</TextField>}
        {reason && <TextField fullWidth multiline minRows={2} label={reasonOptions?.length ? 'Comment' : (reasonLabel ?? (reason === 'required' ? 'Reason (required)' : 'Comment'))} value={r} onChange={(e) => setR(e.target.value)} />}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>Cancel</Button>
        <Button variant="contained" color={danger ? 'error' : 'primary'} disabled={busy || (reason === 'required' && !r.trim())}
          onClick={async () => { setBusy(true); setError(null); try { await onConfirm(r.trim()); onClose(); } catch (e) { setError((e as Error).message); } finally { setBusy(false); } }}>{confirm}</Button>
      </DialogActions>
    </Dialog>
  );
}

/** Warns before the browser leaves a page with unsaved changes; returns a guard for in-app navigation. */
export function useUnsaved(dirty: boolean) {
  useEffect(() => {
    if (!dirty) return;
    const h = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = ''; };
    window.addEventListener('beforeunload', h);
    return () => window.removeEventListener('beforeunload', h);
  }, [dirty]);
  return (go: () => void) => { if (!dirty || window.confirm('Discard unsaved changes?')) go(); };
}

export interface Col<T> { key: string; label: string; render?: (row: T) => ReactNode; sort?: boolean; width?: number | string }
export interface Paged<T> { items: T[]; total: number }

/** Server-paged, sortable table with loading, empty and error states (wide tables scroll horizontally). */
export function PagedTable<T extends { id?: string }>({ q, cols, page, size, onPage, onSize, sort, onSort, onRow, empty, selected, onSelect, locked }: {
  q: { data?: Paged<T>; isLoading: boolean; error: unknown; refetch: () => void }; cols: Col<T>[]; page: number; size: number; onPage: (p: number) => void; onSize: (s: number) => void;
  sort?: string; onSort?: (s: string) => void; onRow?: (row: T) => void; locked?: (row: T) => boolean; empty: string;
  /** Tick boxes on the rows: the ticked ids, and the change handler. */
  selected?: string[]; onSelect?: (ids: string[]) => void;
}) {
  const tabs = useTabs();
  // Ctrl/Cmd-click, middle-click or the right-click menu open the row in another tab
  const rowEvents = (row: T) => !onRow ? {} : {
    onClick: (e: React.MouseEvent) => { if (tabs && (e.ctrlKey || e.metaKey)) tabs.openHandlerInTab(() => onRow(row)); else onRow(row); },
    onAuxClick: (e: React.MouseEvent) => { if (tabs && e.button === 1) { e.preventDefault(); tabs.openHandlerInTab(() => onRow(row)); } },
    onMouseDown: (e: React.MouseEvent) => { if (tabs && e.button === 1) e.preventDefault(); },
    onContextMenu: (e: React.MouseEvent) => { if (tabs && !e.shiftKey && !(e.target as HTMLElement).closest('a[href]')) tabs.showOpenMenu(e, () => onRow(row)); },
  };
  if (q.isLoading) return <Loading />;
  if (q.error) return <ErrorState error={q.error} retry={q.refetch} />;
  const rows = q.data?.items ?? [];
  if (rows.length === 0) return <Empty text={empty} />;
  const [sk, sd] = (sort ?? '').split(':');
  const pageIds = rows.map((r) => r.id).filter((x): x is string => !!x);
  const ticked = selected ?? [];
  const allOnPage = pageIds.length > 0 && pageIds.every((x) => ticked.includes(x));
  const tick = (id: string) => onSelect?.(ticked.includes(id) ? ticked.filter((x) => x !== id) : [...ticked, id]);
  return (
    <Paper variant="outlined">
      <TableContainer sx={{ overflowX: 'auto' }}>
        <Table size="small">
          <TableHead><TableRow>{onSelect && <TableCell padding="checkbox"><Checkbox size="small" checked={allOnPage} indeterminate={!allOnPage && pageIds.some((x) => ticked.includes(x))} inputProps={{ 'aria-label': 'Select all on this page' }}
            onChange={() => onSelect(allOnPage ? ticked.filter((x) => !pageIds.includes(x)) : [...new Set([...ticked, ...pageIds])])} /></TableCell>}{cols.map((c) => <TableCell key={c.key} sx={{ width: c.width, whiteSpace: 'nowrap' }}>
            {c.sort && onSort ? <TableSortLabel active={sk === c.key} direction={sk === c.key && sd === 'desc' ? 'desc' : 'asc'} onClick={() => onSort(`${c.key}:${sk === c.key && sd !== 'desc' ? 'desc' : 'asc'}`)}>{c.label}</TableSortLabel> : c.label}
          </TableCell>)}</TableRow></TableHead>
          <TableBody>{rows.map((r, i) => { const shut = !!locked?.(r); return <TableRow key={r.id ?? i} hover={!!onRow && !shut} {...(shut ? {} : rowEvents(r))} data-canceled={shut || undefined} sx={shut ? (t) => ({ bgcolor: alpha(t.palette.error.main, t.palette.mode === 'dark' ? 0.22 : 0.1) }) : { cursor: onRow ? 'pointer' : undefined }}>
            {onSelect && r.id && <TableCell padding="checkbox" onClick={(e) => e.stopPropagation()} onContextMenu={(e) => e.stopPropagation()}><Checkbox size="small" checked={ticked.includes(r.id)} onChange={() => tick(r.id!)} inputProps={{ 'aria-label': 'Select row' }} /></TableCell>}
            {cols.map((c) => <TableCell key={c.key}>{c.render ? c.render(r) : String((r as Record<string, unknown>)[c.key] ?? '—')}</TableCell>)}</TableRow>; })}</TableBody>
        </Table>
      </TableContainer>
      <TablePagination component="div" count={q.data?.total ?? 0} page={page - 1} rowsPerPage={size} rowsPerPageOptions={[10, 25, 50, 100]}
        onPageChange={(_, p) => onPage(p + 1)} onRowsPerPageChange={(e) => onSize(Number(e.target.value))} />
    </Paper>
  );
}

/** Filter bar: search box plus select filters; Clear filters resets everything. */
export function Filters({ search, onSearch, filters, values, onChange, onClear, extra }: {
  search: string; onSearch: (v: string) => void; filters: { key: string; label: string; options: [string, string][] }[]; values: Record<string, string>;
  onChange: (key: string, v: string) => void; onClear: () => void; extra?: ReactNode;
}) {
  const [q, setQ] = useState(search);
  useEffect(() => setQ(search), [search]);
  return (
    <ListToolbar onSubmit={() => onSearch(q)}>
      <TextField size="small" label="Search" value={q} onChange={(e) => setQ(e.target.value)} sx={{ width: { xs: '100%', sm: 280 } }} />
      {filters.map((f) => <TextField key={f.key} select size="small" label={f.label} value={values[f.key] ?? ''} onChange={(e) => onChange(f.key, e.target.value)} sx={{ minWidth: 150 }}>
        <MenuItem value="">All</MenuItem>{f.options.map(([v, l]) => <MenuItem key={v} value={v}>{l}</MenuItem>)}</TextField>)}
      {extra}
      <Button type="submit" variant="outlined">Search</Button>
      <Button onClick={() => { setQ(''); onClear(); }}>Clear filters</Button>
    </ListToolbar>
  );
}

/** Query string for list endpoints, skipping empty values. */
export const qs = (o: Record<string, string | number | boolean | null | undefined>) =>
  Object.entries(o).filter(([, v]) => v !== '' && v !== null && v !== undefined).map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`).join('&');

export const Grid = ({ children, cols = 3 }: { children: ReactNode; cols?: number }) =>
  <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: 'minmax(0, 1fr)', sm: 'repeat(2, minmax(0, 1fr))', md: `repeat(${cols}, minmax(0, 1fr))` } }}>{children}</Box>;

/** Red Delete button with a "can't be undone" confirm. Hidden without the permission. Server reasons (e.g. "in use") show in the dialog. */
/** Delete with a named confirmation, checked against the permission and recorded by the server. Returns whether it may be shown, how to start it and its dialog. */
export function useDeleteAction({ what, url, perm = 'sequfloh.delete_records', extra, onDone, show = true, to }: {
  what: string; url: string; to?: string; perm?: string; extra?: string; onDone?: () => void; show?: boolean;
}) {
  const { can } = useAuth();
  const qc = useQueryClient();
  const nav = useNavigate();
  const [open, setOpen] = useState(false);
  const allowed = show && can(perm);
  const dialog = allowed ? <Confirm open={open} danger title={`Delete ${what}?`} confirm="Delete"
    text={`${extra ? extra + ' ' : ''}This can't be undone.`}
    onClose={() => setOpen(false)}
    onConfirm={async () => { await del(url); if (to) nav(to, { replace: true }); onDone?.(); void qc.invalidateQueries(); }} /> : null;
  return { allowed, start: () => setOpen(true), dialog };
}

export function DeleteButton({ size = 'medium', label = 'Delete', ...p }: {
  what: string; url: string; to?: string; perm?: string; extra?: string; onDone?: () => void; size?: 'small' | 'medium'; show?: boolean; label?: string;
}) {
  const d = useDeleteAction(p);
  if (!d.allowed) return null;
  return (
    <>
      <Button size={size} color="error" data-testid="delete-button" onClick={d.start}>{label}</Button>
      {d.dialog}
    </>
  );
}

/** Fixed choices of the part form and Parts import. */
export const UNITS = ['Each', 'Lot', 'Batch', 'Skid'];
export const LOT_CHOICES = ['Yes', 'No', 'Not applicable'];
