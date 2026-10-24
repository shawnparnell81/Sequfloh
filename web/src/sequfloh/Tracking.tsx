import { setupOptions } from '../components/SetupLink';
import { useEffect, useState, type ReactNode } from 'react';
import { Link as RouterLink, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { Alert, Autocomplete, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, MenuItem, Paper, Stack, Table, TableBody, TableCell,
  TableContainer, TableHead, TableRow, TextField, Typography } from '@mui/material';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put } from '../api';
import { useAuth } from '../auth';
import { Facts, useSites, useUsers } from '../components/Kit';
import { fmtDateTime } from '../components/Status';
import { AssigneesPanel, TimePanel, type Person } from './TimePanel';
import { CrewPanel } from './CrewPanel';
import { DeleteButton, Empty, ErrorState, Filters, Grid, Loading, Mono, PagedTable, S, SqfPage, SqfStatus, fmtSeconds, nul, numOrNull, qs, useOptions, useUnsaved, words } from './common';
import { NewInspectionRequestButton } from './InspectionRequests';
import { type AuditRow, HistoryTable, SimpleTable } from './Parts';
import { ProblemList } from './Designer';
import { FindingsPanel } from './Findings';
import { JobPanel, StatusBadge, StatusEditor, useStatusFilter } from './Job';
import { PartPicker } from './Instructions';
import { PartDrawingLinks, RecordDrawings } from './Drawings';

export const trackingTypes: Record<string, string> = { Validation: 'Validation', Fai: 'FAI', Repair: 'Repair', Production: 'Production', Inspection: 'Inspection' };
export const trackingStatuses = ['NotStarted', 'Assigned', 'InProgress', 'Paused', 'Completed', 'Failed', 'Canceled'];
const createPerm: Record<string, string> = { Validation: 'sequfloh.create_validation_record', Fai: 'sequfloh.create_fai_record', Repair: 'sequfloh.create_repair_record', Production: 'sequfloh.create_production_task', Inspection: 'sequfloh.create_production_task' };
export const trCrumbs = (label: string, folder?: { label: string; to: string }) =>
  [{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Tracking', to: '/sequfloh/tracking/open' }, ...(folder ? [folder] : []), { label }];
/** Operation ID for one user action: the server applies an ID once, so a repeated click or resubmission cannot apply the change twice. */
export const newOpId = () => (globalThis.crypto?.randomUUID ? globalThis.crypto.randomUUID()
  : 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => { const r = (Math.random() * 16) | 0; return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16); }));

/** Elapsed time shown from persisted totals: the server value plus the time since it was read, only while the task is running. */
export function LiveClock({ seconds, running, label }: { seconds: number; running: boolean; label?: string }) {
  const [base] = useState(() => Date.now());
  const [now, setNow] = useState(Date.now());
  useEffect(() => { if (!running) return; const t = setInterval(() => setNow(Date.now()), 1000); return () => clearInterval(t); }, [running]);
  const v = seconds + (running ? Math.max(0, (now - base) / 1000) : 0);
  return <Mono><span aria-label={label ?? 'Elapsed time'}>{fmtSeconds(v)}</span></Mono>;
}

// ------------------------------------------------------------------ folders
export interface TrRow { id: string; number: string; type: string; status: string; result: string; priority: string; workInstructionNumber: string; workInstructionRevision: string;
  workInstructionTitle: string; partNumber?: string; partDescription?: string; serialNumber?: string; lotNumber?: string; workOrder?: string; repairOrder?: string; operation?: string; dueAt?: string;
  actualStart?: string; actualCompletion?: string; createdAt: string; assignedUser?: string; assignedUserId?: string; assignees?: string[]; owner?: string; department?: string; site?: string; elapsedSeconds: number; statusTag?: string | null; statusNote?: string | null;
  pausedSeconds: number; holdSeconds: number; minBenchmarkMinutes?: number; maxBenchmarkMinutes?: number; varianceMinutes?: number; stateVersion: number }
const folders: Record<string, { title: string; description: string; type?: string }> = {
  validation: { title: 'Validation Tracking', type: 'Validation', description: 'Validation records executed against released Validation and Validation / FAI work instructions.' },
  fai: { title: 'FAI Tracking', type: 'Fai', description: 'First article inspection records with characteristic results, verifications and final disposition.' },
  repair: { title: 'Repair Tracking', type: 'Repair', description: 'Repair records: conditions, operations, material quantities, repair tests and final disposition.' },
  production: { title: 'Production Tracking', type: 'Production', description: 'Production tasks with work order, operation, benchmark range and persisted timing.' },
  open: { title: 'Open Activities', description: 'Every tracking record and task that is not completed, failed or canceled.' },
  completed: { title: 'Completed Activities', description: 'Completed tracking records and tasks.' },
};
const Elapsed = ({ r }: { r: TrRow }) => <LiveClock seconds={r.elapsedSeconds} running={r.status === 'InProgress'} />;
export const trackingCols = (extra: { key: string; label: string; render?: (r: TrRow) => ReactNode; sort?: boolean }[] = []) => [
  { key: 'number', label: 'Tracking number', sort: true, render: (r: TrRow) => <Mono>{r.number}</Mono> },
  { key: 'type', label: 'Type', render: (r: TrRow) => trackingTypes[r.type] ?? r.type },
  { key: 'wi', label: 'Work instruction', render: (r: TrRow) => <><Mono>{r.workInstructionNumber}</Mono> rev {r.workInstructionRevision}</> },
  { key: 'partNumber', label: 'Part', sort: true, render: (r: TrRow) => r.partNumber ?? '—' },
  ...extra,
  { key: 'assignedUser', label: 'Assigned', render: (r: TrRow) => (r.assignees?.length ? r.assignees.join(', ') : r.assignedUser ?? '—') },
  { key: 'status', label: 'Status', sort: true, render: (r: TrRow) => <SqfStatus status={r.status} /> },
  { key: 'result', label: 'Result', render: (r: TrRow) => <SqfStatus status={r.result} /> },
  { key: 'priority', label: 'Priority', sort: true },
  { key: 'dueAt', label: 'Due', sort: true, render: (r: TrRow) => fmtDateTime(r.dueAt) },
  { key: 'elapsed', label: 'Elapsed', render: (r: TrRow) => <Elapsed r={r} /> },
  { key: 'statusTag', label: 'Official status in NetSuite', render: (r: TrRow) => <StatusBadge tag={r.statusTag} note={r.statusNote} /> },
];

export function TrackingFolderPage() {
  const { folder = 'open' } = useParams();
  const cfg = folders[folder] ?? folders.open;
  const { can } = useAuth();
  const nav = useNavigate();
  const [sp, setSp] = useSearchParams();
  const v = (k: string) => sp.get(k) ?? '';
  const [page, setPage] = useState(1); const [size, setSize] = useState(25); const [sort, setSort] = useState('createdAt:desc');
  useEffect(() => setPage(1), [folder]);
  const set = (k: string, val: string) => { const n = new URLSearchParams(sp); if (val) n.set(k, val); else n.delete(k); setSp(n); setPage(1); };
  const users = useUsers(); const sites = useSites(); const priorities = useOptions('priorities');
  const q = useQuery({ queryKey: ['sqf-tr', folder, sp.toString(), page, size, sort], queryFn: () => get<{ items: TrRow[]; total: number }>(`${S}/tracking?${qs({ folder, q: v('q'), status: v('status'),
    type: v('type'), owner: v('owner'), assigned: v('assigned'), part: v('part'), department: v('department'), site: v('site'), dateField: v('dateField'), from: v('from'), to: v('to'), priority: v('priority'),
    result: v('result'), tag: v('tag'), sort, page, pageSize: size })}`) });
  const tagFilter = useStatusFilter();
  const userOpts: [string, string][] = (users.data ?? []).map((u) => [u.id, u.displayName]);
  const depts: [string, string][] = (sites.data ?? []).filter((s) => !v('site') || s.id === v('site')).flatMap((s) => s.departments.map((d) => [d.id, `${s.code} · ${d.name}`] as [string, string]));
  const filters: { key: string; label: string; options: [string, string][] }[] = [
    { key: 'status', label: 'Status', options: trackingStatuses.map((s) => [s, words(s)]) },
    ...(cfg.type ? [] : [{ key: 'type', label: 'Type', options: Object.entries(trackingTypes) as [string, string][] }]),
    { key: 'owner', label: 'Owner', options: userOpts }, { key: 'assigned', label: 'Assigned user', options: userOpts },
    { key: 'department', label: 'Department', options: depts },
    { key: 'site', label: 'Site', options: (sites.data ?? []).map((s) => [s.id, s.code]) },
    { key: 'priority', label: 'Priority', options: (priorities.data ?? []).map((p) => [p, p]) },
    { key: 'result', label: 'Result', options: ['Pending', 'Pass', 'Fail', 'Conditional'].map((r) => [r, r]) },
    tagFilter,
  ];
  const extra = folder === 'repair' ? [{ key: 'serialNumber', label: 'Serial', render: (r: TrRow) => r.serialNumber ?? '—' }, { key: 'repairOrder', label: 'Repair order', render: (r: TrRow) => r.repairOrder ?? '—' }]
    : folder === 'production' ? [{ key: 'workOrder', label: 'Work order', render: (r: TrRow) => r.workOrder ?? '—' }, { key: 'operation', label: 'Operation', render: (r: TrRow) => r.operation ?? '—' }]
    : [{ key: 'lotSerial', label: 'Lot / serial', render: (r: TrRow) => [r.lotNumber, r.serialNumber].filter(Boolean).join(' / ') || '—' }];
  const newType = cfg.type ?? Object.keys(createPerm).find((t) => can(createPerm[t]));
  return (
    <SqfPage title={cfg.title} crumbs={trCrumbs(cfg.title)} description={cfg.description} permission="sequfloh.view_live_execution"
      actions={<Stack direction="row" gap={1} flexWrap="wrap"><NewInspectionRequestButton variant="outlined" />{newType && can(createPerm[newType]) && <Button variant="contained" component={RouterLink} to={`/sequfloh/tracking/new?type=${newType}`}>New {cfg.type ? `${trackingTypes[cfg.type]} record` : 'record'}</Button>}</Stack>}>
      <Filters search={v('q')} onSearch={(s) => set('q', s)} filters={filters} values={Object.fromEntries(filters.map((f) => [f.key, v(f.key)]))} onChange={set} onClear={() => { setSp({}); setPage(1); }}
        extra={<>
          <TextField size="small" label="Part number" defaultValue={v('part')} key={'p' + v('part')} onBlur={(e) => set('part', e.target.value.trim())} sx={{ width: 150 }} />
          <TextField select size="small" label="Date" value={v('dateField') || 'created'} onChange={(e) => set('dateField', e.target.value === 'created' ? '' : e.target.value)} sx={{ width: 130 }}>
            {[['created', 'Created'], ['due', 'Due'], ['started', 'Started'], ['completed', 'Completed']].map(([k, l]) => <MenuItem key={k} value={k}>{l}</MenuItem>)}</TextField>
          <TextField size="small" type="date" label="From" value={v('from')} onChange={(e) => set('from', e.target.value)} InputLabelProps={{ shrink: true }} sx={{ width: 150 }} />
          <TextField size="small" type="date" label="To" value={v('to')} onChange={(e) => set('to', e.target.value)} InputLabelProps={{ shrink: true }} sx={{ width: 150 }} />
        </>} />
      <PagedTable q={q} page={page} size={size} onPage={setPage} onSize={setSize} sort={sort} onSort={setSort} onRow={(r) => nav(`/sequfloh/record/${r.id}`)}
        empty={sp.toString() ? 'No tracking records match these filters.' : 'No tracking records in this folder yet.'} cols={trackingCols(extra)} />
    </SqfPage>
  );
}

// ------------------------------------------------------------------ new record
interface WiOpt { id: string; number: string; revision: string; title: string; type: string; partId?: string; partNumber?: string; partDescription?: string; operation?: string; repairDescription?: string; siteId: string; departmentId?: string }
export function NewTrackingPage() {
  const nav = useNavigate();
  const [sp] = useSearchParams();
  const { can } = useAuth();
  const sites = useSites(); const users = useUsers(); const priorities = useOptions('priorities');
  const allowed = Object.keys(createPerm).filter((t) => can(createPerm[t]));
  const [type, setType] = useState(sp.get('type') && allowed.includes(sp.get('type')!) ? sp.get('type')! : allowed[0] ?? '');
  const [wi, setWi] = useState<WiOpt | null>(null);
  const [wiText, setWiText] = useState('');
  const [part, setPart] = useState<{ id: string; partNumber: string; name?: string } | null>(null);
  const [f, setF] = useState<Record<string, string>>({ priority: 'Normal' });
  const [siteId, setSiteId] = useState(''); const [departmentId, setDepartmentId] = useState(''); const [assigned, setAssigned] = useState('');
  const [err, setErr] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  useEffect(() => { if (!siteId && sites.data?.length) setSiteId(sites.data[0].id); }, [sites.data, siteId]);
  const wis = useQuery({ queryKey: ['sqf-tr-wi', type, wiText], queryFn: () => get<WiOpt[]>(`${S}/tracking/instructions?${qs({ type, q: wiText })}`), enabled: !!type });
  const site = sites.data?.find((s) => s.id === siteId);
  const fld = (k: string, label: string, props: Record<string, unknown> = {}) => <TextField size="small" label={label} value={f[k] ?? ''} onChange={(e) => setF({ ...f, [k]: e.target.value })} {...props} />;
  const iso = (d?: string) => (d ? new Date(d).toISOString() : null);
  const create = async () => {
    setBusy(true); setErr(null);
    try {
      const r = await post<{ id: string }>(`${S}/tracking`, { type, workInstructionId: wi?.id, siteId, departmentId: nul(departmentId), partId: part?.id ?? null, assignedUserId: nul(assigned),
        serialNumber: nul(f.serialNumber), lotNumber: nul(f.lotNumber), workOrder: nul(f.workOrder), repairOrder: nul(f.repairOrder), customerReturnReference: nul(f.customerReturnReference),
        project: nul(f.project), operation: nul(f.operation), priority: nul(f.priority), plannedStart: iso(f.plannedStart), dueAt: iso(f.dueAt), minBenchmarkMinutes: numOrNull(f.min),
        maxBenchmarkMinutes: numOrNull(f.max), reportedCondition: nul(f.reportedCondition) });
      nav(`/sequfloh/record/${r.id}`, { replace: true });
    } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return (
    <SqfPage title="New tracking record" crumbs={trCrumbs('New tracking record')} recent={null} permission={allowed.length ? undefined : 'sequfloh.create_validation_record'}
      description="Choose the record type and a released work instruction. The record keeps that exact revision with its steps, characteristics and parts, so later revisions never change it.">
      <Paper variant="outlined" sx={{ p: 3, maxWidth: 980 }}>
        {err && <Alert severity="error" sx={{ mb: 2 }}>{err}</Alert>}
        <Grid cols={2}>
          <TextField select size="small" label="Tracking type" value={type} onChange={(e) => { setType(e.target.value); setWi(null); }}>
            {allowed.map((t) => <MenuItem key={t} value={t}>{trackingTypes[t]}</MenuItem>)}</TextField>
          <Autocomplete size="small" options={wis.data ?? []} value={wi} loading={wis.isLoading} filterOptions={(x) => x} isOptionEqualToValue={(a, b) => a.id === b.id}
            getOptionLabel={(o) => `${o.number} rev ${o.revision} — ${o.title}`} onInputChange={(_, t, r) => { if (r === 'input') setWiText(t); }}
            onChange={(_, o) => { setWi(o); if (o?.partId) setPart({ id: o.partId, partNumber: o.partNumber ?? '', name: o.partDescription }); if (o?.operation && !f.operation) setF({ ...f, operation: o.operation }); }}
            renderInput={(p) => <TextField {...p} label="Released work instruction" helperText={wis.data && wis.data.length === 0 ? 'No released instruction of a matching type.' : undefined} />} />
          <PartPicker label={type === 'Repair' ? 'Repairable part' : 'Part'} repairable={type === 'Repair'} value={part} onChange={(p) => setPart(p)} />
          <TextField size="small" label="Part description" value={part?.name ?? ''} disabled InputLabelProps={{ shrink: true }} />
          {part && <Box sx={{ gridColumn: { md: 'span 2' } }} data-testid="new-record-drawings">
            <Typography variant="caption" color="text.secondary" component="div" sx={{ mb: 0.5 }}>
              {type === 'Validation' ? 'Drawings for this part (attached to the record when it is created)' : 'Drawings for this part'}</Typography>
            <PartDrawingLinks partId={part.id} /></Box>}
          <TextField select size="small" label="Site" value={sites.data ? siteId : ''} onChange={(e) => { setSiteId(e.target.value); setDepartmentId(''); }}>
            {(sites.data ?? []).map((s) => <MenuItem key={s.id} value={s.id}>{s.code} · {s.name}</MenuItem>)}{setupOptions('site', sites.data)}</TextField>
          <TextField select size="small" label="Department" value={site ? departmentId : ''} onChange={(e) => setDepartmentId(e.target.value)}>
            <MenuItem value=""><em>None</em></MenuItem>{(site?.departments ?? []).map((d) => <MenuItem key={d.id} value={d.id}>{d.name}</MenuItem>)}{site && setupOptions('department', site.departments)}</TextField>
          <TextField select size="small" label="Assigned user" value={users.data ? assigned : ''} onChange={(e) => setAssigned(e.target.value)} helperText="Assigning dispatches the task.">
            <MenuItem value=""><em>Not assigned</em></MenuItem>{(users.data ?? []).map((u) => <MenuItem key={u.id} value={u.id}>{u.displayName}</MenuItem>)}{setupOptions('user', users.data)}</TextField>
          <TextField select size="small" label="Priority" value={priorities.data ? f.priority : ''} onChange={(e) => setF({ ...f, priority: e.target.value })}>
            {(priorities.data ?? []).map((p) => <MenuItem key={p} value={p}>{p}</MenuItem>)}</TextField>
          {fld('serialNumber', 'Serial number')}{fld('lotNumber', 'Lot number')}
          {fld('workOrder', 'Work order')}{type === 'Repair' ? fld('repairOrder', 'Repair order') : fld('project', 'Project')}
          {type === 'Repair' && fld('customerReturnReference', 'Customer-return reference')}
          {fld('operation', 'Operation')}
          {fld('plannedStart', 'Planned start', { type: 'datetime-local', InputLabelProps: { shrink: true } })}{fld('dueAt', 'Due date', { type: 'datetime-local', InputLabelProps: { shrink: true } })}
          {fld('min', 'Minimum benchmark (minutes)', { type: 'number' })}{fld('max', 'Maximum benchmark (minutes)', { type: 'number' })}
          {type === 'Repair' && <Box sx={{ gridColumn: '1 / -1' }}>{fld('reportedCondition', 'Reported condition', { multiline: true, minRows: 2, fullWidth: true })}</Box>}
        </Grid>
        <Stack direction="row" gap={1} sx={{ mt: 3 }}>
          <Button variant="contained" disabled={!type || !wi || !siteId || busy} onClick={create}>Create record</Button>
          <Button component={RouterLink} to="/sequfloh/tracking/open">Cancel</Button>
        </Stack>
      </Paper>
    </SqfPage>
  );
}

// ------------------------------------------------------------------ record
type J = Record<string, unknown>;
interface TrRecord extends J { id: string; number: string; type: string; status: string; result: string; priority: string; workInstructionId: string; workInstructionNumber: string;
  workInstructionRevision: string; workInstructionTitle: string; stateVersion: number; elapsedSeconds: number; pausedSeconds: number; holdSeconds: number; steps: J[]; characteristics: J[];
  partsRows: J[]; details: Record<string, string>; assignedUserId?: string; assignees?: Person[]; createdAt: string }
interface TrEvent { id: string; eventType: string; fromStatus?: string; toStatus?: string; createdAt: string; userName: string; reason?: string; comment?: string; activeSeconds: number; pausedSeconds: number;
  holdSeconds: number; operationId: string; assignedBefore?: string; assignedAfter?: string }
export interface TrAction { action: string; label: string; reasonRequired: boolean }
interface TrResponse { record: TrRecord; events: TrEvent[]; actions: TrAction[]; completionProblems: string[]; canEnter: boolean; canPlan: boolean; canQualityComment: boolean; canEngineeringComment: boolean; canAssign?: boolean; canDelete?: boolean; serverTime: string }
const planKeys = ['serialNumber', 'lotNumber', 'workOrder', 'repairOrder', 'customerReturnReference', 'project', 'priority', 'plannedStart', 'dueAt'];
const entryKeys = ['evidenceReferences', 'technicianComments', 'deviations', 'nonconformanceReferences', 'reportedCondition', 'observedCondition', 'finalDisposition', 'varianceReason', 'result', 'qualityComments',
  'engineeringComments', 'engineeringConclusion'];
const s = (v: unknown) => (v === undefined || v === null ? '' : String(v));

export function TrackingRecordPage() {
  const { id = '' } = useParams();
  const qc = useQueryClient();
  const [sp, setSp] = useSearchParams();
  const tab = sp.get('tab') ?? 'record';
  const q = useQuery({ queryKey: ['sqf-tr-one', id], queryFn: () => get<TrResponse>(`${S}/tracking/${id}`) });
  const history = useQuery({ queryKey: ['sqf-tr-history', id], queryFn: () => get<AuditRow[]>(`${S}/tracking/${id}/history`), enabled: tab === 'history' });
  const [doc, setDoc] = useState<TrRecord | null>(null);
  const [dirty, setDirty] = useState(false);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ sev: 'success' | 'error'; text: string } | null>(null);
  const [act, setAct] = useState<TrAction | null>(null);
  useUnsaved(dirty);
  useEffect(() => { if (q.data && !dirty) setDoc(structuredClone(q.data.record)); }, [q.data]); // eslint-disable-line react-hooks/exhaustive-deps
  if (q.isLoading) return <SqfPage title="Tracking record" crumbs={trCrumbs('Loading')}><Loading /></SqfPage>;
  if (q.error) return <SqfPage title="Tracking record" crumbs={trCrumbs('Not available')}><ErrorState error={q.error} retry={q.refetch} /></SqfPage>;
  if (!doc || !q.data) return null;
  const rec = q.data.record;
  const upd = (patch: Partial<TrRecord>) => { setDoc({ ...doc, ...patch }); setDirty(true); };
  const refresh = async () => { setDirty(false); await qc.invalidateQueries({ queryKey: ['sqf-tr-one', id] }); qc.invalidateQueries({ queryKey: ['sqf-tr'] }); qc.invalidateQueries({ queryKey: ['sqf-tr-history', id] }); };
  const save = async (quiet = false) => {
    setBusy(true); setMsg(null);
    try {
      const body: J = { operationId: newOpId(), stateVersion: rec.stateVersion };
      for (const k of [...planKeys, ...entryKeys]) body[k] = nul(s(doc[k]));
      if (q.data!.canEnter) { body.steps = doc.steps; body.characteristics = doc.characteristics; body.partsRows = doc.partsRows; body.details = doc.details; }
      await put(`${S}/tracking/${id}`, body);
      setDirty(false); await refresh(); if (!quiet) setMsg({ sev: 'success', text: 'Saved.' }); return true;
    } catch (e) { setMsg({ sev: 'error', text: (e as Error).message }); return false; } finally { setBusy(false); }
  };
  const runAction = async (a: TrAction, input: J) => {
    let version = rec.stateVersion; // the state the user acted on: a change by someone else is refused, not overwritten
    if (dirty) { if (!(await save(true))) throw new Error('Save your changes first.'); version = (await get<TrResponse>(`${S}/tracking/${id}`)).record.stateVersion; }
    await post(`${S}/tracking/${id}/actions/${a.action}`, { ...input, stateVersion: version });
    await refresh(); setMsg({ sev: 'success', text: `${a.label}: done.` });
  };
  const enter = q.data.canEnter && !busy;
  const plan = q.data.canPlan && !busy;
  const folder = { Validation: 'validation', Fai: 'fai', Repair: 'repair', Production: 'production' }[rec.type] ?? 'open';
  const title = `${rec.number} — ${trackingTypes[rec.type]} record`;
  const tabs = [{ label: 'Record', value: 'record' }, ...(doc.steps.length ? [{ label: rec.type === 'Repair' ? 'Operations' : 'Steps', value: 'steps' }] : []),
    ...(rec.type === 'Fai' ? [{ label: 'Characteristics', value: 'characteristics' }] : []), ...(rec.type === 'Repair' ? [{ label: 'Repair parts', value: 'parts' }] : []),
    { label: 'Job', value: 'job' }, { label: 'Time', value: 'time' }, { label: 'Findings', value: 'findings' }, { label: 'Comments', value: 'review' }, { label: 'Task events', value: 'events' }, { label: 'History', value: 'history' }];
  const setRow = (list: 'steps' | 'characteristics' | 'partsRows', i: number, patch: J) => upd({ [list]: doc[list].map((r, j) => (j === i ? { ...r, ...patch } : r)) } as Partial<TrRecord>);
  const det = (k: string, label: string, multiline = true) => <TextField size="small" label={label} value={doc.details[k] ?? ''} disabled={!enter} multiline={multiline} minRows={multiline ? 2 : undefined}
    onChange={(e) => upd({ details: { ...doc.details, [k]: e.target.value } })} />;
  const txt = (k: string, label: string, disabled: boolean, multiline = true) => <TextField size="small" label={label} value={s(doc[k])} disabled={disabled} multiline={multiline} minRows={multiline ? 2 : undefined}
    onChange={(e) => upd({ [k]: e.target.value } as Partial<TrRecord>)} />;
  const running = rec.status === 'InProgress';
  return (
    <SqfPage title={title} crumbs={trCrumbs(rec.number, { label: folders[folder].title, to: `/sequfloh/tracking/${folder}` })} recent={{ label: `${rec.number} ${rec.workInstructionTitle}`, itemType: 'Tracking record', recordId: rec.id }}
      tabs={tabs} tab={tab} onTab={(t) => { const n = new URLSearchParams(sp); n.set('tab', t); setSp(n, { replace: true }); }}
      actions={<Stack direction="row" gap={1} flexWrap="wrap" alignItems="center">
        <SqfStatus status={rec.status} />
        {(q.data.canEnter || q.data.canPlan || q.data.canQualityComment || q.data.canEngineeringComment) && <Button variant="contained" disabled={!dirty || busy} onClick={() => save()}>Save</Button>}
        {q.data.actions.map((a) => <Button key={a.action} variant="outlined" disabled={busy} color={['fail', 'cancel'].includes(a.action) ? 'error' : 'primary'} onClick={() => setAct(a)}>{a.label}</Button>)}
        <Button variant="outlined" component={RouterLink} to={`/sequfloh/record/${rec.id}/report`}>Job report</Button>
        <Button variant="outlined" component={RouterLink} to={`/sequfloh/chargebacks/new?wo=${rec.id}`} data-testid="wo-create-chargeback">Create chargeback report</Button>
        <Button variant="outlined" href={`${S}/tracking/${rec.id}/print`} target="_blank" rel="noopener" disabled={dirty}>Print</Button>
        <DeleteButton what={`work order ${rec.number}`} url={`${S}/tracking/${rec.id}`} show={!!q.data.canDelete} to={`/sequfloh/tracking/${folder}`}
          extra="Use this for work orders made by mistake. Work orders with logged time are kept for chargebacks and can only be cancelled." />
      </Stack>}>
      <StatusEditor kind="work-order" id={rec.id} tag={rec.statusTag as string | null} note={rec.statusNote as string | null} canEdit={!dirty} onSaved={refresh} />
      {msg && <Alert severity={msg.sev} sx={{ mb: 2 }} onClose={() => setMsg(null)}>{msg.text}</Alert>}
      {dirty && <Alert severity="info" sx={{ mb: 2 }}>You have unsaved changes.</Alert>}
      {rec.status === 'InProgress' && <ProblemList title="Before this record can be completed:" problems={q.data.completionProblems} />}

      {tab === 'record' && <Stack gap={2}>
        <TimePanel recordId={rec.id} />
        <CrewPanel recordId={rec.id} />
        <AssigneesPanel recordId={rec.id} people={rec.assignees ?? []} canAssign={!!q.data.canAssign} />
        <RecordDrawings recordId={rec.id} />
        <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Tracking record">
          <Typography variant="h3" sx={{ mb: 1.5 }}>Tracking record</Typography>
          <Facts items={[['Tracking number', <Mono>{rec.number}</Mono>], ['Tracking type', trackingTypes[rec.type]],
            ['Work instruction', <><Link to={`/sequfloh/instruction/${rec.workInstructionId}`}><Mono>{rec.workInstructionNumber}</Mono></Link> rev {rec.workInstructionRevision} — {rec.workInstructionTitle}</>],
            ['Part', rec.partNumber ? `${s(rec.partNumber)} — ${s(rec.partDescription)}` : '—'], ['Status', <SqfStatus status={rec.status} />], ['Result', <SqfStatus status={rec.result} />],
            ['Assigned people', rec.assignees?.length ? rec.assignees.map((a) => a.displayName).join(', ') : s(rec.assignedUser) || '—'], ['Active operator', s(rec.activeOperator) || '—'], ['Department', s(rec.department) || '—'],
            ['Site', s(rec.site) || '—'], ['Owner', s(rec.owner) || '—'], ['Created', `${fmtDateTime(rec.createdAt)}${rec.createdBy ? ' · ' + s(rec.createdBy) : ''}`],
            ['Updated', `${fmtDateTime(s(rec.updatedAt))}${rec.updatedBy ? ' · ' + s(rec.updatedBy) : ''}`],
            ...(rec.repairDescription ? [['Repair description', s(rec.repairDescription)] as [string, ReactNode]] : []),
            ...(['pauseReason', 'failureReason', 'cancelReason'].filter((k) => rec[k]).map((k) => [words(k.replace(/^./, (c) => c.toUpperCase())), s(rec[k])] as [string, ReactNode]))]} />
        </Paper>
        <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Timing">
          <Typography variant="h3" sx={{ mb: 1.5 }}>Timing</Typography>
          <Facts items={[['Elapsed (active)', <LiveClock key={rec.stateVersion} seconds={rec.elapsedSeconds} running={running} />],
            ['Paused time', <LiveClock key={'p' + rec.stateVersion} seconds={rec.pausedSeconds} running={rec.status === 'Paused'} label="Paused time" />],
            ['Benchmark range', rec.minBenchmarkMinutes != null || rec.maxBenchmarkMinutes != null ? `${s(rec.minBenchmarkMinutes) || '—'} – ${s(rec.maxBenchmarkMinutes) || '—'} min` : '—'],
            ['Variance', rec.varianceMinutes != null ? `${s(rec.varianceMinutes)} min` : '—'], ['Planned start', fmtDateTime(s(rec.plannedStart))], ['Actual start', fmtDateTime(s(rec.actualStart))],
            ['Due', fmtDateTime(s(rec.dueAt))], ['Actual completion', `${fmtDateTime(s(rec.actualCompletion))}${rec.completedBy ? ' · ' + s(rec.completedBy) : ''}`]]} />
        </Paper>
        <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Planning">
          <Typography variant="h3" sx={{ mb: 1.5 }}>Planning</Typography>
          <Grid cols={3}>
            {txt('serialNumber', 'Serial number', !plan, false)}{txt('lotNumber', 'Lot number', !plan, false)}{txt('workOrder', 'Work order', !plan, false)}
            {txt('repairOrder', 'Repair order', !plan, false)}{txt('customerReturnReference', 'Customer-return reference', !plan, false)}{txt('project', 'Project', !plan, false)}
            <PrioritySelect value={s(doc.priority)} disabled={!plan} onChange={(v) => upd({ priority: v })} />
            <TextField size="small" type="datetime-local" label="Planned start" value={local(s(doc.plannedStart))} disabled={!plan} InputLabelProps={{ shrink: true }} onChange={(e) => upd({ plannedStart: e.target.value ? new Date(e.target.value).toISOString() : null })} />
            <TextField size="small" type="datetime-local" label="Due date" value={local(s(doc.dueAt))} disabled={!plan} InputLabelProps={{ shrink: true }} onChange={(e) => upd({ dueAt: e.target.value ? new Date(e.target.value).toISOString() : null })} />
          </Grid>
        </Paper>
        <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Results and evidence">
          <Typography variant="h3" sx={{ mb: 1.5 }}>Results and evidence</Typography>
          {!q.data.canEnter && <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>Results are recorded while the record is in progress or pending inspection.</Typography>}
          <Grid cols={2}>
            {(rec.type === 'Production' || rec.type === 'Inspection') && <TextField select size="small" label="Result" value={s(doc.result)} disabled={!enter} onChange={(e) => upd({ result: e.target.value })}>
              {['Pending', 'Pass', 'Fail', 'Conditional'].map((r) => <MenuItem key={r} value={r}>{r}</MenuItem>)}</TextField>}
            {rec.type === 'Validation' && <>{det('sampleIdentification', 'Sample identification')}{det('controlledConditions', 'Controlled conditions (recorded)')}</>}
            {rec.type === 'Fai' && <>{det('materialVerification', 'Material verification')}{det('specialProcessVerification', 'Special-process verification')}{det('certificateReview', 'Certificate review')}
              {det('drawingBalloonReferences', 'Drawing-balloon references', false)}{det('dimensionalResults', 'Dimensional results')}{det('visualResults', 'Visual results')}{det('functionalResults', 'Functional results')}
              <OptionSelect list="inspection-results" label="Inspection result" value={s(doc.finalDisposition)} disabled={!enter} onChange={(v) => upd({ finalDisposition: v })} /></>}
            {rec.type === 'Repair' && <>{txt('reportedCondition', 'Reported condition', !enter)}{txt('observedCondition', 'Observed condition', !enter)}
              <OptionSelect list="repair-test-results" label="Repair test result" value={doc.details.repairTestResult ?? ''} disabled={!enter} onChange={(v) => upd({ details: { ...doc.details, repairTestResult: v } })} />
              {det('repairTestReference', 'Repair test reference', false)}{det('postRepairInspection', 'Post-repair inspection')}{det('failureHandling', 'Failure handling')}{det('rejectionHandling', 'Rejection handling')}</>}
            {(rec.type === 'Production' || rec.type === 'Inspection') && <>{det('qualityCheckpoints', 'Quality checkpoints')}{det('resultEntry', 'Result entry')}</>}
            {txt('evidenceReferences', 'Evidence references', !enter)}{txt('technicianComments', 'Technician comments', !enter)}
            {(rec.type !== 'Production' && rec.type !== 'Inspection') && txt('deviations', 'Deviations', !enter)}{txt('nonconformanceReferences', 'Nonconformance references', !enter)}
            {txt('varianceReason', 'Variance reason', !enter)}
          </Grid>
        </Paper>
      </Stack>}

      {tab === 'steps' && <EntryTable title={rec.type === 'Repair' ? 'Operations' : 'Steps'} rows={doc.steps} disabled={!enter}
        cols={[['sequence', 'Seq'], [rec.type === 'Repair' ? 'operationStep' : 'title', rec.type === 'Repair' ? 'Operation' : 'Title'], ['spec', 'Specification / limits'], ['expectedResult', 'Expected result']]}
        entry={(r, i) => <>
          <TableCell><TextField select size="small" label="Result" value={s(r.result)} disabled={!enter} sx={{ minWidth: 130 }} onChange={(e) => setRow('steps', i, { result: e.target.value })}>
            <MenuItem value=""><em>Not recorded</em></MenuItem>{(rec.type === 'Repair' ? ['Pass', 'Fail', 'Complete', 'Not applicable'] : ['Pass', 'Fail', 'Conditional', 'Not applicable']).map((o) => <MenuItem key={o} value={o}>{o}</MenuItem>)}</TextField></TableCell>
          <TableCell><TextField size="small" label="Actual result" value={s(r.actualResult)} disabled={!enter} onChange={(e) => setRow('steps', i, { actualResult: e.target.value })} /></TableCell>
          <TableCell><TextField size="small" label="Evidence" value={s(r.evidence)} disabled={!enter} onChange={(e) => setRow('steps', i, { evidence: e.target.value })} helperText={s(r.evidenceRequired ?? r.evidenceRequirement) || undefined} /></TableCell>
          <TableCell><TextField size="small" label="Comment" value={s(r.comment)} disabled={!enter} onChange={(e) => setRow('steps', i, { comment: e.target.value })} /></TableCell>
          {rec.type === 'Repair' && <TableCell>
            <FormControlLabel control={<Checkbox size="small" checked={r.completed === true} disabled={!enter} onChange={(_, c) => setRow('steps', i, { completed: c })} />} label="Completed" />
          </TableCell>}
        </>} entryHead={['Result', 'Actual result', 'Evidence', 'Comment', ...(rec.type === 'Repair' ? ['Done'] : [])]} />}

      {tab === 'characteristics' && <EntryTable title="FAI characteristics" rows={doc.characteristics} disabled={!enter}
        cols={[['characteristicNumber', 'No.'], ['requirement', 'Requirement'], ['spec', 'Specification / limits'], ['inspectionMethod', 'Method']]}
        entryHead={['Actual result', 'Result', 'Evidence reference', 'Nonconformance reference']}
        entry={(r, i) => <>
          <TableCell><TextField size="small" label="Actual" value={s(r.actualResult)} disabled={!enter} onChange={(e) => setRow('characteristics', i, { actualResult: e.target.value })} /></TableCell>
          <TableCell><OptionSelect list="characteristic-results" label="Result" value={s(r.result)} disabled={!enter} onChange={(v) => setRow('characteristics', i, { result: v })} /></TableCell>
          <TableCell><TextField size="small" label="Evidence" value={s(r.evidenceReference)} disabled={!enter} onChange={(e) => setRow('characteristics', i, { evidenceReference: e.target.value })} /></TableCell>
          <TableCell><TextField size="small" label="NC reference" value={s(r.nonconformanceReference)} disabled={!enter} onChange={(e) => setRow('characteristics', i, { nonconformanceReference: e.target.value })} /></TableCell>
        </>} />}

      {tab === 'parts' && <EntryTable title="Repair parts" rows={doc.partsRows} disabled={!enter}
        cols={[['replacementPartNumber', 'Part number'], ['description', 'Description'], ['quantity', 'Planned qty'], ['required', 'Required']]}
        entryHead={['Issued', 'Used', 'Returned', 'Lot', 'Serial']}
        entry={(r, i) => <>
          {['issuedQuantity', 'usedQuantity', 'returnedQuantity'].map((k) => <TableCell key={k}><TextField size="small" type="number" label={words(k.replace('Quantity', '')).replace(/^./, (c) => c.toUpperCase())}
            value={s(r[k])} disabled={!enter} sx={{ width: 96 }} onChange={(e) => setRow('partsRows', i, { [k]: e.target.value === '' ? null : Number(e.target.value) })} /></TableCell>)}
          <TableCell><TextField size="small" label="Lot" value={s(r.lotNumber)} disabled={!enter} required={r.lotNumberRequired === true} onChange={(e) => setRow('partsRows', i, { lotNumber: e.target.value })} sx={{ width: 120 }} /></TableCell>
          <TableCell><TextField size="small" label="Serial" value={s(r.serialNumber)} disabled={!enter} required={r.serialNumberRequired === true} onChange={(e) => setRow('partsRows', i, { serialNumber: e.target.value })} sx={{ width: 120 }} /></TableCell>
        </>} />}

      {tab === 'review' && <Stack gap={2}>
        <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Comments">
          <Typography variant="h3" sx={{ mb: 1.5 }}>Quality and Engineering comments</Typography>
          <Grid cols={2}>
            {txt('qualityComments', 'Quality comments', !q.data.canQualityComment || busy)}
            {txt('engineeringComments', 'Engineering comments', !q.data.canEngineeringComment || busy)}
            {txt('engineeringConclusion', 'Engineering conclusion', !q.data.canEngineeringComment || busy)}
          </Grid>
        </Paper>
      </Stack>}

      {tab === 'job' && <JobPanel id={rec.id} canWork={rec.status !== 'Canceled'} />}
      {tab === 'time' && <Stack gap={2}><TimePanel recordId={rec.id} /><CrewPanel recordId={rec.id} /></Stack>}
      {tab === 'findings' && <FindingsPanel id={rec.id} canWork={rec.status !== 'Canceled'} wi={`${rec.workInstructionNumber} rev ${rec.workInstructionRevision}`} />}
      {tab === 'events' && <TaskEvents events={q.data.events} />}
      {tab === 'history' && <HistoryTable q={history} name={`${rec.number}-history`} />}
      <TaskActionDialog action={act} record={rec} onClose={() => setAct(null)} onRun={runAction} />
    </SqfPage>
  );
}

const Link = ({ to, children }: { to: string; children: ReactNode }) => <RouterLink to={to} style={{ color: 'inherit' }}>{children}</RouterLink>;
const local = (iso: string) => { if (!iso) return ''; const d = new Date(iso); const p = (n: number) => String(n).padStart(2, '0'); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`; };

function OptionSelect({ list, label, value, disabled, onChange }: { list: string; label: string; value: string; disabled?: boolean; onChange: (v: string) => void }) {
  const o = useOptions(list);
  const opts = o.data ?? [];
  return <TextField select size="small" label={label} value={opts.includes(value) || !value ? value : ''} disabled={disabled} sx={{ minWidth: 150 }} onChange={(e) => onChange(e.target.value)}>
    <MenuItem value=""><em>Not recorded</em></MenuItem>{opts.map((x) => <MenuItem key={x} value={x}>{x}</MenuItem>)}</TextField>;
}
function PrioritySelect({ value, disabled, onChange }: { value: string; disabled?: boolean; onChange: (v: string) => void }) {
  const o = useOptions('priorities');
  return <TextField select size="small" label="Priority" value={o.data ? value : ''} disabled={disabled} onChange={(e) => onChange(e.target.value)}>
    {(o.data ?? []).map((x) => <MenuItem key={x} value={x}>{x}</MenuItem>)}</TextField>;
}

const specText = (r: J) => [s(r.specification), r.target != null ? `target ${s(r.target)}` : '', r.lowerLimit != null || r.upperLimit != null ? `${s(r.lowerLimit) || '—'} to ${s(r.upperLimit) || '—'}` : '',
  r.minimum != null || r.maximum != null ? `${s(r.minimum) || '—'} to ${s(r.maximum) || '—'}` : '', s(r.unit)].filter(Boolean).join(' · ') || '—';
/** Copied instruction rows (read-only) with the execution fields entered beside them. */
function EntryTable({ title, rows, cols, entryHead, entry }: { title: string; rows: J[]; cols: [string, string][]; entryHead: string[]; entry: (r: J, i: number) => ReactNode; disabled: boolean }) {
  if (rows.length === 0) return <Empty text={`This record has no ${title.toLowerCase()}.`} />;
  return (
    <Paper variant="outlined" component="section" aria-label={title}>
      <Typography variant="h3" sx={{ p: 2, pb: 1 }}>{title}</Typography>
      <TableContainer sx={{ overflowX: 'auto' }}>
        <Table size="small">
          <TableHead><TableRow>{cols.map(([, l]) => <TableCell key={l}>{l}</TableCell>)}{entryHead.map((h) => <TableCell key={h}>{h}</TableCell>)}</TableRow></TableHead>
          <TableBody>{rows.map((r, i) => <TableRow key={s(r.id)}>
            {cols.map(([k]) => <TableCell key={k}>{k === 'spec' ? specText(r) : typeof r[k] === 'boolean' ? (r[k] ? 'Yes' : 'No') : s(r[k]) || '—'}</TableCell>)}
            {entry(r, i)}</TableRow>)}</TableBody>
        </Table>
      </TableContainer>
    </Paper>
  );
}

export function TaskEvents({ events }: { events: TrEvent[] }) {
  if (events.length === 0) return <Empty text="No task events yet." />;
  return <SimpleTable head={['When', 'Event', 'Previous status', 'New status', 'User', 'Assigned before', 'Assigned after', 'Reason', 'Comment', 'Active', 'Paused']}
    rows={events.map((e) => [fmtDateTime(e.createdAt), words(e.eventType), e.fromStatus ? words(e.fromStatus) : '—', e.toStatus ? words(e.toStatus) : '—', e.userName, e.assignedBefore ?? '—', e.assignedAfter ?? '—',
      e.reason ?? '—', e.comment ?? '—', <Mono>{fmtSeconds(e.activeSeconds)}</Mono>, <Mono>{fmtSeconds(e.pausedSeconds)}</Mono>])} />;
}

const actionText: Record<string, string> = {
  dispatch: 'Dispatches the task to the assigned user.', assign: 'Assigns the task.', start: 'Starts the task. Timing runs from the database clock and survives refresh, sign-out and closing the browser.',
  pause: 'Pauses the task. Paused time is kept separately from active time.', resume: 'Resumes the task.', 
  restart: 'Restarts the record for rework. Earlier results and events stay in the history.', complete: 'Completes the record.',
  fail: 'Records the failure.', reassign: 'Moves the task to another user.', cancel: 'Cancels the task. A canceled task cannot be completed or resumed.',
};
const reasonList: Record<string, string> = { cancel: 'cancellation-reasons', fail: 'repair-failure-reasons' };
/** Action dialog: one operation ID per opening, so a double click cannot apply the action twice; the button is disabled while the request runs. */
export function TaskActionDialog({ action, record, onClose, onRun }: { action: TrAction | null; record: { number: string; assignedUserId?: string | null; minBenchmarkMinutes?: unknown; maxBenchmarkMinutes?: unknown; status: string; type?: string };
  onClose: () => void; onRun: (a: TrAction, input: J) => Promise<void> }) {
  const users = useUsers();
  const [reason, setReason] = useState(''); const [comment, setComment] = useState(''); const [user, setUser] = useState(''); const [variance, setVariance] = useState('');
  const [op, setOp] = useState(newOpId());
  const [busy, setBusy] = useState(false); const [err, setErr] = useState<string | null>(null);
  const options = useOptions(action && reasonList[action.action] && (action.action !== 'fail' || record.type === 'Repair') ? reasonList[action.action] : 'priorities');
  if (!action) return null;
  const needsUser = ['dispatch', 'assign', 'reassign'].includes(action.action);
  const needsVariance = action.action === 'complete' && record.status === 'InProgress' && (record.minBenchmarkMinutes != null || record.maxBenchmarkMinutes != null);
  const opts = reasonList[action.action] && (action.action !== 'fail' || record.type === 'Repair') ? options.data ?? [] : [];
  const danger = ['fail', 'cancel'].includes(action.action);
  return (
    <Dialog open onClose={busy ? undefined : onClose} fullWidth maxWidth="sm" TransitionProps={{ onEnter: () => { setReason(''); setComment(''); setVariance(''); setUser(action.action === 'dispatch' ? record.assignedUserId ?? '' : ''); setErr(null); setOp(newOpId()); } }}>
      <DialogTitle>{action.label} — {record.number}</DialogTitle>
      <DialogContent>
        <Typography variant="body2" sx={{ mb: 2 }}>{actionText[action.action] ?? ''}</Typography>
        {err && <Alert severity="error" sx={{ mb: 2 }}>{err}</Alert>}
        {needsUser && <TextField select fullWidth size="small" label="Assign to" value={users.data ? user : ''} onChange={(e) => setUser(e.target.value)} sx={{ mb: 2 }}>
          {(users.data ?? []).filter((u) => action.action !== 'reassign' || u.id !== record.assignedUserId).map((u) => <MenuItem key={u.id} value={u.id}>{u.displayName}</MenuItem>)}{setupOptions('user', users.data)}</TextField>}
        {opts.length > 0 && <TextField select fullWidth size="small" label="Reason" value={opts.includes(reason) ? reason : ''} onChange={(e) => setReason(e.target.value)} sx={{ mb: 2 }}>
          {opts.map((o) => <MenuItem key={o} value={o}>{o}</MenuItem>)}</TextField>}
        {action.reasonRequired && opts.length === 0 && <TextField fullWidth size="small" label="Reason (required)" value={reason} onChange={(e) => setReason(e.target.value)} sx={{ mb: 2 }} />}
        {needsVariance && <TextField fullWidth size="small" label="Variance reason (when outside the benchmark range)" value={variance} onChange={(e) => setVariance(e.target.value)} sx={{ mb: 2 }} />}
        <TextField fullWidth multiline minRows={2} size="small" label="Comment" value={comment} onChange={(e) => setComment(e.target.value)} />
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>Cancel</Button>
        <Button variant="contained" color={danger ? 'error' : 'primary'} disabled={busy || (action.reasonRequired && !reason.trim()) || (needsUser && !user)}
          onClick={async () => {
            setBusy(true); setErr(null);
            try {
              await onRun(action, { operationId: op, reason: nul(reason), comment: nul(comment), varianceReason: nul(variance), assignedUserId: needsUser ? user : null,
                expectedAssignedUserId: action.action === 'reassign' ? record.assignedUserId ?? null : null });
              onClose();
            } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
          }}>{action.label}</Button>
      </DialogActions>
    </Dialog>
  );
}
