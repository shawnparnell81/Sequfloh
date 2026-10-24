import { useEffect, useState, type ReactNode } from 'react';
import { Link as RouterLink, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { Alert, Box, Button, Chip, List, ListItemButton, ListItemText, Paper, Stack, TextField, Typography } from '@mui/material';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put } from '../api';
import { useSites, useUsers } from '../components/Kit';
import { fmtDateTime } from '../components/Status';
import { Empty, ErrorState, Filters, Loading, Mono, PagedTable, S, SqfPage, SqfStatus, fmtSeconds, nul, qs, useOptions, words, type Col } from './common';
import { OpenInspectionRequestsPanel } from './InspectionRequests';
import { LiveClock, TaskActionDialog, newOpId, trackingStatuses, trackingTypes, type TrAction, type TrRow } from './Tracking';
import { RecordDrawings } from './Drawings';
import { ProblemList } from './Designer';

type J = Record<string, unknown>;
export interface ExRow extends TrRow { actions: TrAction[]; pauseReason?: string; holdReason?: string; pausedAt?: string; completedBy?: string; activeOperator?: string; overdueSeconds: number;
  durationSeconds: number; varianceReason?: string; failureReason?: string; cancelReason?: string; updatedAt?: string }
const exCrumbs = (label: string) => [{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Live Execution', to: '/sequfloh/execution/dispatch' }, { label }];
const s = (v: unknown) => (v === undefined || v === null ? '' : String(v));

/** Live execution views and the actions offered on their rows (the server decides which actions the user may take). */
const views: Record<string, { api: string; title: string; description: string; actions: string[]; empty: string }> = {
  dispatch: { api: 'dispatch', title: 'Dispatch Queue', description: 'Tasks awaiting start, with assignment, priority, due date, part, operation and the work instruction revision they execute.',
    actions: ['dispatch', 'assign', 'reassign', 'start', 'cancel'], empty: 'No tasks are waiting to start.' },
  paused: { api: 'paused', title: 'Paused Tasks', description: 'Paused tasks with the pause reason, when they were paused and the accumulated paused time. Timing continues from the stored values on resume.',
    actions: ['resume', 'reassign', 'cancel'], empty: 'No tasks are paused.' },
  completed: { api: 'finished', title: 'Completed Tasks', description: 'Completed, failed and canceled tasks with result, duration, variance and who finished them.',
    actions: ['restart', 'reverse-result'], empty: 'No finished tasks yet.' },
  mine: { api: 'mine', title: 'My Assigned Work', description: 'Open tasks assigned to you. Filter by status, priority and due date.',
    actions: ['start', 'resume', 'pause'], empty: 'Nothing is assigned to you right now.' },
  overdue: { api: 'overdue', title: 'Overdue Work', description: 'Open tasks past their due date by more than the configured overdue threshold (SEQUFLOH Settings › Tracking).',
    actions: ['reassign', 'resume', 'cancel'], empty: 'No work is overdue.' },
};

function useRowAction() {
  const qc = useQueryClient();
  const [act, setAct] = useState<{ a: TrAction; r: ExRow } | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const run = async (a: TrAction, input: J) => {
    await post(`${S}/tracking/${act!.r.id}/actions/${a.action}`, { ...input, stateVersion: act!.r.stateVersion });
    setMsg(`${act!.r.number}: ${a.label.toLowerCase()} done.`);
    qc.invalidateQueries({ queryKey: ['sqf-ex'] }); qc.invalidateQueries({ queryKey: ['sqf-tr'] }); qc.invalidateQueries({ queryKey: ['sqf-tr-one'] });
  };
  const buttons = (r: ExRow, allowed: string[]) => <Stack direction="row" gap={0.5} onClick={(e) => e.stopPropagation()}>
    {r.actions.filter((a) => allowed.includes(a.action)).map((a) => <Button key={a.action} size="small" variant="outlined" color={['cancel'].includes(a.action) ? 'error' : 'primary'}
      onClick={() => setAct({ a, r })} aria-label={`${a.label} ${r.number}`}>{a.label}</Button>)}</Stack>;
  const dialog = <TaskActionDialog action={act?.a ?? null} record={act?.r ?? { number: '', status: '' }} onClose={() => setAct(null)} onRun={run} />;
  return { buttons, dialog, msg, clear: () => setMsg(null) };
}

export function ExecutionQueuePage() {
  const { view = 'dispatch' } = useParams();
  const cfg = views[view] ?? views.dispatch;
  const nav = useNavigate();
  const [sp, setSp] = useSearchParams();
  const v = (k: string) => sp.get(k) ?? '';
  const [page, setPage] = useState(1); const [size, setSize] = useState(25); const [sort, setSort] = useState('');
  useEffect(() => { setPage(1); setSort(''); }, [view]);
  const set = (k: string, val: string) => { const n = new URLSearchParams(sp); if (val) n.set(k, val); else n.delete(k); setSp(n); setPage(1); };
  const users = useUsers(); const sites = useSites(); const priorities = useOptions('priorities');
  const q = useQuery({ queryKey: ['sqf-ex', view, sp.toString(), page, size, sort], refetchInterval: 30_000,
    queryFn: () => get<{ items: ExRow[]; total: number }>(`${S}/execution/queue/${cfg.api}?${qs({ q: v('q'), status: v('status'), type: v('type'), assigned: v('assigned'), part: v('part'),
      department: v('department'), site: v('site'), priority: v('priority'), result: v('result'), dateField: v('from') || v('to') ? 'due' : '', from: v('from'), to: v('to'), sort, page, pageSize: size })}`) });
  const row = useRowAction();
  const userOpts: [string, string][] = (users.data ?? []).map((u) => [u.id, u.displayName]);
  const depts: [string, string][] = (sites.data ?? []).filter((x) => !v('site') || x.id === v('site')).flatMap((x) => x.departments.map((d) => [d.id, `${x.code} · ${d.name}`] as [string, string]));
  const statusOpts = { dispatch: ['NotStarted', 'Assigned'], paused: ['Paused'], completed: ['Completed', 'Failed', 'Canceled'] }[view] ?? trackingStatuses.filter((x) => !['Completed', 'Failed', 'Canceled'].includes(x));
  const filters: { key: string; label: string; options: [string, string][] }[] = [
    ...(statusOpts.length > 1 ? [{ key: 'status', label: 'Status', options: statusOpts.map((x) => [x, words(x)] as [string, string]) }] : []),
    { key: 'type', label: 'Type', options: Object.entries(trackingTypes) as [string, string][] },
    { key: 'priority', label: 'Priority', options: (priorities.data ?? []).map((p) => [p, p]) },
    ...(view === 'mine' ? [] : [{ key: 'assigned', label: 'Assigned user', options: userOpts }]),
    { key: 'department', label: 'Department', options: depts },
    { key: 'site', label: 'Site', options: (sites.data ?? []).map((x) => [x.id, x.code]) },
    ...(view === 'completed' ? [{ key: 'result', label: 'Result', options: ['Pending', 'Pass', 'Fail', 'Conditional'].map((r) => [r, r] as [string, string]) }] : []),
  ];
  const base: Col<ExRow>[] = [
    { key: 'number', label: 'Tracking number', sort: true, render: (r) => <Mono>{r.number}</Mono> },
    { key: 'type', label: 'Type', render: (r) => trackingTypes[r.type] ?? r.type },
    { key: 'partNumber', label: 'Part', sort: true, render: (r) => r.partNumber ?? '—' },
    { key: 'operation', label: 'Operation', render: (r) => r.operation ?? '—' },
    { key: 'wi', label: 'Instruction / revision', render: (r) => (r.workInstructionNumber ? <><Mono>{r.workInstructionNumber}</Mono> rev {r.workInstructionRevision}</> : '—') },
  ];
  const tail: Col<ExRow>[] = [{ key: 'actions', label: 'Actions', render: (r) => row.buttons(r, cfg.actions) }];
  const cols: Col<ExRow>[] = view === 'paused' ? [...base,
    { key: 'assignedUser', label: 'Assigned', render: (r) => (r.assignees?.length ? r.assignees.join(', ') : r.assignedUser ?? '—') },
    { key: 'pauseReason', label: 'Pause reason', render: (r) => r.pauseReason ?? '—' },
    { key: 'pausedAt', label: 'Paused at', sort: true, render: (r) => fmtDateTime(r.pausedAt) },
    { key: 'paused', label: 'Paused time', render: (r) => <LiveClock seconds={r.pausedSeconds} running label="Paused time" /> },
    { key: 'elapsed', label: 'Active time', render: (r) => <Mono>{fmtSeconds(r.elapsedSeconds)}</Mono> }, ...tail]
    : view === 'completed' ? [...base,
    { key: 'status', label: 'Status', sort: true, render: (r) => <SqfStatus status={r.status} /> },
    { key: 'duration', label: 'Duration (active)', render: (r) => <Mono>{fmtSeconds(r.elapsedSeconds)}</Mono> },
    { key: 'variance', label: 'Variance', render: (r) => (r.varianceMinutes != null ? `${r.varianceMinutes} min` : '—') },
    { key: 'actualCompletion', label: 'Finished', sort: true, render: (r) => `${fmtDateTime(r.actualCompletion)}${r.completedBy ? ' · ' + r.completedBy : ''}` },
    { key: 'reason', label: 'Reason', render: (r) => r.failureReason ?? r.cancelReason ?? '—' }, ...tail]
    : view === 'overdue' ? [...base,
    { key: 'status', label: 'Status', sort: true, render: (r) => <SqfStatus status={r.status} /> },
    { key: 'assignedUser', label: 'Assigned', render: (r) => (r.assignees?.length ? r.assignees.join(', ') : r.assignedUser ?? '—') },
    { key: 'department', label: 'Department', render: (r) => r.department ?? '—' },
    { key: 'dueAt', label: 'Due', sort: true, render: (r) => fmtDateTime(r.dueAt) },
    { key: 'overdue', label: 'Overdue by', render: (r) => <Mono>{fmtSeconds(r.overdueSeconds)}</Mono> }, ...tail]
    : [...base,
    { key: 'assignedUser', label: 'Assigned', render: (r) => (r.assignees?.length ? r.assignees.join(', ') : r.assignedUser ?? '—') },
    { key: 'status', label: 'Status', sort: true, render: (r) => <SqfStatus status={r.status} /> },
    { key: 'priority', label: 'Priority', sort: true },
    { key: 'dueAt', label: 'Due', sort: true, render: (r) => <>{fmtDateTime(r.dueAt)}{r.overdueSeconds > 0 && <Chip size="small" color="error" variant="outlined" label="Overdue" sx={{ ml: 0.5 }} />}</> },
    ...(view === 'mine' ? [{ key: 'elapsed', label: 'Elapsed', render: (r: ExRow) => <LiveClock seconds={r.elapsedSeconds} running={r.status === 'InProgress'} /> }] : []), ...tail];
  return (
    <SqfPage title={cfg.title} crumbs={exCrumbs(cfg.title)} description={cfg.description} permission="sequfloh.view_live_execution"
      actions={view === 'mine' && <Button variant="contained" component={RouterLink} to="/sequfloh/execution/active">Open Active Station</Button>}>
      {row.msg && <Alert severity="success" sx={{ mb: 2 }} onClose={row.clear}>{row.msg}</Alert>}
      {view === 'dispatch' && <OpenInspectionRequestsPanel />}
      <Filters search={v('q')} onSearch={(x) => set('q', x)} filters={filters} values={Object.fromEntries(filters.map((f) => [f.key, v(f.key)]))} onChange={set} onClear={() => { setSp({}); setPage(1); }}
        extra={<>
          <TextField size="small" label="Part number" defaultValue={v('part')} key={'p' + v('part')} onBlur={(e) => set('part', e.target.value.trim())} sx={{ width: 150 }} />
          <TextField size="small" type="date" label="Due from" value={v('from')} onChange={(e) => set('from', e.target.value)} InputLabelProps={{ shrink: true }} sx={{ width: 150 }} />
          <TextField size="small" type="date" label="Due to" value={v('to')} onChange={(e) => set('to', e.target.value)} InputLabelProps={{ shrink: true }} sx={{ width: 150 }} />
        </>} />
      <PagedTable q={q} page={page} size={size} onPage={setPage} onSize={setSize} sort={sort} onSort={setSort} onRow={(r) => nav(`/sequfloh/record/${r.id}`)}
        empty={sp.toString() ? 'No tasks match these filters.' : cfg.empty} cols={cols} />
      {row.dialog}
    </SqfPage>
  );
}

// ------------------------------------------------------------------ active station
interface StationResponse { record: J & { id: string; number: string; type: string; status: string; stateVersion: number; elapsedSeconds: number; pausedSeconds: number; holdSeconds: number;
  steps: J[]; characteristics: J[]; details: Record<string, string>; assignedUserId?: string; minBenchmarkMinutes?: number; maxBenchmarkMinutes?: number };
  actions: TrAction[]; completionProblems: string[]; nextAction: string; canEnter: boolean; canPlan: boolean }
const planKeys = ['serialNumber', 'lotNumber', 'workOrder', 'repairOrder', 'customerReturnReference', 'project', 'priority', 'plannedStart', 'dueAt'];
const entryKeys = ['evidenceReferences', 'technicianComments', 'deviations', 'nonconformanceReferences', 'reportedCondition', 'observedCondition', 'finalDisposition', 'varianceReason', 'result',
  'qualityComments', 'engineeringComments', 'engineeringConclusion'];

export function ActiveStationPage() {
  const [sp, setSp] = useSearchParams();
  const qc = useQueryClient();
  const list = useQuery({ queryKey: ['sqf-ex', 'station'], queryFn: () => get<{ items: ExRow[]; total: number }>(`${S}/execution/queue/station?pageSize=50`), refetchInterval: 30_000 });
  const selected = sp.get('task') ?? list.data?.items[0]?.id ?? '';
  const one = useQuery({ queryKey: ['sqf-tr-one', selected], queryFn: () => get<StationResponse>(`${S}/tracking/${selected}`), enabled: !!selected });
  const [form, setForm] = useState<Record<string, string>>({});
  const [dirty, setDirty] = useState(false);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ sev: 'success' | 'error'; text: string } | null>(null);
  const [act, setAct] = useState<TrAction | null>(null);
  useEffect(() => {
    if (!one.data || dirty) return;
    const r = one.data.record;
    setForm({ technicianComments: s(r.technicianComments), evidenceReferences: s(r.evidenceReferences), result: s(r.result), resultEntry: s(r.details.resultEntry), qualityCheckpoints: s(r.details.qualityCheckpoints) });
  }, [one.data]); // eslint-disable-line react-hooks/exhaustive-deps
  const refresh = async () => { setDirty(false); await qc.invalidateQueries({ queryKey: ['sqf-tr-one', selected] }); qc.invalidateQueries({ queryKey: ['sqf-ex'] }); qc.invalidateQueries({ queryKey: ['sqf-tr'] }); };
  const save = async (quiet = false) => {
    const r = one.data!.record;
    setBusy(true); setMsg(null);
    try {
      const body: J = { operationId: newOpId(), stateVersion: r.stateVersion };
      for (const k of [...planKeys, ...entryKeys]) body[k] = nul(s(r[k]));
      body.technicianComments = nul(form.technicianComments); body.evidenceReferences = nul(form.evidenceReferences);
      body.details = { resultEntry: form.resultEntry ?? '', qualityCheckpoints: form.qualityCheckpoints ?? '' };
      await put(`${S}/tracking/${r.id}`, body);
      await refresh(); if (!quiet) setMsg({ sev: 'success', text: 'Saved.' }); return true;
    } catch (e) { setMsg({ sev: 'error', text: (e as Error).message }); return false; } finally { setBusy(false); }
  };
  const run = async (a: TrAction, input: J) => {
    let version = one.data!.record.stateVersion;
    if (dirty) { if (!(await save(true))) throw new Error('Save your entries first.'); version = (await get<StationResponse>(`${S}/tracking/${selected}`)).record.stateVersion; }
    await post(`${S}/tracking/${selected}/actions/${a.action}`, { ...input, stateVersion: version });
    await refresh(); setMsg({ sev: 'success', text: `${a.label}: done.` });
  };
  const pick = (id: string) => { if (dirty && !window.confirm('Discard unsaved changes?')) return; setDirty(false); setMsg(null); const n = new URLSearchParams(sp); n.set('task', id); setSp(n, { replace: true }); };
  const f = (k: string, label: string, disabled: boolean, multiline = true) => <TextField size="small" label={label} value={form[k] ?? ''} disabled={disabled} multiline={multiline} minRows={multiline ? 2 : undefined}
    onChange={(e) => { setForm({ ...form, [k]: e.target.value }); setDirty(true); }} />;
  const d = one.data; const r = d?.record;
  const checkpoints = (r?.steps ?? []).filter((x) => x.qualityCheckpoint || x.visualCheckpoint).map((x) => `${s(x.sequence)} ${s(x.title ?? x.operationStep)}: ${s(x.qualityCheckpoint ?? x.visualCheckpoint)}`);
  const enter = !!d?.canEnter && !busy;
  const Fact = ({ k, children }: { k: string; children: ReactNode }) => <Box><Typography variant="caption" color="text.secondary" component="div">{k}</Typography><Typography variant="body2" component="div">{children}</Typography></Box>;
  return (
    <SqfPage title="Active Station" crumbs={exCrumbs('Active Station')} permission="sequfloh.view_live_execution"
      description="Your tasks at this station. The clock is calculated from the stored start time and accumulated durations, so refresh, sign-out or closing the browser never resets it.">
      {list.isLoading ? <Loading /> : list.error ? <ErrorState error={list.error} retry={list.refetch} /> : (list.data?.items.length ?? 0) === 0
        ? <Empty text="You have no assigned or active tasks." action={<Button component={RouterLink} to="/sequfloh/execution/dispatch">Open the Dispatch Queue</Button>} />
        : <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: 'minmax(0, 1fr)', md: '300px minmax(0, 1fr)' } }}>
          <Paper variant="outlined" component="nav" aria-label="My station tasks">
            <List dense disablePadding>{list.data!.items.map((t) => <ListItemButton key={t.id} selected={t.id === selected} onClick={() => pick(t.id)}>
              <ListItemText primary={<Stack direction="row" gap={1} alignItems="center"><Mono>{t.number}</Mono><SqfStatus status={t.status} /></Stack>}
                secondary={`${t.partNumber ?? '—'} · ${t.operation ?? t.workInstructionTitle}`} /></ListItemButton>)}</List>
          </Paper>
          <Box>
            {one.isLoading || !r ? <Loading /> : one.error ? <ErrorState error={one.error} retry={one.refetch} /> : <Stack gap={2}>
              {msg && <Alert severity={msg.sev} onClose={() => setMsg(null)}>{msg.text}</Alert>}
              <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Active task">
                <Stack direction={{ xs: 'column', sm: 'row' }} justifyContent="space-between" gap={1} sx={{ mb: 2 }}>
                  <Box>
                    <Typography variant="h3"><Mono>{r.number}</Mono> · {trackingTypes[r.type]}</Typography>
                    <Typography variant="body2" color="text.secondary">{s(r.workInstructionTitle)}</Typography>
                  </Box>
                  <Box sx={{ textAlign: { sm: 'right' } }}>
                    <Typography variant="caption" color="text.secondary" component="div">Elapsed (active)</Typography>
                    <Typography variant="h2" component="div"><LiveClock key={r.stateVersion} seconds={r.elapsedSeconds} running={r.status === 'InProgress'} /></Typography>
                  </Box>
                </Stack>
                <Box sx={{ display: 'grid', gap: 1.5, gridTemplateColumns: { xs: '1fr 1fr', md: 'repeat(4, minmax(0, 1fr))' } }}>
                  <Fact k="Tracking number"><Mono>{r.number}</Mono></Fact>
                  <Fact k="Work instruction">{r.workInstructionNumber ? <Mono>{s(r.workInstructionNumber)}</Mono> : '—'}</Fact>
                  <Fact k="Revision">{s(r.workInstructionRevision)}</Fact>
                  <Fact k="Status"><SqfStatus status={r.status} /></Fact>
                  <Fact k="Part">{r.partNumber ? `${s(r.partNumber)} — ${s(r.partDescription)}` : '—'}</Fact>
                  <Fact k="Operation">{s(r.operation) || '—'}</Fact>
                  <Fact k="Assigned user">{s(r.assignedUser) || '—'}</Fact>
                  <Fact k="Priority">{s(r.priority)}</Fact>
                  <Fact k="Target range">{r.minBenchmarkMinutes != null || r.maxBenchmarkMinutes != null ? `${s(r.minBenchmarkMinutes) || '—'} – ${s(r.maxBenchmarkMinutes) || '—'} min` : '—'}</Fact>
                  <Fact k="Paused time"><LiveClock key={'p' + r.stateVersion} seconds={r.pausedSeconds} running={r.status === 'Paused'} label="Paused time" /></Fact>
                  <Fact k="Due">{fmtDateTime(s(r.dueAt))}</Fact>
                </Box>
                <Box sx={{ mt: 2 }}><RecordDrawings recordId={r.id} /></Box>
                <Alert severity="info" sx={{ mt: 2 }} icon={false} aria-label="Next required action"><strong>Next required action:</strong> {d.nextAction}</Alert>
                <Stack direction="row" gap={1} flexWrap="wrap" sx={{ mt: 2 }}>
                  {d.canEnter && <Button variant="contained" disabled={!dirty || busy} onClick={() => save()}>Save</Button>}
                  {d.actions.map((a) => <Button key={a.action} variant="outlined" disabled={busy} color={['fail', 'cancel'].includes(a.action) ? 'error' : 'primary'} onClick={() => setAct(a)}>{a.label}</Button>)}
                  <Button component={RouterLink} to={`/sequfloh/record/${r.id}`}>Open full record</Button>
                </Stack>
              </Paper>
              {r.status === 'InProgress' && <ProblemList title="Before this task can be completed:" problems={d.completionProblems} />}
              <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Quality checkpoints">
                <Typography variant="h3" sx={{ mb: 1 }}>Quality checkpoints</Typography>
                {checkpoints.length ? <ul style={{ margin: 0 }}>{checkpoints.map((c) => <li key={c}><Typography variant="body2">{c}</Typography></li>)}</ul>
                  : <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>The work instruction lists no step checkpoints.</Typography>}
                <Box sx={{ mt: 1 }}>{f('qualityCheckpoints', 'Checkpoint notes', !enter)}</Box>
              </Paper>
              <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Result entry">
                <Typography variant="h3" sx={{ mb: 1.5 }}>Result entry</Typography>
                {!d.canEnter && <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>Results are entered while the task is in progress.</Typography>}
                <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: 'minmax(0, 1fr)', md: '1fr 1fr' } }}>
                  {r.type !== 'Production' && r.type !== 'Inspection' && r.type !== 'Warranty' && <Typography variant="body2">Step and characteristic results are entered on the <RouterLink to={`/sequfloh/record/${r.id}?tab=${r.type === 'Fai' ? 'characteristics' : 'steps'}`}>full work order</RouterLink>.</Typography>}
                  {f('resultEntry', 'Result entry notes', !enter)}
                  {f('evidenceReferences', 'Evidence references', !enter)}
                  {f('technicianComments', 'Technician comments', !enter)}
                </Box>
              </Paper>
            </Stack>}
          </Box>
        </Box>}
      {r && <TaskActionDialog action={act} record={{ ...r, number: r.number, status: r.status, assignedUserId: r.assignedUserId ?? null }} onClose={() => setAct(null)} onRun={run} />}
    </SqfPage>
  );
}

// ------------------------------------------------------------------ work history
interface EvRow { id: string; trackingRecordId: string; trackingNumber: string; eventType: string; fromStatus?: string; toStatus?: string; createdAt: string; userName: string; reason?: string; comment?: string;
  activeSeconds: number; pausedSeconds: number; holdSeconds: number; operationId: string; type: string; assignedBefore?: string; assignedAfter?: string }
const eventTypes = ['Created', 'Dispatch', 'Assignment', 'Start', 'Pause', 'Resume', 'Hold', 'Restart', 'Completion', 'Failure', 'Reassignment', 'Cancellation',
  'AdministrativeOverride', 'RecordUpdate'];

export function WorkHistoryPage() {
  const nav = useNavigate();
  const [sp, setSp] = useSearchParams();
  const tab = sp.get('tab') ?? 'events';
  const v = (k: string) => sp.get(k) ?? '';
  const [page, setPage] = useState(1); const [size, setSize] = useState(25); const [sort, setSort] = useState('createdAt:desc');
  const set = (k: string, val: string) => { const n = new URLSearchParams(sp); if (val) n.set(k, val); else n.delete(k); setSp(n); setPage(1); };
  const users = useUsers(); const sites = useSites();
  const ev = useQuery({ queryKey: ['sqf-ex', 'events', sp.toString(), page, size, sort], enabled: tab === 'events',
    queryFn: () => get<{ items: EvRow[]; total: number }>(`${S}/execution/events?${qs({ q: v('q'), eventType: v('eventType'), user: v('user'), type: v('type'), site: v('site'), from: v('from'), to: v('to'), sort, page, pageSize: size })}`) });
  const done = useQuery({ queryKey: ['sqf-ex', 'history-finished', sp.toString(), page, size, sort], enabled: tab === 'completed',
    queryFn: () => get<{ items: ExRow[]; total: number }>(`${S}/execution/queue/finished?${qs({ q: v('q'), type: v('type'), site: v('site'), assigned: v('user'), dateField: 'completed', from: v('from'), to: v('to'),
      sort: sort.startsWith('createdAt') ? '' : sort, page, pageSize: size })}`) });
  const filters: { key: string; label: string; options: [string, string][] }[] = [
    ...(tab === 'events' ? [{ key: 'eventType', label: 'Event', options: eventTypes.map((e) => [e, words(e)] as [string, string]) }] : []),
    { key: 'type', label: 'Type', options: Object.entries(trackingTypes) as [string, string][] },
    { key: 'user', label: tab === 'events' ? 'User' : 'Assigned user', options: (users.data ?? []).map((u) => [u.id, u.displayName]) },
    { key: 'site', label: 'Site', options: (sites.data ?? []).map((x) => [x.id, x.code]) },
  ];
  return (
    <SqfPage title="Work History" crumbs={exCrumbs('Work History')} permission="sequfloh.view_live_execution"
      description="Completed execution history and the append-only task events behind it. Events cannot be edited or deleted."
      tabs={[{ label: 'Task events', value: 'events' }, { label: 'Completed executions', value: 'completed' }]} tab={tab}
      onTab={(t) => { const n = new URLSearchParams(sp); n.set('tab', t); setSp(n, { replace: true }); setPage(1); setSort(t === 'events' ? 'createdAt:desc' : 'actualCompletion:desc'); }}>
      <Filters search={v('q')} onSearch={(x) => set('q', x)} filters={filters} values={Object.fromEntries(filters.map((f) => [f.key, v(f.key)]))} onChange={set}
        onClear={() => { setSp(tab === 'events' ? {} : { tab }); setPage(1); }}
        extra={<>
          <TextField size="small" type="date" label="From" value={v('from')} onChange={(e) => set('from', e.target.value)} InputLabelProps={{ shrink: true }} sx={{ width: 150 }} />
          <TextField size="small" type="date" label="To" value={v('to')} onChange={(e) => set('to', e.target.value)} InputLabelProps={{ shrink: true }} sx={{ width: 150 }} />
        </>} />
      {tab === 'events' ? <PagedTable q={ev} page={page} size={size} onPage={setPage} onSize={setSize} sort={sort} onSort={setSort} onRow={(r) => nav(`/sequfloh/record/${r.trackingRecordId}?tab=events`)}
        empty={sp.toString() ? 'No task events match these filters.' : 'No task events yet.'} cols={[
          { key: 'createdAt', label: 'When', sort: true, render: (r) => fmtDateTime(r.createdAt) },
          { key: 'trackingNumber', label: 'Tracking number', sort: true, render: (r) => <Mono>{r.trackingNumber}</Mono> },
          { key: 'type', label: 'Type', render: (r) => trackingTypes[r.type] ?? r.type },
          { key: 'eventType', label: 'Event', sort: true, render: (r) => words(r.eventType) },
          { key: 'status', label: 'Status change', render: (r) => (r.fromStatus || r.toStatus ? `${words(r.fromStatus ?? '')} → ${words(r.toStatus ?? '')}` : '—') },
          { key: 'userName', label: 'User', sort: true },
          { key: 'assign', label: 'Assignment', render: (r) => (r.assignedBefore !== r.assignedAfter ? `${r.assignedBefore ?? '—'} → ${r.assignedAfter ?? '—'}` : '—') },
          { key: 'reason', label: 'Reason / comment', render: (r) => [r.reason, r.comment].filter(Boolean).join(' · ') || '—' },
          { key: 'active', label: 'Active', render: (r) => <Mono>{fmtSeconds(r.activeSeconds)}</Mono> },
          { key: 'paused', label: 'Paused', render: (r) => <Mono>{fmtSeconds(r.pausedSeconds)}</Mono> },
        ]} />
        : <PagedTable q={done} page={page} size={size} onPage={setPage} onSize={setSize} sort={sort} onSort={setSort} onRow={(r) => nav(`/sequfloh/record/${r.id}?tab=events`)}
          empty={sp.toString() !== 'tab=completed' ? 'No completed executions match these filters.' : 'No completed executions yet.'} cols={[
            { key: 'number', label: 'Tracking number', sort: true, render: (r) => <Mono>{r.number}</Mono> },
            { key: 'type', label: 'Type', render: (r) => trackingTypes[r.type] ?? r.type },
            { key: 'partNumber', label: 'Part', sort: true, render: (r) => r.partNumber ?? '—' },
            { key: 'wi', label: 'Instruction / revision', render: (r) => (r.workInstructionNumber ? <><Mono>{r.workInstructionNumber}</Mono> rev {r.workInstructionRevision}</> : '—') },
            { key: 'status', label: 'Status', sort: true, render: (r) => <SqfStatus status={r.status} /> },
                    { key: 'active', label: 'Active', render: (r) => <Mono>{fmtSeconds(r.elapsedSeconds)}</Mono> },
            { key: 'paused', label: 'Paused', render: (r) => <Mono>{fmtSeconds(r.pausedSeconds)}</Mono> },
              { key: 'variance', label: 'Variance', render: (r) => (r.varianceMinutes != null ? `${r.varianceMinutes} min` : '—') },
            { key: 'actualCompletion', label: 'Finished', sort: true, render: (r) => `${fmtDateTime(r.actualCompletion)}${r.completedBy ? ' · ' + r.completedBy : ''}` },
          ]} />}
    </SqfPage>
  );
}
