import { useState } from 'react';
import { FormDialogTitle } from '../components/FullScreen';
import { Link as RouterLink, useParams } from 'react-router-dom';
import { Alert, Box, Button, Checkbox, FormControl, FormControlLabel, FormLabel, Radio, RadioGroup, Dialog, DialogActions, DialogContent, IconButton, Link, MenuItem, Paper, Stack, Table, TableBody, TableCell, TableHead, TableRow,
  TextField, Typography } from '@mui/material';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post, put } from '../api';
import { ImportButtons } from '../components/ImportButtons';
import { fmtDateTime } from '../components/Status';
import type { CostLine, Findings } from './Findings';
import { ErrorState, Loading, Mono, S, SqfPage } from './common';
import { EightDFile, eightDUrl } from './EightD';

// ------------------------------------------------------------------ Inventory status
export const INVENTORY_NOTE = 'Inventory status is kept in NetSuite. Sequfloh does not connect to NetSuite.';
/** One short line where an inventory status might be expected. Sequfloh keeps no inventory. */
export function InventoryNote() {
  return <Typography variant="caption" color="text.secondary" component="p" data-testid="inventory-note" sx={{ mb: 2 }}>{INVENTORY_NOTE}</Typography>;
}


// ------------------------------------------------------------------ CSV
export function downloadCsv(name: string, head: string[], rows: (string | number | null | undefined)[][]) {
  const cell = (v: unknown) => { let s = v == null ? '' : String(v); if (/^[=+\-@\t\r]/.test(s)) s = `'${s}`; return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s; };
  const text = [head, ...rows].map((r) => r.map(cell).join(',')).join('\r\n');
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([text], { type: 'text/csv' })); a.download = name; a.click(); URL.revokeObjectURL(a.href);
}

// ------------------------------------------------------------------ Job panel
interface PartLine { partNumber: string; description?: string | null; perAssembly: number; quantity?: number | null; unitCost?: number | null; total?: number }
interface TestFile { id: string; kind?: string; fileName: string; batch?: string; testDate?: string; uploadedAt: string; tested?: number; passed?: number; failed?: number }
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
  const tests = j.files.filter((f) => (f.kind ?? 'test') === 'test');
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
      <Table size="small"><TableHead><TableRow><TableCell>Part number</TableCell><TableCell>Description</TableCell><TableCell>Per assembly</TableCell><TableCell>Quantity (blank = auto)</TableCell><TableCell>Unit cost (optional)</TableCell>{parts && <TableCell />}</TableRow></TableHead>
        <TableBody>{lines.length === 0 && <TableRow><TableCell colSpan={6}>No parts recorded yet.</TableCell></TableRow>}
          {lines.map((p, i) => parts ? <TableRow key={i}>
            <TableCell><TextField size="small" value={p.partNumber} onChange={(e) => setParts(lines.map((x, k) => k === i ? { ...x, partNumber: e.target.value } : x))} inputProps={{ 'aria-label': 'Part number' }} /></TableCell>
            <TableCell><TextField size="small" value={p.description ?? ''} onChange={(e) => setParts(lines.map((x, k) => k === i ? { ...x, description: e.target.value } : x))} inputProps={{ 'aria-label': 'Description' }} /></TableCell>
            <TableCell><TextField size="small" type="number" value={p.perAssembly} onChange={(e) => setParts(lines.map((x, k) => k === i ? { ...x, perAssembly: Number(e.target.value) } : x))} sx={{ width: 100 }} inputProps={{ 'aria-label': 'Per assembly' }} /></TableCell>
            <TableCell><TextField size="small" type="number" value={p.quantity ?? ''} placeholder={String(p.perAssembly * j.quantityCompleted)} onChange={(e) => setParts(lines.map((x, k) => k === i ? { ...x, quantity: n(e.target.value) } : x))} sx={{ width: 130 }} inputProps={{ 'aria-label': 'Quantity override' }} /></TableCell>
            <TableCell><TextField size="small" type="number" value={p.unitCost ?? ''} onChange={(e) => setParts(lines.map((x, k) => k === i ? { ...x, unitCost: n(e.target.value) } : x))} sx={{ width: 110 }} inputProps={{ 'aria-label': 'Unit cost' }} /></TableCell>
            <TableCell><IconButton aria-label="Remove part" onClick={() => setParts(lines.filter((_, k) => k !== i))}><DeleteOutline /></IconButton></TableCell>
          </TableRow> : <TableRow key={i}><TableCell><Mono>{p.partNumber}</Mono></TableCell><TableCell>{p.description ?? '—'}</TableCell><TableCell>{p.perAssembly}</TableCell>
            <TableCell>{p.total}{p.quantity != null ? ' (entered)' : ''}</TableCell><TableCell>{p.unitCost != null ? p.unitCost.toFixed(2) : '—'}</TableCell></TableRow>)}</TableBody></Table>
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
        <ImportButtons surface="work-order-tester-files" accept={['.csv', '.xlsx', '.pdf', '.txt']} what="tester file" onDone={refresh}
          upload={(f) => { const fd = new FormData(); fd.append('files', f); if (batch) fd.append('batch', batch); if (testDate) fd.append('testDate', testDate); return post(`${S}/tracking/${id}/job/test-files`, fd); }} />
      </Stack>}
      <Typography variant="caption" color="text.secondary" component="div" sx={{ mb: 1 }}>CSV or Excel exports are read for pass/fail counts; PDF and text files are attached as they are. The column choice is remembered for this job.</Typography>
      {tests.length > 0 && <Table size="small"><TableHead><TableRow><TableCell>File</TableCell><TableCell>Batch</TableCell><TableCell>Test date</TableCell><TableCell>Tested</TableCell><TableCell>Passed</TableCell><TableCell>Failed</TableCell><TableCell>Uploaded</TableCell><TableCell /></TableRow></TableHead>
        <TableBody>{tests.map((f) => <TableRow key={f.id}>
          <TableCell><a href={`${S}/tracking/job/test-files/${f.id}`}>{f.fileName}</a></TableCell><TableCell>{f.batch ?? '—'}</TableCell><TableCell>{f.testDate ?? '—'}</TableCell>
          <TableCell>{f.tested ?? '—'}</TableCell><TableCell>{f.passed ?? '—'}</TableCell><TableCell>{f.failed ?? '—'}</TableCell><TableCell>{fmtDateTime(f.uploadedAt)}</TableCell>
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
  return <Dialog open onClose={onClose} fullWidth maxWidth="md"><FormDialogTitle>Tester file columns</FormDialogTitle>
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
interface Opts { perTech: boolean; timeLog: boolean; findings: boolean; tests: boolean; attachments: boolean; photos: boolean; cost: boolean }
interface Att { id: string; fileName: string; uploadedAt: string }
interface Report { id: string; number: string; type: string; status: string; title: string; partNumber?: string; partDescription?: string; workOrder?: string;
  quantityTarget?: number; quantityCompleted: number; quantityNotRepaired: number; people: { name: string; seconds: number; pausedSeconds: number; sessions: number }[]; totalSeconds: number;
  pausedSeconds: number; parts: { partNumber: string; description?: string; perAssembly: number; quantity: number; overridden: boolean; unitCost?: number | null }[]; partsTotal: number;
  files: { fileName: string; batch?: string; testDate?: string; tested?: number; passed?: number; failed?: number }[]; tested: number; passed: number; failed: number; generatedAt: string;
  timeLog: { name: string; kind: string; at: string }[]; findings: Findings; costLines: CostLine[]; options: Opts; attachments: Att[]; photos: Att[];
  workInstructionNumber: string; workInstructionRevision: string; partsCost: number; otherCost: number; eightD?: EightDFile[] }

const hrs = (s: number) => (s / 3600).toFixed(2);
const money = (v: number) => v.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const SECTIONS: [keyof Opts, string][] = [['perTech', 'Per-technician summary'], ['timeLog', 'Detailed time log'], ['findings', 'Findings'],
  ['attachments', 'Attachments'], ['photos', 'Photos / evidence'], ['cost', 'Cost breakdown']];
const NONE: Opts = { perTech: false, timeLog: false, findings: false, tests: false, attachments: false, photos: false, cost: false };
/** Every section; test results keep the explicit Yes / No choice. */
const ALL = (tests: boolean): Opts => ({ perTech: true, timeLog: true, findings: true, tests, attachments: true, photos: true, cost: true });

export function JobReportPage() {
  const { id } = useParams();
  const q = useQuery({ queryKey: ['sqf-job-report', id], queryFn: () => get<Report>(`${S}/tracking/${id}/job/report`) });
  const [supplier, setSupplier] = useState(''); const [reference, setReference] = useState(''); const [rate, setRate] = useState('');
  const [opts, setOpts] = useState<Opts | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const r = q.data;
  const o = opts ?? r?.options ?? NONE;
  const choose = async (next: Opts) => {
    setOpts(next); setErr(null);
    try { await put(`${S}/tracking/${id}/job/report-options`, next); if (next.tests !== o.tests) await q.refetch(); } catch (e) { setErr((e as Error).message); }
  };
  const rateN = rate.trim() && !Number.isNaN(Number(rate)) ? Number(rate) : null;
  const pdf = `${S}/tracking/${id}/job/report/pdf?${new URLSearchParams({ ...(supplier && { supplier }), ...(reference && { reference }), ...(rateN != null && { rate: String(rateN) }) })}`;
  const blank = (v: string | null | undefined) => v || '________________';
  const fd = r?.findings ?? {};
  const hasFindings = Object.entries(fd).some(([k, v]) => k !== 'erpReference' && v != null && v !== '');
  const labour = r && rateN != null ? Math.round(rateN * r.totalSeconds / 36) / 100 : null;
  const total = r ? (labour ?? 0) + r.partsCost + r.otherCost : 0;
  const opt = (pairs: [string, unknown][]) => pairs.filter(([, v]) => v != null && v !== '').map(([k, v]) => [k, String(v)] as [string, string]);
  return <SqfPage title={r ? `Job report · ${r.number}` : 'Job report'} crumbs={[{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Work orders', to: '/sequfloh/tracking' }, { label: r?.number ?? '…', to: `/sequfloh/record/${id}` }, { label: 'Job report' }]}
    actions={<Stack direction="row" gap={1} className="no-print"><Button component={RouterLink} to={`/sequfloh/record/${id}`}>Back</Button>
      <Button variant="outlined" onClick={() => window.print()}>Print</Button>
      <Button variant="outlined" data-testid="save-job-report" onClick={async () => { setErr(null); setSaved(false); try { await post(`${S}/tracking/${id}/job/report/save`, {}); setSaved(true); } catch (e) { setErr((e as Error).message); } }}>Save to Reports</Button><Button variant="contained" href={pdf}>Download PDF</Button></Stack>}>
    <style>{'@media print { .no-print, header, nav, .MuiDrawer-root, .MuiAppBar-root { display: none !important; } .job-report { border: none !important; } }'}</style>
    <Paper variant="outlined" className="no-print" sx={{ p: 2, mb: 2 }}><Stack direction="row" gap={1} flexWrap="wrap">
      <TextField size="small" label="Supplier" value={supplier} onChange={(e) => setSupplier(e.target.value)} placeholder={fd.supplier ?? undefined} />
      <TextField size="small" label="Chargeback reference" value={reference} onChange={(e) => setReference(e.target.value)} placeholder={fd.purchaseOrder ?? undefined} />
      <TextField size="small" label="Rate per hour (optional)" type="number" value={rate} onChange={(e) => setRate(e.target.value)} />
    </Stack><Typography variant="caption" color="text.secondary">These are used for this printout only and are not saved. Without a rate, labor cost stays blank.</Typography>
      <Typography variant="subtitle2" sx={{ mt: 2 }}>Include in the report</Typography>
      <Stack direction="row" flexWrap="wrap" alignItems="center" data-testid="report-options">
        {SECTIONS.map(([k, l]) => <FormControlLabel key={k} label={l} control={<Checkbox size="small" checked={o[k]} onChange={(e) => choose({ ...o, [k]: e.target.checked })} inputProps={{ 'aria-label': l }} />} />)}
        <Button size="small" variant="outlined" data-testid="full-detail" onClick={() => choose(ALL(o.tests))}>Full detail</Button>
        <Button size="small" sx={{ ml: 1 }} data-testid="summary-only" onClick={() => choose({ ...NONE, tests: o.tests })}>Summary only</Button>
      </Stack>
      <FormControl sx={{ mt: 1 }} data-testid="include-tests">
        <FormLabel id="include-tests-label" sx={{ fontSize: 14 }}>Include test results report?</FormLabel>
        <RadioGroup row aria-labelledby="include-tests-label" value={o.tests ? 'yes' : 'no'} onChange={(e) => choose({ ...o, tests: e.target.value === 'yes' })}>
          <FormControlLabel value="no" control={<Radio size="small" />} label="No" /><FormControlLabel value="yes" control={<Radio size="small" />} label="Yes" />
        </RadioGroup>
      </FormControl>
      <Typography variant="caption" color="text.secondary" component="div">Your choices are remembered for this work order. Sections with nothing in them are left out. Full detail and Summary only keep the Yes / No answer above.</Typography>
      {err && <Alert severity="error" sx={{ mt: 1 }}>{err}</Alert>}
      {saved && <Alert severity="success" sx={{ mt: 1 }} onClose={() => setSaved(false)}>Saved to Reports.</Alert>}</Paper>
    {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : r && <Paper variant="outlined" className="job-report" sx={{ p: 3 }} data-testid="job-report">
      <Typography variant="h5" sx={{ color: '#1257C4' }}>Chargeback job report</Typography>
      <Typography variant="body2" color="text.secondary" gutterBottom>Generated {fmtDateTime(r.generatedAt)}</Typography>
      <Sec title="Chargeback"><Rows rows={[['Supplier', blank(supplier || fd.supplier)], ['Reference / PO', blank(reference || fd.purchaseOrder)]]} /></Sec>
      <Sec title="Job"><Rows rows={[['Work order', r.workOrder ?? r.number], ['Record', r.number], ['Work instruction', r.workInstructionNumber ? `${r.workInstructionNumber} rev ${r.workInstructionRevision} ${r.title}` : '—'],
        ['Assembly', [r.partNumber, r.partDescription].filter(Boolean).join(' · ') || '—'], ['Status', r.status], ['Completed / repaired', String(r.quantityCompleted)],
        ...opt([['Could not repair', r.quantityNotRepaired || null], ['Target', r.quantityTarget]]), ['Total man hours', `${hrs(r.totalSeconds)} (paused time excluded)`]]} /></Sec>
      {r.parts.length > 0 && <Sec title="Parts replaced"><Grid2 head={['Part number', 'Description', 'Per assembly', 'Quantity']} rows={[...r.parts.map((p) => [p.partNumber, p.description ?? '', String(p.perAssembly), String(p.quantity)]), ['Total', '', '', String(r.partsTotal)]]} /></Sec>}
      {o.findings && hasFindings && <Sec title="Findings / nonconformance"><Rows rows={opt([['Discovery method', fd.discoveryMethod], ['Date discovered', fd.dateDiscovered], ['Discovered by', fd.discoveredBy],
        ['Site', fd.site], ['Supplier', fd.supplier], ['Supplier part #', fd.supplierPartNumber], ['Lot / date codes', fd.lotDateCodes], ['PO', fd.purchaseOrder], ['Qty inspected', fd.qtyInspected],
        ['Qty nonconforming', fd.qtyNonconforming], ['Qty affected', fd.qtyAffected], ['Warranty claim #', fd.warrantyClaim], ['Alert / recall #', fd.alertRecall],
        ['Description', fd.description], ['Repair method', fd.repairMethod], ['Work instruction used', fd.repairMethod && r.workInstructionNumber ? `${r.workInstructionNumber} rev ${r.workInstructionRevision}` : null], ['Supplier root cause', fd.rootCause]])} /></Sec>}
      {o.perTech && r.people.length > 0 && <Sec title="Man hours by technician"><Grid2 head={['Technician', 'Sessions', 'Worked hours', 'Paused hours (not counted)']}
        rows={[...r.people.map((p) => [p.name, String(p.sessions), hrs(p.seconds), hrs(p.pausedSeconds)]), ['Total', String(r.people.reduce((a, p) => a + p.sessions, 0)), hrs(r.totalSeconds), hrs(r.pausedSeconds)]]} /></Sec>}
      {o.timeLog && r.timeLog.length > 0 && <Sec title="Detailed time log"><Grid2 noTotal head={['When', 'Technician', 'Event']} rows={r.timeLog.map((e) => [fmtDateTime(e.at), e.name, e.kind])} /></Sec>}
      {o.tests && r.files.length > 0 && <Sec title="Test results"><Grid2 head={['File', 'Batch', 'Test date', 'Tested', 'Passed', 'Failed']} rows={[...r.files.map((f) => [f.fileName, f.batch ?? '', f.testDate ?? '', f.tested ?? '', f.passed ?? '', f.failed ?? ''].map(String)), ['Total', '', '', String(r.tested), String(r.passed), String(r.failed)]]} /></Sec>}
      {!!r.eightD?.length && <Sec title="8D Report"><Table size="small" data-testid="job-eightd"><TableHead><TableRow><TableCell>8D Report</TableCell><TableCell>Uploaded by</TableCell><TableCell>When</TableCell></TableRow></TableHead>
        <TableBody>{r.eightD.map((f) => <TableRow key={f.id}><TableCell><Link href={eightDUrl(f.id)} target="_blank" rel="noopener">{f.fileName}</Link></TableCell><TableCell>{f.uploadedBy ?? '—'}</TableCell><TableCell>{fmtDateTime(f.uploadedAt)}</TableCell></TableRow>)}</TableBody></Table></Sec>}
      {o.attachments && r.attachments.length > 0 && <Sec title="Attachments"><Grid2 noTotal head={['File', 'Added']} rows={r.attachments.map((a) => [a.fileName, fmtDateTime(a.uploadedAt)])} /></Sec>}
      {o.photos && r.photos.length > 0 && <Sec title="Photos and evidence"><Stack direction="row" gap={2} flexWrap="wrap">{r.photos.map((p) => <Box key={p.id} sx={{ width: 260 }}>
        <Box component="img" src={`${S}/tracking/job/test-files/${p.id}`} alt={p.fileName} sx={{ width: '100%', border: '1px solid #ddd' }} />
        <Typography variant="caption">{p.fileName} · {fmtDateTime(p.uploadedAt)}</Typography></Box>)}</Stack></Sec>}
      <Sec title="Chargeback summary">
        {o.cost && <Grid2 noTotal head={['Item', 'Basis', 'Amount']} rows={[['Labor', `${hrs(r.totalSeconds)} h × ${rateN != null ? money(rateN) : '____'}`, labour != null ? money(labour) : '____'],
          ...r.parts.filter((p) => p.unitCost != null).map((p) => [`Part ${p.partNumber}`, `${p.quantity} × ${money(p.unitCost!)}`, money(p.quantity * p.unitCost!)]),
          ...r.costLines.map((c) => [c.description, '', money(c.amount)])]} />}
        <Rows rows={[['Labor', labour != null ? money(labour) : '____ (computed when a rate is entered)'], ...opt([['Parts', r.partsCost ? money(r.partsCost) : null], ['Other costs', r.otherCost ? money(r.otherCost) : null]]),
          ['Total chargeback', labour == null && total === 0 ? '____' : money(total) + (labour == null ? ' (labor not included: no rate entered)' : '')]]} /></Sec>
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
function Grid2({ head, rows, noTotal }: { head: string[]; rows: string[][]; noTotal?: boolean }) {
  return <Table size="small"><TableHead><TableRow>{head.map((h) => <TableCell key={h} sx={{ fontWeight: 600 }}>{h}</TableCell>)}</TableRow></TableHead>
    <TableBody>{rows.map((r, i) => <TableRow key={i} sx={!noTotal && i === rows.length - 1 ? { '& td': { fontWeight: 600 } } : undefined}>{r.map((c, k) => <TableCell key={k}>{c}</TableCell>)}</TableRow>)}</TableBody></Table>;
}
