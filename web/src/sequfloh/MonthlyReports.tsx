import { useEffect, useState, type ReactNode } from 'react';
import { Link as RouterLink, useNavigate, useParams } from 'react-router-dom';
import { Alert, Box, Button, Chip, Dialog, DialogActions, DialogContent, IconButton, MenuItem, Paper, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import Add from '@mui/icons-material/Add';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, getBlob, post, put } from '../api';
import { useAuth } from '../auth';
import { fmtDateTime } from '../components/Status';
import { Empty, ErrorState, Loading, S, SqfPage, SqfStatus } from './common';

interface Origin { fileType: string; fileName: string; tab: string; computed: string; overridden: boolean }
interface PartRow { partNumber: string; description: string; totalClaims: number }
interface VehicleRow { name: string; count: number }
interface Note { label: string; text: string }
interface Fai { category: string; totalCompleted: number; passed: number; failed: number; passedWithDeviation: number }
interface Ncr { number: string; partNumber: string; defect: string; disposition: string; status: string }
interface Pair { problem: string; response: string }
interface Body {
  documentId: string; documentRevision: string; documentTitle: string; documentDate?: string | null; approvedById?: string | null; approvedByName: string;
  departmentStatus: string; primaryAchievement: string; criticalRisks: string[];
  totalClaimsRaw: number | null; totalClaimsProcessed: number | null; dmaParts: number | null; carterParts: number | null;
  totalAmountRequested: number | null; partsAmountRequested: number | null; laborAmountRequested: number | null;
  claimsApproved: number | null; claimsDenied: number | null; claimsPending: number | null; claimsPendingNote: string;
  mttfDays: number | null; medianTtfDays: number | null; topParts: PartRow[]; vehicleModels: VehicleRow[];
  rootCause: Note[]; recommendations: Note[];
  alertsTotal: number | null; alertsOpen: number | null; hoursInvestigating: number | null; avgDaysToClose: number | null; alertNotes: string;
  quarantine: string; firstArticle: Fai[];
  ncrCount: number | null; ncrOpen: number | null; ncrClosed: number | null; carCount: number | null; pirCount: number | null; rpnCount: number | null; ncrs: Ncr[];
  recalls: string; collabtic: string; fieldQuestion: string; fitmentIntro: string; fitment: Pair[]; productInfo: Pair[]; blackLabelPallets: string;
  trendImageKey?: string | null; highReturnImageKey?: string | null; vehicleImageKey?: string | null; emailImageKey?: string | null;
  origin: Record<string, Origin>;
}
interface FileRow { id: string; fileType: string; fileName: string; uploadedAt: string; uploadedByName: string; result?: { applied?: boolean; note?: string; warnings?: string[]; skipped?: { tab: string; row: number; reason: string }[]; includedRows?: number; distinctCount?: number | null } }
interface Trend { label: string; current: boolean; hasReport: boolean; totalClaims: number | null; productAlerts: number | null; totalAmount: number | null; partsAmount: number | null; laborAmount: number | null; ratio: string; mttf: string; median: string }
interface Outline { kind: string; text?: string; items?: string[]; headers?: string[]; rows?: string[][]; slot?: string }
interface Detail {
  id: string; year: number; month: number; title: string; status: string; flatRate: number; rowVersion: number;
  finalizedAt?: string | null; finalizedByName?: string | null; reopenReason?: string | null;
  body: Body; trend: Trend[]; outline: Outline[]; files: FileRow[];
  calculated: { potentialLiability: number | null; approvedLiability: number | null; deniedSavings: number | null; ratio: string; faiTotal: Fai };
}
interface SourceKind { type: string; label: string; hint: string; ready: boolean; extensions: string[]; fills: string[] }
interface ListRow { id: string; year: number; month: number; title: string; status: string; updatedAt: string; createdByName: string }

const empty = 'no new data for this month';
const money = (n?: number | null) => (n === null || n === undefined ? empty : n.toLocaleString('en-US', { style: 'currency', currency: 'USD' }));
const num = (v: string) => (v.trim() === '' ? null : Number(v));
const pictures: SourceKind[] = [
  { type: 'email-issues', label: 'OR/NAPA email issues picture', hint: 'The picture for section 6.9.', ready: true, extensions: ['.png', '.jpg', '.jpeg', '.webp'], fills: ['6.9 OR/NAPA Email Issues'] },
  { type: 'chart-trend', label: 'Trend chart picture', hint: 'Replaces the chart drawn from the six-month tables.', ready: true, extensions: ['.png', '.jpg', '.jpeg', '.webp'], fills: ['6.2 Labor Claims chart'] },
  { type: 'chart-high-return', label: 'High-return chart picture', hint: 'Replaces the chart drawn from top returning parts.', ready: true, extensions: ['.png', '.jpg', '.jpeg', '.webp'], fills: ['6.2.2 Analysis of High-Return Items'] },
  { type: 'chart-vehicles', label: 'Vehicle model chart picture', hint: 'Replaces the chart drawn from vehicle counts.', ready: true, extensions: ['.png', '.jpg', '.jpeg', '.webp'], fills: ['Top Vehicle Models'] },
];

function FromFile({ origin, field }: { origin: Record<string, Origin>; field: string }) {
  const o = origin?.[field];
  if (!o) return null;
  const where = o.fileType === 'sequfloh' ? o.fileName : `${o.fileName}${o.tab ? ` (${o.tab})` : ''}`;
  return <Typography variant="caption" color={o.overridden ? 'warning.main' : 'text.secondary'} data-testid={`from-${field}`} sx={{ display: 'block' }}>
    {o.overridden ? `Overridden. ${where} had ${o.computed || 'blank'}.` : `From ${where}.`}
  </Typography>;
}

export function MonthlyReportsPage() {
  const q = useQuery({ queryKey: ['sqf-monthly'], queryFn: () => get<ListRow[]>(`${S}/monthly-reports`) });
  const sources = useQuery({ queryKey: ['sqf-monthly-sources'], queryFn: () => get<SourceKind[]>(`${S}/monthly-reports/sources`) });
  const { can } = useAuth();
  const nav = useNavigate();
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);
  const [month, setMonth] = useState('2026-09');
  const [copy, setCopy] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const create = async () => {
    setError(null);
    const [y, m] = month.split('-').map(Number);
    try {
      const created = await post<Detail>(`${S}/monthly-reports`, { year: y, month: m, copyText: copy });
      await qc.invalidateQueries({ queryKey: ['sqf-monthly'] });
      nav(`/sequfloh/monthly-reports/${created.id}`);
    } catch (e) { setError((e as Error).message); }
  };
  return <SqfPage title="Monthly Reports" permission="sequfloh.monthly_reports_view" crumbs={[{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Monthly Reports' }]}
    description="One quality report for each month, with the same sections every time."
    actions={can('sequfloh.monthly_reports_edit') && <Button variant="contained" data-testid="create-monthly-report" onClick={() => setOpen(true)}>New report</Button>}>
    {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} /> : !(q.data ?? []).length ? <Empty text="No monthly reports yet." /> :
      <Paper variant="outlined"><Table size="small" data-testid="monthly-reports-list">
        <TableHead><TableRow><TableCell>Report</TableCell><TableCell>Status</TableCell><TableCell>Updated</TableCell><TableCell /></TableRow></TableHead>
        <TableBody>{(q.data ?? []).map((r) => <TableRow key={r.id} hover>
          <TableCell><Button component={RouterLink} to={`/sequfloh/monthly-reports/${r.id}`} data-testid="monthly-row">{r.title}</Button></TableCell>
          <TableCell><SqfStatus status={r.status} /></TableCell>
          <TableCell>{fmtDateTime(r.updatedAt)} · {r.createdByName}</TableCell>
          <TableCell align="right"><Button size="small" component={RouterLink} to={`/sequfloh/monthly-reports/${r.id}`}>{r.status === 'Final' ? 'View' : 'Edit'}</Button></TableCell>
        </TableRow>)}</TableBody>
      </Table></Paper>}
    <Paper variant="outlined" sx={{ p: 2, mt: 2 }}>
      <Typography variant="subtitle2" sx={{ mb: 1 }}>Source files</Typography>
      <Stack gap={0.5}>{(sources.data ?? []).concat(pictures).map((s) => <Typography key={s.type} variant="body2"><b>{s.label}.</b> {s.ready ? `Fills ${s.fills.join('; ')}.` : s.hint}</Typography>)}</Stack>
    </Paper>
    <Dialog open={open} onClose={() => setOpen(false)} fullWidth maxWidth="xs">
      <DialogContent>
        <Typography variant="h6" sx={{ mb: 1 }}>New monthly report</Typography>
        {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
        <TextField fullWidth type="month" label="Month" value={month} onChange={(e) => setMonth(e.target.value)} sx={{ mt: 1 }} slotProps={{ inputLabel: { shrink: true } }} />
        <TextField select fullWidth label="Starting text" value={copy ? 'copy' : 'blank'} onChange={(e) => setCopy(e.target.value === 'copy')} sx={{ mt: 2 }}>
          <MenuItem value="copy">Copy last month's document header</MenuItem>
          <MenuItem value="blank">Start with the standard header</MenuItem>
        </TextField>
      </DialogContent>
      <DialogActions><Button onClick={() => setOpen(false)}>Cancel</Button><Button variant="contained" onClick={create}>Create</Button></DialogActions>
    </Dialog>
  </SqfPage>;
}

export function MonthlyReportPage() {
  const { id = '' } = useParams();
  const q = useQuery({ queryKey: ['sqf-monthly', id], queryFn: () => get<Detail>(`${S}/monthly-reports/${id}`) });
  const { can } = useAuth();
  if (q.isLoading) return <SqfPage title="Monthly report" permission="sequfloh.monthly_reports_view" crumbs={[{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Monthly Reports', to: '/sequfloh/monthly-reports' }, { label: 'Report' }]}><Loading /></SqfPage>;
  if (q.error || !q.data) return <SqfPage title="Monthly report" permission="sequfloh.monthly_reports_view" crumbs={[{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Monthly Reports', to: '/sequfloh/monthly-reports' }, { label: 'Report' }]}><ErrorState error={q.error} /></SqfPage>;
  const draft = q.data.status !== 'Final' && can('sequfloh.monthly_reports_edit');
  return draft ? <Editor detail={q.data} /> : <Reader detail={q.data} canFinalize={can('sequfloh.monthly_reports_finalize')} />;
}

function Reader({ detail, canFinalize }: { detail: Detail; canFinalize: boolean }) {
  const qc = useQueryClient();
  const [reason, setReason] = useState('');
  const [ask, setAsk] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const reopen = async () => {
    try {
      await post(`${S}/monthly-reports/${detail.id}/reopen`, { reason });
      setAsk(false);
      await qc.invalidateQueries({ queryKey: ['sqf-monthly', detail.id] });
    } catch (e) { setError((e as Error).message); }
  };
  return <SqfPage title={detail.title} permission="sequfloh.monthly_reports_view" crumbs={[{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Monthly Reports', to: '/sequfloh/monthly-reports' }, { label: detail.title }]}
    actions={<Stack direction="row" gap={1}><Chip label={detail.status} color={detail.status === 'Final' ? 'success' : 'default'} /><Button component="a" href={`${S}/monthly-reports/${detail.id}/pdf`} target="_blank">PDF</Button><Button component="a" href={`${S}/monthly-reports/${detail.id}/docx`}>Word</Button>
      {detail.status === 'Final' && canFinalize && <Button variant="outlined" onClick={() => setAsk(true)}>Reopen</Button>}</Stack>}>
    <Stack gap={1.25} data-testid="monthly-report-view">
      <HeaderLines body={detail.body} />
      {detail.finalizedByName && <Typography variant="body2" color="text.secondary">Finalized by {detail.finalizedByName}{detail.reopenReason ? `. Last reopen reason: ${detail.reopenReason}` : ''}</Typography>}
      {error && <Alert severity="error">{error}</Alert>}
      {detail.outline.map((b, i) => <BlockView key={i} block={b} id={detail.id} />)}
    </Stack>
    <Dialog open={ask} onClose={() => setAsk(false)} fullWidth maxWidth="sm">
      <DialogContent><Typography variant="h6">Reopen this report</Typography><TextField fullWidth multiline minRows={2} label="Reason" value={reason} onChange={(e) => setReason(e.target.value)} sx={{ mt: 1 }} /></DialogContent>
      <DialogActions><Button onClick={() => setAsk(false)}>Cancel</Button><Button variant="contained" disabled={!reason.trim()} onClick={reopen}>Reopen</Button></DialogActions>
    </Dialog>
  </SqfPage>;
}

function BlockView({ block, id }: { block: Outline; id: string }) {
  if (block.kind === 'status') {
    const color = block.text === 'Green' ? '#00FF00' : block.text === 'Red' ? '#FF5050' : block.text === 'Yellow' ? '#FFFF00' : 'transparent';
    return <Typography>Status of Department: {block.text ? <Box component="span" sx={{ bgcolor: color, px: 0.5 }}>{block.text}</Box> : null}</Typography>;
  }
  if (block.kind === 'title') return <Typography variant="h4" sx={{ fontWeight: 800, fontSize: 28 }}>{block.text}</Typography>;
  if (block.kind === 'h2') return <Typography variant="h5" sx={{ mt: 2, color: '#0F4761', fontWeight: 800 }}>{block.text}</Typography>;
  if (block.kind === 'h3') return <Typography variant="h6" sx={{ mt: 1, color: '#0F4761', fontWeight: 700 }}>{block.text}</Typography>;
  if (block.kind === 'line') return <Typography>{block.text}</Typography>;
  if (block.kind === 'bullets') return <Box component="ul" sx={{ my: 0 }}>{(block.items ?? []).map((item, i) => <li key={i}><Typography>{item}</Typography></li>)}{(block.items ?? []).length === 0 && <Typography>{empty}</Typography>}</Box>;
  if (block.kind === 'image') return <Box component="img" alt={block.slot} src={`${S}/monthly-reports/${id}/images/${block.slot}`} sx={{ maxWidth: '100%', border: '1px solid', borderColor: 'divider' }} />;
  return <Table size="small" sx={{ '& td, & th': { border: '1px solid', borderColor: 'divider' } }}><TableHead><TableRow>{(block.headers ?? []).map((h) => <TableCell key={h} sx={{ fontWeight: 700 }}>{h}</TableCell>)}</TableRow></TableHead>
    <TableBody>{(block.rows ?? []).map((r, i) => <TableRow key={i}>{r.map((c, j) => <TableCell key={j}>{c}</TableCell>)}</TableRow>)}</TableBody></Table>;
}

function Editor({ detail }: { detail: Detail }) {
  const qc = useQueryClient();
  const sources = useQuery({ queryKey: ['sqf-monthly-sources'], queryFn: () => get<SourceKind[]>(`${S}/monthly-reports/sources`) });
  const people = useQuery({ queryKey: ['sqf-monthly-people'], queryFn: () => get<{ id: string; displayName: string }[]>(`${S}/monthly-reports/people`) });
  const [body, setBody] = useState<Body>(detail.body);
  const [rate, setRate] = useState(String(detail.flatRate));
  const [error, setError] = useState<string | null>(null);
  const [notes, setNotes] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  useEffect(() => { setBody(detail.body); setRate(String(detail.flatRate)); }, [detail.rowVersion, detail.id]);
  const save = async () => {
    setBusy(true); setError(null);
    try {
      await put(`${S}/monthly-reports/${detail.id}`, { body, flatRate: Number(rate), rowVersion: detail.rowVersion });
      await qc.invalidateQueries({ queryKey: ['sqf-monthly', detail.id] });
    } catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  };
  const upload = async (type: string, file: File) => {
    setError(null);
    const fd = new FormData();
    fd.set('type', type);
    fd.set('file', file);
    try {
      await post(`${S}/monthly-reports/${detail.id}/sources`, fd);
      await qc.invalidateQueries({ queryKey: ['sqf-monthly', detail.id] });
    } catch (e) { setError((e as Error).message); }
  };
  const fill = async () => {
    setError(null);
    try {
      const r = await post<{ notes: string[] }>(`${S}/monthly-reports/${detail.id}/fill`);
      setNotes(r.notes ?? []);
      await qc.invalidateQueries({ queryKey: ['sqf-monthly', detail.id] });
    } catch (e) { setError((e as Error).message); }
  };
  const finalize = async () => {
    if (!window.confirm('Finalize this report? It stays locked until someone reopens it with a reason.')) return;
    try {
      await post(`${S}/monthly-reports/${detail.id}/finalize`);
      await qc.invalidateQueries({ queryKey: ['sqf-monthly', detail.id] });
    } catch (e) { setError((e as Error).message); }
  };
  const download = async (kind: 'pdf' | 'docx') => {
    const blob = await getBlob(`${S}/monthly-reports/${detail.id}/${kind}`);
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = `${detail.title}.${kind}`; a.click();
    URL.revokeObjectURL(url);
  };
  const set = (patch: Partial<Body>) => setBody({ ...body, ...patch });
  const n = (field: keyof Body) => (value: string) => set({ [field]: num(value) } as Partial<Body>);
  return <SqfPage title={detail.title} permission="sequfloh.monthly_reports_edit" crumbs={[{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Monthly Reports', to: '/sequfloh/monthly-reports' }, { label: detail.title }]}
    actions={<Stack direction="row" gap={1} flexWrap="wrap"><Chip label="Draft" /><Button onClick={fill}>Fill from Sequfloh</Button><Button onClick={() => download('pdf')}>PDF</Button><Button onClick={() => download('docx')}>Word</Button><Button variant="outlined" onClick={finalize}>Finalize</Button><Button variant="contained" disabled={busy} onClick={save} data-testid="save-monthly-report">Save</Button></Stack>}>
    <Stack gap={2} data-testid="monthly-report-form">
      {error && <Alert severity="error">{error}</Alert>}
      {notes.length > 0 && <Alert severity="info">{notes.join(' ')}</Alert>}
      <Paper variant="outlined" sx={{ p: 2 }} data-testid="monthly-document-header">
        <Typography variant="h6" sx={{ mb: 1 }}>Document header</Typography>
        <Typography variant="body2" sx={{ fontWeight: 700 }}>{body.documentId || 'TMP-ENG-001'}: {body.documentTitle || 'Monthly Engineering Development Report'}</Typography>
        <Typography variant="body2" sx={{ mb: 1 }}>Document ID: {body.documentId || 'TMP-ENG-001'} | Rev: {body.documentRevision || 'C'} | Title: {body.documentTitle || 'Monthly Engineering Development Report'} | Date: {headerDate(body.documentDate)} | Approved By: {body.approvedByName || ''} |</Typography>
        <Stack direction="row" gap={2} flexWrap="wrap">
          <TextField size="small" label="Document ID" value={body.documentId ?? ''} onChange={(e) => set({ documentId: e.target.value })} />
          <TextField size="small" label="Rev" value={body.documentRevision ?? ''} onChange={(e) => set({ documentRevision: e.target.value })} sx={{ maxWidth: 100 }} />
          <TextField size="small" label="Header title" value={body.documentTitle ?? ''} onChange={(e) => set({ documentTitle: e.target.value })} sx={{ minWidth: 320 }} />
          <TextField size="small" type="date" label="Date" value={body.documentDate ?? ''} onChange={(e) => set({ documentDate: e.target.value })} slotProps={{ inputLabel: { shrink: true } }} />
          <TextField select size="small" label="Approved By" value={body.approvedById ?? ''} onChange={(e) => set({ approvedById: e.target.value || null, approvedByName: people.data?.find((p) => p.id === e.target.value)?.displayName ?? '' })} sx={{ minWidth: 220 }}>
            <MenuItem value=""> </MenuItem>
            {(people.data ?? []).map((p) => <MenuItem key={p.id} value={p.id}>{p.displayName}</MenuItem>)}
          </TextField>
        </Stack>
      </Paper>
      <Paper variant="outlined" sx={{ p: 2 }} data-testid="monthly-sources">
        <Typography variant="h6" sx={{ mb: 1 }}>Source files</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>Upload a workbook to fill the fields it owns. Every filled value stays editable. A value you change is kept if the same file is uploaded again.</Typography>
        <Stack gap={1.5}>
          {(sources.data ?? []).concat(pictures).map((s) => <Box key={s.type}>
            <Stack direction="row" gap={1} alignItems="center" flexWrap="wrap">
              <Button variant="outlined" component="label" size="small">{s.label}<input hidden type="file" data-testid="monthly-report-source" accept={s.extensions.join(',')} onChange={(e) => { const f = e.target.files?.[0]; if (f) upload(s.type, f); e.target.value = ''; }} /></Button>
              <Typography variant="body2" color="text.secondary">{s.ready ? `Fills ${s.fills.join('; ')}.` : s.hint}</Typography>
            </Stack>
            {detail.files.filter((f) => f.fileType === s.type).slice(0, 1).map((f) => <Box key={f.id} sx={{ mt: 0.5 }}>
              <Typography variant="caption">{f.fileName} · {fmtDateTime(f.uploadedAt)} · {f.uploadedByName}{f.result?.includedRows != null ? ` · ${f.result.includedRows} rows` : ''}{f.result?.distinctCount != null ? ` · ${f.result.distinctCount} distinct` : ''}</Typography>
              {(f.result?.warnings ?? []).slice(0, 8).map((w, i) => <Typography key={i} variant="caption" display="block" color="text.secondary">{w}</Typography>)}
              {(f.result?.skipped ?? []).slice(0, 8).map((w, i) => <Typography key={i} variant="caption" display="block" color="warning.main">{w.tab} row {w.row}: {w.reason}</Typography>)}
            </Box>)}
          </Box>)}
        </Stack>
      </Paper>

      <Section title="6.1 Executive Summary">
        <TextField select label="Status of Department" value={body.departmentStatus} onChange={(e) => set({ departmentStatus: e.target.value })} sx={{ maxWidth: 280 }}>
          <MenuItem value=""> </MenuItem><MenuItem value="Green">Green</MenuItem><MenuItem value="Yellow">Yellow</MenuItem><MenuItem value="Red">Red</MenuItem>
        </TextField>
        <TextField label="Primary Achievement" placeholder={empty} value={body.primaryAchievement} onChange={(e) => set({ primaryAchievement: e.target.value })} fullWidth multiline minRows={2} />
        <Lines label="Critical Risk/Blocker" rows={body.criticalRisks} onChange={(criticalRisks) => set({ criticalRisks })} />
        <Stack direction="row" gap={2} flexWrap="wrap">
          <Num label="Total Claims (raw export)" value={body.totalClaimsRaw} onChange={n('totalClaimsRaw')} />
          <Num label="Total Claims (tracker processed)" value={body.totalClaimsProcessed} onChange={n('totalClaimsProcessed')} note={<FromFile origin={body.origin} field="totalClaimsProcessed" />} />
          <Num label="DMA Parts" value={body.dmaParts} onChange={n('dmaParts')} note={<FromFile origin={body.origin} field="dmaParts" />} />
          <Num label="Carter / Non-DMA Pumps" value={body.carterParts} onChange={n('carterParts')} note={<FromFile origin={body.origin} field="carterParts" />} />
        </Stack>
        <Typography variant="h6">Financials</Typography>
        <Stack direction="row" gap={2} flexWrap="wrap">
          <Num label="Total Amount Requested" value={body.totalAmountRequested} onChange={n('totalAmountRequested')} money />
          <Num label="Parts Amount Requested" value={body.partsAmountRequested} onChange={n('partsAmountRequested')} money />
          <Num label="Labor Amount Requested" value={body.laborAmountRequested} onChange={n('laborAmountRequested')} money />
        </Stack>
        <Typography variant="h6">Financials ({detail.title.replace('Monthly Quality Report - ', '').split(' ')[0]} Payout Policy)</Typography>
        <TextField label="Flat rate" value={rate} onChange={(e) => setRate(e.target.value)} sx={{ maxWidth: 180 }} />
        <Stack direction="row" gap={2} flexWrap="wrap">
          <Num label="Approved claims" value={body.claimsApproved} onChange={n('claimsApproved')} note={<FromFile origin={body.origin} field="claimsApproved" />} />
          <Num label="Denied claims" value={body.claimsDenied} onChange={n('claimsDenied')} note={<FromFile origin={body.origin} field="claimsDenied" />} />
          <Num label="Pending / unentered" value={body.claimsPending} onChange={n('claimsPending')} />
          <TextField size="small" label="Pending / unentered note" value={body.claimsPendingNote ?? ''} onChange={(e) => set({ claimsPendingNote: e.target.value })} sx={{ minWidth: 320 }} />
        </Stack>
      </Section>

      <Section title="6.2 Labor Claims">
        <Stack direction="row" gap={2} flexWrap="wrap">
          <Num label="Mean time to failure (days)" value={body.mttfDays} onChange={n('mttfDays')} />
          <Num label="Median time to failure (days)" value={body.medianTtfDays} onChange={n('medianTtfDays')} />
        </Stack>
        <TrendTable trend={detail.trend} />
        <Chart src={`${S}/monthly-reports/${detail.id}/images/trend?v=${detail.rowVersion}`} show={Boolean(body.trendImageKey) || detail.trend.some((t) => t.totalClaims != null || t.productAlerts != null)} alt="Six-month trend" />
        <Typography variant="h6">Top Returning Parts</Typography>
        <Table size="small" data-testid="monthly-top-parts"><TableHead><TableRow><TableCell>Part Number</TableCell><TableCell>Description / Application</TableCell><TableCell>Total Claims</TableCell><TableCell /></TableRow></TableHead>
          <TableBody>{body.topParts.map((p, i) => <TableRow key={i}>
            <TableCell><TextField size="small" value={p.partNumber} onChange={(e) => set({ topParts: body.topParts.map((x, j) => j === i ? { ...x, partNumber: e.target.value } : x) })} /></TableCell>
            <TableCell><TextField size="small" value={p.description} onChange={(e) => set({ topParts: body.topParts.map((x, j) => j === i ? { ...x, description: e.target.value } : x) })} /></TableCell>
            <TableCell><TextField size="small" value={p.totalClaims} onChange={(e) => set({ topParts: body.topParts.map((x, j) => j === i ? { ...x, totalClaims: Number(e.target.value || 0) } : x) })} /></TableCell>
            <TableCell><IconButton aria-label="Remove part" onClick={() => set({ topParts: body.topParts.filter((_, j) => j !== i) })}><DeleteOutline /></IconButton></TableCell>
          </TableRow>)}</TableBody></Table>
        {body.topParts.length === 0 && <Typography>{empty}</Typography>}
        {body.topParts.length < 10 && <Button startIcon={<Add />} onClick={() => set({ topParts: [...body.topParts, { partNumber: '', description: '', totalClaims: 0 }] })}>Add part</Button>}
      </Section>

      <Section title="6.2.2 Analysis of High-Return Items">
        <Chart src={`${S}/monthly-reports/${detail.id}/images/high-return?v=${detail.rowVersion}`} show={Boolean(body.highReturnImageKey) || body.topParts.some((p) => p.totalClaims > 0)} alt="High-return items" />
        <Typography variant="h6">Top Vehicle Models</Typography>
        <Chart src={`${S}/monthly-reports/${detail.id}/images/vehicles?v=${detail.rowVersion}`} show={Boolean(body.vehicleImageKey) || body.vehicleModels.some((v) => v.name && v.count > 0)} alt="Vehicle models" />
      </Section>

      <Section title="6.2.3 Root Cause Analysis (RCA)">
        <Notes rows={body.rootCause} onChange={(rootCause) => set({ rootCause })} />
      </Section>
      <Section title="6.2.4 Recommendation">
        <Notes rows={body.recommendations} onChange={(recommendations) => set({ recommendations })} />
      </Section>

      <Section title="6.3 Product Alerts">
        <Stack direction="row" gap={2} flexWrap="wrap">
          <Num label="Total Product Alerts" value={body.alertsTotal} onChange={n('alertsTotal')} note={<FromFile origin={body.origin} field="alertsTotal" />} />
          <Num label="Open" value={body.alertsOpen} onChange={n('alertsOpen')} note={<FromFile origin={body.origin} field="alertsOpen" />} />
          <Num label="Hours Spent Investigating" value={body.hoursInvestigating} onChange={n('hoursInvestigating')} note={<FromFile origin={body.origin} field="hoursInvestigating" />} />
          <Num label="Avg time to close (days)" value={body.avgDaysToClose} onChange={n('avgDaysToClose')} note={<FromFile origin={body.origin} field="avgDaysToClose" />} />
        </Stack>
        <TextField label="Notes" placeholder={empty} value={body.alertNotes} onChange={(e) => set({ alertNotes: e.target.value })} fullWidth multiline minRows={2} />
      </Section>

      <Section title="6.4 Quarantine">
        <TextField label="Quarantine" placeholder={empty} value={body.quarantine} onChange={(e) => set({ quarantine: e.target.value })} fullWidth multiline minRows={3} />
      </Section>

      <Section title="6.5 First Article Verifications">
        <FromFile origin={body.origin} field="firstArticle" />
        <Table size="small" data-testid="monthly-fai-table"><TableHead><TableRow>
          <TableCell>Product Category</TableCell><TableCell>Total Completed</TableCell><TableCell>Passed</TableCell><TableCell>Failed</TableCell><TableCell>Passed w/ Deviation</TableCell><TableCell />
        </TableRow></TableHead><TableBody>
          {body.firstArticle.map((r, i) => <TableRow key={i}>
            {(['category', 'totalCompleted', 'passed', 'failed', 'passedWithDeviation'] as const).map((k) => <TableCell key={k}><TextField size="small" value={r[k]} onChange={(e) => set({ firstArticle: body.firstArticle.map((x, j) => j === i ? { ...x, [k]: k === 'category' ? e.target.value : Number(e.target.value || 0) } : x) })} /></TableCell>)}
            <TableCell><IconButton aria-label="Remove category" onClick={() => set({ firstArticle: body.firstArticle.filter((_, j) => j !== i) })}><DeleteOutline /></IconButton></TableCell>
          </TableRow>)}
          {body.firstArticle.length === 0 && <TableRow><TableCell colSpan={6}>{empty}</TableCell></TableRow>}
          {body.firstArticle.length > 0 && <TableRow><TableCell>Total</TableCell><TableCell>{detail.calculated.faiTotal.totalCompleted}</TableCell><TableCell>{detail.calculated.faiTotal.passed}</TableCell><TableCell>{detail.calculated.faiTotal.failed}</TableCell><TableCell>{detail.calculated.faiTotal.passedWithDeviation}</TableCell><TableCell /></TableRow>}
        </TableBody></Table>
        <Button startIcon={<Add />} onClick={() => set({ firstArticle: [...body.firstArticle, { category: '', totalCompleted: 0, passed: 0, failed: 0, passedWithDeviation: 0 }] })}>Add category</Button>
      </Section>

      <Section title="6.6 NCRs, CARs, PIRs, & RPNs">
        <Stack direction="row" gap={2} flexWrap="wrap">
          <Num label="NCRs" value={body.ncrCount} onChange={n('ncrCount')} />
          <Num label="NCR open" value={body.ncrOpen} onChange={n('ncrOpen')} />
          <Num label="NCR closed" value={body.ncrClosed} onChange={n('ncrClosed')} />
          <Num label="CARs" value={body.carCount} onChange={n('carCount')} />
          <Num label="RPNs" value={body.rpnCount} onChange={n('rpnCount')} />
        </Stack>
        <Table size="small"><TableHead><TableRow><TableCell>NCR Number</TableCell><TableCell>Part Number</TableCell><TableCell>Description of Defect</TableCell><TableCell>Disposition</TableCell><TableCell>Status</TableCell><TableCell /></TableRow></TableHead>
          <TableBody>{body.ncrs.map((r, i) => <TableRow key={i}>
            {(['number', 'partNumber', 'defect', 'disposition', 'status'] as const).map((k) => <TableCell key={k}><TextField size="small" value={r[k]} onChange={(e) => set({ ncrs: body.ncrs.map((x, j) => j === i ? { ...x, [k]: e.target.value } : x) })} /></TableCell>)}
            <TableCell><IconButton aria-label="Remove NCR" onClick={() => set({ ncrs: body.ncrs.filter((_, j) => j !== i) })}><DeleteOutline /></IconButton></TableCell>
          </TableRow>)}</TableBody></Table>
        {body.ncrs.length === 0 && <Typography>{empty}</Typography>}
        <Button startIcon={<Add />} onClick={() => set({ ncrs: [...body.ncrs, { number: '', partNumber: '', defect: '', disposition: '', status: '' }] })}>Add NCR</Button>
      </Section>

      <Section title="6.7 - Recalls"><TextField value={body.recalls} placeholder={empty} onChange={(e) => set({ recalls: e.target.value })} fullWidth multiline minRows={2} label="Recalls" /></Section>
      <Section title="6.8 - NAPA Tech Line: Collabtic"><TextField value={body.collabtic} placeholder={empty} onChange={(e) => set({ collabtic: e.target.value })} fullWidth multiline minRows={2} label="Collabtic" /></Section>
      <Section title="6.9 - OR/NAPA Email Issues">
        <Chart src={`${S}/monthly-reports/${detail.id}/images/email-issues?v=${detail.rowVersion}`} show={Boolean(body.emailImageKey)} alt="Email issues" />
      </Section>
      <Section title="6.10 - Field question regarding fuel pumps"><TextField value={body.fieldQuestion} placeholder={empty} onChange={(e) => set({ fieldQuestion: e.target.value })} fullWidth multiline minRows={2} label="Field question" /></Section>
      <Section title="6.11 - Fitment Verification - O'Reilly Auto Parts">
        <TextField label="Intro" value={body.fitmentIntro ?? ''} onChange={(e) => set({ fitmentIntro: e.target.value })} fullWidth />
        <Pairs rows={body.fitment} onChange={(fitment) => set({ fitment })} />
        {!body.fitmentIntro && body.fitment.length === 0 && <Typography>{empty}</Typography>}
      </Section>
      <Section title="6.12 - Product Information"><Pairs rows={body.productInfo} onChange={(productInfo) => set({ productInfo })} /></Section>
      <Section title="6.13 – NAPA black label defective fuel pump pallets"><TextField value={body.blackLabelPallets} placeholder={empty} onChange={(e) => set({ blackLabelPallets: e.target.value })} fullWidth multiline minRows={2} label="Pallets" /></Section>
    </Stack>
  </SqfPage>;
}

function headerDate(value?: string | null) {
  if (!value) return '';
  const parsed = new Date(`${value}T00:00:00`);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleDateString('en-US');
}

function HeaderLines({ body }: { body: Body }) {
  const date = headerDate(body.documentDate);
  const id = body.documentId || 'TMP-ENG-001';
  const title = body.documentTitle || 'Monthly Engineering Development Report';
  return <Box>
    <Typography sx={{ fontWeight: 700 }}>{id}: {title}</Typography>
    <Typography>Document ID: {id} | Rev: {body.documentRevision || 'C'} | Title: {title} | Date: {date} | Approved By: {body.approvedByName || ''} |</Typography>
  </Box>;
}
function Section({ title, children }: { title: string; children: ReactNode }) {
  return <Paper variant="outlined" sx={{ p: 2 }}><Typography variant="h5" sx={{ color: '#0F4761', fontWeight: 800, mb: 1.5 }}>{title}</Typography><Stack gap={1.5}>{children}</Stack></Paper>;
}
function Num({ label, value, onChange, note, money: isMoney }: { label: string; value: number | null; onChange: (v: string) => void; note?: ReactNode; money?: boolean }) {
  return <Box sx={{ minWidth: 220 }}><TextField size="small" fullWidth label={label} value={value ?? ''} onChange={(e) => onChange(e.target.value)} />{note}{isMoney && value != null && <Typography variant="caption">{money(value)}</Typography>}</Box>;
}
function Lines({ label, rows, onChange }: { label: string; rows: string[]; onChange: (rows: string[]) => void }) {
  return <Box><Typography variant="body2" sx={{ mb: 0.5 }}>{label}</Typography>
    {rows.map((r, i) => <Stack key={i} direction="row" gap={1} sx={{ mb: 1 }}><TextField fullWidth size="small" value={r} onChange={(e) => onChange(rows.map((x, j) => j === i ? e.target.value : x))} /><IconButton aria-label="Remove line" onClick={() => onChange(rows.filter((_, j) => j !== i))}><DeleteOutline /></IconButton></Stack>)}
    <Button startIcon={<Add />} onClick={() => onChange([...rows, ''])}>Add</Button></Box>;
}
function Notes({ rows, onChange }: { rows: Note[]; onChange: (rows: Note[]) => void }) {
  return <Box>{rows.map((r, i) => <Stack key={i} direction="row" gap={1} sx={{ mb: 1 }}>
    <TextField size="small" label="Label" value={r.label} onChange={(e) => onChange(rows.map((x, j) => j === i ? { ...x, label: e.target.value } : x))} />
    <TextField size="small" fullWidth label="Text" value={r.text} onChange={(e) => onChange(rows.map((x, j) => j === i ? { ...x, text: e.target.value } : x))} />
    <IconButton aria-label="Remove note" onClick={() => onChange(rows.filter((_, j) => j !== i))}><DeleteOutline /></IconButton>
  </Stack>)}<Button startIcon={<Add />} onClick={() => onChange([...rows, { label: '', text: '' }])}>Add</Button></Box>;
}
function Pairs({ rows, onChange }: { rows: Pair[]; onChange: (rows: Pair[]) => void }) {
  return <Box>{rows.map((r, i) => <Stack key={i} gap={1} sx={{ mb: 1 }}>
    <TextField size="small" label="Problem" value={r.problem} onChange={(e) => onChange(rows.map((x, j) => j === i ? { ...x, problem: e.target.value } : x))} fullWidth multiline />
    <TextField size="small" label="Response" value={r.response} onChange={(e) => onChange(rows.map((x, j) => j === i ? { ...x, response: e.target.value } : x))} fullWidth multiline />
    <Button color="inherit" onClick={() => onChange(rows.filter((_, j) => j !== i))}>Remove</Button>
  </Stack>)}<Button startIcon={<Add />} onClick={() => onChange([...rows, { problem: '', response: '' }])}>Add pair</Button></Box>;
}
function Chart({ src, show, alt }: { src: string; show: boolean; alt: string }) {
  if (!show) return <Typography>{empty}</Typography>;
  return <Box component="img" alt={alt} src={src} sx={{ maxWidth: 640, border: '1px solid', borderColor: 'divider' }} />;
}
function TrendTable({ trend }: { trend: Trend[] }) {
  const head = trend.map((t) => t.label);
  return <Stack gap={1}>
    <Mini headers={['Metric', ...head]} rows={[['Total Claims', ...trend.map((t) => t.totalClaims == null ? empty : String(t.totalClaims))], ['Total Product Alerts', ...trend.map((t) => t.productAlerts == null ? empty : String(t.productAlerts))]]} />
    <Mini headers={['Financial Category', ...head]} rows={[['Total Amount Requested', ...trend.map((t) => money(t.totalAmount))], ['Parts Amount Requested', ...trend.map((t) => money(t.partsAmount))], ['Labor Amount Requested', ...trend.map((t) => money(t.laborAmount))]]} />
    <Mini headers={['Warranty Metric', ...head]} rows={[['Labor-to-Parts Ratio', ...trend.map((t) => t.ratio)], ['Mean Time to Failure', ...trend.map((t) => t.mttf)], ['Median Time to Failure', ...trend.map((t) => t.median)]]} />
  </Stack>;
}
function Mini({ headers, rows }: { headers: string[]; rows: string[][] }) {
  return <Table size="small"><TableHead><TableRow>{headers.map((h) => <TableCell key={h}>{h}</TableCell>)}</TableRow></TableHead><TableBody>{rows.map((r) => <TableRow key={r[0]}>{r.map((c, i) => <TableCell key={i}>{c}</TableCell>)}</TableRow>)}</TableBody></Table>;
}
