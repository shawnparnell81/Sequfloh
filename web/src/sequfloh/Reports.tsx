import { setupOptions } from '../components/SetupLink';
import { useEffect, useState } from 'react';
import { Link as RouterLink, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, Link, List, ListItemButton, ListItemText, MenuItem, Paper, Stack, TextField, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { get } from '../api';
import { useUsers } from '../components/Kit';
import { fmtDate, fmtDateTime } from '../components/Status';
import { Empty, ErrorState, Filters, Grid, Loading, Mono, PagedTable, S, SqfPage, qs, words } from './common';
import { Stat, Stats } from './Personnel';

interface Info { key: string; title: string; description: string; report: boolean; needsAudit: boolean }
interface Column { key: string; label: string; type: string }
interface Run { key: string; title: string; description: string; columns: Column[]; total: number; items: Record<string, unknown>[]; statusCounts?: Record<string, number> | null }

/** Menu slug → report key. */
const slugs: Record<string, string> = { 'task-performance': 'task-performance', 'validation-status': 'validation', 'fai-status': 'fai', 'repair-status': 'repair',
  'work-instruction-status': 'work-instructions', 'user-activity': 'user-activity', 'time-variance': 'time-variance' };
const rCrumbs = (label: string) => [{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Reports', to: '/sequfloh/reports/task-performance' }, { label }];
const trackingStatuses = ['NotStarted', 'Assigned', 'InProgress', 'Paused', 'OnHold', 'Completed', 'Failed', 'Canceled'];
const wiStatuses = ['Draft', 'Released', 'Superseded', 'Obsolete'];
const opt = (l: string[]): [string, string][] => l.map((v) => [v, words(v)]);
const filterSets: Record<string, { status?: [string, string][]; type?: [string, string][] }> = {
  'task-performance': { status: opt(trackingStatuses), type: [['Validation', 'Validation'], ['Fai', 'FAI'], ['Repair', 'Repair'], ['Production', 'Production']] },
  validation: { status: opt(trackingStatuses) }, fai: { status: opt(trackingStatuses) }, repair: { status: opt(trackingStatuses) }, production: { status: opt(trackingStatuses) },
  'time-variance': { type: [['Validation', 'Validation'], ['Fai', 'FAI'], ['Repair', 'Repair'], ['Production', 'Production']] },
  'work-instructions': { status: opt(wiStatuses), type: [['Validation', 'Validation'], ['ValidationFai', 'Validation / FAI'], ['Fai', 'FAI'], ['RepairTest', 'Repair Test'], ['RepairWork', 'Repair Work Instruction']] },
  'parts-lists': { status: [['Draft', 'Draft'], ['Released', 'Released'], ['Inactive', 'Inactive']] },
};

const cell = (v: unknown, type: string) => {
  if (v === null || v === undefined || v === '') return '—';
  if (type === 'datetime') return fmtDateTime(String(v));
  if (type === 'date') return fmtDate(String(v));
  if (typeof v === 'boolean') return v ? 'Yes' : 'No';
  return String(v);
};

/** Shared filter state for a report or export, kept in the address bar. */
function useReportFilters() {
  const [sp, setSp] = useSearchParams();
  const v = (k: string) => sp.get(k) ?? '';
  const set = (k: string, val: string) => { const n = new URLSearchParams(sp); if (val) n.set(k, val); else n.delete(k); setSp(n); };
  const params = { q: v('q'), status: v('status'), type: v('type'), user: v('user'), from: v('from'), to: v('to') };
  return { sp, setSp, v, set, params };
}

function DateUserFilters({ v, set }: { v: (k: string) => string; set: (k: string, val: string) => void }) {
  const users = useUsers();
  return <>
    <TextField size="small" type="date" label="From" InputLabelProps={{ shrink: true }} value={v('from')} onChange={(e) => set('from', e.target.value)} sx={{ width: 160 }} />
    <TextField size="small" type="date" label="To" InputLabelProps={{ shrink: true }} value={v('to')} onChange={(e) => set('to', e.target.value)} sx={{ width: 160 }} />
    <TextField select size="small" label="User" value={users.data ? v('user') : ''} onChange={(e) => set('user', e.target.value)} sx={{ minWidth: 170 }}>
      <MenuItem value="">All</MenuItem>{(users.data ?? []).map((u) => <MenuItem key={u.id} value={u.id}>{u.displayName}</MenuItem>)}{setupOptions('user', users.data)}</TextField>
  </>;
}

/** One SEQUFLOH report: filters, server-paged table of the full filtered result, status counts and Excel export of everything that matches. */
export function ReportPage() {
  const { slug = 'task-performance' } = useParams();
  const key = slugs[slug] ?? slug;
  const nav = useNavigate();
  const { sp, setSp, v, set, params } = useReportFilters();
  const [page, setPage] = useState(1); const [size, setSize] = useState(25);
  useEffect(() => setPage(1), [sp, key]);
  const catalog = useQuery({ queryKey: ['sqf-reports'], queryFn: () => get<{ reports: Info[]; canExport: boolean }>(`${S}/reports`) });
  const info = catalog.data?.reports.find((r) => r.key === key);
  const q = useQuery({ queryKey: ['sqf-report', key, sp.toString(), page, size], enabled: !!info, queryFn: () => get<Run>(`${S}/reports/${key}?${qs({ ...params, page, pageSize: size })}`) });
  const fs = filterSets[key] ?? {};
  const [fileName, setFileName] = useState('');
  const title = info?.title ?? 'Report';
  const rows = { data: q.data ? { items: q.data.items.map((r, i) => ({ ...r, id: String(r._url ?? r._id ?? i) })), total: q.data.total } : undefined, isLoading: q.isLoading || catalog.isLoading, error: q.error ?? catalog.error, refetch: q.refetch };
  return (
    <SqfPage title={title} crumbs={rCrumbs(title)} description={info?.description} permission="sequfloh.view_reports"
      actions={catalog.data?.canExport && info && <Stack direction="row" gap={1} alignItems="center">
        <TextField size="small" label="File name (optional)" value={fileName} onChange={(e) => setFileName(e.target.value)} sx={{ width: 220 }} />
        <Button variant="contained" href={`${S}/reports/${key}/xlsx?${qs({ ...params, fileName })}`}>Export to Excel</Button>
      </Stack>}>
      {catalog.data && !info ? <Empty text="This report is not available to you." /> : <>
        <Filters search={v('q')} onSearch={(x) => set('q', x)} values={{ status: v('status'), type: v('type') }} onChange={set} onClear={() => setSp({})}
          filters={[...(fs.status ? [{ key: 'status', label: 'Status', options: fs.status }] : []), ...(fs.type ? [{ key: 'type', label: 'Type', options: fs.type }] : [])]}
          extra={<DateUserFilters v={v} set={set} />} />
        {q.data?.statusCounts && Object.keys(q.data.statusCounts).length > 0 && <Stats>{Object.entries(q.data.statusCounts).map(([k, n]) => <Stat key={k} label={k || 'No status'} value={n} />)}</Stats>}
        <PagedTable q={rows} page={page} size={size} onPage={setPage} onSize={setSize} onRow={(r) => { const u = (r as Record<string, unknown>)._url; if (u) nav(String(u)); }}
          empty={sp.toString() ? 'No records match these filters.' : 'No records yet.'}
          cols={(q.data?.columns ?? []).map((c) => ({ key: c.key, label: c.label, render: (row: { id: string }) => { const r = row as Record<string, unknown>; return c.key === 'number' ? <Mono>{cell(r[c.key], c.type)}</Mono> : cell(r[c.key], c.type); } }))} />
      </>}
    </SqfPage>
  );
}

/** Export Center: every SEQUFLOH export with the same filters; each workbook carries the full filtered result and a title block, and is audited. */
export function ExportCenterPage() {
  const { setSp, v, set, params } = useReportFilters();
  const catalog = useQuery({ queryKey: ['sqf-reports'], queryFn: () => get<{ reports: Info[]; canExport: boolean }>(`${S}/reports`) });
  const [fileName, setFileName] = useState<Record<string, string>>({});
  return (
    <SqfPage title="Export Center" crumbs={rCrumbs('Export Center')} permission="sequfloh.view_reports"
      description="Excel exports of SEQUFLOH records. Each workbook holds every record that matches the filters (not just one page), with the report title, organization, generated date and user, and the filters used. Exports are recorded in the audit trail.">
      {catalog.isLoading ? <Loading /> : catalog.error ? <ErrorState error={catalog.error} retry={catalog.refetch} /> : !catalog.data?.canExport ? <Empty text="You do not have permission to export SEQUFLOH records." /> : <>
        <Filters search={v('q')} onSearch={(x) => set('q', x)} values={{}} onChange={set} onClear={() => setSp({})} filters={[]} extra={<DateUserFilters v={v} set={set} />} />
        <Grid cols={2}>{catalog.data.reports.map((r) => (
          <Paper key={r.key} variant="outlined" sx={{ p: 2 }} component="section" aria-label={r.title}>
            <Typography variant="h3">{r.title}</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ my: 1 }}>{r.description}</Typography>
            <Stack direction="row" gap={1} alignItems="center">
              <TextField size="small" label="File name (optional)" value={fileName[r.key] ?? ''} onChange={(e) => setFileName({ ...fileName, [r.key]: e.target.value })} sx={{ flex: 1 }} />
              <Button variant="outlined" href={`${S}/reports/${r.key}/xlsx?${qs({ ...params, fileName: fileName[r.key] })}`} aria-label={`Export ${r.title}`}>Export to Excel</Button>
            </Stack>
          </Paper>))}</Grid>
      </>}
    </SqfPage>
  );
}

// ------------------------------------------------------------------ audit trail
const auditViews: Record<string, string> = { system: 'System Audit Events', instructions: 'Work Instruction History', access: 'User Access History', changes: 'Record Change History', exports: 'Export History' };
interface AuditRow { id: number; occurredAt: string; userName: string; action: string; entityType: string; entityId: string; description: string; reason?: string }
interface AuditDetail extends AuditRow { changes?: string; correlationId?: string; site?: string; path?: string }

/** SEQUFLOH audit trail views over the shared, append-only audit trail: who, what, when and description. */
export function AuditPage() {
  const { view = 'system' } = useParams();
  const nav = useNavigate();
  const [sp, setSp] = useSearchParams();
  const v = (k: string) => sp.get(k) ?? '';
  const set = (k: string, val: string) => { const n = new URLSearchParams(sp); if (val) n.set(k, val); else n.delete(k); setSp(n); };
  const [page, setPage] = useState(1); const [size, setSize] = useState(25); const [sort, setSort] = useState('occurredAt:desc');
  useEffect(() => setPage(1), [sp, view]);
  const params = { q: v('q'), user: v('user'), action: v('action'), from: v('from'), to: v('to') };
  const q = useQuery({ queryKey: ['sqf-audit', view, sp.toString(), page, size, sort], queryFn: () => get<{ title: string; items: AuditRow[]; total: number; actions: string[] }>(`${S}/audit/${view}?${qs({ ...params, sort, page, pageSize: size })}`) });
  const [open, setOpen] = useState<number | null>(null);
  const title = auditViews[view] ?? 'Audit Trail';
  const rows = { data: q.data ? { items: q.data.items.map((r) => ({ ...r, id: String(r.id) })), total: q.data.total } : undefined, isLoading: q.isLoading, error: q.error, refetch: q.refetch };
  return (
    <SqfPage title={title} crumbs={[{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Audit Trail', to: '/sequfloh/audit/system' }, { label: title }]} permission="sequfloh.view_audit_trail"
      tabs={Object.entries(auditViews).map(([value, label]) => ({ value, label }))} tab={view} onTab={(t) => nav(`/sequfloh/audit/${t}`)}
      description="Audit events are written automatically when work happens and cannot be edited or deleted. Open an event for its full detail."
      actions={<Button variant="outlined" href={`${S}/audit/${view}/print?${qs(params)}`} target="_blank" rel="noopener">Print summary</Button>}>
      <Filters search={v('q')} onSearch={(x) => set('q', x)} values={{ action: v('action') }} onChange={set} onClear={() => setSp({})}
        filters={[{ key: 'action', label: 'Action', options: (q.data?.actions ?? []).map((a) => [a, words(a)]) }]} extra={<DateUserFilters v={v} set={set} />} />
      <PagedTable q={rows} page={page} size={size} onPage={setPage} onSize={setSize} sort={sort} onSort={setSort} onRow={(r) => setOpen(Number(r.id))}
        empty={sp.toString() ? 'No audit events match these filters.' : 'No audit events yet.'} cols={[
          { key: 'occurredAt', label: 'When', sort: true, render: (r) => fmtDateTime(r.occurredAt) }, { key: 'user', label: 'Who', sort: true, render: (r) => r.userName },
          { key: 'action', label: 'What', sort: true, render: (r) => words(r.action) }, { key: 'description', label: 'Description' }, { key: 'reason', label: 'Reason', render: (r) => r.reason ?? '—' }]} />
      {open !== null && <AuditDetailDialog id={open} onClose={() => setOpen(null)} />}
    </SqfPage>
  );
}

function AuditDetailDialog({ id, onClose }: { id: number; onClose: () => void }) {
  const q = useQuery({ queryKey: ['sqf-audit-entry', id], queryFn: () => get<AuditDetail>(`${S}/audit/entry/${id}`) });
  const d = q.data;
  let changes = d?.changes ?? '';
  try { if (changes) changes = JSON.stringify(JSON.parse(changes), null, 2); } catch { /* shown as stored */ }
  return (
    <Dialog open onClose={onClose} maxWidth="md" fullWidth>
      <DialogTitle>Audit event</DialogTitle>
      <DialogContent>
        {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : d && <>
          <Box component="dl" sx={{ display: 'grid', gridTemplateColumns: { xs: 'minmax(0, 1fr)', md: '160px minmax(0, 1fr)' }, gap: 1, m: 0 }}>
            {([['When', fmtDateTime(d.occurredAt)], ['Who', d.userName], ['What', words(d.action)], ['Description', d.description], ['Reason', d.reason ?? '—'], ['Record type', words(d.entityType.replace('Sequfloh', ''))],
              ['Site', d.site ?? '—'], ['Correlation', d.correlationId ?? '—']] as [string, string][]).map(([k, val]) => <Box key={k} sx={{ display: 'contents' }}>
              <Typography component="dt" variant="body2" color="text.secondary">{k}</Typography><Typography component="dd" variant="body2" sx={{ m: 0 }}>{val}</Typography></Box>)}
          </Box>
          {d.path && <Link component={RouterLink} to={d.path} sx={{ display: 'inline-block', mt: 2 }}>Open the record</Link>}
          {changes && <Paper variant="outlined" sx={{ mt: 2, p: 1.5, bgcolor: 'grey.50' }}><Typography variant="caption" color="text.secondary">Recorded changes</Typography>
            <Box component="pre" sx={{ m: 0, fontSize: 12, whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>{changes}</Box></Paper>}
        </>}
      </DialogContent>
      <DialogActions><Button onClick={onClose}>Close</Button></DialogActions>
    </Dialog>
  );
}

// ------------------------------------------------------------------ search
interface Group { key: string; label: string; items: { id: string; number: string; title: string; status: string; path: string }[] }
/** SEQUFLOH search across permitted work instructions, parts, parts lists, tracking records, tasks and (for user administrators) users. */
export function SearchPage() {
  const [sp, setSp] = useSearchParams();
  const term = sp.get('q') ?? '';
  const [text, setText] = useState(term);
  useEffect(() => setText(term), [term]);
  const q = useQuery({ queryKey: ['sqf-search', term], enabled: term.trim().length >= 2, queryFn: () => get<{ groups: Group[] }>(`${S}/search?${qs({ q: term })}`) });
  const groups = q.data?.groups.filter((g) => g.items.length > 0) ?? [];
  return (
    <SqfPage title="Search SEQUFLOH" crumbs={[{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Search' }]} description="Find work instructions, parts, parts lists, tracking records and tasks you have access to.">
      <Stack direction="row" gap={1} component="form" sx={{ mb: 2 }} onSubmit={(e) => { e.preventDefault(); setSp(text.trim() ? { q: text.trim() } : {}); }}>
        <TextField size="small" label="Search" value={text} onChange={(e) => setText(e.target.value)} sx={{ width: { xs: '100%', sm: 360 } }} autoFocus />
        <Button type="submit" variant="contained">Search</Button>
      </Stack>
      {term.trim().length < 2 ? <Empty text="Enter at least two characters." /> : q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} />
        : groups.length === 0 ? <Empty text={`Nothing found for “${term}”.`} /> :
        <Grid cols={2}>{groups.map((g) => <Paper key={g.key} variant="outlined" sx={{ p: 2 }} component="section" aria-label={g.label}>
          <Typography variant="h3" sx={{ mb: 1 }}>{g.label}</Typography>
          <List dense disablePadding>{g.items.map((i) => <ListItemButton key={i.id} component={RouterLink} to={i.path}>
            <ListItemText primary={<Mono>{i.number}</Mono>} secondary={[i.title, words(i.status)].filter(Boolean).join(' · ')} /></ListItemButton>)}</List>
        </Paper>)}</Grid>}
    </SqfPage>
  );
}
