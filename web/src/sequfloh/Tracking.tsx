import { setupOptions } from '../components/SetupLink';
import { FormDialogTitle } from '../components/FullScreen';
import { useEffect, useState, type ReactNode } from 'react';
import { Link as RouterLink, Navigate, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { Alert, Autocomplete, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, FormControlLabel, MenuItem, Paper, Stack, Table, TableBody, TableCell,
  TableContainer, TableHead, TableRow, TextField, Typography } from '@mui/material';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put } from '../api';
import { useAuth } from '../auth';
import { Facts, useSites, useUsers } from '../components/Kit';
import { fmtDateTime } from '../components/Status';
import { AssigneesPanel, TimePanel, type Person } from './TimePanel';
import { CrewPanel } from './CrewPanel';
import { Empty, ErrorState, Filters, Grid, Loading, Mono, PagedTable, S, SqfPage, SqfStatus, fmtSeconds, nul, numOrNull, qs, useOptions, useUnsaved, words } from './common';
import { NewInspectionRequestButton } from './InspectionRequests';
import { SimpleTable } from './Parts';
import { RecordActions } from '../components/RecordActions';
import { NumberField } from '../components/RecordNumber';
import { ProblemList } from './Designer';
import { FindingsPanel } from './Findings';
import { InventoryNote, JobPanel } from './Job';
import { PartPicker } from './Instructions';
import { ViewDrawingButton } from './Drawings';
import { PartDrawingLinks, RecordDrawings } from './Drawings';
import { useFamilies } from './Imports';

/** Work order type names. Validation and FAI are one type to the user: records stored as either show as Validation / FAI. */
export const trackingTypes: Record<string, string> = { Validation: 'Validation / FAI', Fai: 'Validation / FAI', Repair: 'Repair / Rework', Production: 'Production', Inspection: 'Inspection', Warranty: 'Warranty', SaltSpray: 'Salt Spray Test' };
/** One entry per type for type pickers and filters (Validation / FAI listed once). */
export const trackingTypeOptions: [string, string][] = [['Validation', 'Validation / FAI'], ['Repair', 'Repair / Rework'], ['Inspection', 'Inspection'], ['Warranty', 'Warranty'], ['SaltSpray', 'Salt Spray Test'], ['Production', 'Production']];
/** Types offered by the New work order builder; Production stays available for links that ask for it. */
const builderTypes: Record<string, string> = { Validation: 'Validation / FAI', Repair: 'Repair / Rework', Inspection: 'Inspection', Warranty: 'Warranty', SaltSpray: 'Salt Spray Test' };
export const trackingStatuses = ['NotStarted', 'Assigned', 'InProgress', 'Paused', 'Completed', 'Failed', 'Canceled'];
const createPerm: Record<string, string> = { Validation: 'sequfloh.create_validation_record', Fai: 'sequfloh.create_fai_record', Repair: 'sequfloh.create_repair_record', Production: 'sequfloh.create_production_task', Inspection: 'sequfloh.create_production_task', Warranty: 'sequfloh.create_production_task', SaltSpray: 'sequfloh.create_production_task' };
export const trCrumbs = (label: string, folder?: { label: string; to: string }) =>
  [{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Work Orders', to: '/sequfloh/tracking/open' }, ...(folder ? [folder] : []), { label }];
/** The one way to start a work order: the type is chosen inside the builder. */
export function NewWorkOrderButton({ type, variant = 'contained' }: { type?: string; variant?: 'contained' | 'outlined' }) {
  const { can } = useAuth();
  if (!Object.values(createPerm).some((p) => can(p))) return null;
  return <Button variant={variant} component={RouterLink} to={`/sequfloh/tracking/new${type ? `?type=${type}` : ''}`} data-testid="new-work-order">New work order</Button>;
}
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
  'validation-fai': { title: 'Validation / FAI', type: 'Validation', description: 'Validation / FAI work orders.' },
  repair: { title: 'Repair / Rework', type: 'Repair', description: 'Repair / Rework work orders: conditions, operations, material quantities, test results and final disposition.' },
  production: { title: 'Production', type: 'Production', description: 'Production tasks with work order, operation, benchmark range and persisted timing.' },
  inspection: { title: 'Inspection', type: 'Inspection', description: 'Inspection work orders: sub-type, what to inspect, and qty good, qty bad, further inspection and quarantine.' },
  warranty: { title: 'Warranty', type: 'Warranty', description: 'Warranty work orders with the same fields and results as inspections.' },
  'salt-spray': { title: 'Salt Spray Test', type: 'SaltSpray', description: 'Salt Spray Test work orders, with the same clock, crew and day-worker time as every other work order.' },
  open: { title: 'Open Work Orders', description: 'Every work order that is not completed, failed or canceled.' },
  completed: { title: 'Completed Work Orders', description: 'Completed work orders.' },
};
const Elapsed = ({ r }: { r: TrRow }) => <LiveClock seconds={r.elapsedSeconds} running={r.status === 'InProgress'} />;
export const trackingCols = (extra: { key: string; label: string; render?: (r: TrRow) => ReactNode; sort?: boolean }[] = []) => [
  { key: 'number', label: 'Work order', sort: true, render: (r: TrRow) => <Mono>{r.number}</Mono> },
  { key: 'type', label: 'Type', render: (r: TrRow) => trackingTypes[r.type] ?? r.type },
  { key: 'wi', label: 'Work instruction', render: (r: TrRow) => (r.workInstructionNumber ? <><Mono>{r.workInstructionNumber}</Mono> rev {r.workInstructionRevision}</> : '—') },
  { key: 'partNumber', label: 'Part', sort: true, render: (r: TrRow) => r.partNumber ?? '—' },
  ...extra,
  { key: 'assignedUser', label: 'Assigned', render: (r: TrRow) => (r.assignees?.length ? r.assignees.join(', ') : r.assignedUser ?? '—') },
  { key: 'status', label: 'Status', sort: true, render: (r: TrRow) => <SqfStatus status={r.status} /> },
  { key: 'priority', label: 'Priority', sort: true },
  { key: 'dueAt', label: 'Due', sort: true, render: (r: TrRow) => fmtDateTime(r.dueAt) },
  { key: 'elapsed', label: 'Elapsed', render: (r: TrRow) => <Elapsed r={r} /> },
];

export function TrackingFolderPage() {
  const { folder: asked = 'open' } = useParams();
  // the old Validation and FAI folders open Validation / FAI
  if (asked === 'validation' || asked === 'fai') return <Navigate to="/sequfloh/tracking/validation-fai" replace />;
  return <TrackingFolder folder={asked} />;
}
function TrackingFolder({ folder }: { folder: string }) {
  const cfg = folders[folder] ?? folders.open;
  const nav = useNavigate();
  const [sp, setSp] = useSearchParams();
  const v = (k: string) => sp.get(k) ?? '';
  const [page, setPage] = useState(1); const [size, setSize] = useState(25); const [sort, setSort] = useState('createdAt:desc');
  useEffect(() => setPage(1), [folder]);
  const set = (k: string, val: string) => { const n = new URLSearchParams(sp); if (val) n.set(k, val); else n.delete(k); setSp(n); setPage(1); };
  const users = useUsers(); const sites = useSites(); const priorities = useOptions('priorities');
  const q = useQuery({ queryKey: ['sqf-tr', folder, sp.toString(), page, size, sort], queryFn: () => get<{ items: TrRow[]; total: number }>(`${S}/tracking?${qs({ folder, q: v('q'), status: v('status'),
    type: v('type'), owner: v('owner'), assigned: v('assigned'), part: v('part'), department: v('department'), site: v('site'), dateField: v('dateField'), from: v('from'), to: v('to'), priority: v('priority'),
    tag: v('tag'), sort, page, pageSize: size })}`) });
  const userOpts: [string, string][] = (users.data ?? []).map((u) => [u.id, u.displayName]);
  const depts: [string, string][] = (sites.data ?? []).filter((s) => !v('site') || s.id === v('site')).flatMap((s) => s.departments.map((d) => [d.id, `${s.code} · ${d.name}`] as [string, string]));
  const filters: { key: string; label: string; options: [string, string][] }[] = [
    { key: 'status', label: 'Status', options: trackingStatuses.map((s) => [s, words(s)]) },
    ...(cfg.type ? [] : [{ key: 'type', label: 'Type', options: trackingTypeOptions }]),
    { key: 'owner', label: 'Owner', options: userOpts }, { key: 'assigned', label: 'Assigned user', options: userOpts },
    { key: 'department', label: 'Department', options: depts },
    { key: 'site', label: 'Site', options: (sites.data ?? []).map((s) => [s.id, s.code]) },
    { key: 'priority', label: 'Priority', options: (priorities.data ?? []).map((p) => [p, p]) },
  ];
  const extra = folder === 'repair' ? [{ key: 'serialNumber', label: 'Serial', render: (r: TrRow) => r.serialNumber ?? '—' }, { key: 'repairOrder', label: 'Repair order', render: (r: TrRow) => r.repairOrder ?? '—' }]
    : folder === 'production' ? [{ key: 'workOrder', label: 'Work order', render: (r: TrRow) => r.workOrder ?? '—' }, { key: 'operation', label: 'Operation', render: (r: TrRow) => r.operation ?? '—' }]
    : [{ key: 'lotSerial', label: 'Lot / serial', render: (r: TrRow) => [r.lotNumber, r.serialNumber].filter(Boolean).join(' / ') || '—' }];
  return (
    <SqfPage title={cfg.title} crumbs={trCrumbs(cfg.title)} description={cfg.description} permission="sequfloh.view_live_execution"
      actions={<Stack direction="row" gap={1} flexWrap="wrap"><NewInspectionRequestButton variant="outlined" /><NewWorkOrderButton type={cfg.type} /></Stack>}>
      <Filters search={v('q')} onSearch={(s) => set('q', s)} filters={filters} values={Object.fromEntries(filters.map((f) => [f.key, v(f.key)]))} onChange={set} onClear={() => { setSp({}); setPage(1); }}
        extra={<>
          <TextField size="small" label="Part number" defaultValue={v('part')} key={'p' + v('part')} onBlur={(e) => set('part', e.target.value.trim())} sx={{ width: 150 }} />
          <TextField select size="small" label="Date" value={v('dateField') || 'created'} onChange={(e) => set('dateField', e.target.value === 'created' ? '' : e.target.value)} sx={{ width: 130 }}>
            {[['created', 'Created'], ['due', 'Due'], ['started', 'Started'], ['completed', 'Completed']].map(([k, l]) => <MenuItem key={k} value={k}>{l}</MenuItem>)}</TextField>
          <TextField size="small" type="date" label="From" value={v('from')} onChange={(e) => set('from', e.target.value)} InputLabelProps={{ shrink: true }} sx={{ width: 150 }} />
          <TextField size="small" type="date" label="To" value={v('to')} onChange={(e) => set('to', e.target.value)} InputLabelProps={{ shrink: true }} sx={{ width: 150 }} />
        </>} />
      <PagedTable q={q} page={page} size={size} onPage={setPage} onSize={setSize} sort={sort} onSort={setSort} onRow={(r) => nav(`/sequfloh/record/${r.id}`)} locked={(r) => r.status === 'Canceled'}
        empty={sp.toString() ? 'No work orders match these filters.' : 'No work orders in this folder yet.'} cols={trackingCols(extra)} />
    </SqfPage>
  );
}

// ------------------------------------------------------------------ new work order
export interface ValidationForms { blanks: { id: string; number: string; title: string }[]; families: { id: string; label: string; blankFormId?: string | null; suggestedId?: string | null }[]; tests?: { key: string; label: string; blankFormId?: string | null; suggestedId?: string | null }[] }
interface WiOpt { id: string; number: string; revision: string; title: string; type: string; partId?: string; partNumber?: string; partDescription?: string; operation?: string; repairDescription?: string; siteId: string; departmentId?: string }
export function NewTrackingPage() {
  const nav = useNavigate();
  const [sp] = useSearchParams();
  const { can } = useAuth();
  const sites = useSites(); const users = useUsers(); const priorities = useOptions('priorities');
  const asked = sp.get('type') ?? '';
  const allowed = Object.keys(createPerm).filter((t) => can(createPerm[t]) && (t in builderTypes || t === asked));
  const [type, setType] = useState(asked && allowed.includes(asked) ? asked : allowed[0] ?? '');
  const [familyId, setFamilyId] = useState('');
  const families = useFamilies(); const subTypes = useOptions('inspection-sub-types');
  const forms = useQuery({ queryKey: ['sqf-validation-forms'], queryFn: () => get<ValidationForms>(`${S}/validation-forms`), enabled: type === 'Validation' });
  const famForm = forms.data?.families.find((x) => x.id === familyId);
  const famBlank = famForm?.blankFormId ? forms.data?.blanks.find((b) => b.id === famForm.blankFormId) : undefined;
  const isInsp = type === 'Inspection' || type === 'Warranty';
  const [wi, setWi] = useState<WiOpt | null>(null);
  const [wiText, setWiText] = useState('');
  const [part, setPart] = useState<{ id: string; partNumber: string; name?: string } | null>(null);
  const [f, setF] = useState<Record<string, string>>({ priority: 'Normal' });
  const [siteId, setSiteId] = useState(''); const [departmentId, setDepartmentId] = useState(''); const [assigned, setAssigned] = useState('');
  const [err, setErr] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  useEffect(() => { if (!siteId && sites.data?.length) setSiteId(sites.data[0].id); }, [sites.data, siteId]);
  // automatic number when the type and the product family both have a code; it stays editable
  const famForNumber = type === 'Validation' ? familyId : '';
  const next = useQuery({ queryKey: ['sqf-tr-next', type, famForNumber, part?.id, siteId], enabled: !!type && !!siteId,
    queryFn: () => get<{ number: string | null }>(`${S}/tracking/next-number?${qs({ type, familyId: famForNumber || undefined, partId: famForNumber ? undefined : part?.id, siteId })}`) });
  const [suggested, setSuggested] = useState('');
  useEffect(() => {
    const n = next.data?.number ?? '';
    setF((cur) => (!cur.number || cur.number === suggested ? { ...cur, number: n } : cur));
    setSuggested(n);
  }, [next.data?.number]); // eslint-disable-line react-hooks/exhaustive-deps
  const wis = useQuery({ queryKey: ['sqf-tr-wi', type, wiText], queryFn: () => get<WiOpt[]>(`${S}/tracking/instructions?${qs({ type, q: wiText })}`), enabled: !!type });
  const site = sites.data?.find((s) => s.id === siteId);
  const fld = (k: string, label: string, props: Record<string, unknown> = {}) => <TextField size="small" label={label} value={f[k] ?? ''} onChange={(e) => setF({ ...f, [k]: e.target.value })} {...props} />;
  const iso = (d?: string) => (d ? new Date(d).toISOString() : null);
  const create = async () => {
    setBusy(true); setErr(null);
    try {
      if (!f.number?.trim()) throw new Error('Enter the number.');
      const auto = !!suggested && f.number.trim() === suggested;
      if (isInsp && !f.subType) throw new Error('Choose the inspection sub-type.');
      const r = await post<{ id: string }>(`${S}/tracking`, { number: auto ? null : f.number.trim(), type, quantity: numOrNull(f.quantity), familyId: type === 'Validation' ? nul(familyId) : null,
        inspectionSubType: isInsp ? nul(f.subType) : null, inspectionDescription: isInsp ? nul(f.inspectionDescription) : null, workInstructionId: wi?.id, siteId, departmentId: nul(departmentId), partId: part?.id ?? null, assignedUserId: nul(assigned),
        serialNumber: nul(f.serialNumber), lotNumber: nul(f.lotNumber), workOrder: nul(f.workOrder), repairOrder: nul(f.repairOrder), customerReturnReference: nul(f.customerReturnReference),
        project: nul(f.project), operation: nul(f.operation), priority: nul(f.priority), plannedStart: iso(f.plannedStart), dueAt: iso(f.dueAt), minBenchmarkMinutes: numOrNull(f.min),
        maxBenchmarkMinutes: numOrNull(f.max), reportedCondition: nul(f.reportedCondition) });
      nav(`/sequfloh/record/${r.id}`, { replace: true });
    } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return (
    <SqfPage title="New work order" crumbs={trCrumbs('New work order')} recent={null} permission={allowed.length ? undefined : 'sequfloh.create_validation_record'}
      description="Choose the type, then fill in the work order. A work instruction is optional: when you add one, its steps are copied onto the work order. The drawing for the exact part number is attached automatically.">
      <Paper variant="outlined" sx={{ p: 3, maxWidth: 980 }}>
        {err && <Alert severity="error" sx={{ mb: 2 }}>{err}</Alert>}
        <Grid cols={2}>
          <NumberField size="small" value={f.number ?? ''} onChange={(e) => setF({ ...f, number: e.target.value })} inputProps={{ 'data-testid': 'new-wo-number', maxLength: 200 }} />
          <TextField select size="small" label="Work order type" value={type} onChange={(e) => { setType(e.target.value); setWi(null); }} inputProps={{ 'data-testid': 'new-wo-type' }}>
            {allowed.map((t) => <MenuItem key={t} value={t}>{builderTypes[t] ?? trackingTypes[t]}</MenuItem>)}</TextField>
          <Autocomplete size="small" options={wis.data ?? []} value={wi} loading={wis.isLoading} filterOptions={(x) => x} isOptionEqualToValue={(a, b) => a.id === b.id}
            getOptionLabel={(o) => `${o.number} rev ${o.revision} — ${o.title}`} onInputChange={(_, t, r) => { if (r === 'input') setWiText(t); }}
            onChange={(_, o) => { setWi(o); if (o?.partId) setPart({ id: o.partId, partNumber: o.partNumber ?? '', name: o.partDescription }); if (o?.operation && !f.operation) setF({ ...f, operation: o.operation }); }}
            renderInput={(p) => <TextField {...p} label="Work instruction number (optional)" inputProps={{ ...p.inputProps, 'data-testid': 'new-wo-wi' }} helperText={wis.data && wis.data.length === 0 ? 'No released instruction of a matching type. You can create the work order without one.' : 'Optional how-to steps for the technician.'} />} />
          <PartPicker label={type === 'Repair' ? 'Repairable part' : 'Part'} repairable={type === 'Repair'} value={part} onChange={(p) => setPart(p)} />
          <TextField size="small" label="Part description" value={part?.name ?? ''} disabled InputLabelProps={{ shrink: true }} />
          {fld('quantity', 'Qty', { type: 'number', inputProps: { min: 0, 'data-testid': 'new-wo-qty' } })}
          {type === 'Validation' && <TextField select size="small" label="Product family" value={families.data ? familyId : ''} onChange={(e) => setFamilyId(e.target.value)} inputProps={{ 'data-testid': 'new-wo-family' }}
            helperText="The confirmed validation form for this family is attached as a copy to fill out.">
            <MenuItem value=""><em>None</em></MenuItem>{(families.data ?? []).filter((x) => x.isActive !== false).map((x) => <MenuItem key={x.id} value={x.id}>{x.label}</MenuItem>)}</TextField>}
          {type === 'Validation' && familyId && forms.data && <Box sx={{ gridColumn: '1 / -1' }}>
            {famBlank ? <Alert severity="success" data-testid="new-wo-form-note">A copy of {famBlank.number} {famBlank.title} will be attached to fill out.</Alert>
              : <Alert severity="warning" data-testid="new-wo-form-note">No validation form is confirmed for this product family. An administrator can set one in Settings, Validation forms by product.</Alert>}</Box>}
          {isInsp && <TextField select size="small" label="Inspection sub-type" value={subTypes.data ? f.subType ?? '' : ''} onChange={(e) => setF({ ...f, subType: e.target.value })} inputProps={{ 'data-testid': 'new-wo-subtype' }}>
            {(subTypes.data ?? []).map((o) => <MenuItem key={o} value={o}>{o}</MenuItem>)}</TextField>}
          {isInsp && <Box sx={{ gridColumn: '1 / -1' }}>{fld('inspectionDescription', 'Description of the inspection', { multiline: true, minRows: 2, fullWidth: true, inputProps: { 'data-testid': 'new-wo-description' } })}</Box>}
          {part && <Box sx={{ gridColumn: { md: 'span 2' } }} data-testid="new-record-drawings">
            <Typography variant="caption" color="text.secondary" component="div" sx={{ mb: 0.5 }}>
              Drawings for this exact part number (attached to the work order when it is created)</Typography>
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
          {fld('plannedStart', 'Date', { type: 'datetime-local', InputLabelProps: { shrink: true } })}{fld('dueAt', 'Expected completion date', { type: 'datetime-local', InputLabelProps: { shrink: true } })}
          {fld('min', 'Minimum benchmark (minutes)', { type: 'number' })}{fld('max', 'Maximum benchmark (minutes)', { type: 'number' })}
          {type === 'Repair' && <Box sx={{ gridColumn: '1 / -1' }}>{fld('reportedCondition', 'Reported condition', { multiline: true, minRows: 2, fullWidth: true })}</Box>}
        </Grid>
        <Stack direction="row" gap={1} sx={{ mt: 3 }}>
          <Button variant="contained" disabled={!type || !siteId || busy} onClick={create} data-testid="new-wo-create">Create work order</Button>
          <Button component={RouterLink} to="/sequfloh/tracking/open">Cancel</Button>
        </Stack>
      </Paper>
    </SqfPage>
  );
}

// ------------------------------------------------------------------ record
type J = Record<string, unknown>;
interface TrRecord extends J { id: string; number: string; type: string; status: string; result: string; priority: string; workInstructionId?: string | null; workInstructionNumber: string;
  workInstructionRevision: string; workInstructionTitle: string; stateVersion: number; elapsedSeconds: number; pausedSeconds: number; holdSeconds: number; steps: J[]; characteristics: J[];
  partsRows: J[]; details: Record<string, string>; assignedUserId?: string; assignees?: Person[]; createdAt: string }
interface TrEvent { id: string; eventType: string; fromStatus?: string; toStatus?: string; createdAt: string; userName: string; reason?: string; comment?: string; activeSeconds: number; pausedSeconds: number;
  holdSeconds: number; operationId: string; assignedBefore?: string; assignedAfter?: string }
export interface TrAction { action: string; label: string; reasonRequired: boolean }
interface TrResponse { record: TrRecord; events: TrEvent[]; actions: TrAction[]; completionProblems: string[]; canEnter: boolean; canPlan: boolean; canQualityComment: boolean; canEngineeringComment: boolean; canAttachForm?: boolean; canAssign?: boolean; canDelete?: boolean; serverTime: string }
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
  const [doc, setDoc] = useState<TrRecord | null>(null);
  const [dirty, setDirty] = useState(false);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ sev: 'success' | 'error'; text: string } | null>(null);
  const [act, setAct] = useState<TrAction | null>(null);
  const [attach, setAttach] = useState(false);
  useUnsaved(dirty);
  useEffect(() => { if (q.data && !dirty) setDoc(structuredClone(q.data.record)); }, [q.data]); // eslint-disable-line react-hooks/exhaustive-deps
  if (q.isLoading) return <SqfPage title="Work order" crumbs={trCrumbs('Loading')}><Loading /></SqfPage>;
  if (q.error) return <SqfPage title="Work order" crumbs={trCrumbs('Not available')}><ErrorState error={q.error} retry={q.refetch} /></SqfPage>;
  if (!doc || !q.data) return null;
  const rec = q.data.record;
  if (rec.status === 'Canceled') return <SqfPage title={`Work order ${rec.number}`} crumbs={trCrumbs(rec.number)}><Alert severity="info" data-testid="wo-canceled">This work order is canceled and can't be opened.</Alert></SqfPage>;
  const closed = rec.status === 'Completed' || rec.status === 'Canceled';
  const upd = (patch: Partial<TrRecord>) => { setDoc({ ...doc, ...patch }); setDirty(true); };
  const refresh = async () => { setDirty(false); await qc.invalidateQueries({ queryKey: ['sqf-tr-one', id] }); qc.invalidateQueries({ queryKey: ['sqf-tr'] }); };
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
  const folder = { Validation: 'validation-fai', Fai: 'validation-fai', Repair: 'repair', Production: 'production', Inspection: 'inspection', Warranty: 'warranty', SaltSpray: 'salt-spray' }[rec.type] ?? 'open';
  const title = `${rec.number} — ${trackingTypes[rec.type]} work order`;
  const vf = (rec.details as unknown as { validationForm?: { family?: string; test?: string; fileId?: string; fileNumber?: string; fileTitle?: string; message?: string } }).validationForm;
  const tabs = [{ label: 'Record', value: 'record' }, ...(doc.steps.length ? [{ label: rec.type === 'Repair' ? 'Operations' : 'Steps', value: 'steps' }] : []),
    ...(rec.type === 'Fai' ? [{ label: 'Characteristics', value: 'characteristics' }] : []), ...(rec.type === 'Repair' ? [{ label: 'Repair parts', value: 'parts' }] : []),
    { label: 'Job', value: 'job' }, { label: 'Time', value: 'time' }, { label: 'Findings', value: 'findings' }, { label: 'Comments', value: 'review' }, { label: 'Task events', value: 'events' }];
  const setRow = (list: 'steps' | 'characteristics' | 'partsRows', i: number, patch: J) => upd({ [list]: doc[list].map((r, j) => (j === i ? { ...r, ...patch } : r)) } as Partial<TrRecord>);
  const det = (k: string, label: string, multiline = true) => <TextField size="small" label={label} value={doc.details[k] ?? ''} disabled={!enter} multiline={multiline} minRows={multiline ? 2 : undefined}
    onChange={(e) => upd({ details: { ...doc.details, [k]: e.target.value } })} />;
  const txt = (k: string, label: string, disabled: boolean, multiline = true) => <TextField size="small" label={label} value={s(doc[k])} disabled={disabled} multiline={multiline} minRows={multiline ? 2 : undefined}
    onChange={(e) => upd({ [k]: e.target.value } as Partial<TrRecord>)} />;
  const running = rec.status === 'InProgress';
  return (
    <SqfPage title={title} crumbs={trCrumbs(rec.number, { label: folders[folder].title, to: `/sequfloh/tracking/${folder}` })} recent={{ label: `${rec.number} ${rec.workInstructionTitle}`, itemType: 'Work order', recordId: rec.id }}
      tabs={tabs} tab={tab} onTab={(t) => { const n = new URLSearchParams(sp); n.set('tab', t); setSp(n, { replace: true }); }}
      actions={<RecordActions surface="work-order" dirty={dirty} busy={busy}
        save={(q.data.canEnter || q.data.canPlan || q.data.canQualityComment || q.data.canEngineeringComment) && { onClick: () => void save() }}
        cancel={(q.data.canEnter || q.data.canPlan || q.data.canQualityComment || q.data.canEngineeringComment) && { onClick: () => { setDoc(structuredClone(q.data!.record)); setDirty(false); } }}
        print={{ href: `${S}/tracking/${rec.id}/print` }}
        rename={q.data.canPlan && { kind: 'work-order', id: rec.id, number: rec.number, onDone: () => { void q.refetch(); } }}
        del={{ what: `work order ${rec.number}`, url: `${S}/tracking/${rec.id}`, show: !!q.data.canDelete, to: `/sequfloh/tracking/${folder}`,
          extra: 'Use this for work orders made by mistake. Work orders with logged time are kept for chargebacks and can only be cancelled.' }}>
        <SqfStatus status={rec.status} />
        {q.data.actions.map((a) => <Button key={a.action} variant="outlined" disabled={busy} color={['fail', 'cancel'].includes(a.action) ? 'error' : 'primary'} onClick={() => setAct(a)}>{a.label}</Button>)}
        <Button variant="outlined" component={RouterLink} to={`/sequfloh/record/${rec.id}/report`}>Job report</Button>
        <Button variant="outlined" component={RouterLink} to={`/sequfloh/chargebacks/new?wo=${rec.id}`} data-testid="wo-create-chargeback">Create chargeback report</Button>
      </RecordActions>}>
      <InventoryNote />
      {msg && <Alert severity={msg.sev} sx={{ mb: 2 }} onClose={() => setMsg(null)}>{msg.text}</Alert>}
      {dirty && <Alert severity="info" sx={{ mb: 2 }}>You have unsaved changes.</Alert>}
      {rec.status === 'InProgress' && <ProblemList title="Before this record can be completed:" problems={q.data.completionProblems} />}

      {tab === 'record' && <Stack gap={2}>
        <TimePanel recordId={rec.id} closed={closed} />
        <CrewPanel recordId={rec.id} />
        <AssigneesPanel recordId={rec.id} people={rec.assignees ?? []} canAssign={!!q.data.canAssign} />
        <RecordDrawings recordId={rec.id} />
        {vf && <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Validation form" data-testid="wo-validation-form">
          <Typography variant="h3" sx={{ mb: 1 }}>{vf.test ? `${vf.test} form` : `Validation form${vf.family ? ` — ${vf.family}` : ''}`}</Typography>
          {vf.fileId ? <Typography variant="body2">Fill out <RouterLink to={`/sequfloh/imported-files/${vf.fileId}`}>{vf.fileNumber} {vf.fileTitle}</RouterLink>. {vf.test ? '' : 'It is saved under Completed Validation / FAI for this product family.'}</Typography>
            : <Alert severity="warning">{vf.message ?? 'No validation form is confirmed for this product family.'}</Alert>}
          {q.data.canAttachForm && <Button variant="outlined" sx={{ mt: 1.5 }} disabled={busy || dirty} onClick={() => setAttach(true)} data-testid="wo-attach-form">Attach validation form</Button>}
        </Paper>}
        {attach && <AttachFormDialog id={rec.id} number={rec.number} onClose={() => setAttach(false)} onDone={() => { setAttach(false); void refresh(); setMsg({ sev: 'success', text: 'The form is attached.' }); }} />}
        {(rec.type === 'Inspection' || rec.type === 'Warranty') && <InspectionResults rec={rec} onSaved={refresh} />}
        <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Work order">
          <Typography variant="h3" sx={{ mb: 1.5 }}>Work order</Typography>
          <Facts items={[['Work order number', <Mono>{rec.number}</Mono>], ['Type', trackingTypes[rec.type]],
            ...(rec.inspectionSubType ? [['Inspection sub-type', s(rec.inspectionSubType)] as [string, ReactNode]] : []),
            ...(rec.inspectionDescription ? [['Description', s(rec.inspectionDescription)] as [string, ReactNode]] : []),
            ...(rec.quantityTarget != null ? [['Qty', s(rec.quantityTarget)] as [string, ReactNode]] : []),
            ['Work instruction', <InstructionRef key="wi" rec={rec} canChange={['NotStarted', 'Assigned'].includes(rec.status) && !dirty} onSaved={refresh} />],
            ['Part', rec.partNumber ? <span key="p">{`${s(rec.partNumber)} — ${s(rec.partDescription)}`}<ViewDrawingButton partId={rec.partId as string | undefined} partNumber={s(rec.partNumber)} /></span> : '—'], ['Status', <SqfStatus status={rec.status} />],
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
            {rec.type === 'Validation' && <>{det('sampleIdentification', 'Sample identification')}{det('controlledConditions', 'Controlled conditions (recorded)')}</>}
            {rec.type === 'Fai' && <>{det('materialVerification', 'Material verification')}{det('specialProcessVerification', 'Special-process verification')}{det('certificateReview', 'Certificate review')}
              {det('drawingBalloonReferences', 'Drawing-balloon references', false)}{det('dimensionalResults', 'Dimensional results')}{det('visualResults', 'Visual results')}{det('functionalResults', 'Functional results')}
              <OptionSelect list="inspection-results" label="Inspection result" value={s(doc.finalDisposition)} disabled={!enter} onChange={(v) => upd({ finalDisposition: v })} /></>}
            {rec.type === 'Repair' && <>{txt('reportedCondition', 'Reported condition', !enter)}{txt('observedCondition', 'Observed condition', !enter)}
              <Box sx={{ gridColumn: '1 / -1' }} component="section" aria-label="Test results" data-testid="wo-test-results"><Typography variant="subtitle2" sx={{ mb: 1 }}>Test results</Typography>
                <Grid cols={2}><OptionSelect list="repair-test-results" label="Test result" value={doc.details.repairTestResult ?? ''} disabled={!enter} onChange={(v) => upd({ details: { ...doc.details, repairTestResult: v } })} />
                {det('repairTestReference', 'Test reference', false)}</Grid></Box>{det('postRepairInspection', 'Post-repair inspection')}{det('failureHandling', 'Failure handling')}{det('rejectionHandling', 'Rejection handling')}</>}
            {(rec.type === 'Production' || rec.type === 'Inspection' || rec.type === 'Warranty') && <>{det('qualityCheckpoints', 'Quality checkpoints')}{det('resultEntry', 'Result entry')}</>}
            {txt('evidenceReferences', 'Evidence references', !enter)}{txt('technicianComments', 'Technician comments', !enter)}
            {(rec.type !== 'Production' && rec.type !== 'Inspection' && rec.type !== 'Warranty') && txt('deviations', 'Deviations', !enter)}{txt('nonconformanceReferences', 'Nonconformance references', !enter)}
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

      {tab === 'job' && <JobPanel id={rec.id} canWork={!closed} />}
      {tab === 'time' && <Stack gap={2}><TimePanel recordId={rec.id} closed={closed} /><CrewPanel recordId={rec.id} /></Stack>}
      {tab === 'findings' && <FindingsPanel id={rec.id} canWork={!closed} wi={rec.workInstructionNumber ? `${rec.workInstructionNumber} rev ${rec.workInstructionRevision}` : '—'} />}
      {tab === 'events' && <TaskEvents events={q.data.events} />}
      <TaskActionDialog action={act} record={rec} onClose={() => setAct(null)} onRun={runAction} />
    </SqfPage>
  );
}

const Link = ({ to, children }: { to: string; children: ReactNode }) => <RouterLink to={to} style={{ color: 'inherit' }}>{children}</RouterLink>;
const local = (iso: string) => { if (!iso) return ''; const d = new Date(iso); const p = (n: number) => String(n).padStart(2, '0'); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`; };

/** Inspection and warranty results: qty good, qty bad (feeds the chargeback report), further inspection and the quarantine flag. Saved on its own and audited. */
/** Starts the confirmed form copy on a work order that has none yet; the number starts as the work order number and can be changed. */
function AttachFormDialog({ id, number, onClose, onDone }: { id: string; number: string; onClose: () => void; onDone: () => void }) {
  const [n, setN] = useState(number);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);
  const go = async () => {
    setBusy(true); setErr('');
    try { await post(`${S}/tracking/${id}/validation-form`, { number: n }); onDone(); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return (
    <Dialog open onClose={onClose} maxWidth="xs" fullWidth>
      <FormDialogTitle>Attach validation form</FormDialogTitle>
      <DialogContent>
        <Stack gap={2} sx={{ pt: 1 }}>
          <TextField label="Form number" required value={n} onChange={(e) => setN(e.target.value)} inputProps={{ 'data-testid': 'attach-form-number' }} />
          {err && <Alert severity="error" data-testid="attach-form-error">{err}</Alert>}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={busy || !n.trim()} onClick={() => void go()} data-testid="attach-form-go">Attach</Button>
      </DialogActions>
    </Dialog>
  );
}

function InspectionResults({ rec, onSaved }: { rec: TrRecord; onSaved: () => Promise<void> }) {
  const init = () => ({ good: s(rec.qtyGood), bad: s(rec.qtyBad), further: rec.furtherInspection == null ? '' : rec.furtherInspection ? 'yes' : 'no', quarantine: rec.quarantine ? 'yes' : 'no', why: s(rec.quarantineReason) });
  const [v, setV] = useState(init);
  const [err, setErr] = useState<string | null>(null); const [ok, setOk] = useState(false); const [busy, setBusy] = useState(false);
  useEffect(() => setV(init()), [rec.stateVersion, rec.qtyGood, rec.qtyBad, rec.quarantine]); // eslint-disable-line react-hooks/exhaustive-deps
  const locked = rec.status === 'Canceled' || rec.status === 'Completed';
  const yn = (k: 'further' | 'quarantine', label: string, testid: string) => <TextField select size="small" label={label} value={v[k]} disabled={locked} onChange={(e) => setV({ ...v, [k]: e.target.value })} inputProps={{ 'data-testid': testid }}>
    {k === 'further' && <MenuItem value=""><em>Not recorded</em></MenuItem>}<MenuItem value="yes">Yes</MenuItem><MenuItem value="no">No</MenuItem></TextField>;
  const saveIt = async () => {
    setBusy(true); setErr(null); setOk(false);
    try {
      if (v.quarantine === 'yes' && !v.why.trim()) throw new Error('Say why quarantine is required.');
      await put(`${S}/tracking/${rec.id}/inspection-results`, { qtyGood: numOrNull(v.good), qtyBad: numOrNull(v.bad), furtherInspection: v.further === '' ? null : v.further === 'yes', quarantine: v.quarantine === 'yes', quarantineReason: nul(v.why) });
      setOk(true); await onSaved();
    } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return (
    <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Inspection results" data-testid="wo-inspection-results">
      <Typography variant="h3" sx={{ mb: 1.5 }}>Inspection results</Typography>
      {err && <Alert severity="error" sx={{ mb: 1.5 }}>{err}</Alert>}
      {ok && <Alert severity="success" sx={{ mb: 1.5 }} onClose={() => setOk(false)}>Inspection results saved.</Alert>}
      <Grid cols={4}>
        <TextField size="small" type="number" label="Qty good" value={v.good} disabled={locked} onChange={(e) => setV({ ...v, good: e.target.value })} inputProps={{ min: 0, 'data-testid': 'insp-good' }} />
        <TextField size="small" type="number" label="Qty bad" value={v.bad} disabled={locked} onChange={(e) => setV({ ...v, bad: e.target.value })} inputProps={{ min: 0, 'data-testid': 'insp-bad' }} helperText="Feeds Qty bad on the chargeback report." />
        {yn('further', 'Further inspection', 'insp-further')}
        {yn('quarantine', 'Quarantine', 'insp-quarantine')}
        {v.quarantine === 'yes' && <Box sx={{ gridColumn: '1 / -1' }}><TextField size="small" fullWidth required label="Why?" value={v.why} disabled={locked} onChange={(e) => setV({ ...v, why: e.target.value })} inputProps={{ 'data-testid': 'insp-why' }}
          helperText="Quarantine is a flag on this work order only." /></Box>}
      </Grid>
      {!locked && <Button variant="contained" sx={{ mt: 1.5 }} disabled={busy} onClick={saveIt} data-testid="insp-save">Save inspection results</Button>}
    </Paper>
  );
}

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
      <FormDialogTitle>{action.label} — {record.number}</FormDialogTitle>
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

/** The optional work instruction of a work order: shown as a link (or a dash), with Add, Change and Remove before work starts. */
function InstructionRef({ rec, canChange, onSaved }: { rec: TrRecord; canChange: boolean; onSaved: () => void }) {
  const [open, setOpen] = useState(false); const [wi, setWi] = useState<WiOpt | null>(null); const [text, setText] = useState(''); const [err, setErr] = useState<string | null>(null);
  const wis = useQuery({ queryKey: ['sqf-tr-wi', rec.type, text], queryFn: () => get<WiOpt[]>(`${S}/tracking/instructions?${qs({ type: rec.type, q: text })}`), enabled: open });
  const set = async (id: string | null) => { setErr(null); try { await put(`${S}/tracking/${rec.id}/instruction`, { workInstructionId: id }); setOpen(false); onSaved(); } catch (e) { setErr((e as Error).message); } };
  return <Stack direction="row" gap={1} alignItems="center" flexWrap="wrap" component="span" data-testid="wo-instruction">
    {rec.workInstructionId ? <span><Link to={`/sequfloh/instruction/${rec.workInstructionId}`}><Mono>{rec.workInstructionNumber}</Mono></Link> rev {rec.workInstructionRevision} — {rec.workInstructionTitle}</span> : <span>—</span>}
    {canChange && <Button size="small" data-testid="wo-instruction-change" onClick={() => { setWi(null); setErr(null); setOpen(true); }}>{rec.workInstructionId ? 'Change' : 'Add work instruction'}</Button>}
    {canChange && rec.workInstructionId && <Button size="small" color="error" data-testid="wo-instruction-remove" onClick={() => set(null)}>Remove</Button>}
    <Dialog open={open} onClose={() => setOpen(false)} fullWidth maxWidth="sm">
      <FormDialogTitle>Work instruction</FormDialogTitle>
      <DialogContent><Stack gap={2} sx={{ pt: 1 }}>
        {err && <Alert severity="error">{err}</Alert>}
        <Typography variant="body2" color="text.secondary">Its steps, characteristics and parts are copied onto this work order. Nothing else on the work order changes.</Typography>
        <Autocomplete options={wis.data ?? []} value={wi} onChange={(_, o) => setWi(o)} onInputChange={(_, v, rs) => { if (rs === 'input') setText(v); }} filterOptions={(x) => x}
          getOptionLabel={(o) => `${o.number} rev ${o.revision} — ${o.title}`} isOptionEqualToValue={(a, b) => a.id === b.id}
          renderInput={(p) => <TextField {...p} label="Released work instruction" helperText={wis.data && wis.data.length === 0 ? 'No released instruction of a matching type.' : undefined} />} />
      </Stack></DialogContent>
      <DialogActions><Button onClick={() => setOpen(false)}>Cancel</Button><Button variant="contained" disabled={!wi} onClick={() => wi && set(wi.id)}>Save</Button></DialogActions>
    </Dialog>
  </Stack>;
}
