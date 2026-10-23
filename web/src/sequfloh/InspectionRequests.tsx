import { useEffect, useState } from 'react';
import { Link as RouterLink, useNavigate, useParams } from 'react-router-dom';
import { Alert, Autocomplete, Box, Button, Chip, Dialog, DialogActions, DialogContent, DialogTitle, MenuItem, Paper, Stack, Tab, Table, TableBody, TableCell, TableContainer,
  TableHead, TableRow, Tabs, TextField, Typography } from '@mui/material';
import AddTaskOutlined from '@mui/icons-material/AddTaskOutlined';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put } from '../api';
import { useAuth } from '../auth';
import { Facts, useSites } from '../components/Kit';
import { FilesPanel } from '../components/Files';
import { fmtDateTime } from '../components/Status';
import { DeleteButton, Empty, ErrorState, Loading, Mono, S, SqfPage, qs, useOptions, words } from './common';
import { type AuditRow, HistoryTable } from './Parts';

const A = `${S}/inspection-requests`;
export interface InspectionRequest {
  id: string; number: string; siteId: string; partId?: string | null; partNumber: string; partDescription?: string | null; quantity?: number | null; reason?: string | null;
  requirement?: string | null; workInstructionId?: string | null; workInstructionLabel?: string | null; requestedDate?: string | null; priority: string; status: string;
  requestedByName?: string | null; createdAt: string; workOrderId?: string | null; workOrderNumber?: string | null; overdue: boolean;
}
interface PartOpt { id: string; partNumber: string; name?: string | null; description?: string | null }
interface WiOpt { id: string; number: string; revision: string; title: string; type?: string }

const prColor: Record<string, 'error' | 'warning' | 'primary' | 'default'> = { Critical: 'error', High: 'warning', Normal: 'primary', Low: 'default' };
export const PriorityChip = ({ p }: { p: string }) => <Chip size="small" label={p} color={prColor[p] ?? 'default'} variant={p === 'Normal' || p === 'Low' ? 'outlined' : 'filled'} />;
const stColor: Record<string, 'default' | 'primary' | 'success'> = { Open: 'primary', InProgress: 'primary', Done: 'success', Cancelled: 'default' };
const StatusChip = ({ s }: { s: string }) => <Chip size="small" label={words(s)} color={stColor[s] ?? 'default'} variant={s === 'Open' ? 'outlined' : 'filled'} />;
const fmtDate = (d?: string | null) => (d ? new Date(`${d}T00:00:00`).toLocaleDateString() : '—');

function RequestForm({ value, onChange }: { value: Form; onChange: (v: Form) => void }) {
  const sites = useSites(); const reasons = useOptions('inspection-reasons'); const prios = useOptions('inspection-priorities');
  const [text, setText] = useState(value.partNumber);
  const parts = useQuery({ queryKey: ['sqf-ir-parts', text], queryFn: () => get<{ items: PartOpt[] }>(`${S}/parts?${qs({ q: text, view: 'active', pageSize: 20, sort: 'partNumber:asc' })}`) });
  const [wiText, setWiText] = useState('');
  const wis = useQuery({ queryKey: ['sqf-ir-wi', wiText], queryFn: () => get<{ items: WiOpt[] }>(`${S}/instructions?${qs({ folder: 'released', q: wiText, pageSize: 20, sort: 'number:asc' })}`) });
  const set = (p: Partial<Form>) => onChange({ ...value, ...p });
  useEffect(() => { if (!value.siteId && sites.data?.length) set({ siteId: sites.data[0].id }); }, [sites.data]); // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <Stack gap={2} sx={{ pt: 1 }}>
      {(sites.data?.length ?? 0) > 1 && <TextField select label="Site" required value={value.siteId} onChange={(e) => set({ siteId: e.target.value })}>
        {sites.data!.map((s) => <MenuItem key={s.id} value={s.id}>{s.code} — {s.name}</MenuItem>)}</TextField>}
      <Autocomplete freeSolo options={parts.data?.items ?? []} inputValue={text}
        getOptionLabel={(o) => (typeof o === 'string' ? o : o.partNumber)}
        renderOption={(props, o) => <li {...props} key={o.id}><Box><Mono>{o.partNumber}</Mono><Typography variant="caption" display="block" color="text.secondary">{o.name ?? o.description}</Typography></Box></li>}
        onInputChange={(_, v, reason) => { setText(v); if (reason === 'input') set({ partNumber: v, partId: null }); }}
        onChange={(_, o) => { if (o && typeof o !== 'string') set({ partNumber: o.partNumber, partId: o.id, partDescription: o.name ?? o.description ?? value.partDescription }); }}
        renderInput={(p) => <TextField {...p} required label="Part number" helperText="Pick from the Part Master to fill the description, or type any part number." />} />
      <TextField label="Part description" value={value.partDescription} onChange={(e) => set({ partDescription: e.target.value })} />
      <Stack direction={{ xs: 'column', sm: 'row' }} gap={2}>
        <TextField label="Qty (per NetSuite)" type="number" value={value.quantity} onChange={(e) => set({ quantity: e.target.value })} sx={{ flex: 1 }} inputProps={{ min: 0, step: 'any' }} />
        <TextField select label="Priority" value={value.priority} onChange={(e) => set({ priority: e.target.value })} sx={{ flex: 1 }}>
          {(prios.data ?? ['Critical', 'High', 'Normal', 'Low']).map((p) => <MenuItem key={p} value={p}>{p}</MenuItem>)}</TextField>
      </Stack>
      <Stack direction={{ xs: 'column', sm: 'row' }} gap={2}>
        <TextField select label="Reason" value={value.reason} onChange={(e) => set({ reason: e.target.value })} sx={{ flex: 1 }}>
          <MenuItem value="">—</MenuItem>{(reasons.data ?? []).map((p) => <MenuItem key={p} value={p}>{p}</MenuItem>)}</TextField>
        <TextField label="Requested completion date" type="date" value={value.requestedDate} onChange={(e) => set({ requestedDate: e.target.value })} sx={{ flex: 1 }} InputLabelProps={{ shrink: true }} />
      </Stack>
      <TextField label="Inspection requirement" multiline minRows={3} value={value.requirement} onChange={(e) => set({ requirement: e.target.value })}
        helperText="What to inspect and how. Link a work instruction below; add attachments on the request after it is saved." />
      <Autocomplete options={wis.data?.items ?? []} value={value.wi} onChange={(_, o) => set({ wi: o })} onInputChange={(_, v, r) => r === 'input' && setWiText(v)}
        getOptionLabel={(o) => `${o.number} rev ${o.revision} — ${o.title}`} isOptionEqualToValue={(a, b) => a.id === b.id}
        renderInput={(p) => <TextField {...p} label="Linked work instruction (optional)" />} />
    </Stack>
  );
}
interface Form { siteId: string; partNumber: string; partId: string | null; partDescription: string; quantity: string; reason: string; requirement: string; wi: WiOpt | null; requestedDate: string; priority: string }
const blank: Form = { siteId: '', partNumber: '', partId: null, partDescription: '', quantity: '', reason: '', requirement: '', wi: null, requestedDate: '', priority: 'Normal' };
const body = (f: Form) => ({ siteId: f.siteId, partNumber: f.partNumber.trim() || null, partId: f.partId, partDescription: f.partDescription.trim() || null,
  quantity: f.quantity === '' ? null : Number(f.quantity), reason: f.reason || null, requirement: f.requirement.trim() || null, workInstructionId: f.wi?.id ?? null,
  requestedDate: f.requestedDate || null, priority: f.priority });

export function InspectionRequestDialog({ open, onClose, initial, id }: { open: boolean; onClose: () => void; initial?: Form; id?: string }) {
  const qc = useQueryClient(); const nav = useNavigate();
  const [f, setF] = useState<Form>(initial ?? blank); const [err, setErr] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  useEffect(() => { if (open) { setF(initial ?? blank); setErr(null); } }, [open]); // eslint-disable-line react-hooks/exhaustive-deps
  const save = async () => {
    if (!f.partNumber.trim()) return setErr('Enter a part number.');
    if (!f.siteId) return setErr('Choose a site.');
    setBusy(true); setErr(null);
    try {
      const r = id ? await put<InspectionRequest>(`${A}/${id}`, body(f)) : await post<InspectionRequest>(A, body(f));
      await qc.invalidateQueries({ queryKey: ['sqf-ir'] }); onClose();
      if (!id) nav(`/sequfloh/inspection-request/${r.id}`);
    } catch (e) { setErr(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  };
  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" aria-labelledby="ir-title">
      <DialogTitle id="ir-title">{id ? 'Edit inspection request' : 'New inspection request'}</DialogTitle>
      <DialogContent>{err && <Alert severity="error" sx={{ mb: 1 }}>{err}</Alert>}<RequestForm value={f} onChange={setF} /></DialogContent>
      <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" onClick={save} disabled={busy}>{id ? 'Save' : 'Submit request'}</Button></DialogActions>
    </Dialog>
  );
}

/** Quick action: opens the inspection request form in a dialog from any page. */
export function NewInspectionRequestButton({ variant = 'contained' }: { variant?: 'contained' | 'outlined' }) {
  const { can } = useAuth(); const [open, setOpen] = useState(false);
  if (!can('sequfloh.access')) return null;
  return <><Button variant={variant} color="success" startIcon={<AddTaskOutlined />} onClick={() => setOpen(true)}>New inspection request</Button>
    <InspectionRequestDialog open={open} onClose={() => setOpen(false)} /></>;
}

function RequestTable({ rows, compact }: { rows: InspectionRequest[]; compact?: boolean }) {
  const nav = useNavigate();
  return (
    <TableContainer component={Paper}><Table size="small" aria-label="Inspection requests">
      <TableHead><TableRow><TableCell>Number</TableCell><TableCell>Priority</TableCell><TableCell>Requested by date</TableCell><TableCell>Part</TableCell>
        {!compact && <TableCell>Qty</TableCell>}<TableCell>Reason</TableCell><TableCell>Status</TableCell>{!compact && <TableCell>Work order</TableCell>}{!compact && <TableCell>Submitted by</TableCell>}</TableRow></TableHead>
      <TableBody>{rows.map((r) => (
        <TableRow key={r.id} hover onClick={() => nav(`/sequfloh/inspection-request/${r.id}`)} sx={{ cursor: 'pointer', ...(r.overdue ? { bgcolor: 'rgba(211,47,47,0.08)', '& td:first-of-type': { borderLeft: '4px solid #d32f2f' } } : {}) }}>
          <TableCell><Mono>{r.number}</Mono></TableCell><TableCell><PriorityChip p={r.priority} /></TableCell>
          <TableCell>{fmtDate(r.requestedDate)}{r.overdue && <Chip size="small" color="error" label="Overdue" sx={{ ml: 1 }} />}</TableCell>
          <TableCell><Mono>{r.partNumber}</Mono><Typography variant="caption" display="block" color="text.secondary">{r.partDescription}</Typography></TableCell>
          {!compact && <TableCell>{r.quantity ?? '—'}</TableCell>}<TableCell>{r.reason ?? '—'}</TableCell><TableCell><StatusChip s={r.status} /></TableCell>
          {!compact && <TableCell>{r.workOrderNumber ? <Mono>{r.workOrderNumber}</Mono> : '—'}</TableCell>}{!compact && <TableCell>{r.requestedByName ?? '—'}</TableCell>}
        </TableRow>))}</TableBody>
    </Table></TableContainer>
  );
}

const crumbs = [{ label: 'SEQUFLOH Home', to: '/sequfloh' }, { label: 'Inspection Requests' }];
export function InspectionRequestsPage() {
  const [status, setStatus] = useState('active'); const [q, setQ] = useState('');
  const list = useQuery({ queryKey: ['sqf-ir', status, q], queryFn: () => get<InspectionRequest[]>(`${A}?${qs({ status: status === 'all' ? '' : status, q })}`) });
  return (
    <SqfPage title="Inspection Requests" crumbs={crumbs} description="Requests for inspection, in priority order and then by requested completion date. Overdue requests are highlighted."
      permission="sequfloh.access" actions={<NewInspectionRequestButton />}>
      <Stack direction={{ xs: 'column', md: 'row' }} gap={2} alignItems={{ md: 'center' }} sx={{ mb: 2 }}>
        <Tabs value={status} onChange={(_, v) => setStatus(v)} variant="scrollable">
          {[['active', 'Open & in progress'], ['Open', 'Open'], ['InProgress', 'In progress'], ['Done', 'Done'], ['Cancelled', 'Cancelled'], ['all', 'All']].map(([v, l]) => <Tab key={v} value={v} label={l} />)}
        </Tabs>
        <TextField size="small" label="Search" value={q} onChange={(e) => setQ(e.target.value)} sx={{ ml: { md: 'auto' }, minWidth: 240 }} />
      </Stack>
      {list.isLoading ? <Loading /> : list.error ? <ErrorState error={list.error} retry={() => list.refetch()} />
        : (list.data ?? []).length === 0 ? <Empty text="No inspection requests here." /> : <RequestTable rows={list.data!} />}
    </SqfPage>
  );
}

/** Open requests shown on the dispatch queue so they are picked up with the rest of the work. */
export function OpenInspectionRequestsPanel() {
  const list = useQuery({ queryKey: ['sqf-ir', 'active', ''], queryFn: () => get<InspectionRequest[]>(`${A}?status=active`) });
  const rows = (list.data ?? []).filter((r) => r.status === 'Open');
  if (!rows.length) return null;
  return (
    <Box sx={{ mb: 3 }}>
      <Stack direction="row" alignItems="center" gap={1} sx={{ mb: 1 }}><Typography variant="h6">Open inspection requests</Typography>
        <Button size="small" component={RouterLink} to="/sequfloh/inspection-requests">View all</Button></Stack>
      <RequestTable rows={rows.slice(0, 10)} compact />
    </Box>
  );
}

function ConvertDialog({ r, open, onClose }: { r: InspectionRequest; open: boolean; onClose: () => void }) {
  const qc = useQueryClient(); const nav = useNavigate();
  const [type, setType] = useState('Inspection'); const [wi, setWi] = useState<WiOpt | null>(null); const [text, setText] = useState('');
  const [err, setErr] = useState<string | null>(null);
  const wis = useQuery({ queryKey: ['sqf-tr-wi', type, text], queryFn: () => get<WiOpt[]>(`${S}/tracking/instructions?${qs({ type, q: text })}`), enabled: open });
  useEffect(() => { if (open && r.workInstructionId && wis.data && !wi) setWi(wis.data.find((w) => w.id === r.workInstructionId) ?? null); }, [open, wis.data]); // eslint-disable-line react-hooks/exhaustive-deps
  const go = async () => {
    if (!wi) return setErr('Choose a released work instruction.');
    try {
      const res = await post<{ workOrderId: string }>(`${A}/${r.id}/convert`, { workInstructionId: wi.id, type });
      onClose(); await qc.invalidateQueries({ queryKey: ['sqf-ir'] }); nav(`/sequfloh/record/${res.workOrderId}`);
    } catch (e) { setErr(e instanceof Error ? e.message : String(e)); }
  };
  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle>Convert to work order</DialogTitle>
      <DialogContent>
        <Stack gap={2} sx={{ pt: 1 }}>
          {err && <Alert severity="error">{err}</Alert>}
          <Typography variant="body2" color="text.secondary">Part, quantity, priority, requested date and the inspection requirement carry over. The part's drawings are attached.</Typography>
          <TextField select label="Work order type" value={type} onChange={(e) => { setType(e.target.value); setWi(null); }}>
            {[['Inspection', 'Inspection'], ['Production', 'Production'], ['Fai', 'FAI'], ['Validation', 'Validation'], ['Repair', 'Repair']].map(([v, l]) => <MenuItem key={v} value={v}>{l}</MenuItem>)}</TextField>
          <Autocomplete options={wis.data ?? []} value={wi} onChange={(_, o) => setWi(o)} onInputChange={(_, v, rs) => rs === 'input' && setText(v)}
            getOptionLabel={(o) => `${o.number} rev ${o.revision} — ${o.title}`} isOptionEqualToValue={(a, b) => a.id === b.id}
            renderInput={(p) => <TextField {...p} label="Work instruction" required />} />
        </Stack>
      </DialogContent>
      <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" onClick={go}>Create work order</Button></DialogActions>
    </Dialog>
  );
}

export function InspectionRequestPage() {
  const { id = '' } = useParams(); const { can } = useAuth(); const qc = useQueryClient();
  const q = useQuery({ queryKey: ['sqf-ir', 'one', id], queryFn: () => get<InspectionRequest>(`${A}/${id}`) });
  const history = useQuery({ queryKey: ['sqf-ir', 'history', id], queryFn: () => get<AuditRow[]>(`${A}/${id}/history`) });
  const [edit, setEdit] = useState(false); const [convert, setConvert] = useState(false); const [err, setErr] = useState<string | null>(null);
  if (q.isLoading) return <Loading />;
  if (q.error || !q.data) return <ErrorState error={q.error} />;
  const r = q.data; const closed = r.status === 'Done' || r.status === 'Cancelled';
  const status = async (s: string) => { setErr(null); try { await post(`${A}/${id}/status`, { status: s }); await qc.invalidateQueries({ queryKey: ['sqf-ir'] }); } catch (e) { setErr(e instanceof Error ? e.message : String(e)); } };
  const initial: Form = { siteId: r.siteId, partNumber: r.partNumber, partId: r.partId ?? null, partDescription: r.partDescription ?? '', quantity: r.quantity?.toString() ?? '', reason: r.reason ?? '',
    requirement: r.requirement ?? '', wi: r.workInstructionId ? { id: r.workInstructionId, number: r.workInstructionLabel ?? '', revision: '', title: '' } : null, requestedDate: r.requestedDate ?? '', priority: r.priority };
  return (
    <SqfPage title={<>{r.number} <PriorityChip p={r.priority} /></>} crumbs={[crumbs[0], { label: 'Inspection Requests', to: '/sequfloh/inspection-requests' }, { label: r.number }]}
      permission="sequfloh.access" recent={{ label: `${r.number} ${r.partNumber}`, itemType: 'Inspection request', recordId: r.id }}
      actions={<Stack direction="row" gap={1} flexWrap="wrap">
        {!r.workOrderId && !closed && <Button variant="contained" onClick={() => setConvert(true)}>Convert to work order</Button>}
        {!closed && <Button variant="outlined" onClick={() => setEdit(true)}>Edit</Button>}
        {r.status === 'Open' && <Button onClick={() => status('InProgress')}>Start</Button>}
        {!closed && <Button onClick={() => status('Done')}>Mark done</Button>}
        {!closed && <Button color="inherit" onClick={() => status('Cancelled')}>Cancel request</Button>}
        {closed && <Button onClick={() => status('Open')}>Reopen</Button>}
        <DeleteButton what={`inspection request ${r.number}`} url={`${A}/${r.id}`} to="/sequfloh/inspection-requests"
          extra={r.workOrderNumber ? `Work order ${r.workOrderNumber} is kept.` : undefined} />
      </Stack>}>
      {err && <Alert severity="error" sx={{ mb: 2 }}>{err}</Alert>}
      {r.overdue && <Alert severity="error" sx={{ mb: 2 }}>Overdue: the requested completion date was {fmtDate(r.requestedDate)}.</Alert>}
      <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: 'minmax(0,1fr)', md: '1fr 1fr' } }}>
        <Facts items={[['Status', <StatusChip key="s" s={r.status} />], ['Part number', <Mono key="p">{r.partNumber}</Mono>], ['Part description', r.partDescription ?? '—'],
          ['Qty (per NetSuite)', r.quantity ?? '—'], ['Reason', r.reason ?? '—'], ['Requested completion date', fmtDate(r.requestedDate)], ['Priority', r.priority],
          ['Work instruction', r.workInstructionId ? <RouterLink key="w" to={`/sequfloh/instruction/${r.workInstructionId}`}>{r.workInstructionLabel}</RouterLink> : '—'],
          ['Work order', r.workOrderId ? <RouterLink key="o" to={`/sequfloh/record/${r.workOrderId}`}>{r.workOrderNumber}</RouterLink> : '—'],
          ['Submitted', `${r.requestedByName ?? '—'} · ${fmtDateTime(r.createdAt)}`]]} />
        <Paper sx={{ p: 2 }}><Typography variant="subtitle2" gutterBottom>Inspection requirement</Typography>
          <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>{r.requirement || '—'}</Typography></Paper>
      </Box>
      <Box sx={{ mt: 3 }}><Typography variant="h6" gutterBottom>Attachments</Typography><FilesPanel type="SequflohInspectionRequest" id={r.id} canEdit={!closed && can('sequfloh.access')} /></Box>
      <Box sx={{ mt: 3 }}><Typography variant="h6" gutterBottom>History</Typography><HistoryTable q={history} /></Box>
      <InspectionRequestDialog open={edit} onClose={() => setEdit(false)} initial={initial} id={r.id} />
      <ConvertDialog r={r} open={convert} onClose={() => setConvert(false)} />
    </SqfPage>
  );
}
