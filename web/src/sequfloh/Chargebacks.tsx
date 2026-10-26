import { useEffect, useState } from 'react';
import { Link as RouterLink, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { Alert, Autocomplete, Box, Button, Checkbox, FormControlLabel, IconButton, MenuItem, Paper, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put } from '../api';
import { fmtDateTime } from '../components/Status';
import { Empty, ErrorState, Loading, Mono, S, SqfPage, qs, useOptions, useSqf, useUnsaved } from './common';
import { RecordActions } from '../components/RecordActions';
import { otherNumbers, usePartSearch } from './partSearch';
import { partNo } from './partLabel';
import { useFamilies } from './Imports';
import { useSites } from '../components/Kit';
import { useProjects } from './Projects';

interface Period { kind: string; from?: string | null; to?: string | null; person?: string | null; type?: string | null; subType?: string | null; familyId?: string | null; siteId?: string | null; projectId?: string | null }
const WO_TYPES: [string, string][] = [['Validation', 'Validation / FAI'], ['Repair', 'Repair / Rework'], ['Inspection', 'Inspection'], ['Warranty', 'Warranty'], ['SaltSpray', 'Salt Spray Test'], ['Development', 'Development'], ['Rebox', 'Rebox'], ['Test', 'Test'], ['Production', 'Production']];

interface PartOpt { id: string; partNumber: string; name?: string | null; description?: string | null; attributes?: string | null }
interface WoOpt { id: string; number: string; workOrder?: string | null; partNumber?: string | null; workInstructionTitle?: string | null }
interface Line { description: string; amount: string }
interface PartLine { partNumber: string; description: string; quantity: string; unitCost: string }
interface Report { locked?: boolean;
  id: string; workOrderNumber: string; trackingRecordId?: string | null; trackingNumber?: string | null; supplier?: string | null; supplierReference?: string | null; partId?: string | null;
  partNumber?: string | null; partDescription?: string | null; taskDescription?: string | null; result?: string | null; qtyInspected?: number | null; qtyBad?: number | null;
  totalPersonnel?: number | null; totalHours?: number | null; crewHours?: number | null; userHours?: number | null; dateFrom?: string | null; dateTo?: string | null; hourlyRate?: number | null;
  laborCost?: number | null; discoveryMethod?: string | null; supplierPartNumber?: string | null; lotDateCodes?: string | null; rootCause?: string | null; correctiveAction?: string | null;
  parts: { partNumber: string; description?: string | null; quantity: number; unitCost?: number | null }[]; costLines: { description: string; amount: number }[]; notes?: string | null;
  sections: string[]; period?: Period | null; partsCost: number; otherCost: number; total?: number | null; createdAt: string; createdByName: string;
}
type Form = Record<string, string>;
interface SectionOpt { key: string; label: string; default: boolean }
interface Block { key: string; heading: string; fields?: { label: string; value: string }[] | null; columns?: string[] | null; rows?: (string | null)[][] | null; photos?: string[] | null }
const useSectionOptions = () => useQuery({ queryKey: ['sqf-cb-sections'], queryFn: () => get<SectionOpt[]>(`${S}/chargebacks/sections`), staleTime: Infinity });

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
          <TableCell align="right">Qty bad</TableCell><TableCell align="right">Personnel</TableCell><TableCell align="right">Hours</TableCell><TableCell align="right">Total</TableCell><TableCell>Created</TableCell><TableCell /></TableRow></TableHead>
        <TableBody>{q.data.map((r) => <TableRow key={r.id} hover sx={{ cursor: 'pointer' }} onClick={() => openChargebackPdf(r.id)}>
          <TableCell><Mono>{r.workOrderNumber}</Mono></TableCell><TableCell>{r.supplier ?? '—'}</TableCell><TableCell><Mono>{r.partNumber ?? '—'}</Mono></TableCell><TableCell>{r.result ?? '—'}</TableCell>
          <TableCell align="right">{num(r.qtyInspected)}</TableCell><TableCell align="right">{num(r.qtyBad)}</TableCell><TableCell align="right">{num(r.totalPersonnel)}</TableCell>
          <TableCell align="right">{num(r.totalHours, 2)}</TableCell><TableCell align="right">{money(r.total)}</TableCell><TableCell>{fmtDateTime(r.createdAt)} · {r.createdByName}</TableCell>
          <TableCell onClick={(e) => e.stopPropagation()}>{canEdit && !r.locked && <Button size="small" onClick={() => nav(`/sequfloh/chargebacks/${r.id}`)} data-testid="cb-row-edit">Edit</Button>}</TableCell>
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
  const [hydrated, setHydrated] = useState(false);
  const [sections, setSections] = useState<string[] | null>(null);
  const [period, setPeriod] = useState<Period | null>(() => sp.get('project') ? { kind: 'Project', projectId: sp.get('project') } : null);
  const projects = useProjects();
  const families = useFamilies(); const subTypes = useOptions('inspection-sub-types');
  // the person list follows the linked work order, or the period with its filters
  const peopleArgs = { trackingRecordId: wo?.id, projectId: period?.kind === 'Project' ? period.projectId || undefined : undefined, kind: period?.kind === 'Project' ? (period.projectId ? 'Project' : undefined) : period?.kind && period.from ? period.kind : undefined, from: period?.from || undefined,
    to: period?.kind === 'Custom' ? period.to || undefined : undefined, type: period?.type || undefined, subType: period?.subType || undefined, familyId: period?.familyId || undefined, siteId: period?.siteId || undefined };
  const sites = useSites();
  const peopleReady = !!peopleArgs.trackingRecordId || (!!peopleArgs.kind && (period?.kind !== 'Custom' || !!peopleArgs.to));
  const people = useQuery({ queryKey: ['sqf-cb-people', peopleArgs], queryFn: () => get<string[]>(`${S}/chargebacks/people?${qs(peopleArgs)}`), enabled: peopleReady, staleTime: 15_000 });
  const peopleList = peopleReady ? people.data : [];
  useEffect(() => {
    const cur = period?.person; if (!cur || !peopleList || (peopleReady && people.isFetching)) return;
    if (!peopleList.some((n) => n.toLowerCase() === cur.toLowerCase())) setPeriod((p) => (p ? { ...p, person: null } : p));
  }, [peopleList, people.isFetching]); // eslint-disable-line react-hooks/exhaustive-deps
  const setP = (v: Partial<Period>) => setPeriod({ ...(period ?? { kind: 'Day' }), ...v });
  const sectionOpts = useSectionOptions();
  const picked = sections ?? (sectionOpts.data ?? []).filter((o) => o.default).map((o) => o.key);
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
    setSections(r.sections); setPeriod(r.period ?? null); setBase(null); setHydrated(true);
  }, [existing.data]);
  const snap = JSON.stringify([f, parts, lines, wo?.id ?? null, sections, period]);
  const [base, setBase] = useState<string | null>(null);
  useEffect(() => { if (base === null && (isNew || hydrated)) setBase(snap); }, [snap, base, isNew, hydrated]);
  const dirty = base !== null && snap !== base;
  useUnsaved(dirty);
  const labor = n(f.totalHours) !== null && n(f.hourlyRate) !== null ? Math.round(n(f.totalHours)! * n(f.hourlyRate)! * 100) / 100 : null;
  const save = async () => {
    setBusy(true); setErr(null);
    const body = {
      workOrderNumber: f.workOrderNumber ?? '', trackingRecordId: wo?.id ?? null, supplier: t(f.supplier), supplierReference: t(f.supplierReference), partId: f.partId || null, partNumber: t(f.partNumber),
      partDescription: t(f.partDescription), taskDescription: t(f.taskDescription), result: t(f.result), qtyInspected: n(f.qtyInspected), qtyBad: n(f.qtyBad), totalPersonnel: n(f.totalPersonnel),
      totalHours: n(f.totalHours), dateFrom: t(f.dateFrom), dateTo: t(f.dateTo), hourlyRate: n(f.hourlyRate), discoveryMethod: t(f.discoveryMethod), supplierPartNumber: t(f.supplierPartNumber),
      lotDateCodes: t(f.lotDateCodes), rootCause: t(f.rootCause), correctiveAction: t(f.correctiveAction), notes: t(f.notes), sections: picked,
      period: period && period.kind ? { kind: period.kind, from: period.from || null, to: period.kind === 'Custom' ? period.to || null : null, person: period.person || null, type: period.type || null, subType: period.subType || null, familyId: period.familyId || null, siteId: period.siteId || null, projectId: period.kind === 'Project' ? period.projectId || null : null } : null,
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
    actions={<RecordActions surface="chargeback-report-edit" dirty={dirty} busy={busy} cancel={{ onClick: () => nav(-1), always: true }}>
      <Button variant="contained" disabled={busy || !dirty} onClick={() => void save()} data-action="save" data-testid="save-chargeback">Save</Button>
    </RecordActions>}>
    <Stack gap={2} sx={{ maxWidth: 1100 }} data-testid="chargeback-form">
      {err && <Alert severity="error" onClose={() => setErr(null)}>{err}</Alert>}
      <Section title="Job">
        {row(
          <TextField key="wo" label={period ? 'Report number' : 'Work order #'} required={period?.kind !== 'Project'} value={f.workOrderNumber ?? ''} onChange={(e) => set({ workOrderNumber: e.target.value })} fullWidth helperText={period ? 'Your own number for this report, exactly as your company writes it' : 'Your own work order number'} inputProps={{ 'data-testid': 'cb-wo' }} />,
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
            renderOption={(props, o) => <li {...props} key={o.id}><Box><Mono>{partNo(o)}</Mono><Typography variant="caption" display="block" color="text.secondary">{o.description || o.name}{otherNumbers(o) ? ` · ${otherNumbers(o)}` : ''}</Typography></Box></li>}
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
      <Section title="Period">
        <Typography variant="body2" color="text.secondary">Choose a period to add the period sections below (hours, counts, results, pauses and the work order detail). Leave it off for a report on one work order.</Typography>
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '1fr 1fr 1fr' }, gap: 2 }} data-testid="cb-period">
          <TextField select label="Period" value={period?.kind ?? ''} onChange={(e) => setPeriod(e.target.value ? { ...(period ?? {}), kind: e.target.value } : null)} inputProps={{ 'data-testid': 'cb-period-kind' }}>
            <MenuItem value="">No period</MenuItem>
            <MenuItem value="Day">Single day</MenuItem><MenuItem value="Week">Week</MenuItem><MenuItem value="Month">Month</MenuItem><MenuItem value="Custom">Custom range</MenuItem><MenuItem value="Project">Project</MenuItem>
          </TextField>
          {period?.kind === 'Project' && <TextField select label="Project" value={period.projectId ?? ''} onChange={(e) => setP({ projectId: e.target.value })} helperText="Totals every work order and inspection in the project" inputProps={{ 'data-testid': 'cb-period-project' }}>
            {(projects.data ?? []).map((x) => <MenuItem key={x.id} value={x.id}>{x.number} {x.name}{x.status === 'Closed' ? ' (closed)' : ''}</MenuItem>)}</TextField>}
          {period && period.kind !== 'Project' && <TextField type="date" label={period.kind === 'Custom' ? 'From' : period.kind === 'Day' ? 'Day' : `A day in the ${period.kind.toLowerCase()}`} InputLabelProps={{ shrink: true }}
            value={period.from ?? ''} onChange={(e) => setP({ from: e.target.value })} inputProps={{ 'data-testid': 'cb-period-from' }} />}
          {period?.kind === 'Custom' && <TextField type="date" label="To" InputLabelProps={{ shrink: true }} value={period.to ?? ''} onChange={(e) => setP({ to: e.target.value })} inputProps={{ 'data-testid': 'cb-period-to' }} />}
        </Box>
        {period && <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '1fr 1fr 1fr' }, gap: 2 }} data-testid="cb-filters">
          <TextField select label="Site (optional)" SelectProps={{ displayEmpty: true }} InputLabelProps={{ shrink: true }} value={period.siteId ?? ''} onChange={(e) => setP({ siteId: e.target.value })} inputProps={{ 'data-testid': 'cb-filter-site' }}>
            <MenuItem value="">All sites</MenuItem>{(sites.data ?? []).map((x) => <MenuItem key={x.id} value={x.id}>{x.name}</MenuItem>)}
          </TextField>
          <TextField select label="Person (optional)" SelectProps={{ displayEmpty: true }} InputLabelProps={{ shrink: true }} value={period.person ?? ''} onChange={(e) => setP({ person: e.target.value })} helperText="People with recorded time on the linked work order or in the period, including day workers" inputProps={{ 'data-testid': 'cb-filter-person' }}>
            <MenuItem value="">All people</MenuItem>{[...(peopleList ?? []), ...(period.person && !(peopleList ?? []).some((n) => n.toLowerCase() === period.person!.toLowerCase()) ? [period.person] : [])].map((x) => <MenuItem key={x} value={x}>{x}</MenuItem>)}
          </TextField>
          <TextField select label="Type (optional)" value={period.type ?? ''} onChange={(e) => setP({ type: e.target.value })} inputProps={{ 'data-testid': 'cb-filter-type' }}>
            <MenuItem value="">All types</MenuItem>{WO_TYPES.map(([x, l]) => <MenuItem key={x} value={x}>{l}</MenuItem>)}
          </TextField>
          <TextField select label="Sub-type (optional)" value={period.subType ?? ''} onChange={(e) => setP({ subType: e.target.value })} inputProps={{ 'data-testid': 'cb-filter-subtype' }}>
            <MenuItem value="">All sub-types</MenuItem>{(subTypes.data ?? []).map((x) => <MenuItem key={x} value={x}>{x}</MenuItem>)}
          </TextField>
          <TextField select label="Product family (optional)" value={period.familyId ?? ''} onChange={(e) => setP({ familyId: e.target.value })} inputProps={{ 'data-testid': 'cb-filter-family' }}>
            <MenuItem value="">All families</MenuItem>{(families.data ?? []).map((x) => <MenuItem key={x.id} value={x.id}>{x.label}</MenuItem>)}
          </TextField>
        </Box>}
      </Section>
      <Section title="What goes in the report">
        <Typography variant="body2" color="text.secondary">Tick what appears in the PDF, the Excel file and on screen. Sections with nothing in them are left out.</Typography>
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' }, columnGap: 2 }} data-testid="cb-sections">
          {(sectionOpts.data ?? []).map((o) => <FormControlLabel key={o.key} label={o.label}
            control={<Checkbox checked={picked.includes(o.key)} inputProps={{ 'data-testid': `section-${o.key}` } as React.InputHTMLAttributes<HTMLInputElement>}
              onChange={(e) => setSections(e.target.checked ? [...picked, o.key] : picked.filter((k) => k !== o.key))} />} />)}
        </Box>
        <Stack direction="row" gap={1}>
          <Button size="small" onClick={() => setSections((sectionOpts.data ?? []).map((o) => o.key))}>Tick all</Button>
          <Button size="small" onClick={() => setSections((sectionOpts.data ?? []).filter((o) => o.default).map((o) => o.key))}>Defaults</Button>
          <Button size="small" onClick={() => setSections([])}>Untick all</Button>
        </Stack>
      </Section>
      <Section title="Findings and notes">
        {fld('rootCause', 'Supplier root cause', { multiline: true, minRows: 2 })}
        {fld('correctiveAction', 'Corrective action requested', { multiline: true, minRows: 2 })}
        {fld('notes', 'Notes', { multiline: true, minRows: 2 })}
      </Section>
    </Stack>
  </SqfPage>;
}

export function ChargebackViewPage() {
  const { id } = useParams();
  const nav = useNavigate();
  const canEdit = useCanEdit();
  const q = useQuery({ queryKey: ['sqf-cb-one', id], queryFn: () => get<Report>(`${S}/chargebacks/${id}`) });
  if (q.isLoading) return <Loading />;
  if (q.error || !q.data) return <ErrorState error={q.error} />;
  const r = q.data;
  return <SqfPage title={`Chargeback report ${r.workOrderNumber}`} crumbs={crumbs(r.workOrderNumber)} permission="sequfloh.view_reports"
    actions={<RecordActions surface="chargeback-report" edit={canEdit && !r.locked && { onClick: () => nav(`/sequfloh/chargebacks/${r.id}`) }}
      print={{ href: `${S}/chargebacks/${r.id}/pdf`, testid: 'cb-pdf' }}
      del={{ what: `chargeback report for work order ${r.workOrderNumber}`, url: `${S}/chargebacks/${r.id}`, to: '/sequfloh/chargebacks', perm: 'sequfloh.manage_chargebacks', show: canEdit && !r.locked }}>
      <Button variant="outlined" href={`${S}/chargebacks/${r.id}/xlsx`} data-testid="cb-xlsx">Export Excel</Button>
    </RecordActions>}>
    <ReportBlocks id={r.id} title={`Chargeback Report: ${r.partNumber ?? r.workOrderNumber}`} supplier={r.supplier} />
  </SqfPage>;
}

/** Opens the saved chargeback report's PDF in a new browser tab. */
export const openChargebackPdf = (id: string) => window.open(`${S}/chargebacks/${id}/pdf`, '_blank', 'noopener');

/** The saved report on screen, in the same order and look as its PDF: blue section bars, label / value rows and tables. */
function ReportBlocks({ id, title, supplier }: { id: string; title: string; supplier?: string | null }) {
  const q = useQuery({ queryKey: ['sqf-cb-view', id], queryFn: () => get<Block[]>(`${S}/chargebacks/${id}/view`) });
  if (q.isLoading) return <Loading />;
  if (q.error || !q.data) return <ErrorState error={q.error} />;
  return <Paper variant="outlined" sx={{ p: 3, maxWidth: 900 }} data-testid="chargeback-view">
    <Typography variant="h2">{title}</Typography>
    {supplier && <Typography variant="body2" sx={{ mb: 1 }}>Supplier: {supplier}</Typography>}
    {q.data.length === 0 && <Typography color="text.secondary">No sections are ticked for this report.</Typography>}
    {q.data.map((b, i) => <Box key={i} sx={{ mt: b.heading ? 2 : 1 }} data-section={b.key}>
      {b.heading && <Typography sx={{ bgcolor: '#E8EEF5', color: 'primary.main', fontWeight: 600, px: 1, py: 0.5, mb: 1 }}>{b.heading}</Typography>}
      {b.fields && <Box sx={{ display: 'grid', gridTemplateColumns: '220px 1fr', rowGap: 0.25, columnGap: 2, px: 1 }}>
        {b.fields.map((f) => [<Typography key={`${f.label}-k`} variant="body2" sx={{ fontWeight: 600 }}>{f.label}</Typography>,
          <Typography key={`${f.label}-v`} variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>{f.value}</Typography>])}
      </Box>}
      {b.columns && <Table size="small"><TableHead><TableRow>{b.columns.map((c) => <TableCell key={c} sx={{ fontWeight: 600 }}>{c}</TableCell>)}</TableRow></TableHead>
        <TableBody>{(b.rows ?? []).map((r, j) => <TableRow key={j}>{r.map((c, k) => <TableCell key={k} sx={r[0] === 'Total' ? { fontWeight: 600 } : undefined}>
          {b.columns?.[k] === 'Chart' ? <Box data-testid="chart-bar" sx={{ height: 12, bgcolor: 'primary.main', borderRadius: 0.5, width: `${(c ?? '').length * 5}%` }} /> : c}</TableCell>)}</TableRow>)}</TableBody></Table>}
      {b.photos?.map((p) => <Typography key={p} variant="body2" sx={{ px: 1 }}>Photo: {p}</Typography>)}
    </Box>)}
  </Paper>;
}
