import { useState } from 'react';
import { Alert, Box, Button, Checkbox, Chip, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, MenuItem, Radio, RadioGroup, Stack, Table, TableBody, TableCell,
  TableContainer, TableHead, TableRow, TextField, Typography } from '@mui/material';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post } from '../api';
import { useSites } from '../components/Kit';
import { S } from './common';

interface ImportTable { fileName: string; headers: string[]; rows: string[][]; mapping: Record<string, number>; warnings: string[] }
interface PlanRow { row: number; action: 'Add' | 'Update' | 'Skip' | 'Error'; partNumber?: string; name?: string; messages: string[] }
interface Plan { rows: PlanRow[]; add: number; update: number; skip: number; errors: number }
const actionColor = { Add: 'success', Update: 'info', Skip: 'default', Error: 'error' } as const;

/** Bulk import into the Part Master: choose a file, check the column mapping, preview, then confirm. */
export function ImportPartsDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const sites = useSites();
  const fields = useQuery({ queryKey: ['sqf-part-import-fields'], queryFn: () => get<{ key: string; label: string }[]>(`${S}/parts/import/fields`), enabled: open });
  const qc = useQueryClient();
  const [file, setFile] = useState<File | null>(null);
  const [allTables, setAllTables] = useState(false);
  const [table, setTable] = useState<ImportTable | null>(null);
  const [mapping, setMapping] = useState<Record<string, number>>({});
  const [siteId, setSiteId] = useState('');
  const [update, setUpdate] = useState(false);
  const [plan, setPlan] = useState<Plan | null>(null);
  const [done, setDone] = useState<Plan | null>(null);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);
  const site = siteId || sites.data?.[0]?.id || '';
  const reset = () => { setFile(null); setTable(null); setMapping({}); setPlan(null); setDone(null); setErr(''); setUpdate(false); setAllTables(false); };
  const run = async (fn: () => Promise<void>) => { setBusy(true); setErr(''); try { await fn(); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); } };
  const read = () => run(async () => {
    const fd = new FormData(); fd.append('file', file!);
    const t = await post<ImportTable>(`${S}/parts/import/read?allTables=${allTables}`, fd);
    setTable(t); setMapping(t.mapping); setPlan(null);
  });
  const body = (upd = update, map = mapping) => ({ fileName: table!.fileName, headers: table!.headers, rows: table!.rows, mapping: map, siteId: site, updateExisting: upd });
  const preview = (upd = update, map = mapping) => run(async () => setPlan(await post<Plan>(`${S}/parts/import/preview`, body(upd, map))));
  const confirm = () => run(async () => { setDone(await post<Plan>(`${S}/parts/import`, body())); qc.invalidateQueries({ queryKey: ['sqf-parts'] }); });
  const setMap = (key: string, v: string) => { const n = { ...mapping }; if (v === '') delete n[key]; else n[key] = Number(v); setMapping(n); setPlan(null); };
  const isDocx = !!file?.name.toLowerCase().endsWith('.docx');
  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="lg" TransitionProps={{ onEnter: reset }}>
      <DialogTitle>Import parts</DialogTitle>
      <DialogContent>
        {err && <Alert severity="error" sx={{ mb: 2 }}>{err}</Alert>}
        {done ? <Alert severity="success" data-testid="import-done">Imported from {table?.fileName}: {done.add} added, {done.update} updated, {done.skip} skipped{done.errors ? `, ${done.errors} rows with errors not imported` : ''}.</Alert> : <>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
            Excel (.xlsx, .xlsm, .xls), CSV or a Word (.docx) table with a header row. The file is only read; macros never run. Serial numbers are not imported.
          </Typography>
          <Stack direction="row" gap={2} alignItems="center" flexWrap="wrap">
            <Button variant="outlined" component="label">Choose file<input hidden type="file" data-testid="import-file" accept=".xlsx,.xlsm,.xls,.csv,.docx"
              onChange={(e) => { setFile(e.target.files?.[0] ?? null); setTable(null); setPlan(null); e.target.value = ''; }} /></Button>
            <Typography variant="body2">{file?.name ?? 'No file chosen'}</Typography>
            {isDocx && <FormControlLabel control={<Checkbox checked={allTables} onChange={(e) => setAllTables(e.target.checked)} />} label="Read all tables (same headings)" />}
            <TextField select size="small" label="Site for new parts" value={site} onChange={(e) => { setSiteId(e.target.value); setPlan(null); }} sx={{ minWidth: 220 }}>
              {(sites.data ?? []).map((x) => <MenuItem key={x.id} value={x.id}>{x.code} · {x.name}</MenuItem>)}
            </TextField>
            <Button variant="contained" disabled={!file || busy} onClick={read}>Read file</Button>
          </Stack>
          {table && <Box sx={{ mt: 3 }}>
            {table.warnings.map((w) => <Alert key={w} severity="info" sx={{ mb: 1 }}>{w}</Alert>)}
            <Typography variant="subtitle2" sx={{ mb: 1 }}>Column mapping · {table.rows.length} rows found</Typography>
            <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(230px, 1fr))', gap: 1.5 }}>
              {(fields.data ?? []).map((f) => (
                <TextField key={f.key} select size="small" label={f.label} value={mapping[f.key] ?? ''} onChange={(e) => setMap(f.key, e.target.value)} inputProps={{ 'data-field': f.key }}>
                  <MenuItem value=""><em>Not imported</em></MenuItem>
                  {table.headers.map((h, i) => <MenuItem key={i} value={i}>{h || `Column ${i + 1}`}</MenuItem>)}
                </TextField>
              ))}
            </Box>
            <Stack direction="row" gap={2} alignItems="center" sx={{ mt: 2 }}>
              <Typography variant="body2">Parts already in the Part Master:</Typography>
              <RadioGroup row value={update ? 'update' : 'skip'} onChange={(e) => { const u = e.target.value === 'update'; setUpdate(u); if (plan) preview(u); }}>
                <FormControlLabel value="skip" control={<Radio size="small" />} label="Skip existing" />
                <FormControlLabel value="update" control={<Radio size="small" />} label="Update existing" />
              </RadioGroup>
              <Button variant="outlined" disabled={busy} onClick={() => preview()}>Preview</Button>
            </Stack>
          </Box>}
          {plan && <Box sx={{ mt: 2 }}>
            <Stack direction="row" gap={1} sx={{ mb: 1 }} data-testid="import-counts">
              <Chip color="success" label={`Add ${plan.add}`} /><Chip color="info" label={`Update ${plan.update}`} /><Chip label={`Skip ${plan.skip}`} /><Chip color="error" label={`Errors ${plan.errors}`} />
            </Stack>
            <TableContainer sx={{ maxHeight: 340 }}><Table size="small" stickyHeader>
              <TableHead><TableRow><TableCell>Row</TableCell><TableCell>Action</TableCell><TableCell>Part number</TableCell><TableCell>Name</TableCell><TableCell>Notes</TableCell></TableRow></TableHead>
              <TableBody>{plan.rows.map((r) => (
                <TableRow key={r.row}><TableCell>{r.row}</TableCell><TableCell><Chip size="small" color={actionColor[r.action]} label={r.action} /></TableCell>
                  <TableCell>{r.partNumber}</TableCell><TableCell>{r.name}</TableCell><TableCell>{r.messages.join(' ')}</TableCell></TableRow>))}
              </TableBody></Table></TableContainer>
          </Box>}
        </>}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>{done ? 'Close' : 'Cancel'}</Button>
        {!done && <Button variant="contained" disabled={!plan || busy || plan.add + plan.update === 0} onClick={confirm}>Import {plan ? plan.add + plan.update : ''} parts</Button>}
      </DialogActions>
    </Dialog>
  );
}
