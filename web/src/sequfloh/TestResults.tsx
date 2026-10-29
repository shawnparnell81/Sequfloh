import { useEffect, useState } from 'react';
import { Alert, Box, Button, Chip, Dialog, DialogActions, DialogContent, Paper, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put } from '../api';
import { FormDialogTitle } from '../components/FullScreen';
import { S } from './common';
import { nyTime } from './JobNotes';
import { DrawingPicker, PartDrawingLinks, type PartDrawing } from './Drawings';

interface Field { key: string; label: string; defaultUnit: string; added?: boolean }
interface SpecRow { key: string; label: string; value: string; unit: string }
interface PartRow { id: string; partIndex: number; note?: string | null; recordedBy: string; recordedAt: string; editedAt?: string | null; editedBy?: string | null; values: { key: string; value: string }[] }
interface Removed { partIndex: number; removedAt: string; removedBy?: string; removeReason?: string }
export interface TestData {
  template: { key: string; label: string; fields: Field[] } | null; familyName?: string | null;
  spec?: SpecRow[]; specSaved?: boolean; parts?: PartRow[]; removed?: Removed[]; open?: boolean; canCorrect?: boolean; canRemove?: boolean;
}

/** On the New work order screen for a Test order: the drawings linked to this exact part, or a manual pick when there are none. */
export function TestDrawingSection({ partId, picked, onPick }: { partId?: string | null; picked: { id: string; label: string }[]; onPick: (v: { id: string; label: string }[]) => void }) {
  const q = useQuery({ queryKey: ['drawings-for-part', partId ?? '', ''], enabled: !!partId, staleTime: 15_000,
    queryFn: () => get<PartDrawing[]>(`/api/drawings/for-part?partId=${partId}`) });
  if (!partId) return null;
  if (q.isLoading) return <Typography variant="caption" color="text.secondary">Looking for drawings…</Typography>;
  if ((q.data ?? []).length > 0) return <Box data-testid="test-drawings"><Typography variant="caption" color="text.secondary" component="div">Drawing linked to this part, attached automatically:</Typography><PartDrawingLinks partId={partId} /></Box>;
  return <Stack gap={1} data-testid="test-drawings">
    <Typography variant="body2" color="text.secondary" data-testid="test-no-drawing">No drawing linked to this part</Typography>
    <DrawingPicker value={picked} onChange={onPick} label="Pick a drawing by hand (optional)" />
  </Stack>;
}

export const attachDrawings = async (id: string, ids: string[]) => { for (const d of ids) await post(`${S}/tracking/${id}/drawings`, { drawingId: d }); };

const blank = (v?: string | null) => !v || !v.trim();

/** The test work instruction results on a Test work order: the drawing spec once, then Part 1, Part 2, ... each against the spec. No automatic pass or fail. */
export function TestResultsPanel({ id }: { id: string }) {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['sqf-test', id], queryFn: () => get<TestData>(`${S}/tracking/${id}/test`) });
  const [err, setErr] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  const refresh = () => qc.invalidateQueries({ queryKey: ['sqf-test', id] });
  const run = async (fn: () => Promise<unknown>) => { setBusy(true); setErr(null); try { await fn(); await refresh(); return true; } catch (e) { setErr((e as Error).message); return false; } finally { setBusy(false); } };
  const d = q.data;
  if (!d) return null;
  if (!d.template) return <Typography variant="body2" color="text.secondary" data-testid="test-none">No test fields set up for this product family yet.</Typography>;
  const spec = d.spec ?? [];
  const added = d.template.fields.filter((f) => f.added);
  return <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Test work instruction results" data-testid="test-results">
    <Typography variant="h3" sx={{ mb: 0.5 }}>Test work instruction results</Typography>
    <Typography variant="caption" color="text.secondary" component="div" sx={{ mb: 1.5 }}>Enter the drawing spec once, then add each tested part's own results. Every part prints as its own section on the test report. There is no automatic pass or fail.</Typography>
    {err && <Alert severity="error" sx={{ mb: 1.5 }} onClose={() => setErr(null)}>{err}</Alert>}
    <SpecEditor key={JSON.stringify(spec)} spec={spec} d={d} busy={busy} onSave={(values, reason) => run(() => put(`${S}/tracking/${id}/test/spec`, { values, reason }))} />
    {spec.length === 0 && <Typography variant="body2" color="text.secondary" data-testid="test-no-fields">No spec fields yet. Use Add spec field to add the ones on the drawing.</Typography>}
    <SpecFields id={id} d={d} added={added} busy={busy} run={run} />
    <Stack gap={2} sx={{ mt: 2 }}>
      {(d.parts ?? []).map((p) => <PartEditor key={p.id + (p.editedAt ?? '') + spec.map((x) => x.key + x.label + x.unit).join()} p={p} spec={spec} d={d} busy={busy}
        onSave={(values, note, reason) => run(() => put(`${S}/tracking/test/parts/${p.id}`, { values, note, reason }))}
        onRemove={(reason) => run(() => post(`${S}/tracking/test/parts/${p.id}/remove`, { reason }))} />)}
    </Stack>
    {d.open && <Button sx={{ mt: 2 }} variant="contained" disabled={busy} data-testid="test-add-part" onClick={() => run(() => post(`${S}/tracking/${id}/test/parts`, { values: [] }))}>Add part result</Button>}
    {(d.removed ?? []).length > 0 && <Stack gap={0.5} sx={{ mt: 2 }} data-testid="test-removed">{d.removed!.map((r) =>
      <Typography key={r.partIndex} variant="caption" color="text.secondary">Part {r.partIndex} test results removed by {r.removedBy} on {nyTime(r.removedAt)}{r.removeReason ? `. Reason: ${r.removeReason}` : ''} (kept in history)</Typography>)}</Stack>}
  </Paper>;
}

function SpecEditor({ spec, d, busy, onSave }: { spec: SpecRow[]; d: TestData; busy: boolean; onSave: (v: { key: string; value: string; unit: string }[], reason: string) => Promise<boolean> }) {
  const [rows, setRows] = useState(spec); const [reason, setReason] = useState('');
  useEffect(() => setRows(spec), [spec]);
  const changed = rows.some((r, i) => r.value !== spec[i].value || r.unit !== spec[i].unit);
  const correcting = rows.some((r, i) => (!blank(spec[i].value) && r.value !== spec[i].value) || (d.specSaved && r.unit !== spec[i].unit));
  const locked = (i: number) => !d.open && !d.canCorrect || (!blank(spec[i].value) && !d.canCorrect);
  return <Box data-testid="test-spec">
    <Typography variant="subtitle2" sx={{ mb: 1 }}>Drawing spec</Typography>
    <Stack gap={1}>{rows.map((r, i) => <Stack key={r.key} direction={{ xs: 'column', sm: 'row' }} gap={1} alignItems={{ sm: 'center' }}>
      <Typography variant="body2" sx={{ minWidth: 230 }}>{r.label}</Typography>
      <TextField size="small" label={`Spec ${r.label}`} value={r.value} disabled={locked(i)} inputProps={{ inputMode: 'decimal', 'data-testid': `spec-${r.key}` }}
        onChange={(e) => setRows(rows.map((x, j) => j === i ? { ...x, value: e.target.value } : x))} />
      <TextField size="small" label="Unit" value={r.unit} disabled={locked(i) || (!!d.specSaved && !d.canCorrect)} sx={{ width: 110 }} inputProps={{ maxLength: 20, 'data-testid': `spec-unit-${r.key}` }}
        onChange={(e) => setRows(rows.map((x, j) => j === i ? { ...x, unit: e.target.value } : x))} />
    </Stack>)}</Stack>
    {changed && <Stack direction={{ xs: 'column', sm: 'row' }} gap={1} sx={{ mt: 1 }}>
      {correcting && <TextField size="small" required label="Reason for the correction" value={reason} onChange={(e) => setReason(e.target.value)} sx={{ minWidth: 300 }} inputProps={{ maxLength: 500, 'data-testid': 'spec-reason' }} />}
      <Button variant="contained" disabled={busy || (correcting && !reason.trim())} data-testid="spec-save" onClick={async () => { if (await onSave(rows.map(({ key, value, unit }) => ({ key, value, unit })), reason)) setReason(''); }}>Save spec</Button>
    </Stack>}
  </Box>;
}

function PartEditor({ p, spec, d, busy, onSave, onRemove }: { p: PartRow; spec: SpecRow[]; d: TestData; busy: boolean;
  onSave: (v: { key: string; value: string }[], note: string, reason: string) => Promise<boolean>; onRemove: (reason: string) => Promise<boolean> }) {
  const [vals, setVals] = useState(p.values); const [note, setNote] = useState(p.note ?? ''); const [reason, setReason] = useState('');
  const [rm, setRm] = useState(false); const [rmWhy, setRmWhy] = useState('');
  const changed = vals.some((v, i) => v.value !== p.values[i].value) || note !== (p.note ?? '');
  const correcting = vals.some((v, i) => !blank(p.values[i].value) && v.value !== p.values[i].value) || (!!p.note && note !== p.note);
  const locked = (i: number) => (!d.open && !d.canCorrect) || (!blank(p.values[i].value) && !d.canCorrect);
  const unit = (k: string) => spec.find((s) => s.key === k)?.unit ?? '';
  return <Paper variant="outlined" sx={{ p: 1.5 }} data-testid={`test-part-${p.partIndex}`}>
    <Stack direction="row" alignItems="center" gap={1} sx={{ mb: 1 }}>
      <Typography variant="subtitle1" sx={{ fontWeight: 600 }}>Part {p.partIndex} test results</Typography>
      <Box sx={{ flex: 1 }} />
      {d.canRemove && <Button size="small" color="error" onClick={() => setRm(true)} data-testid={`test-part-remove-${p.partIndex}`}>Remove</Button>}
    </Stack>
    <Table size="small"><TableHead><TableRow><TableCell>Test</TableCell><TableCell>Spec</TableCell><TableCell>Actual</TableCell></TableRow></TableHead>
      <TableBody>{spec.map((s, i) => { const v = vals[i]?.value ?? ''; return <TableRow key={s.key}>
        <TableCell>{s.label}</TableCell>
        <TableCell>{blank(s.value) ? <Typography variant="body2" color="text.secondary">Not entered</Typography> : `${s.value} ${s.unit}`}</TableCell>
        <TableCell><Stack direction="row" alignItems="center" gap={1}>
          <TextField size="small" label={`Actual ${s.label}`} value={v} disabled={locked(i)} color={blank(v) ? 'warning' : undefined} focused={blank(v) ? true : undefined}
            inputProps={{ inputMode: 'decimal', 'data-testid': `part-${p.partIndex}-${s.key}` }} sx={{ width: 160 }}
            onChange={(e) => setVals(vals.map((x, j) => j === i ? { ...x, value: e.target.value } : x))} />
          <Typography variant="body2">{unit(s.key)}</Typography>
          {blank(p.values[i]?.value) && <Chip size="small" color="warning" variant="outlined" label="Blank" />}
        </Stack></TableCell></TableRow>; })}</TableBody></Table>
    <TextField sx={{ mt: 1 }} fullWidth size="small" multiline label="Note (optional)" value={note} disabled={!!p.note && !d.canCorrect} onChange={(e) => setNote(e.target.value)} inputProps={{ maxLength: 2000, 'data-testid': `part-${p.partIndex}-note` }} />
    <Typography variant="caption" color="text.secondary" component="div" sx={{ mt: 0.5 }}>Recorded by {p.recordedBy}, {nyTime(p.recordedAt)}{p.editedAt ? ` · last changed by ${p.editedBy}, ${nyTime(p.editedAt)}` : ''}</Typography>
    {changed && <Stack direction={{ xs: 'column', sm: 'row' }} gap={1} sx={{ mt: 1 }}>
      {correcting && <TextField size="small" required label="Reason for the correction" value={reason} onChange={(e) => setReason(e.target.value)} sx={{ minWidth: 300 }} inputProps={{ maxLength: 500, 'data-testid': `part-${p.partIndex}-reason` }} />}
      <Button variant="contained" disabled={busy || (correcting && !reason.trim())} data-testid={`part-${p.partIndex}-save`} onClick={async () => { if (await onSave(vals, note, reason)) setReason(''); }}>Save Part {p.partIndex}</Button>
    </Stack>}
    <Dialog open={rm} onClose={() => setRm(false)} maxWidth="xs" fullWidth>
      <FormDialogTitle>Remove Part {p.partIndex} test results?</FormDialogTitle>
      <DialogContent><Typography variant="body2" sx={{ mb: 1.5 }}>The results are kept in history and the other parts keep their numbers.</Typography>
        <TextField fullWidth size="small" required label="Reason" value={rmWhy} onChange={(e) => setRmWhy(e.target.value)} inputProps={{ maxLength: 500, 'data-testid': 'test-part-remove-reason' }} /></DialogContent>
      <DialogActions><Button onClick={() => setRm(false)}>Cancel</Button>
        <Button color="error" variant="contained" disabled={busy || !rmWhy.trim()} data-testid="test-part-remove-confirm" onClick={async () => { if (await onRemove(rmWhy)) setRm(false); }}>Remove</Button></DialogActions>
    </Dialog>
  </Paper>;
}

/** Spec fields added on this order only (the family template is not changed): anyone working the open order can add one; renaming or removing needs the correct test results permission and a reason. */
function SpecFields({ id, d, added, busy, run }: { id: string; d: TestData; added: Field[]; busy: boolean; run: (fn: () => Promise<unknown>) => Promise<boolean> }) {
  const [dlg, setDlg] = useState<null | { mode: 'add' | 'edit' | 'remove'; key?: string; label: string; unit: string; value: string; reason: string }>(null);
  const set = (k: string, v: string) => setDlg(dlg && { ...dlg, [k]: v });
  const save = async () => {
    if (!dlg) return;
    const ok = await run(() => dlg.mode === 'add' ? post(`${S}/tracking/${id}/test/fields`, { label: dlg.label, unit: dlg.unit, value: dlg.value })
      : dlg.mode === 'edit' ? put(`${S}/tracking/${id}/test/fields/${dlg.key}`, { label: dlg.label, unit: dlg.unit, reason: dlg.reason })
      : post(`${S}/tracking/${id}/test/fields/${dlg.key}/remove`, { reason: dlg.reason }));
    if (ok) setDlg(null);
  };
  const ready = dlg && (dlg.mode === 'add' ? !!dlg.label.trim() : dlg.mode === 'edit' ? !!dlg.label.trim() && !!dlg.reason.trim() : !!dlg.reason.trim());
  return <Box sx={{ mt: 1 }}>
    {added.length > 0 && <Stack gap={0.5} sx={{ mb: 1 }} data-testid="test-added-fields">{added.map((f) => <Stack key={f.key} direction="row" alignItems="center" gap={1}>
      <Chip size="small" variant="outlined" label={`Added on this order: ${f.label}${f.defaultUnit ? ` (${f.defaultUnit})` : ''}`} />
      {d.canCorrect && <><Button size="small" data-testid={`test-field-edit-${f.label}`} onClick={() => setDlg({ mode: 'edit', key: f.key, label: f.label, unit: f.defaultUnit, value: '', reason: '' })}>Rename</Button>
        <Button size="small" color="error" data-testid={`test-field-remove-${f.label}`} onClick={() => setDlg({ mode: 'remove', key: f.key, label: f.label, unit: f.defaultUnit, value: '', reason: '' })}>Remove</Button></>}
    </Stack>)}</Stack>}
    {d.open && <Button size="small" variant="outlined" disabled={busy} data-testid="test-add-field" onClick={() => setDlg({ mode: 'add', label: '', unit: '', value: '', reason: '' })}>Add spec field</Button>}
    <Dialog open={!!dlg} onClose={() => setDlg(null)} maxWidth="xs" fullWidth>
      <FormDialogTitle>{dlg?.mode === 'add' ? 'Add spec field' : dlg?.mode === 'edit' ? 'Rename spec field' : `Remove ${dlg?.label}?`}</FormDialogTitle>
      <DialogContent><Stack gap={1.5} sx={{ pt: 1 }}>
        {dlg?.mode === 'add' && <Typography variant="body2" color="text.secondary">Added to this work order only. It shows in the drawing spec and in every part's results.</Typography>}
        {dlg?.mode === 'remove' && <Typography variant="body2" color="text.secondary">The field and any values entered stay in history.</Typography>}
        {dlg?.mode !== 'remove' && <><TextField size="small" required label="Field name" value={dlg?.label ?? ''} onChange={(e) => set('label', e.target.value)} inputProps={{ maxLength: 80, 'data-testid': 'test-field-label' }} />
          <TextField size="small" label="Unit" value={dlg?.unit ?? ''} onChange={(e) => set('unit', e.target.value)} inputProps={{ maxLength: 20, 'data-testid': 'test-field-unit' }} /></>}
        {dlg?.mode === 'add' && <TextField size="small" label="Drawing spec" value={dlg.value} onChange={(e) => set('value', e.target.value)} inputProps={{ inputMode: 'decimal', 'data-testid': 'test-field-value' }} />}
        {dlg?.mode !== 'add' && <TextField size="small" required label="Reason" value={dlg?.reason ?? ''} onChange={(e) => set('reason', e.target.value)} inputProps={{ maxLength: 500, 'data-testid': 'test-field-reason' }} />}
      </Stack></DialogContent>
      <DialogActions><Button onClick={() => setDlg(null)}>Cancel</Button>
        <Button variant="contained" color={dlg?.mode === 'remove' ? 'error' : 'primary'} disabled={busy || !ready} data-testid="test-field-save" onClick={save}>{dlg?.mode === 'add' ? 'Add field' : dlg?.mode === 'edit' ? 'Save' : 'Remove'}</Button></DialogActions>
    </Dialog>
  </Box>;
}
