import { setupOptions } from '../components/SetupLink';
import { useEffect, useState, type ReactNode } from 'react';
import { Link as RouterLink, Navigate, useNavigate, useSearchParams } from 'react-router-dom';
import { Alert, Box, Button, Checkbox, FormControlLabel, List, ListItemButton, ListItemText, MenuItem, Paper, Stack, TextField, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { get, put } from '../api';
import { useAuth } from '../auth';
import { PanelHead, Readout, useUsers } from '../components/Kit';
import { fmtDate, fmtDateTime } from '../components/Status';
import { ErrorState, Filters, Grid, Loading, Mono, PagedTable, S, SqfPage, SqfStatus, nul, qs, words } from './common';
import { InstructionsFolderPage } from './Instructions';

const typeLabel: Record<string, string> = { Validation: 'Validation', Fai: 'FAI', Repair: 'Repair', Production: 'Production', ValidationFai: 'Validation / FAI', RepairTest: 'Repair Test', RepairWork: 'Repair Work Instruction' };
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
        <List dense disablePadding>{rows!.map((r) => <ListItemButton key={r.id} component={RouterLink} to={kind === 'instruction' ? `/sequfloh/instruction/${r.id}` : `/sequfloh/record/${r.id}`}>
          <ListItemText primary={<Stack direction="row" gap={1} alignItems="center"><Mono>{r.number}{r.revision ? ` rev ${r.revision}` : ''}</Mono><SqfStatus status={r.result === 'Fail' ? 'Fail' : r.status} /></Stack>}
            secondary={[typeLabel[r.type] ?? r.type, r.title ?? (r.workInstructionNumber ? `${r.workInstructionNumber} rev ${r.workInstructionRevision}` : ''), r.partNumber, fmtDateTime(r.releasedAt ?? r.updatedAt)].filter(Boolean).join(' · ')} />
        </ListItemButton>)}</List>}
    </Paper>
  );
}
const Two = ({ children }: { children: ReactNode }) => <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: 'minmax(0, 1fr)', md: '1fr 1fr' } }}>{children}</Box>;

// ------------------------------------------------------------------ quality
interface QDash { validationPendingReview: number; repairPendingReview: number; approvalsAwaitingQuality: number; failedValidations: number; failedFai: number; failedRepairTests: number;
  overdueQualityActions: number; failedRecords: Mini[]; overdueRows: Mini[]; recentlyReleased: Mini[]; recentlyRevised: Mini[] }
export function QualityDashboardPage() {
  const q = useQuery({ queryKey: ['sqf-dash', 'quality'], queryFn: () => get<QDash>(`${S}/quality/dashboard`) });
  const d = q.data;
  return (
    <SqfPage title="Quality Dashboard" crumbs={qCrumbs('Quality Dashboard')} permission="sequfloh.view_quality_folder"
      description="Reviews, approvals, failed records and overdue Quality actions across the records you can see."
      actions={<Button variant="contained" component={RouterLink} to="/sequfloh/quality/review-queue">Open review queue</Button>}>
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : <>
        <Stats>
          <Stat label="Validation instructions pending review" value={d?.validationPendingReview} to="/sequfloh/quality/review-queue?kind=instruction" />
          <Stat label="Repair instructions pending review" value={d?.repairPendingReview} to="/sequfloh/quality/review-queue?kind=instruction" />
          <Stat label="Approvals awaiting Quality" value={d?.approvalsAwaitingQuality} to="/sequfloh/quality/review-queue" />
          <Stat label="Overdue Quality actions" value={d?.overdueQualityActions} to="/sequfloh/quality/review-queue?kind=tracking" />
          <Stat label="Failed validations" value={d?.failedValidations} to="/sequfloh/tracking/validation?result=Fail" />
          <Stat label="Failed FAI records" value={d?.failedFai} to="/sequfloh/tracking/fai?result=Fail" />
          <Stat label="Failed repair tests" value={d?.failedRepairTests} to="/sequfloh/tracking/repair?result=Fail" />
        </Stats>
        <Two>
          <MiniList title="Failed records" rows={d?.failedRecords} empty="No failed records." kind="record" />
          <MiniList title="Overdue Quality actions" rows={d?.overdueRows} empty="Nothing is overdue for Quality." kind="record" />
          <MiniList title="Recently released instructions" rows={d?.recentlyReleased} empty="No instructions released yet." kind="instruction" />
          <MiniList title="Recently revised instructions" rows={d?.recentlyRevised} empty="No revised instructions released yet." kind="instruction" />
        </Two>
      </>}
    </SqfPage>
  );
}
export const QualityValidationPage = () => <InstructionsFolderPage fixedFolder="validation" crumbsOverride={qCrumbs('Validation Work Instructions')} permission="sequfloh.view_quality_folder" />;
export const QualityRepairPage = () => <InstructionsFolderPage fixedFolder="repair" crumbsOverride={qCrumbs('Repair Work Instructions')} permission="sequfloh.view_quality_folder" />;

// ------------------------------------------------------------------ review queues
interface QueueRow { id: string; kind: string; number: string; title: string; type: string; status: string; partNumber?: string; waitingSince: string; dueAt?: string; path: string }
export function ReviewQueuePage({ area }: { area: 'quality' | 'engineering' }) {
  const nav = useNavigate();
  const [sp, setSp] = useSearchParams();
  const v = (k: string) => sp.get(k) ?? '';
  const set = (k: string, val: string) => { const n = new URLSearchParams(sp); if (val) n.set(k, val); else n.delete(k); setSp(n); };
  const q = useQuery({ queryKey: ['sqf-queue', area, sp.toString()], queryFn: () => get<QueueRow[]>(`${S}/${area}/review-queue?${qs({ q: v('q'), kind: v('kind') })}`) });
  const title = area === 'quality' ? 'Quality Review Queue' : 'Engineering Review Queue';
  const [page, setPage] = useState(1); const [size, setSize] = useState(25);
  useEffect(() => setPage(1), [sp]);
  const rows = q.data ?? [];
  const paged = { data: { items: rows.slice((page - 1) * size, page * size), total: rows.length }, isLoading: q.isLoading, error: q.error, refetch: q.refetch };
  return (
    <SqfPage title={title} crumbs={area === 'quality' ? qCrumbs(title) : eCrumbs(title)} permission={area === 'quality' ? 'sequfloh.view_quality_folder' : 'sequfloh.view_engineering_folder'}
      description={area === 'quality' ? 'Work instructions waiting for review or approval, and tracking records waiting for inspection or Quality review, oldest first. Open an item to review, comment, return, approve or reject.'
        : 'Tracking records waiting for Engineering review and work instructions returned for changes or in validation review, oldest first. Open an item to enter Engineering conclusions or respond to changes.'}>
      <Filters search={v('q')} onSearch={(x) => set('q', x)} values={{ kind: v('kind') }} onChange={set} onClear={() => setSp({})}
        filters={[{ key: 'kind', label: 'Item', options: [['instruction', 'Work instructions'], ['tracking', 'Tracking records']] }]} />
      <PagedTable q={paged} page={page} size={size} onPage={setPage} onSize={setSize} onRow={(r) => nav(r.path)} empty={sp.toString() ? 'Nothing matches these filters.' : 'The queue is empty.'} cols={[
        { key: 'kind', label: 'Item' }, { key: 'number', label: 'Number', render: (r) => <Mono>{r.number}</Mono> }, { key: 'title', label: 'Title / instruction' },
        { key: 'type', label: 'Type', render: (r) => typeLabel[r.type] ?? r.type }, { key: 'status', label: 'Status', render: (r) => <SqfStatus status={r.status} /> },
        { key: 'partNumber', label: 'Part', render: (r) => r.partNumber ?? '—' }, { key: 'waitingSince', label: 'Waiting since', render: (r) => fmtDateTime(r.waitingSince) },
        { key: 'dueAt', label: 'Due', render: (r) => fmtDateTime(r.dueAt) }]} />
    </SqfPage>
  );
}

// ------------------------------------------------------------------ engineering
interface EDash { inDevelopment: number; draftValidation: number; draftRepair: number; changesRequested: number; recommendedForRelease: number; openValidationActivities: number; failedValidations: number;
  awaitingEngineeringReview: number; releasedValidationInstructions: number; myDevelopment: Mini[]; awaitingRows: Mini[] }
export function EngineeringDashboardPage() {
  const q = useQuery({ queryKey: ['sqf-dash', 'engineering'], queryFn: () => get<EDash>(`${S}/engineering/dashboard`) });
  const d = q.data;
  return (
    <SqfPage title="Engineering Dashboard" crumbs={eCrumbs('Engineering Dashboard')} permission="sequfloh.view_engineering_folder"
      description="Instructions in development, requested changes, validation activity and records waiting for Engineering review."
      actions={<Button variant="contained" component={RouterLink} to="/sequfloh/engineering/review-queue">Open review queue</Button>}>
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : <>
        <Stats>
          <Stat label="Instructions in development" value={d?.inDevelopment} to="/sequfloh/engineering/development" />
          <Stat label="Draft validation instructions" value={d?.draftValidation} to="/sequfloh/engineering/development" />
          <Stat label="Draft repair instructions" value={d?.draftRepair} to="/sequfloh/engineering/development" />
          <Stat label="Changes requested" value={d?.changesRequested} to="/sequfloh/engineering/review-queue?kind=instruction" />
          <Stat label="Recommended for release" value={d?.recommendedForRelease} />
          <Stat label="Open validation activities" value={d?.openValidationActivities} to="/sequfloh/engineering/validation?tab=open" />
          <Stat label="Failed validation records" value={d?.failedValidations} to="/sequfloh/engineering/validation?tab=failed" />
          <Stat label="Awaiting Engineering review" value={d?.awaitingEngineeringReview} to="/sequfloh/engineering/review-queue?kind=tracking" />
        </Stats>
        <Two>
          <MiniList title="My development work" rows={d?.myDevelopment} empty="No instructions in development are assigned to you." kind="instruction" />
          <MiniList title="Records awaiting Engineering review" rows={d?.awaitingRows} empty="Nothing is waiting for Engineering." kind="record" />
        </Two>
      </>}
    </SqfPage>
  );
}

/** Engineering › Validation: instructions, plans, open activities, results, failed records, records needing Engineering review and revision history. */
interface TrMini { id: string; number: string; type: string; status: string; result: string; approvalStatus: string; workInstructionNumber: string; workInstructionRevision: string; partNumber?: string;
  assignedUser?: string; dueAt?: string; actualCompletion?: string; updatedAt?: string }
export function EngineeringValidationPage() {
  const nav = useNavigate();
  const [sp, setSp] = useSearchParams();
  const tab = sp.get('tab') ?? 'instructions';
  const [page, setPage] = useState(1); const [size, setSize] = useState(25);
  useEffect(() => setPage(1), [tab]);
  const trackFolder: Record<string, Record<string, string>> = { open: { folder: 'validation', status: '' }, results: { folder: 'validation', result: '' }, failed: { folder: 'validation', result: 'Fail' },
    review: { folder: 'validation', status: 'PendingReview' } };
  const tr = useQuery({ queryKey: ['sqf-tr', 'eng', tab, page, size], enabled: !!trackFolder[tab],
    queryFn: () => get<{ items: TrMini[]; total: number }>(`${S}/tracking?${qs({ ...trackFolder[tab], page, pageSize: size, sort: 'updatedAt:desc' })}`)
      .then((r) => (tab === 'open' ? { ...r, items: r.items.filter((x) => !['Completed', 'Failed', 'Rejected', 'Canceled'].includes(x.status)) } : r)) });
  const tabs = [{ label: 'Validation work instructions', value: 'instructions' }, { label: 'Validation plans', value: 'plans' }, { label: 'Open validation activities', value: 'open' },
    { label: 'Validation results', value: 'results' }, { label: 'Failed validation records', value: 'failed' }, { label: 'Requiring Engineering review', value: 'review' },
    { label: 'Released validation instructions', value: 'released' }, { label: 'Revision history', value: 'revisions' }];
  if (tab === 'instructions' || tab === 'plans' || tab === 'released') {
    return <InstructionsFolderPage key={tab} fixedFolder={tab === 'released' ? 'released' : tab === 'plans' ? 'draft' : 'validation'} tabs={tabs} tab={tab} onTab={(t) => setSp({ tab: t })}
      crumbsOverride={eCrumbs('Validation')} titleOverride="Validation" permission="sequfloh.view_engineering_folder"
      descriptionOverride={tab === 'plans' ? 'Validation plans being developed: draft, in development and returned instructions.' : tab === 'released' ? 'Released instructions available for validation execution.' : 'Validation and Validation / FAI work instructions.'} />;
  }
  if (tab === 'revisions') return <Navigate to="/sequfloh/revision-history" replace />;
  return (
    <SqfPage title="Validation" crumbs={eCrumbs('Validation')} permission="sequfloh.view_engineering_folder" tabs={tabs} tab={tab} onTab={(t) => setSp({ tab: t })}
      description="Validation activity and results from the validation tracking records you can see.">
      <PagedTable q={tr} page={page} size={size} onPage={setPage} onSize={setSize} onRow={(r) => nav(`/sequfloh/record/${r.id}${tab === 'review' ? '?tab=review' : ''}`)} empty="No validation records here." cols={[
        { key: 'number', label: 'Tracking number', render: (r) => <Mono>{r.number}</Mono> }, { key: 'wi', label: 'Instruction / revision', render: (r) => <><Mono>{r.workInstructionNumber}</Mono> rev {r.workInstructionRevision}</> },
        { key: 'partNumber', label: 'Part', render: (r) => r.partNumber ?? '—' }, { key: 'status', label: 'Status', render: (r) => <SqfStatus status={r.status} /> },
        { key: 'result', label: 'Result', render: (r) => <SqfStatus status={r.result} /> }, { key: 'approvalStatus', label: 'Approval', render: (r) => words(r.approvalStatus) },
        { key: 'assignedUser', label: 'Assigned', render: (r) => r.assignedUser ?? '—' }, { key: 'updatedAt', label: 'Updated', render: (r) => fmtDateTime(r.updatedAt) }]} />
    </SqfPage>
  );
}
/** Engineering › Development: instructions in development with the development record fields. */
interface DevRow { id: string; number: string; revision: string; title: string; type: string; status: string; partNumber?: string; project?: string; assignedEngineer?: string; targetCompletionDate?: string;
  developmentStatus?: string; releaseRecommendedAt?: string; reviewedAt?: string; approvedAt?: string; updatedAt: string }
export function DevelopmentPage() {
  const nav = useNavigate();
  const { can } = useAuth();
  const [sp, setSp] = useSearchParams();
  const v = (k: string) => sp.get(k) ?? '';
  const set = (k: string, val: string) => { const n = new URLSearchParams(sp); if (val) n.set(k, val); else n.delete(k); setSp(n); setPage(1); };
  const [page, setPage] = useState(1); const [size, setSize] = useState(25); const [sort, setSort] = useState('updatedAt:desc');
  const q = useQuery({ queryKey: ['sqf-wi', 'development', sp.toString(), page, size, sort],
    queryFn: () => get<{ items: DevRow[]; total: number }>(`${S}/instructions?${qs({ folder: 'development', q: v('q'), type: v('type'), part: v('part'), sort, page, pageSize: size })}`) });
  return (
    <SqfPage title="Development" crumbs={eCrumbs('Development')} permission="sequfloh.view_engineering_folder"
      description="Work instructions in development: draft, in development and returned for changes, with the assigned engineer, target date, project, development status and review and approval status."
      actions={can('sequfloh.create_work_instruction') && <Button variant="contained" component={RouterLink} to="/sequfloh/instructions/new">New work instruction</Button>}>
      <Filters search={v('q')} onSearch={(x) => set('q', x)} values={{ type: v('type') }} onChange={set} onClear={() => { setSp({}); setPage(1); }}
        filters={[{ key: 'type', label: 'Type', options: Object.entries({ Validation: 'Validation', ValidationFai: 'Validation / FAI', Fai: 'FAI', RepairTest: 'Repair Test', RepairWork: 'Repair Work Instruction' }) }]}
        extra={<TextField size="small" label="Part number" defaultValue={v('part')} key={'p' + v('part')} onBlur={(e) => set('part', e.target.value.trim())} sx={{ width: 150 }} />} />
      <PagedTable q={q} page={page} size={size} onPage={setPage} onSize={setSize} sort={sort} onSort={setSort} onRow={(r) => nav(`/sequfloh/instruction/${r.id}?tab=development`)}
        empty={sp.toString() ? 'No instructions match these filters.' : 'No instructions are in development.'} cols={[
          { key: 'number', label: 'Instruction', sort: true, render: (r) => <Mono>{r.number} rev {r.revision}</Mono> }, { key: 'title', label: 'Title', sort: true },
          { key: 'type', label: 'Type', render: (r) => typeLabel[r.type] ?? r.type }, { key: 'partNumber', label: 'Related part', render: (r) => r.partNumber ?? '—' },
          { key: 'project', label: 'Related project', render: (r) => r.project ?? '—' }, { key: 'assignedEngineer', label: 'Assigned engineer', render: (r) => r.assignedEngineer ?? '—' },
          { key: 'targetCompletionDate', label: 'Target completion', render: (r) => fmtDate(r.targetCompletionDate) }, { key: 'developmentStatus', label: 'Development status', render: (r) => r.developmentStatus ?? '—' },
          { key: 'status', label: 'Review status', sort: true, render: (r) => <SqfStatus status={r.status} /> },
          { key: 'approval', label: 'Approval status', render: (r) => (r.approvedAt ? 'Approved' : r.releaseRecommendedAt ? 'Release recommended' : 'Not approved') }]} />
    </SqfPage>
  );
}

/** Development record on the instruction page (Engineering folder permission; not controlled content, so no revision is needed). */
export function DevelopmentPanel({ id, doc, canEdit, onSaved }: { id: string; doc: Record<string, unknown>; canEdit: boolean; onSaved: () => void }) {
  const users = useUsers();
  const s = (k: string) => (doc[k] === null || doc[k] === undefined ? '' : String(doc[k]));
  const [f, setF] = useState<Record<string, string>>({});
  const [rec, setRec] = useState(false);
  const [busy, setBusy] = useState(false); const [msg, setMsg] = useState<{ sev: 'success' | 'error'; text: string } | null>(null);
  useEffect(() => { setF({ assignedEngineerId: s('assignedEngineerId'), targetCompletionDate: s('targetCompletionDate'), project: s('project'), engineeringNotes: s('engineeringNotes'), developmentStatus: s('developmentStatus') }); setRec(!!doc.releaseRecommendedAt); }, [doc]); // eslint-disable-line react-hooks/exhaustive-deps
  const fld = (k: string, label: string, props: Record<string, unknown> = {}) => <TextField size="small" label={label} value={f[k] ?? ''} disabled={!canEdit || busy} onChange={(e) => setF({ ...f, [k]: e.target.value })} {...props} />;
  const save = async () => {
    setBusy(true); setMsg(null);
    try {
      await put(`${S}/instructions/${id}/development`, { assignedEngineerId: nul(f.assignedEngineerId), targetCompletionDate: nul(f.targetCompletionDate), project: nul(f.project),
        engineeringNotes: nul(f.engineeringNotes), developmentStatus: nul(f.developmentStatus), recommendRelease: rec });
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
      <FormControlLabel sx={{ mt: 1 }} control={<Checkbox checked={rec} disabled={!canEdit || busy} onChange={(_, c) => setRec(c)} />}
        label={doc.releaseRecommendedAt ? `Release recommended ${fmtDateTime(s('releaseRecommendedAt'))} · ${s('releaseRecommendedBy')}` : 'Recommend release'} />
      {canEdit && <Stack direction="row" sx={{ mt: 2 }}><Button variant="contained" disabled={busy} onClick={save}>Save development details</Button></Stack>}
    </Paper>
  );
}
