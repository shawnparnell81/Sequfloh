import { setupOptions } from '../components/SetupLink';
import { useEffect, useState, type ReactNode } from 'react';
import { Link as RouterLink, Navigate, useNavigate, useSearchParams } from 'react-router-dom';
import { Alert, Box, Button, List, ListItemButton, ListItemText, MenuItem, Paper, Stack, TextField, Typography, alpha, type Theme } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { get, put } from '../api';
import { useAuth } from '../auth';
import { PanelHead, Readout, useUsers } from '../components/Kit';
import { fmtDate, fmtDateTime } from '../components/Status';
import { ErrorState, Filters, Grid, Loading, Mono, PagedTable, S, SqfPage, SqfStatus, nul, qs } from './common';
import { InstructionsFolderPage } from './Instructions';
import { DEVELOPMENT_LINES, DevelopmentFiles } from './Imports';

const typeLabel: Record<string, string> = { Validation: 'Validation / FAI', Fai: 'Validation / FAI', Repair: 'Repair / Rework', Production: 'Production', Inspection: 'Inspection', ValidationFai: 'Validation / FAI', RepairTest: 'Repair / Rework Work Instruction', RepairWork: 'Repair / Rework Work Instruction' };
const qCrumbs = (label: string) => [{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Quality Personnel', to: '/sequfloh/quality' }, { label }];
const eCrumbs = (label: string) => [{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Engineering Personnel', to: '/sequfloh/engineering' }, { label }];

interface Mini { id: string; number: string; revision?: string; title?: string; type: string; status: string; result?: string; partNumber?: string; workInstructionNumber?: string;
  workInstructionRevision?: string; releasedAt?: string; updatedAt?: string; dueAt?: string; revisionReason?: string }

/** A count card that opens the list behind it. */
export function Stat({ label, value, to }: { label: string; value?: number; to?: string }) {
  return <Readout label={label} value={value ?? '—'} to={to} color="primary.main" />;
}
export const Stats = ({ children }: { children: ReactNode }) =>
  <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: '1fr 1fr', md: 'repeat(4, minmax(0, 1fr))' }, mb: 2 }}>{children}</Box>;
export function MiniList({ title, rows, empty, kind }: { title: string; rows?: Mini[]; empty: string; kind: 'instruction' | 'record' }) {
  return (
    <Paper variant="outlined" sx={{ overflow: 'hidden' }} component="section" aria-label={title}>
      <PanelHead title={title} />
      {(rows ?? []).length === 0 ? <Typography variant="body2" color="text.secondary" sx={{ px: 2, py: 1.75 }}>{empty}</Typography> :
        <List dense disablePadding>{rows!.map((r) => <ListItemButton key={r.id} {...(kind === 'record' && r.status === 'Canceled' ? { disableRipple: true, "data-canceled": true, sx: (t: Theme) => ({ cursor: 'default', bgcolor: alpha(t.palette.error.main, t.palette.mode === 'dark' ? 0.22 : 0.1), '&:hover': { bgcolor: alpha(t.palette.error.main, t.palette.mode === 'dark' ? 0.22 : 0.1) } }) } : { component: RouterLink, to: kind === 'instruction' ? `/sequfloh/instruction/${r.id}` : `/sequfloh/record/${r.id}` })}>
          <ListItemText primary={<Stack direction="row" gap={1} alignItems="center"><Mono>{r.number}{r.revision ? ` rev ${r.revision}` : ''}</Mono><SqfStatus status={r.status} /></Stack>}
            secondary={[typeLabel[r.type] ?? r.type, r.title ?? (r.workInstructionNumber ? `${r.workInstructionNumber} rev ${r.workInstructionRevision}` : ''), r.partNumber, fmtDateTime(r.releasedAt ?? r.updatedAt)].filter(Boolean).join(' · ')} />
        </ListItemButton>)}</List>}
    </Paper>
  );
}
const Two = ({ children }: { children: ReactNode }) => <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: 'minmax(0, 1fr)', md: '1fr 1fr' } }}>{children}</Box>;

// ------------------------------------------------------------------ quality
interface QDash { validationDrafts: number; repairDrafts: number; completedThisWeek: number; failedValidations: number; failedFai: number; failedRepairTests: number;
  overdueRecords: number; failedRecords: Mini[]; overdueRows: Mini[]; recentlyReleased: Mini[]; recentlyRevised: Mini[] }
export function QualityDashboardPage() {
  const q = useQuery({ queryKey: ['sqf-dash', 'quality'], queryFn: () => get<QDash>(`${S}/quality/dashboard`) });
  const d = q.data;
  return (
    <SqfPage title="Quality Dashboard" crumbs={qCrumbs('Quality Dashboard')} permission="sequfloh.view_quality_folder"
      description="Draft instructions waiting to be released, finished work, failed records and overdue records across the records you can see.">
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : <>
        <Stats>
          <Stat label="Draft validation instructions" value={d?.validationDrafts} to="/sequfloh/instructions/draft" />
          <Stat label="Draft repair instructions" value={d?.repairDrafts} to="/sequfloh/instructions/draft" />
          <Stat label="Completed this week" value={d?.completedThisWeek} to="/sequfloh/execution/completed" />
          <Stat label="Overdue records" value={d?.overdueRecords} to="/sequfloh/execution/overdue" />
        </Stats>
        <Two>
          <MiniList title="Overdue records" rows={d?.overdueRows} empty="Nothing is overdue." kind="record" />
          <MiniList title="Recently released instructions" rows={d?.recentlyReleased} empty="No instructions released yet." kind="instruction" />
          <MiniList title="Recently revised instructions" rows={d?.recentlyRevised} empty="No revised instructions released yet." kind="instruction" />
        </Two>
      </>}
    </SqfPage>
  );
}
export const QualityValidationPage = () => <InstructionsFolderPage fixedFolder="validation" crumbsOverride={qCrumbs('Validation Work Instructions')} permission="sequfloh.view_quality_folder" />;
export const QualityRepairPage = () => <InstructionsFolderPage fixedFolder="repair" crumbsOverride={qCrumbs('Repair / Rework Work Instructions')} permission="sequfloh.view_quality_folder" />;

// ------------------------------------------------------------------ engineering
interface EDash { inDevelopment: number; draftValidation: number; draftRepair: number; openValidationActivities: number; failedValidations: number;
  releasedValidationInstructions: number; myDevelopment: Mini[]; openValidationRows: Mini[] }
export function EngineeringDashboardPage() {
  const q = useQuery({ queryKey: ['sqf-dash', 'engineering'], queryFn: () => get<EDash>(`${S}/engineering/dashboard`) });
  const d = q.data;
  return (
    <SqfPage title="Engineering Dashboard" crumbs={eCrumbs('Engineering Dashboard')} permission="sequfloh.view_engineering_folder"
      description="Instructions in development, validation activity and released validation instructions.">
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : <>
        <Stats>
          <Stat label="Instructions in development" value={d?.inDevelopment} to="/sequfloh/engineering/development" />
          <Stat label="Draft validation instructions" value={d?.draftValidation} to="/sequfloh/engineering/development" />
          <Stat label="Draft repair instructions" value={d?.draftRepair} to="/sequfloh/engineering/development" />
          <Stat label="Open validation activities" value={d?.openValidationActivities} to="/sequfloh/engineering/validation?tab=open" />
          <Stat label="Released validation instructions" value={d?.releasedValidationInstructions} to="/sequfloh/instructions/released" />
        </Stats>
        <Two>
          <MiniList title="My development work" rows={d?.myDevelopment} empty="No instructions in development are assigned to you." kind="instruction" />
          <MiniList title="Open validation activities" rows={d?.openValidationRows} empty="No validation activity is open." kind="record" />
        </Two>
      </>}
    </SqfPage>
  );
}

/** Engineering › Validation: instructions, plans, open activities, results, failed records and revision history. */
interface TrMini { id: string; number: string; type: string; status: string; result: string; workInstructionNumber: string; workInstructionRevision: string; partNumber?: string;
  assignedUser?: string; assignees?: string[]; dueAt?: string; actualCompletion?: string; updatedAt?: string }
export function EngineeringValidationPage() {
  const nav = useNavigate();
  const [sp, setSp] = useSearchParams();
  const tab = sp.get('tab') ?? 'instructions';
  const [page, setPage] = useState(1); const [size, setSize] = useState(25);
  useEffect(() => setPage(1), [tab]);
  const trackFolder: Record<string, Record<string, string>> = { open: { folder: 'validation', status: '' }, results: { folder: 'validation', result: '' }, failed: { folder: 'validation', result: 'Fail' } };
  const tr = useQuery({ queryKey: ['sqf-tr', 'eng', tab, page, size], enabled: !!trackFolder[tab],
    queryFn: () => get<{ items: TrMini[]; total: number }>(`${S}/tracking?${qs({ ...trackFolder[tab], page, pageSize: size, sort: 'updatedAt:desc' })}`)
      .then((r) => (tab === 'open' ? { ...r, items: r.items.filter((x) => !['Completed', 'Failed', 'Canceled'].includes(x.status)) } : r)) });
  const tabs = [{ label: 'Validation work instructions', value: 'instructions' }, { label: 'Validation plans', value: 'plans' }, { label: 'Open validation activities', value: 'open' },
    { label: 'Validation results', value: 'results' }, { label: 'Failed validation records', value: 'failed' },
    { label: 'Released validation instructions', value: 'released' }, { label: 'Revision history', value: 'revisions' }];
  if (tab === 'instructions' || tab === 'plans' || tab === 'released') {
    return <InstructionsFolderPage key={tab} fixedFolder={tab === 'released' ? 'released' : tab === 'plans' ? 'draft' : 'validation'} tabs={tabs} tab={tab} onTab={(t) => setSp({ tab: t })}
      crumbsOverride={eCrumbs('Validation / FAI')} titleOverride="Validation / FAI" permission="sequfloh.view_engineering_folder"
      descriptionOverride={tab === 'plans' ? 'Validation plans being developed: draft instructions.' : tab === 'released' ? 'Released instructions available for validation execution.' : 'Validation and Validation / FAI work instructions.'} />;
  }
  if (tab === 'revisions') return <Navigate to="/sequfloh/revision-history" replace />;
  return (
    <SqfPage title="Validation / FAI" crumbs={eCrumbs('Validation / FAI')} permission="sequfloh.view_engineering_folder" tabs={tabs} tab={tab} onTab={(t) => setSp({ tab: t })}
      description="Validation activity and results from the validation work orders you can see.">
      <PagedTable q={tr} page={page} size={size} onPage={setPage} onSize={setSize} onRow={(r) => nav(`/sequfloh/record/${r.id}`)} locked={(r) => r.status === 'Canceled'} empty="No validation records here." cols={[
        { key: 'number', label: 'Tracking number', render: (r) => <Mono>{r.number}</Mono> }, { key: 'wi', label: 'Instruction / revision', render: (r) => (r.workInstructionNumber ? <><Mono>{r.workInstructionNumber}</Mono> rev {r.workInstructionRevision}</> : '—') },
        { key: 'partNumber', label: 'Part', render: (r) => r.partNumber ?? '—' }, { key: 'status', label: 'Status', render: (r) => <SqfStatus status={r.status} /> },
        { key: 'result', label: 'Result', render: (r) => <SqfStatus status={r.result} /> },
        { key: 'assignedUser', label: 'Assigned', render: (r) => (r.assignees?.length ? r.assignees.join(', ') : r.assignedUser ?? '—') }, { key: 'updatedAt', label: 'Updated', render: (r) => fmtDateTime(r.updatedAt) }]} />
    </SqfPage>
  );
}
/** Engineering › Development: instructions in development with the development record fields. */
interface DevRow { id: string; number: string; revision: string; title: string; type: string; status: string; partNumber?: string; project?: string; assignedEngineer?: string; targetCompletionDate?: string;
  developmentStatus?: string; updatedAt: string }
export function DevelopmentPage() {
  const nav = useNavigate();
  const { can } = useAuth();
  const [sp, setSp] = useSearchParams();
  const v = (k: string) => sp.get(k) ?? '';
  const set = (k: string, val: string) => { const n = new URLSearchParams(sp); if (val) n.set(k, val); else n.delete(k); setSp(n); setPage(1); };
  const [page, setPage] = useState(1); const [size, setSize] = useState(25); const [sort, setSort] = useState('updatedAt:desc');
  const line = sp.get('line') ?? '';
  const tabs = [{ label: 'Work instructions', value: '' }, ...DEVELOPMENT_LINES.map(([value, label]) => ({ label, value }))];
  const onTab = (t: string) => { setSp(t ? { line: t } : {}); setPage(1); };
  const q = useQuery({ queryKey: ['sqf-wi', 'development', sp.toString(), page, size, sort], enabled: !line,
    queryFn: () => get<{ items: DevRow[]; total: number }>(`${S}/instructions?${qs({ folder: 'development', q: v('q'), type: v('type'), part: v('part'), sort, page, pageSize: size })}`) });
  return (
    line ? <DevelopmentFiles line={line} page={{ title: 'Development', crumbs: eCrumbs('Development'), tabs, onTab }} /> :
    <SqfPage title="Development" crumbs={eCrumbs('Development')} permission="sequfloh.view_engineering_folder" tabs={tabs} tab={line} onTab={onTab}
      description="Draft work instructions in development, with the assigned engineer, target date, project and development status."
      actions={can('sequfloh.create_work_instruction') && <Button variant="contained" component={RouterLink} to="/sequfloh/instructions/new">New work instruction</Button>}>
      <Filters search={v('q')} onSearch={(x) => set('q', x)} values={{ type: v('type') }} onChange={set} onClear={() => { setSp({}); setPage(1); }}
        filters={[{ key: 'type', label: 'Type', options: [['ValidationFai', 'Validation / FAI'], ['RepairWork', 'Repair / Rework Work Instruction'], ['Inspection', 'Inspection'], ['Procedure', 'Procedure Instructions']] as [string, string][] }]}
        extra={<TextField size="small" label="Part number" defaultValue={v('part')} key={'p' + v('part')} onBlur={(e) => set('part', e.target.value.trim())} sx={{ width: 150 }} />} />
      <PagedTable q={q} page={page} size={size} onPage={setPage} onSize={setSize} sort={sort} onSort={setSort} onRow={(r) => nav(`/sequfloh/instruction/${r.id}?tab=development`)}
        empty={sp.toString() ? 'No instructions match these filters.' : 'No instructions are in development.'} cols={[
          { key: 'number', label: 'Instruction', sort: true, render: (r) => <Mono>{r.number} rev {r.revision}</Mono> }, { key: 'title', label: 'Title', sort: true },
          { key: 'type', label: 'Type', render: (r) => typeLabel[r.type] ?? r.type }, { key: 'partNumber', label: 'Related part', render: (r) => r.partNumber ?? '—' },
          { key: 'project', label: 'Related project', render: (r) => r.project ?? '—' }, { key: 'assignedEngineer', label: 'Assigned engineer', render: (r) => r.assignedEngineer ?? '—' },
          { key: 'targetCompletionDate', label: 'Target completion', render: (r) => fmtDate(r.targetCompletionDate) }, { key: 'developmentStatus', label: 'Development status', render: (r) => r.developmentStatus ?? '—' },
          { key: 'status', label: 'Status', sort: true, render: (r) => <SqfStatus status={r.status} /> }]} />
    </SqfPage>
  );
}

/** Development record on the instruction page (Engineering folder permission; not controlled content, so no revision is needed). */
export function DevelopmentPanel({ id, doc, canEdit, onSaved }: { id: string; doc: Record<string, unknown>; canEdit: boolean; onSaved: () => void }) {
  const users = useUsers();
  const s = (k: string) => (doc[k] === null || doc[k] === undefined ? '' : String(doc[k]));
  const [f, setF] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false); const [msg, setMsg] = useState<{ sev: 'success' | 'error'; text: string } | null>(null);
  useEffect(() => { setF({ assignedEngineerId: s('assignedEngineerId'), targetCompletionDate: s('targetCompletionDate'), project: s('project'), engineeringNotes: s('engineeringNotes'), developmentStatus: s('developmentStatus') }); }, [doc]); // eslint-disable-line react-hooks/exhaustive-deps
  const fld = (k: string, label: string, props: Record<string, unknown> = {}) => <TextField size="small" label={label} value={f[k] ?? ''} disabled={!canEdit || busy} onChange={(e) => setF({ ...f, [k]: e.target.value })} {...props} />;
  const save = async () => {
    setBusy(true); setMsg(null);
    try {
      await put(`${S}/instructions/${id}/development`, { assignedEngineerId: nul(f.assignedEngineerId), targetCompletionDate: nul(f.targetCompletionDate), project: nul(f.project),
        engineeringNotes: nul(f.engineeringNotes), developmentStatus: nul(f.developmentStatus) });
      setMsg({ sev: 'success', text: 'Development details saved.' }); onSaved();
    } catch (e) { setMsg({ sev: 'error', text: (e as Error).message }); } finally { setBusy(false); }
  };
  return (
    <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Development">
      <Typography variant="h3" sx={{ mb: 1.5 }}>Engineering development</Typography>
      {msg && <Alert severity={msg.sev} sx={{ mb: 2 }} onClose={() => setMsg(null)}>{msg.text}</Alert>}
      {!canEdit && <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>Development details are kept by Engineering until the instruction is released.</Typography>}
      <Grid cols={3}>
        <TextField select size="small" label="Assigned engineer" value={users.data ? f.assignedEngineerId ?? '' : ''} disabled={!canEdit || busy} onChange={(e) => setF({ ...f, assignedEngineerId: e.target.value })}>
          <MenuItem value=""><em>Not assigned</em></MenuItem>{(users.data ?? []).map((u) => <MenuItem key={u.id} value={u.id}>{u.displayName}</MenuItem>)}{setupOptions('user', users.data)}</TextField>
        {fld('targetCompletionDate', 'Target completion date', { type: 'date', InputLabelProps: { shrink: true } })}
        {fld('project', 'Related project')}
        {fld('developmentStatus', 'Development status')}
        <Box sx={{ gridColumn: '1 / -1' }}>{fld('engineeringNotes', 'Engineering notes', { multiline: true, minRows: 3, fullWidth: true })}</Box>
      </Grid>
      {canEdit && <Stack direction="row" sx={{ mt: 2 }}><Button variant="contained" disabled={busy} onClick={save}>Save development details</Button></Stack>}
    </Paper>
  );
}
