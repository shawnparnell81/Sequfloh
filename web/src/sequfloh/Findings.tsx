import { useState } from 'react';
import { Alert, Box, Button, IconButton, MenuItem, Paper, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post, put } from '../api';
import { fmtDateTime } from '../components/Status';
import { ErrorState, Loading, S, useOptions } from './common';

export interface Findings { discoveryMethod?: string | null; dateDiscovered?: string | null; discoveredBy?: string | null; site?: string | null; supplier?: string | null;
  supplierPartNumber?: string | null; lotDateCodes?: string | null; purchaseOrder?: string | null; qtyInspected?: number | null; qtyNonconforming?: number | null; qtyAffected?: number | null;
  description?: string | null; warrantyClaim?: string | null; alertRecall?: string | null; erpReference?: string | null; repairMethod?: string | null; rootCause?: string | null }
export interface CostLine { description: string; amount: number }
interface FileRow { id: string; kind: string; fileName: string; uploadedByName: string; uploadedAt: string }
interface Data { findings: Findings; costLines: CostLine[]; files: FileRow[] }

type Key = keyof Findings;
const text: [Key, string][] = [['discoveredBy', 'Discovered by'], ['site', 'Site'], ['supplier', 'Supplier'], ['supplierPartNumber', 'Supplier part #'], ['lotDateCodes', 'Lot / date codes'],
  ['purchaseOrder', 'PO'], ['warrantyClaim', 'Warranty claim #'], ['alertRecall', 'Alert / recall #'], ['erpReference', 'ERP reference']];
const nums: [Key, string][] = [['qtyInspected', 'Qty inspected'], ['qtyNonconforming', 'Qty nonconforming'], ['qtyAffected', 'Qty affected']];

/** Findings / nonconformance, evidence and other costs for a supplier chargeback. Everything is optional. */
export function FindingsPanel({ id, canWork, wi }: { id: string; canWork: boolean; wi: string }) {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['sqf-job', id], queryFn: () => get<Data>(`${S}/tracking/${id}/job`) });
  const methods = useOptions('discovery-methods');
  const [f, setF] = useState<Findings | null>(null);
  const [lines, setLines] = useState<CostLine[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const refresh = () => qc.invalidateQueries({ queryKey: ['sqf-job', id] });
  const run = async (fn: () => Promise<unknown>) => { setErr(null); setSaved(false); try { await fn(); await refresh(); return true; } catch (e) { setErr((e as Error).message); return false; } };
  if (q.isLoading) return <Loading />;
  if (q.error) return <ErrorState error={q.error} retry={q.refetch} />;
  const d = q.data!;
  const v = f ?? d.findings;
  const cl = lines ?? d.costLines;
  const set = (k: Key, val: string | number | null) => setF({ ...v, [k]: val === '' ? null : val });
  const tf = (k: Key, label: string, extra?: object) => <TextField key={k} size="small" label={label} value={v[k] ?? ''} disabled={!canWork} onChange={(e) => set(k, e.target.value)} {...extra} />;
  const upload = (kind: 'evidence' | 'attachment', accept: string, label: string) => canWork && <Button variant="outlined" component="label" size="small">{label}
    <input hidden type="file" multiple accept={accept} data-testid={`${kind}-input`} onChange={(e) => {
      const fl = e.target.files; if (!fl?.length) return;
      const fd = new FormData(); Array.from(fl).forEach((x) => fd.append('files', x)); fd.append('kind', kind); e.target.value = '';
      void run(() => post(`${S}/tracking/${id}/job/test-files`, fd));
    }} /></Button>;
  const list = (kind: string) => d.files.filter((x) => x.kind === kind);
  const files = (kind: string, empty: string) => list(kind).length === 0 ? <Typography variant="body2" color="text.secondary">{empty}</Typography>
    : <Stack gap={0.5}>{list(kind).map((x) => <Stack key={x.id} direction="row" alignItems="center" gap={1}>
      <a href={`${S}/tracking/job/test-files/${x.id}`}>{x.fileName}</a><Typography variant="caption" color="text.secondary">{x.uploadedByName} · {fmtDateTime(x.uploadedAt)}</Typography>
      {canWork && <IconButton size="small" aria-label={`Remove ${x.fileName}`} onClick={() => run(() => del(`${S}/tracking/job/test-files/${x.id}`))}><DeleteOutline fontSize="small" /></IconButton>}</Stack>)}</Stack>;
  return <Stack gap={2} data-testid="findings-panel">
    {err && <Alert severity="error" onClose={() => setErr(null)}>{err}</Alert>}
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Typography variant="subtitle1">Findings / nonconformance</Typography>
      <Typography variant="caption" color="text.secondary" component="div" sx={{ mb: 1.5 }}>All fields are optional. Fill in what helps the supplier approve the chargeback.</Typography>
      <Box sx={{ display: 'grid', gap: 1.5, gridTemplateColumns: { xs: '1fr', md: 'repeat(3, 1fr)' } }}>
        <TextField select size="small" label="Discovery method" value={v.discoveryMethod ?? ''} disabled={!canWork} onChange={(e) => set('discoveryMethod', e.target.value)} data-testid="discovery-method">
          <MenuItem value=""><em>Not set</em></MenuItem>{(methods.data ?? []).map((o) => <MenuItem key={o} value={o}>{o}</MenuItem>)}</TextField>
        {tf('dateDiscovered', 'Date discovered', { type: 'date', InputLabelProps: { shrink: true } })}
        {text.map(([k, l]) => tf(k, l))}
        {nums.map(([k, l]) => <TextField key={k} size="small" type="number" label={l} value={v[k] ?? ''} disabled={!canWork} onChange={(e) => set(k, e.target.value === '' ? null : Number(e.target.value))} />)}
      </Box>
      <Stack gap={1.5} sx={{ mt: 1.5 }}>
        {tf('description', 'Description of the nonconformance', { multiline: true, minRows: 2 })}
        {tf('repairMethod', `Repair method (work instruction used: ${wi})`, { multiline: true, minRows: 2 })}
        {tf('rootCause', 'Supplier root cause (optional)', { multiline: true, minRows: 2 })}
      </Stack>
      {canWork && <Stack direction="row" gap={1} sx={{ mt: 1.5 }} alignItems="center">
        <Button variant="contained" disabled={!f} data-testid="save-findings" onClick={async () => { if (await run(() => put(`${S}/tracking/${id}/job/findings`, v))) { setF(null); setSaved(true); } }}>Save findings</Button>
        {f && <Button onClick={() => setF(null)}>Discard</Button>}{saved && <Typography variant="body2" color="success.main">Saved</Typography>}
      </Stack>}
    </Paper>
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ mb: 1 }}><Typography variant="subtitle1">Photos and evidence</Typography>{upload('evidence', '.jpg,.jpeg,.png', 'Add photos')}</Stack>
      {files('evidence', 'No photos yet.')}
      <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ mt: 2, mb: 1 }}><Typography variant="subtitle1">Attachments</Typography>{upload('attachment', '.pdf,.txt,.csv,.xlsx,.docx,.jpg,.jpeg,.png', 'Add attachments')}</Stack>
      {files('attachment', 'No attachments yet. Claim forms, alerts or supplier letters can go here.')}
    </Paper>
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Stack direction="row" justifyContent="space-between" alignItems="center"><Typography variant="subtitle1">Other cost lines</Typography>
        {canWork && !lines && <Button onClick={() => setLines(d.costLines.map((x) => ({ ...x })))}>Edit cost lines</Button>}</Stack>
      <Typography variant="caption" color="text.secondary" component="div" sx={{ mb: 1 }}>Labor (hours × rate) and parts (quantity × unit cost) are worked out on the report. Add anything else here, such as freight or sorting.</Typography>
      <Table size="small"><TableHead><TableRow><TableCell>Description</TableCell><TableCell align="right">Amount</TableCell>{lines && <TableCell />}</TableRow></TableHead>
        <TableBody>{cl.length === 0 && <TableRow><TableCell colSpan={3}>No other costs.</TableCell></TableRow>}
          {cl.map((c, i) => lines ? <TableRow key={i}>
            <TableCell><TextField size="small" fullWidth value={c.description} inputProps={{ 'aria-label': 'Cost description' }} onChange={(e) => setLines(cl.map((x, k) => k === i ? { ...x, description: e.target.value } : x))} /></TableCell>
            <TableCell align="right"><TextField size="small" type="number" value={c.amount} sx={{ width: 130 }} inputProps={{ 'aria-label': 'Amount' }} onChange={(e) => setLines(cl.map((x, k) => k === i ? { ...x, amount: Number(e.target.value) } : x))} /></TableCell>
            <TableCell><IconButton aria-label="Remove cost line" onClick={() => setLines(cl.filter((_, k) => k !== i))}><DeleteOutline /></IconButton></TableCell></TableRow>
            : <TableRow key={i}><TableCell>{c.description}</TableCell><TableCell align="right">{c.amount.toFixed(2)}</TableCell></TableRow>)}</TableBody></Table>
      {lines && <Stack direction="row" gap={1} sx={{ mt: 1 }}>
        <Button onClick={() => setLines([...cl, { description: '', amount: 0 }])}>Add line</Button><Box flex={1} />
        <Button onClick={() => setLines(null)}>Cancel</Button>
        <Button variant="contained" onClick={async () => { if (await run(() => put(`${S}/tracking/${id}/job/cost-lines`, cl))) setLines(null); }}>Save cost lines</Button></Stack>}
    </Paper>
  </Stack>;
}
