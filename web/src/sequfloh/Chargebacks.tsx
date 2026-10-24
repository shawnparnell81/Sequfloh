import { useEffect, useState } from 'react';
import { Link as RouterLink, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { Alert, Autocomplete, Box, Button, IconButton, MenuItem, Paper, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post, put } from '../api';
import { fmtDateTime } from '../components/Status';
import { Empty, ErrorState, Loading, Mono, S, SqfPage, qs, useOptions, useSqf } from './common';
import { otherNumbers, usePartSearch } from './partSearch';
import { partNo } from './partLabel';

interface PartOpt { id: string; partNumber: string; name?: string | null; description?: string | null; attributes?: string | null }
interface WoOpt { id: string; number: string; workOrder?: string | null; partNumber?: string | null; workInstructionTitle?: string | null }
interface Line { description: string; amount: string }
interface PartLine { partNumber: string; description: string; quantity: string; unitCost: string }
interface Report {
  id: string; workOrderNumber: string; trackingRecordId?: string | null; trackingNumber?: string | null; supplier?: string | null; supplierReference?: string | null; partId?: string | null;
  partNumber?: string | null; partDescription?: string | null; taskDescription?: string | null; result?: string | null; qtyInspected?: number | null; qtyBad?: number | null;
  totalPersonnel?: number | null; totalHours?: number | null; crewHours?: number | null; userHours?: number | null; dateFrom?: string | null; dateTo?: string | null; hourlyRate?: number | null;
  laborCost?: number | null; discoveryMethod?: string | null; supplierPartNumber?: string | null; lotDateCodes?: string | null; rootCause?: string | null; correctiveAction?: string | null;
  parts: { partNumber: string; description?: string | null; quantity: number; unitCost?: number | null }[]; costLines: { description: string; amount: number }[]; notes?: string | null;
  partsCost: number; otherCost: number; total?: number | null; createdAt: string; createdByName: string;
}
type Form = Record<string, string>;

const crumbs = (label: string) => [{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Reports', to: '/sequfloh/reports/task-performance' }, { label: 'Chargeback Reports', to: '/sequfloh/chargebacks' }, { label }];
const money = (n?: number | null) => (n === null || n === undefined ? '—' : n.toLocaleString('en-US', { style: 'currency', currency: 'USD' }));
const num = (n?: number | null, dp = 0) => (n === null || n === undefined ? '—' : n.toLocaleString('en-US', { minimumFractionDigits: dp, maximumFractionDigits: dp }));
const n = (v?: string) => (v === undefined || v.trim() === '' ? null : Number(v));
const t = (v?: string) => (v === undefined || v.trim() === '' ? null : v.trim());
const useCanEdit = () => !!useSqf().data?.permissions.includes('sequfloh.manage_chargebacks');

export function ChargebackListPage() {
  const [text, setText] = useState('');
  const q = useQuery({ queryKey: ['sqf-cb', text], queryFn: () => get<(Report & { total?: number | null })[]>(`${S}/chargebacks?${qs({ q: text || undefined })}`) });
  const nav = useNavigate();
  const canEdit = useCanEdit();
  return <SqfPage title="Chargeback Reports" crumbs={crumbs('All')} permission="sequfloh.view_reports" description="Supplier chargeback reports for inspection, rebox and repair jobs."
    actions={canEdit && <Button variant="contained" component={RouterLink} to="/sequfloh/chargebacks/new" data-testid="create-chargeback">Create report</Button>}>
    <TextField size="small" label="Search work order, part or supplier" value={text} onChange={(e) => setText(e.target.value)} sx={{ mb: 2, minWidth: 320 }} />
    {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} /> : !q.data?.length ? <Empty text="No chargeback reports yet." /> :
      <Paper variant="outlined"><Table size="small" data-testid="chargeback-list">
        <TableHead><TableRow><TableCell>Work order #</TableCell><TableCell>Supplier</TableCell><TableCell>Part number</TableCell><TableCell>Result</TableCell><TableCell align="right">Qty</TableCell>
          <TableCell align="right">Qty bad</TableCell><TableCell align="right">Personnel</TableCell><TableCell align="right">Hours</TableCell><TableCell align="right">Total</TableCell><TableCell>Created</TableCell></TableRow></TableHead>
        <TableBody>{q.data.map((r) => <TableRow key={r.id} hover sx={{ cursor: 'pointer' }} onClick={() => nav(`/sequfloh/chargebacks/${r.id}`)}>
          <TableCell><Mono>{r.workOrderNumber}</Mono></TableCell><TableCell>{r.supplier ?? '—'}</TableCell><TableCell><Mono>{r.partNumber ?? '—'}</Mono></TableCell><TableCell>{r.result ?? '—'}</TableCell>
          <TableCell align="right">{num(r.qtyInspected)}</TableCell><TableCell align="right">{num(r.qtyBad)}</TableCell><TableCell align="right">{num(r.totalPersonnel)}</TableCell>
          <TableCell align="right">{num(r.totalHours, 2)}</TableCell><TableCell align="right">{money(r.total)}</TableCell><TableCell>{fmtDateTime(r.createdAt)} · {r.createdByName}</TableCell>
        </TableRow>)}</TableBody>
      </Table></Paper>}
  </SqfPage>;
}

const Section = ({ title, children }: { title: string; children: React.ReactNode }) =>
  <Paper variant="outlined" sx={{ p: 2 }}><Typography variant="h3" sx={{ mb: 1.5 }}>{title}</Typography><Stack gap={2}>{children}</Stack></Paper>;
const row = (...c: React.ReactNode[]) => <Stack direction={{ xs: 'column', md: 'row' }} gap={2}>{c}</Stack>;

const toForm = (r?: Partial<Report> | null): Form => {
  const f: Form = {};
  if (!r) return f;
  for (const [k, v] of Object.entries(r)) if (v !== null && v !== undefined && typeof v !== 'object') f[k] = String(v);
  return f;
};

export function ChargebackEditPage() {
  const { id } = useParams();
  const [sp] = useSearchParams();
  const nav = useNavigate();
  const qc = useQueryClient();
  const isNew = !id || id === 'new';
  const existing = useQuery({ queryKey: ['sqf-cb-one', id], enabled: !isNew, queryFn: () => get<Report>(`${S}/chargebacks/${id}`) });
  const [f, setF] = useState<Form>({ workOrderNumber: '' });
  const [parts, setParts] = useState<PartLine[]>([]);
  const [lines, setLines] = useState<Line[]>([]);
  const [wo, setWo] = useState<WoOpt | null>(null);
  const [woText, setWoText] = useState('');
  const [partText, setPartText] = useState('');
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const results = useOptions('validation-results');
  const methods = useOptions('discovery-methods');
  const partSearch = usePartSearch<PartOpt>(partText);
  const wos = useQuery({ queryKey: ['sqf-cb-wo', woText], queryFn: () => get<{ items: WoOpt[] }>(`${S}/tracking?${qs({ q: woText || undefined, pageSize: 20, sort: 'createdAt:desc' })}`) });
  const set = (p: Form) => setF((o) => ({ ...o, ...p }));
  const prefill = async (trackingId: string) => {
    try {
      const p = await get<Record<string, unknown>>(`${S}/chargebacks/prefill/${trackingId}`);
      const keep = (k: string) => (f[k] && f[k].trim() !== '' ? {} : p[k] !== null && p[k] !== undefined ? { [k]: String(p[k]) } : {});
      const next: Form = { trackingRecordId: trackingId, crewHours: String(p.crewHours ?? ''), userHours: String(p.userHours ?? ''), totalHours: String(p.totalHours ?? ''), totalPersonnel: String(p.totalPersonnel ?? '') };
      for (const k of ['workOrderNumber', 'partId', 'partNumber', 'partDescription', 'result', 'qtyInspected', 'qtyBad', 'supplier', 'supplierReference', 'discoveryMethod', 'supplierPartNumber', 'lotDateCodes', 'rootCause', 'taskDescription', 'dateFrom', 'dateTo'])
        Object.assign(next, keep(k));
      set(next);
      if (next.partNumber) setPartText(next.partNumber);
      setWo({ id: trackingId, number: String(p.trackingNumber), workOrder: (p.workOrderNumber as string) ?? null });
    } catch (e) { setErr((e as Error).message); }
  };
  useEffect(() => { const w = sp.get('wo'); if (isNew && w) void prefill(w); }, []); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    const r = existing.data; if (!r) return;
    setF(toForm(r)); setPartText(r.partNumber ?? '');
    setParts(r.parts.map((p) => ({ partNumber: p.partNumber, description: p.description ?? '', quantity: String(p.quantity), unitCost: p.unitCost === null || p.unitCost === undefined ? '' : String(p.unitCost) })));
    setLines(r.costLines.map((l) => ({ description: l.description, amount: String(l.amount) })));
    if (r.trackingRecordId) setWo({ id: r.trackingRecordId, number: r.trackingNumber ?? '' });
  }, [existing.data]);
  const labor = n(f.totalHours) !== null && n(f.hourlyRate) !== null ? Math.round(n(f.totalHours)! * n(f.hourlyRate)! * 100) / 100 : null;
  const save = async () => {
    setBusy(true); setErr(null);
    const body = {
      workOrderNumber: f.workOrderNumber ?? '', trackingRecordId: wo?.id ?? null, supplier: t(f.supplier), supplierReference: t(f.supplierReference), partId: f.partId || null, partNumber: t(f.partNumber),
      partDescription: t(f.partDescription), taskDescription: t(f.taskDescription), result: t(f.result), qtyInspected: n(f.qtyInspected), qtyBad: n(f.qtyBad), totalPersonnel: n(f.totalPersonnel),
      totalHours: n(f.totalHours), dateFrom: t(f.dateFrom), dateTo: t(f.dateTo), hourlyRate: n(f.hourlyRate), discoveryMethod: t(f.discoveryMethod), supplierPartNumber: t(f.supplierPartNumber),
      lotDateCodes: t(f.lotDateCodes), rootCause: t(f.rootCause), correctiveAction: t(f.correctiveAction), notes: t(f.notes),
      parts: parts.filter((p) => p.partNumber.trim()).map((p) => ({ partNumber: p.partNumber, description: p.description || null, quantity: Number(p.quantity || 0), unitCost: n(p.unitCost) })),
      costLines: lines.filter((l) => l.description.trim()).map((l) => ({ description: l.description, amount: Number(l.amount || 0) })),
    };
    try {
      const r = isNew ? await post<Report>(`${S}/chargebacks`, body) : await put<Report>(`${S}/chargebacks/${id}`, body);
      await qc.invalidateQueries();
      nav(`/sequfloh/chargebacks/${r.id}/view`);
    } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  if (!isNew && existing.isLoading) return <Loading />;
  const fld = (k: string, label: string, extra: Record<string, unknown> = {}) =>
    <TextField label={label} value={f[k] ?? ''} onChange={(e) => set({ [k]: e.target.value })} fullWidth {...extra} />;
  return <SqfPage title={isNew ? 'Create chargeback report' : `Edit chargeback report ${f.workOrderNumber ?? ''}`} crumbs={crumbs(isNew ? 'Create' : 'Edit')} permission="sequfloh.manage_chargebacks"
    actions={<Stack direction="row" gap={1}><Button onClick={() => nav(-1)}>Cancel</Button><Button variant="contained" disabled={busy} onClick={save} data-testid="save-chargeback">Save report</Button></Stack>}>
    <Stack gap={2} sx={{ maxWidth: 1100 }} data-testid="chargeback-form">
      {err && <Alert severity="error" onClose={() => setErr(null)}>{err}</Alert>}
      <Section title="Job">
        {row(
          <TextField key="wo" label="Work order #" required value={f.workOrderNumber ?? ''} onChange={(e) => set({ workOrderNumber: e.target.value })} fullWidth helperText="Your own work order number" inputProps={{ 'data-testid': 'cb-wo' }} />,
          <Autocomplete key="link" fullWidth options={wos.data?.items ?? []} value={wo} filterOptions={(x) => x} isOptionEqualToValue={(a, b) => a.id === b.id}
            getOptionLabel={(o) => `${o.number}${o.workOrder ? ` · ${o.workOrder}` : ''}${o.partNumber ? ` · ${o.partNumber}` : ''}`}
            onInputChange={(_, v, r) => r === 'input' && setWoText(v)}
            onChange={(_, o) => { setWo(o); if (o) void prefill(o.id); else set({ trackingRecordId: '', crewHours: '', userHours: '' }); }}
            renderInput={(p) => <TextField {...p} label="Linked work order in the app (optional)" helperText="Fills hours, personnel and dates from its crew and clocked time" />} />,
        )}
        {row(fld('supplier', 'Supplier'), fld('supplierReference', 'Supplier reference / PO'))}
        {row(
          <Autocomplete key="pn" freeSolo fullWidth options={partSearch.items} inputValue={partText} filterOptions={(x) => x}
            getOptionLabel={(o) => (typeof o === 'string' ? o : o.partNumber)}
            renderOption={(props, o) => <li {...props} key={o.id}><Box><Mono>{partNo(o)}</Mono><Typography variant="caption" display="block" color="text.secondary">{o.name ?? o.description}{otherNumbers(o) ? ` · ${otherNumbers(o)}` : ''}</Typography></Box></li>}
            onInputChange={(_, v, reason) => { setPartText(v); if (reason === 'input') set({ partNumber: v, partId: '' }); }}
            onChange={(_, o) => { if (o && typeof o !== 'string') set({ partNumber: o.partNumber, partId: o.id, partDescription: o.name ?? o.description ?? f.partDescription ?? '' }); }}
            renderInput={(p) => <TextField {...p} label="Part number" helperText={partSearch.hint ?? 'Search any OR, NAPA, DMA or JH number, or type one'} />} />,
          fld('partDescription', 'Part description'),
        )}
        {row(fld('supplierPartNumber', 'Supplier part number'), fld('lotDateCodes', 'Lot / date codes'))}
        {row(fld('dateFrom', 'Date from', { type: 'date', InputLabelProps: { shrink: true } }), fld('dateTo', 'Date to', { type: 'date', InputLabelProps: { shrink: true } }))}
      </Section>
      <Section title="Task and result">
        {fld('taskDescription', 'Description of the inspection / rebox task', { multiline: true, minRows: 2 })}
        {row(
          <TextField key="dm" select fullWidth label="Discovery method" value={f.discoveryMethod ?? ''} onChange={(e) => set({ discoveryMethod: e.target.value })}>
            <MenuItem value="">—</MenuItem>{(methods.data ?? []).map((x) => <MenuItem key={x} value={x}>{x}</MenuItem>)}</TextField>,
          <TextField key="res" select fullWidth label="Result" value={f.result ?? ''} onChange={(e) => set({ result: e.target.value })} inputProps={{ 'data-testid': 'cb-result' }}>
            <MenuItem value="">—</MenuItem>{(results.data ?? []).map((x) => <MenuItem key={x} value={x}>{x}</MenuItem>)}</TextField>,
        )}
        {row(fld('qtyInspected', 'Qty inspected / reworked', { type: 'number' }), fld('qtyBad', 'Qty bad', { type: 'number' }))}
      </Section>
      <Section title="Labor">
        {f.crewHours !== undefined && f.crewHours !== '' && <Alert severity="info" data-testid="cb-hours-from-wo">
          From the linked work order: crew {num(Number(f.crewHours), 2)} h + registered people {num(Number(f.userHours || 0), 2)} h = {num(Number(f.crewHours) + Number(f.userHours || 0), 2)} h. You can change the totals below.</Alert>}
        {row(fld('totalPersonnel', 'Total personnel on the task', { type: 'number' }), fld('totalHours', 'Total hours worked', { type: 'number' }),
          fld('hourlyRate', 'Hourly rate (optional)', { type: 'number' }),
          <TextField key="lc" label="Labor cost" value={labor === null ? '' : money(labor)} fullWidth InputProps={{ readOnly: true }} />)}
      </Section>
      <Section title="Parts replaced (optional)">
        {parts.map((p, i) => <Stack key={i} direction="row" gap={1}>
          <TextField size="small" label="Part number" value={p.partNumber} onChange={(e) => setParts(parts.map((x, j) => (j === i ? { ...x, partNumber: e.target.value } : x)))} />
          <TextField size="small" label="Description" value={p.description} onChange={(e) => setParts(parts.map((x, j) => (j === i ? { ...x, description: e.target.value } : x)))} sx={{ flex: 1 }} />
          <TextField size="small" type="number" label="Quantity" value={p.quantity} onChange={(e) => setParts(parts.map((x, j) => (j === i ? { ...x, quantity: e.target.value } : x)))} sx={{ width: 110 }} />
          <TextField size="small" type="number" label="Unit cost" value={p.unitCost} onChange={(e) => setParts(parts.map((x, j) => (j === i ? { ...x, unitCost: e.target.value } : x)))} sx={{ width: 120 }} />
          <IconButton aria-label="Remove part" onClick={() => setParts(parts.filter((_, j) => j !== i))}><DeleteOutline /></IconButton></Stack>)}
        <Box><Button size="small" onClick={() => setParts([...parts, { partNumber: '', description: '', quantity: '', unitCost: '' }])}>Add part</Button></Box>
      </Section>
      <Section title="Other costs (optional)">
        {lines.map((l, i) => <Stack key={i} direction="row" gap={1}>
          <TextField size="small" label="Description" value={l.description} onChange={(e) => setLines(lines.map((x, j) => (j === i ? { ...x, description: e.target.value } : x)))} sx={{ flex: 1 }} />
          <TextField size="small" type="number" label="Amount" value={l.amount} onChange={(e) => setLines(lines.map((x, j) => (j === i ? { ...x, amount: e.target.value } : x)))} sx={{ width: 140 }} />
          <IconButton aria-label="Remove cost" onClick={() => setLines(lines.filter((_, j) => j !== i))}><DeleteOutline /></IconButton></Stack>)}
        <Box><Button size="small" onClick={() => setLines([...lines, { description: '', amount: '' }])}>Add cost line</Button></Box>
      </Section>
      <Section title="Findings and notes">
        {fld('rootCause', 'Supplier root cause', { multiline: true, minRows: 2 })}
        {fld('correctiveAction', 'Corrective action requested', { multiline: true, minRows: 2 })}
        {fld('notes', 'Notes', { multiline: true, minRows: 2 })}
      </Section>
    </Stack>
  </SqfPage>;
}

function Facts({ items }: { items: [string, React.ReactNode][] }) {
  const shown = items.filter(([, v]) => v !== null && v !== undefined && v !== '' && v !== '—');
  if (!shown.length) return null;
  return <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '240px 1fr' }, rowGap: 0.75, columnGap: 2 }}>
    {shown.map(([k, v]) => [<Typography key={`${k}-k`} color="text.secondary">{k}</Typography>, <Typography key={`${k}-v`} sx={{ fontWeight: 500, whiteSpace: 'pre-wrap' }}>{v}</Typography>])}
  </Box>;
}

export function ChargebackViewPage() {
  const { id } = useParams();
  const nav = useNavigate();
  const canEdit = useCanEdit();
  const q = useQuery({ queryKey: ['sqf-cb-one', id], queryFn: () => get<Report>(`${S}/chargebacks/${id}`) });
  if (q.isLoading) return <Loading />;
  if (q.error || !q.data) return <ErrorState error={q.error} />;
  const r = q.data;
  const good = r.qtyInspected !== null && r.qtyInspected !== undefined && r.qtyBad !== null && r.qtyBad !== undefined ? r.qtyInspected - r.qtyBad : null;
  const sec = (title: string, body: React.ReactNode) => <Paper variant="outlined" sx={{ p: 2 }}><Typography variant="h3" sx={{ mb: 1.5 }}>{title}</Typography>{body}</Paper>;
  return <SqfPage title={`Chargeback report ${r.workOrderNumber}`} crumbs={crumbs(r.workOrderNumber)} permission="sequfloh.view_reports"
    actions={<Stack direction="row" gap={1}>
      <Button variant="contained" href={`${S}/chargebacks/${r.id}/pdf`} target="_blank" rel="noopener" data-testid="cb-pdf">Print / PDF</Button>
      <Button variant="outlined" href={`${S}/chargebacks/${r.id}/xlsx`} data-testid="cb-xlsx">Export Excel</Button>
      {canEdit && <Button variant="outlined" component={RouterLink} to={`/sequfloh/chargebacks/${r.id}`}>Edit</Button>}
      {canEdit && <Button color="error" onClick={async () => { if (confirm('Delete this chargeback report?')) { await del(`${S}/chargebacks/${r.id}`); nav('/sequfloh/chargebacks'); } }}>Delete</Button>}
    </Stack>}>
    <Stack gap={2} sx={{ maxWidth: 1000 }} data-testid="chargeback-view">
      <Stack direction="row" gap={1.5} flexWrap="wrap">
        {[['Qty inspected / reworked', num(r.qtyInspected)], ['Qty bad', num(r.qtyBad)], ['Personnel', num(r.totalPersonnel)], ['Total hours', num(r.totalHours, 2)], ['Total chargeback', money(r.total)]].map(([k, v]) =>
          <Paper key={k} variant="outlined" sx={{ p: 1.5, minWidth: 160 }}><Typography variant="caption" color="text.secondary">{k}</Typography><Typography variant="h2" sx={{ fontVariantNumeric: 'tabular-nums' }}>{v}</Typography></Paper>)}
      </Stack>
      {sec('Chargeback', <Facts items={[['Supplier', r.supplier], ['Supplier reference / PO', r.supplierReference], ['Created', `${fmtDateTime(r.createdAt)} by ${r.createdByName}`]]} />)}
      {sec('Job', <Facts items={[['Work order #', <Mono>{r.workOrderNumber}</Mono>], ['Linked work order', r.trackingRecordId ? <RouterLink to={`/sequfloh/record/${r.trackingRecordId}`}>{r.trackingNumber}</RouterLink> : null],
        ['Part number', r.partNumber ? <Mono>{r.partNumber}</Mono> : null], ['Description', r.partDescription], ['Supplier part number', r.supplierPartNumber], ['Lot / date codes', r.lotDateCodes],
        ['Dates', r.dateFrom || r.dateTo ? `${r.dateFrom ?? ''} to ${r.dateTo ?? ''}` : null]]} />)}
      {sec('Task and result', <Facts items={[['Description of task', r.taskDescription], ['Discovery method', r.discoveryMethod], ['Result', r.result],
        ['Qty inspected / reworked', num(r.qtyInspected)], ['Qty bad', num(r.qtyBad)], ['Qty good', num(good)]]} />)}
      {sec('Labor', <Facts items={[['Total personnel on the task', num(r.totalPersonnel)], ['Crew labor hours', r.crewHours === null || r.crewHours === undefined ? null : num(r.crewHours, 2)],
        ['Registered people hours', r.userHours === null || r.userHours === undefined ? null : num(r.userHours, 2)], ['Total hours worked', num(r.totalHours, 2)],
        ['Hourly rate', r.hourlyRate === null || r.hourlyRate === undefined ? null : money(r.hourlyRate)], ['Labor cost', r.laborCost === null || r.laborCost === undefined ? null : money(r.laborCost)]]} />)}
      {r.parts.length > 0 && sec('Parts replaced', <Table size="small"><TableHead><TableRow><TableCell>Part number</TableCell><TableCell>Description</TableCell><TableCell align="right">Quantity</TableCell><TableCell align="right">Unit cost</TableCell></TableRow></TableHead>
        <TableBody>{r.parts.map((p, i) => <TableRow key={i}><TableCell><Mono>{p.partNumber}</Mono></TableCell><TableCell>{p.description}</TableCell><TableCell align="right">{num(p.quantity, 0)}</TableCell><TableCell align="right">{money(p.unitCost)}</TableCell></TableRow>)}</TableBody></Table>)}
      {sec('Chargeback summary', <Facts items={[['Labor', money(r.laborCost)], ['Parts', r.partsCost ? money(r.partsCost) : null], ...r.costLines.map((l) => [l.description, money(l.amount)] as [string, string]),
        ['Total chargeback', <b>{money(r.total)}</b>]]} />)}
      {(r.rootCause || r.correctiveAction || r.notes) && sec('Findings and notes', <Facts items={[['Supplier root cause', r.rootCause], ['Corrective action requested', r.correctiveAction], ['Notes', r.notes]]} />)}
    </Stack>
  </SqfPage>;
}
