import { useState } from 'react';
import { Link as RouterLink, useParams } from 'react-router-dom';
import { Alert, Box, Button, Chip, Dialog, DialogActions, DialogContent, DialogTitle, IconButton, MenuItem, Paper, Stack, Table, TableBody, TableCell, TableHead, TableRow,
  TextField, Tooltip, Typography } from '@mui/material';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post, put } from '../api';
import { fmtDateTime } from '../components/Status';
import { ErrorState, Loading, Mono, S, SqfPage, useOptions } from './common';

// ------------------------------------------------------------------ Official status in NetSuite
export const STATUS_LABEL = 'Official status in NetSuite';
export const STATUS_HELP = 'Informational only. Official holds and releases are made in NetSuite; this does not block any work here.';
const tone = (tag?: string | null) => (!tag ? 'default' : /release/i.test(tag) ? 'success' : /hold|quarantin/i.test(tag) ? 'warning' : 'default') as 'default' | 'success' | 'warning';

export function StatusBadge({ tag, note }: { tag?: string | null; note?: string | null }) {
  if (!tag) return <>—</>;
  return <Tooltip title={note ? `${note} · ${STATUS_HELP}` : STATUS_HELP}><Chip size="small" color={tone(tag)} label={tag} data-testid="netsuite-status" /></Tooltip>;
}

/** Filter options for list pages: every option plus None. */
export function useStatusFilter() {
  const o = useOptions('status-tags');
  return { key: 'tag', label: STATUS_LABEL, options: [['none', 'None'] as [string, string], ...(o.data ?? []).map((x) => [x, x] as [string, string])] };
}

/** Dropdown plus optional note. kind is part, instruction or work-order. */
export function StatusEditor({ kind, id, tag, note, canEdit, onSaved, compact }: { kind: 'part' | 'instruction' | 'work-order'; id: string; tag?: string | null; note?: string | null;
  canEdit: boolean; onSaved: () => void; compact?: boolean }) {
  const o = useOptions('status-tags');
  const [open, setOpen] = useState(false);
  const [t, setT] = useState(tag ?? ''); const [n, setN] = useState(note ?? '');
  const [err, setErr] = useState<string | null>(null);
  const save = async (nt: string, nn: string) => { setErr(null); try { await put(`${S}/status-note/${kind}/${id}`, { tag: nt || null, note: nn || null }); onSaved(); return true; } catch (e) { setErr((e as Error).message); return false; } };
  const select = (value: string, onChange: (v: string) => void, size: 'small' | 'medium' = 'small') =>
    <TextField select size={size} label={STATUS_LABEL} value={value} onChange={(e) => onChange(e.target.value)} sx={{ minWidth: 220 }} onClick={(e) => e.stopPropagation()}>
      <MenuItem value="">None</MenuItem>{(o.data ?? []).map((x) => <MenuItem key={x} value={x}>{x}</MenuItem>)}</TextField>;
  if (compact) return canEdit
    ? <Box onClick={(e) => e.stopPropagation()}>{select(tag ?? '', (v) => { void save(v, note ?? ''); })}</Box>
    : <StatusBadge tag={tag} note={note} />;
  return <Paper variant="outlined" sx={{ p: 1.5, mb: 2, borderColor: tag ? (tone(tag) === 'warning' ? '#B26A00' : tone(tag) === 'success' ? '#0E8550' : undefined) : undefined }}>
    <Stack direction="row" gap={1.5} alignItems="center" flexWrap="wrap">
      <Typography variant="subtitle2">{STATUS_LABEL}:</Typography>
      <StatusBadge tag={tag} note={note} />{!tag && <Typography variant="body2" color="text.secondary">None</Typography>}
      {note && <Typography variant="body2">{note}</Typography>}
      {canEdit && <Button size="small" data-testid="edit-netsuite-status" onClick={() => { setT(tag ?? ''); setN(note ?? ''); setOpen(true); }}>Change</Button>}
    </Stack>
    <Typography variant="caption" color="text.secondary">{STATUS_HELP}</Typography>
    <Dialog open={open} onClose={() => setOpen(false)} fullWidth maxWidth="xs">
      <DialogTitle>{STATUS_LABEL}</DialogTitle>
      <DialogContent><Stack gap={2} sx={{ pt: 1 }}>
        <Alert severity="info">{STATUS_HELP}</Alert>
        {select(t, setT, 'medium')}
        <TextField label="Note (optional)" value={n} onChange={(e) => setN(e.target.value)} inputProps={{ maxLength: 300 }} multiline />
        {err && <Alert severity="error">{err}</Alert>}
      </Stack></DialogContent>
      <DialogActions><Button onClick={() => setOpen(false)}>Cancel</Button><Button variant="contained" onClick={async () => { if (await save(t, n)) setOpen(false); }}>Save</Button></DialogActions>
    </Dialog>
  </Paper>;
}

// ------------------------------------------------------------------ CSV
export function downloadCsv(name: string, head: string[], rows: (string | number | null | undefined)[][]) {
  const cell = (v: unknown) => { let s = v == null ? '' : String(v); if (/^[=+\-@\t\r]/.test(s)) s = `'${s}`; return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s; };
  const text = [head, ...rows].map((r) => r.map(cell).join(',')).join('\r\n');
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([text], { type: 'text/csv' })); a.download = name; a.click(); URL.revokeObjectURL(a.href);
}

// ------------------------------------------------------------------ Job panel
interface PartLine { partNumber: string; description?: string | null; perAssembly: number; quantity?: number | null; total?: number }
interface TestFile { id: string; fileName: string; batch?: string; testDate?: string; uploadedByName: string; uploadedAt: string; tested?: number; passed?: number; failed?: number }
interface ColumnMap { resultColumn?: string | null; passValues?: string[] | null; valueColumns?: string[] | null; batchColumn?: string | null; dateColumn?: string | null }
interface JobData { quantityTarget?: number | null; quantityCompleted: number; quantityNotRepaired: number; parts: PartLine[]; columns: ColumnMap; files: TestFile[];
  tests: { tested: number; passed: number; failed: number } }

export function JobPanel({ id, canWork }: { id: string; canWork: boolean }) {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['sqf-job', id], queryFn: () => get<JobData>(`${S}/tracking/${id}/job`) });
  const refresh = () => { qc.invalidateQueries({ queryKey: ['sqf-job', id] }); qc.invalidateQueries({ queryKey: ['sqf-history', id] }); };
  const [err, setErr] = useState<string | null>(null);
  const run = async (f: () => Promise<unknown>) => { setErr(null); try { await f(); refresh(); } catch (e) { setErr((e as Error).message); } };
  const [done, setDone] = useState(''); const [bad, setBad] = useState(''); const [target, setTarget] = useState('');
  const [parts, setParts] = useState<PartLine[] | null>(null);
  const [batch, setBatch] = useState(''); const [testDate, setTestDate] = useState('');
  const [mapFile, setMapFile] = useState<string | null>(null);
  if (q.isLoading) return <Loading />;
  if (q.error) return <ErrorState error={q.error} retry={q.refetch} />;
  const j = q.data!;
  const lines = parts ?? j.parts;
  const n = (s: string) => (s.trim() === '' ? null : Number(s));
  return <Stack gap={2}>
    {err && <Alert severity="error" onClose={() => setErr(null)}>{err}</Alert>}
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Typography variant="subtitle1" gutterBottom>Quantity</Typography>
      <Stack direction="row" gap={3} flexWrap="wrap" sx={{ mb: 1.5 }}>
        <Fact label="Completed / repaired" value={j.quantityCompleted} testid="qty-completed" />
        <Fact label="Could not repair" value={j.quantityNotRepaired} testid="qty-not-repaired" />
        <Fact label="Target" value={j.quantityTarget ?? '—'} />
      </Stack>
      {canWork && <Stack direction="row" gap={1} flexWrap="wrap" alignItems="center">
        <TextField size="small" type="number" label="Add completed" value={done} onChange={(e) => setDone(e.target.value)} sx={{ width: 150 }} />
        <TextField size="small" type="number" label="Add could not repair (optional)" value={bad} onChange={(e) => setBad(e.target.value)} sx={{ width: 230 }} />
        <TextField size="small" type="number" label="Target (optional)" value={target} onChange={(e) => setTarget(e.target.value)} sx={{ width: 150 }} />
        <Button variant="contained" disabled={!done && !bad && !target} onClick={() => run(async () => {
          await post(`${S}/tracking/${id}/job/quantity`, { completed: n(done), notRepaired: n(bad), target: n(target) }); setDone(''); setBad(''); setTarget(''); })}>Add</Button>
      </Stack>}
      <Typography variant="caption" color="text.secondary">Quantity is for the whole job. Use a negative number to correct a mistake. Every change is in the History tab.</Typography>
    </Paper>

    <Paper variant="outlined" sx={{ p: 2 }}>
      <Stack direction="row" justifyContent="space-between" alignItems="center"><Typography variant="subtitle1">Parts replaced</Typography>
        {canWork && !parts && <Button onClick={() => setParts(j.parts.map((p) => ({ ...p })))}>Edit parts</Button>}</Stack>
      <Table size="small"><TableHead><TableRow><TableCell>Part number</TableCell><TableCell>Description</TableCell><TableCell>Per assembly</TableCell><TableCell>Quantity (blank = auto)</TableCell>{parts && <TableCell />}</TableRow></TableHead>
        <TableBody>{lines.length === 0 && <TableRow><TableCell colSpan={5}>No parts recorded yet.</TableCell></TableRow>}
          {lines.map((p, i) => parts ? <TableRow key={i}>
            <TableCell><TextField size="small" value={p.partNumber} onChange={(e) => setParts(lines.map((x, k) => k === i ? { ...x, partNumber: e.target.value } : x))} inputProps={{ 'aria-label': 'Part number' }} /></TableCell>
            <TableCell><TextField size="small" value={p.description ?? ''} onChange={(e) => setParts(lines.map((x, k) => k === i ? { ...x, description: e.target.value } : x))} inputProps={{ 'aria-label': 'Description' }} /></TableCell>
            <TableCell><TextField size="small" type="number" value={p.perAssembly} onChange={(e) => setParts(lines.map((x, k) => k === i ? { ...x, perAssembly: Number(e.target.value) } : x))} sx={{ width: 100 }} inputProps={{ 'aria-label': 'Per assembly' }} /></TableCell>
            <TableCell><TextField size="small" type="number" value={p.quantity ?? ''} placeholder={String(p.perAssembly * j.quantityCompleted)} onChange={(e) => setParts(lines.map((x, k) => k === i ? { ...x, quantity: n(e.target.value) } : x))} sx={{ width: 130 }} inputProps={{ 'aria-label': 'Quantity override' }} /></TableCell>
            <TableCell><IconButton aria-label="Remove part" onClick={() => setParts(lines.filter((_, k) => k !== i))}><DeleteOutline /></IconButton></TableCell>
          </TableRow> : <TableRow key={i}><TableCell><Mono>{p.partNumber}</Mono></TableCell><TableCell>{p.description ?? '—'}</TableCell><TableCell>{p.perAssembly}</TableCell>
            <TableCell>{p.total}{p.quantity != null ? ' (entered)' : ''}</TableCell></TableRow>)}</TableBody></Table>
      {parts && <Stack direction="row" gap={1} sx={{ mt: 1 }}>
        <Button onClick={() => setParts([...lines, { partNumber: '', perAssembly: 1 }])}>Add part</Button><Box flex={1} />
        <Button onClick={() => setParts(null)}>Cancel</Button>
        <Button variant="contained" onClick={() => run(async () => { await put(`${S}/tracking/${id}/job/parts`, lines.filter((p) => p.partNumber.trim())); setParts(null); })}>Save parts</Button>
      </Stack>}
      <Typography variant="caption" color="text.secondary">Quantity is completed × per assembly unless you enter a number.</Typography>
    </Paper>

    <Paper variant="outlined" sx={{ p: 2 }}>
      <Typography variant="subtitle1" gutterBottom>Test results</Typography>
      <Typography variant="body2" sx={{ mb: 1 }}>Tested {j.tests.tested} · Passed {j.tests.passed} · Failed {j.tests.failed}</Typography>
      {canWork && <Stack direction="row" gap={1} flexWrap="wrap" alignItems="center" sx={{ mb: 1 }}>
        <TextField size="small" label="Batch (optional)" value={batch} onChange={(e) => setBatch(e.target.value)} />
        <TextField size="small" type="date" label="Test date (optional)" InputLabelProps={{ shrink: true }} value={testDate} onChange={(e) => setTestDate(e.target.value)} />
        <Button variant="outlined" component="label">Upload tester files<input hidden type="file" multiple accept=".csv,.xlsx,.pdf,.txt" data-testid="test-file-input" onChange={(e) => {
          const fl = e.target.files; if (!fl?.length) return;
          const fd = new FormData(); Array.from(fl).forEach((f) => fd.append('files', f)); if (batch) fd.append('batch', batch); if (testDate) fd.append('testDate', testDate);
          e.target.value = '';
          void run(() => post(`${S}/tracking/${id}/job/test-files`, fd));
        }} /></Button>
      </Stack>}
      <Typography variant="caption" color="text.secondary" component="div" sx={{ mb: 1 }}>CSV or Excel exports are read for pass/fail counts; PDF and text files are attached as they are. The column choice is remembered for this job.</Typography>
      {j.files.length > 0 && <Table size="small"><TableHead><TableRow><TableCell>File</TableCell><TableCell>Batch</TableCell><TableCell>Test date</TableCell><TableCell>Tested</TableCell><TableCell>Passed</TableCell><TableCell>Failed</TableCell><TableCell>Uploaded</TableCell><TableCell /></TableRow></TableHead>
        <TableBody>{j.files.map((f) => <TableRow key={f.id}>
          <TableCell><a href={`${S}/tracking/job/test-files/${f.id}`}>{f.fileName}</a></TableCell><TableCell>{f.batch ?? '—'}</TableCell><TableCell>{f.testDate ?? '—'}</TableCell>
          <TableCell>{f.tested ?? '—'}</TableCell><TableCell>{f.passed ?? '—'}</TableCell><TableCell>{f.failed ?? '—'}</TableCell><TableCell>{`${f.uploadedByName} · ${fmtDateTime(f.uploadedAt)}`}</TableCell>
          <TableCell sx={{ whiteSpace: 'nowrap' }}>{canWork && f.tested != null && <Button size="small" onClick={() => setMapFile(f.id)}>Columns</Button>}
            {canWork && <IconButton aria-label={`Remove ${f.fileName}`} onClick={() => run(() => del(`${S}/tracking/job/test-files/${f.id}`))}><DeleteOutline /></IconButton>}</TableCell>
        </TableRow>)}</TableBody></Table>}
    </Paper>
    {mapFile && <ColumnsDialog id={id} fileId={mapFile} onClose={() => setMapFile(null)} onSaved={refresh} />}
  </Stack>;
}

function Fact({ label, value, testid }: { label: string; value: React.ReactNode; testid?: string }) {
  return <Box><Typography variant="caption" color="text.secondary">{label}</Typography><Typography variant="h6" data-testid={testid}>{value}</Typography></Box>;
}

function ColumnsDialog({ id, fileId, onClose, onSaved }: { id: string; fileId: string; onClose: () => void; onSaved: () => void }) {
  const q = useQuery({ queryKey: ['sqf-test-cols', fileId], queryFn: () => get<{ headers: string[]; sample: string[][]; suggested?: string; current: ColumnMap }>(`${S}/tracking/job/test-files/${fileId}/columns`) });
  const [m, setM] = useState<ColumnMap | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const d = q.data;
  const v = m ?? (d ? { ...d.current, resultColumn: d.current.resultColumn ?? d.suggested ?? null } : {});
  const pick = (label: string, key: 'resultColumn' | 'batchColumn' | 'dateColumn') => <TextField select size="small" label={label} value={v[key] ?? ''} onChange={(e) => setM({ ...v, [key]: e.target.value || null })}>
    <MenuItem value="">(none)</MenuItem>{(d?.headers ?? []).map((h) => <MenuItem key={h} value={h}>{h}</MenuItem>)}</TextField>;
  return <Dialog open onClose={onClose} fullWidth maxWidth="md"><DialogTitle>Tester file columns</DialogTitle>
    <DialogContent>{q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} /> : d && <Stack gap={2} sx={{ pt: 1 }}>
      <Stack direction="row" gap={1} flexWrap="wrap">{pick('Pass/fail column', 'resultColumn')}{pick('Batch column', 'batchColumn')}{pick('Date column', 'dateColumn')}</Stack>
      <TextField size="small" label="Values that mean pass (comma separated)" value={(v.passValues ?? []).join(', ')} placeholder="PASS, PASSED, OK, GOOD"
        onChange={(e) => setM({ ...v, passValues: e.target.value.split(',').map((x) => x.trim()).filter(Boolean) })} />
      <TextField select size="small" label="Value columns to summarise" SelectProps={{ multiple: true }} value={v.valueColumns ?? []} onChange={(e) => setM({ ...v, valueColumns: e.target.value as unknown as string[] })}>
        {d.headers.map((h) => <MenuItem key={h} value={h}>{h}</MenuItem>)}</TextField>
      <Box sx={{ overflowX: 'auto' }}><Table size="small"><TableHead><TableRow>{d.headers.map((h) => <TableCell key={h}>{h}</TableCell>)}</TableRow></TableHead>
        <TableBody>{d.sample.map((r, i) => <TableRow key={i}>{r.map((c, k) => <TableCell key={k}>{c}</TableCell>)}</TableRow>)}</TableBody></Table></Box>
      {err && <Alert severity="error">{err}</Alert>}
    </Stack>}</DialogContent>
    <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" onClick={async () => {
      try { await put(`${S}/tracking/${id}/job/columns`, v); onSaved(); onClose(); } catch (e) { setErr((e as Error).message); } }}>Save for this job</Button></DialogActions>
  </Dialog>;
}

// ------------------------------------------------------------------ report page
interface Report { id: string; number: string; type: string; status: string; title: string; partNumber?: string; partDescription?: string; workOrder?: string;
  quantityTarget?: number; quantityCompleted: number; quantityNotRepaired: number; people: { name: string; seconds: number }[]; totalSeconds: number;
  parts: { partNumber: string; description?: string; perAssembly: number; quantity: number }[]; partsTotal: number;
  files: { fileName: string; batch?: string; testDate?: string; tested?: number; passed?: number; failed?: number }[]; tested: number; passed: number; failed: number; generatedAt: string }

const hrs = (s: number) => (s / 3600).toFixed(2);
const money = (v: number) => v.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

export function JobReportPage() {
  const { id } = useParams();
  const q = useQuery({ queryKey: ['sqf-job-report', id], queryFn: () => get<Report>(`${S}/tracking/${id}/job/report`) });
  const [supplier, setSupplier] = useState(''); const [reference, setReference] = useState(''); const [rate, setRate] = useState('');
  const r = q.data;
  const rateN = rate.trim() && !Number.isNaN(Number(rate)) ? Number(rate) : null;
  const pdf = `${S}/tracking/${id}/job/report/pdf?${new URLSearchParams({ ...(supplier && { supplier }), ...(reference && { reference }), ...(rateN != null && { rate: String(rateN) }) })}`;
  const blank = (v: string) => v || '________________';
  return <SqfPage title={r ? `Job report · ${r.number}` : 'Job report'} crumbs={[{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Work orders', to: '/sequfloh/tracking' }, { label: r?.number ?? '…', to: `/sequfloh/record/${id}` }, { label: 'Job report' }]}
    actions={<Stack direction="row" gap={1} className="no-print"><Button component={RouterLink} to={`/sequfloh/record/${id}`}>Back</Button>
      <Button variant="outlined" onClick={() => window.print()}>Print</Button><Button variant="contained" href={pdf}>Download PDF</Button></Stack>}>
    <style>{'@media print { .no-print, header, nav, .MuiDrawer-root, .MuiAppBar-root { display: none !important; } .job-report { border: none !important; } }'}</style>
    <Paper variant="outlined" className="no-print" sx={{ p: 2, mb: 2 }}><Stack direction="row" gap={1} flexWrap="wrap">
      <TextField size="small" label="Supplier" value={supplier} onChange={(e) => setSupplier(e.target.value)} />
      <TextField size="small" label="Chargeback reference" value={reference} onChange={(e) => setReference(e.target.value)} />
      <TextField size="small" label="Rate per hour (optional)" type="number" value={rate} onChange={(e) => setRate(e.target.value)} />
    </Stack><Typography variant="caption" color="text.secondary">These are used for this printout only and are not saved. Without a rate, cost lines stay blank.</Typography></Paper>
    {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : r && <Paper variant="outlined" className="job-report" sx={{ p: 3 }} data-testid="job-report">
      <Typography variant="h5" sx={{ color: '#1257C4' }}>Chargeback job report</Typography>
      <Typography variant="body2" color="text.secondary" gutterBottom>Generated {fmtDateTime(r.generatedAt)}</Typography>
      <Sec title="Chargeback"><Rows rows={[['Supplier', blank(supplier)], ['Reference', blank(reference)]]} /></Sec>
      <Sec title="Job"><Rows rows={[['Work order', r.workOrder ?? r.number], ['Record', r.number], ['Work instruction', r.title], ['Assembly', [r.partNumber, r.partDescription].filter(Boolean).join(' · ') || '—'],
        ['Status', r.status], ['Completed / repaired', String(r.quantityCompleted)], ...(r.quantityNotRepaired ? [['Could not repair', String(r.quantityNotRepaired)] as [string, string]] : []),
        ...(r.quantityTarget != null ? [['Target', String(r.quantityTarget)] as [string, string]] : [])]} /></Sec>
      <Sec title="Man hours"><Grid2 head={['Person', 'Hours']} rows={[...r.people.map((p) => [p.name, hrs(p.seconds)]), ['Total', hrs(r.totalSeconds)]]} /></Sec>
      <Sec title="Parts replaced"><Grid2 head={['Part number', 'Description', 'Per assembly', 'Quantity']} rows={[...r.parts.map((p) => [p.partNumber, p.description ?? '', String(p.perAssembly), String(p.quantity)]), ['Total', '', '', String(r.partsTotal)]]} /></Sec>
      <Sec title="Cost"><Rows rows={[['Total hours', hrs(r.totalSeconds)], ['Rate per hour', rateN != null ? money(rateN) : '____'], ['Labour cost', rateN != null ? money(rateN * r.totalSeconds / 3600) : '____']]} /></Sec>
      <Sec title="Test results summary">{r.files.length === 0 ? <Typography variant="body2">No test results attached.</Typography>
        : <Grid2 head={['File', 'Batch', 'Test date', 'Tested', 'Passed', 'Failed']} rows={[...r.files.map((f) => [f.fileName, f.batch ?? '', f.testDate ?? '', f.tested ?? '', f.passed ?? '', f.failed ?? ''].map(String)), ['Total', '', '', String(r.tested), String(r.passed), String(r.failed)]]} />}</Sec>
      <Sec title="Sign-off"><Rows rows={[['Prepared by', '________________'], ['Date', '________________'], ['Supplier acknowledgement', '________________']]} /></Sec>
    </Paper>}
  </SqfPage>;
}
function Sec({ title, children }: { title: string; children: React.ReactNode }) {
  return <Box sx={{ mt: 2, breakInside: 'avoid' }}><Typography variant="subtitle1" sx={{ color: '#1257C4', borderBottom: '2px solid #1257C4', mb: 1 }}>{title}</Typography>{children}</Box>;
}
function Rows({ rows }: { rows: [string, string][] }) {
  return <Table size="small"><TableBody>{rows.map(([k, v]) => <TableRow key={k}><TableCell sx={{ width: 220, fontWeight: 600 }}>{k}</TableCell><TableCell>{v}</TableCell></TableRow>)}</TableBody></Table>;
}
function Grid2({ head, rows }: { head: string[]; rows: string[][] }) {
  return <Table size="small"><TableHead><TableRow>{head.map((h) => <TableCell key={h} sx={{ fontWeight: 600 }}>{h}</TableCell>)}</TableRow></TableHead>
    <TableBody>{rows.map((r, i) => <TableRow key={i} sx={i === rows.length - 1 ? { '& td': { fontWeight: 600 } } : undefined}>{r.map((c, k) => <TableCell key={k}>{c}</TableCell>)}</TableRow>)}</TableBody></Table>;
}
