import { useState } from 'react';
import { Link as RouterLink, useNavigate, useParams } from 'react-router-dom';
import { Alert, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, Link, MenuItem, Paper, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post, put } from '../api';
import { Empty, ErrorState, Loading, S, SqfPage, qs, useSqf } from './common';
import { useSites } from '../components/Kit';

export interface ProjectRow { id: string; number: string; name: string; description?: string | null; siteId?: string | null; site?: string | null; status: string; workOrders: number; inspectionRequests: number }
interface ProjectDetail {
  id: string; number: string; name: string; description?: string | null; siteId?: string | null; status: string; canManage: boolean;
  workOrders: { id: string; number: string; type: string; typeLabel: string; status: string; partNumber?: string | null; qtyGood?: number | null; qtyBad?: number | null; hours: number; userHours: number; crewHours: number; costLines: number; chargebacks: number }[];
  inspectionRequests: { id: string; number: string; partNumber: string; status: string; workOrderId?: string | null; workOrderNumber?: string | null; quantity?: number | null }[];
  totals: { hours: number; userHours: number; dayWorkerHours: number; crewHours: number; costLines: number; chargebackReports: number; chargebackTotal: number; totalCost: number;
    workOrders: number; inspectionRequests: number; byType: { type: string; count: number }[]; qtyGood: number; qtyBad: number };
}

export const useProjects = () => useQuery({ queryKey: ['sqf-projects'], queryFn: () => get<ProjectRow[]>(`${S}/projects`), staleTime: 30_000 });
const useCanManage = () => !!useSqf().data?.permissions.includes('sequfloh.manage_projects');
const money = (n: number) => n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const crumbs = (last?: string) => [{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Projects', to: last ? '/sequfloh/projects' : undefined }, ...(last ? [{ label: last }] : [])];

/** Optional project picker for work orders and inspection requests. Only open projects are offered; the current one stays listed. */
export function ProjectPicker({ value, onChange, testId = 'project-picker' }: { value: string; onChange: (id: string) => void; testId?: string }) {
  const q = useProjects();
  const rows = (q.data ?? []).filter((p) => p.status === 'Open' || p.id === value);
  return <TextField select size="small" label="Project (optional)" value={q.data ? value : ''} onChange={(e) => onChange(e.target.value)} inputProps={{ 'data-testid': testId }}>
    <MenuItem value="">None</MenuItem>
    {rows.map((p) => <MenuItem key={p.id} value={p.id}>{p.number} {p.name}{p.status === 'Closed' ? ' (closed)' : ''}</MenuItem>)}
  </TextField>;
}

function ProjectDialog({ open, onClose, initial }: { open: boolean; onClose: (id?: string) => void; initial?: ProjectRow | ProjectDetail }) {
  const sites = useSites();
  const [f, setF] = useState({ number: initial?.number ?? '', name: initial?.name ?? '', description: initial?.description ?? '', siteId: initial?.siteId ?? '' });
  const [err, setErr] = useState<string | null>(null);
  const save = async () => {
    setErr(null);
    const body = { number: f.number.trim(), name: f.name.trim(), description: f.description.trim() || null, siteId: f.siteId || null };
    try {
      if (initial) { await put(`${S}/projects/${initial.id}`, body); onClose(initial.id); }
      else { const r = await post<{ id: string }>(`${S}/projects`, body); onClose(r.id); }
    } catch (e) { setErr((e as Error).message); }
  };
  return <Dialog open={open} onClose={() => onClose()} fullWidth maxWidth="sm">
    <DialogTitle>{initial ? 'Edit project' : 'New project'}</DialogTitle>
    <DialogContent><Stack gap={2} sx={{ mt: 1 }}>
      {err && <Alert severity="error">{err}</Alert>}
      <TextField label="Project number" required value={f.number} onChange={(e) => setF({ ...f, number: e.target.value })} helperText="Your own number, exactly as your company writes it" inputProps={{ 'data-testid': 'project-number' }} />
      <TextField label="Project name" required value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} inputProps={{ 'data-testid': 'project-name' }} />
      <TextField label="Description (optional)" multiline minRows={2} value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} />
      <TextField select label="Site (optional)" value={f.siteId} onChange={(e) => setF({ ...f, siteId: e.target.value })}>
        <MenuItem value="">Any site</MenuItem>{(sites.data ?? []).map((s) => <MenuItem key={s.id} value={s.id}>{s.name}</MenuItem>)}
      </TextField>
    </Stack></DialogContent>
    <DialogActions><Button onClick={() => onClose()}>Cancel</Button><Button variant="contained" onClick={save} disabled={!f.number.trim() || !f.name.trim()} data-testid="project-save">Save</Button></DialogActions>
  </Dialog>;
}

/** Projects list: create, and open a project to see everything linked to it. */
export function ProjectsPage() {
  const [status, setStatus] = useState('Open');
  const [text, setText] = useState('');
  const [open, setOpen] = useState(false);
  const canManage = useCanManage();
  const nav = useNavigate();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['sqf-projects', status, text], queryFn: () => get<ProjectRow[]>(`${S}/projects?${qs({ status: status || undefined, q: text || undefined })}`) });
  return <SqfPage title="Projects" crumbs={crumbs()} permission="sequfloh.access"
    description="A project groups work orders of any type and inspection requests so their hours and costs are totalled together."
    actions={canManage && <Button variant="contained" onClick={() => setOpen(true)} data-testid="project-new">New project</Button>}>
    <Stack direction="row" gap={2} sx={{ mb: 2 }}>
      <TextField size="small" label="Search" value={text} onChange={(e) => setText(e.target.value)} />
      <TextField size="small" select label="Status" value={status} onChange={(e) => setStatus(e.target.value)} sx={{ minWidth: 140 }}>
        <MenuItem value="">All</MenuItem><MenuItem value="Open">Open</MenuItem><MenuItem value="Closed">Closed</MenuItem>
      </TextField>
    </Stack>
    {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} /> : !q.data?.length ? <Empty text="No projects yet." /> :
      <Paper variant="outlined"><Table size="small">
        <TableHead><TableRow><TableCell>Number</TableCell><TableCell>Name</TableCell><TableCell>Site</TableCell><TableCell>Status</TableCell><TableCell align="right">Work orders</TableCell><TableCell align="right">Inspection requests</TableCell></TableRow></TableHead>
        <TableBody>{q.data.map((p) => <TableRow key={p.id} hover>
          <TableCell><Link component={RouterLink} to={`/sequfloh/project/${p.id}`}>{p.number}</Link></TableCell><TableCell>{p.name}</TableCell><TableCell>{p.site ?? '—'}</TableCell>
          <TableCell>{p.status}</TableCell><TableCell align="right">{p.workOrders}</TableCell><TableCell align="right">{p.inspectionRequests}</TableCell>
        </TableRow>)}</TableBody>
      </Table></Paper>}
    {open && <ProjectDialog open onClose={(id) => { setOpen(false); qc.invalidateQueries({ queryKey: ['sqf-projects'] }); if (id) nav(`/sequfloh/project/${id}`); }} />}
  </SqfPage>;
}

/** One project: every linked work order and inspection request with the totals. */
export function ProjectPage() {
  const { id = '' } = useParams();
  const nav = useNavigate();
  const qc = useQueryClient();
  const [edit, setEdit] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const q = useQuery({ queryKey: ['sqf-project', id], queryFn: () => get<ProjectDetail>(`${S}/projects/${id}`) });
  if (q.isLoading) return <Loading />;
  if (q.error || !q.data) return <ErrorState error={q.error} />;
  const p = q.data; const t = p.totals;
  const refresh = () => { qc.invalidateQueries({ queryKey: ['sqf-project', id] }); qc.invalidateQueries({ queryKey: ['sqf-projects'] }); };
  const act = async (fn: () => Promise<unknown>) => { setErr(null); try { await fn(); refresh(); } catch (e) { setErr((e as Error).message); } };
  const linked = t.workOrders + t.inspectionRequests > 0;
  return <SqfPage title={`Project ${p.number}`} crumbs={crumbs(p.number)} permission="sequfloh.access" recent={{ label: `Project ${p.number}`, itemType: 'project', recordId: p.id }}
    actions={p.canManage && <Stack direction="row" gap={1}>
      <Button variant="outlined" onClick={() => setEdit(true)} data-testid="project-edit">Edit</Button>
      {p.status === 'Open' ? <Button variant="outlined" onClick={() => act(() => post(`${S}/projects/${id}/close`))} data-testid="project-close">Close</Button>
        : <Button variant="outlined" onClick={() => act(() => post(`${S}/projects/${id}/reopen`))} data-testid="project-reopen">Reopen</Button>}
      {!linked && <Button color="error" variant="outlined" data-testid="project-delete"
        onClick={() => { if (window.confirm(`Delete project ${p.number}? This can't be undone.`)) act(async () => { await del(`${S}/projects/${id}`); nav('/sequfloh/projects'); }); }}>Delete</Button>}
    </Stack>}>
    {err && <Alert severity="error" sx={{ mb: 2 }}>{err}</Alert>}
    <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
      <Typography variant="h6">{p.name}</Typography>
      <Typography variant="body2" color="text.secondary">Status: {p.status}{p.description ? ` · ${p.description}` : ''}</Typography>
      <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr 1fr', md: 'repeat(4, 1fr)' }, gap: 2, mt: 2 }} data-testid="project-totals">
        {([['Total man hours', t.hours.toFixed(2)], ['Signed-in people', t.userHours.toFixed(2)], ['Day workers', t.dayWorkerHours.toFixed(2)], ['Crew', t.crewHours.toFixed(2)],
          ['Cost lines', money(t.costLines)], ['Chargebacks', `${money(t.chargebackTotal)} (${t.chargebackReports})`], ['Total cost', money(t.totalCost)], ['Qty good / bad', `${t.qtyGood} / ${t.qtyBad}`],
          ['Work orders', String(t.workOrders)], ['Inspection requests', String(t.inspectionRequests)], ['By type', t.byType.map((x) => `${x.type} ${x.count}`).join(', ') || '—']] as [string, string][])
          .map(([l, v]) => <Box key={l}><Typography variant="caption" color="text.secondary">{l}</Typography><Typography>{v}</Typography></Box>)}
      </Box>
      <Button sx={{ mt: 2 }} variant="outlined" component={RouterLink} to={`/sequfloh/chargebacks/new?project=${p.id}`}>Chargeback report for this project</Button>
    </Paper>
    <Typography variant="h6" sx={{ mb: 1 }}>Work orders</Typography>
    {!p.workOrders.length ? <Empty text="No work orders in this project yet. Pick the project on a work order to add it." /> :
      <Paper variant="outlined" sx={{ mb: 2 }}><Table size="small">
        <TableHead><TableRow><TableCell>WO #</TableCell><TableCell>Type</TableCell><TableCell>Status</TableCell><TableCell>Part</TableCell><TableCell align="right">Hours</TableCell><TableCell align="right">Qty good</TableCell><TableCell align="right">Qty bad</TableCell><TableCell align="right">Cost lines</TableCell><TableCell align="right">Chargebacks</TableCell></TableRow></TableHead>
        <TableBody>{p.workOrders.map((w) => <TableRow key={w.id} hover>
          <TableCell><Link component={RouterLink} to={`/sequfloh/record/${w.id}`}>{w.number}</Link></TableCell><TableCell>{w.typeLabel}</TableCell><TableCell>{w.status}</TableCell><TableCell>{w.partNumber ?? '—'}</TableCell>
          <TableCell align="right">{w.hours.toFixed(2)}</TableCell><TableCell align="right">{w.qtyGood ?? ''}</TableCell><TableCell align="right">{w.qtyBad ?? ''}</TableCell><TableCell align="right">{money(w.costLines)}</TableCell><TableCell align="right">{money(w.chargebacks)}</TableCell>
        </TableRow>)}</TableBody>
      </Table></Paper>}
    <Typography variant="h6" sx={{ mb: 1 }}>Inspection requests</Typography>
    {!p.inspectionRequests.length ? <Empty text="No inspection requests in this project yet." /> :
      <Paper variant="outlined"><Table size="small">
        <TableHead><TableRow><TableCell>Number</TableCell><TableCell>Part</TableCell><TableCell>Status</TableCell><TableCell>Work order</TableCell></TableRow></TableHead>
        <TableBody>{p.inspectionRequests.map((r) => <TableRow key={r.id} hover>
          <TableCell><Link component={RouterLink} to={`/sequfloh/inspection-request/${r.id}`}>{r.number}</Link></TableCell><TableCell>{r.partNumber}</TableCell><TableCell>{r.status}</TableCell>
          <TableCell>{r.workOrderId ? <Link component={RouterLink} to={`/sequfloh/record/${r.workOrderId}`}>{r.workOrderNumber}</Link> : '—'}</TableCell>
        </TableRow>)}</TableBody>
      </Table></Paper>}
    {edit && <ProjectDialog open initial={p} onClose={() => { setEdit(false); refresh(); }} />}
  </SqfPage>;
}

/** Shows and changes the project of a saved work order or inspection request. */
export function ProjectField({ kind, recordId, projectId, label, canEdit }: { kind: 'work-orders' | 'inspection-requests'; recordId: string; projectId?: string | null; label?: string | null; canEdit: boolean }) {
  const qc = useQueryClient();
  const [err, setErr] = useState<string | null>(null);
  if (!canEdit) return <Typography variant="body2" data-testid="project-label">Project: {projectId ? <Link component={RouterLink} to={`/sequfloh/project/${projectId}`}>{label ?? 'open'}</Link> : '—'}</Typography>;
  return <Stack direction="row" gap={1} alignItems="center">
    <ProjectPicker value={projectId ?? ''} testId="project-link" onChange={async (v) => {
      setErr(null);
      try { await put(`${S}/projects/${kind}/${recordId}`, { projectId: v || null }); qc.invalidateQueries(); } catch (e) { setErr((e as Error).message); }
    }} />
    {projectId && <Link component={RouterLink} to={`/sequfloh/project/${projectId}`}>Open project</Link>}
    {err && <Alert severity="error">{err}</Alert>}
  </Stack>;
}

/** Project number and name linking to the project page, or a dash. */
export function ProjectName({ id }: { id?: string | null }) {
  const q = useProjects();
  if (!id) return <>—</>;
  const p = q.data?.find((x) => x.id === id);
  return <Link component={RouterLink} to={`/sequfloh/project/${id}`}>{p ? `${p.number} ${p.name}` : 'Open project'}</Link>;
}
