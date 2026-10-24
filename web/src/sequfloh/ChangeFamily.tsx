import { useEffect, useState } from 'react';
import { FormDialogTitle } from '../components/FullScreen';
import { Alert, Box, Button, Dialog, DialogActions, DialogContent, MenuItem, Paper, Stack, Tab, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Tabs, TextField, Typography } from '@mui/material';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, getBlob, post } from '../api';
import { ErrorState, Mono, S } from './common';

interface Family { id: string; code: string; label: string; isActive: boolean }
interface AttrDef { key: string; label: string }
interface Row { id: string; partNumber: string; name: string; drawingNumber?: string | null; numbers: Record<string, string>; family?: string | null; drawing?: string | null }
interface Preview { count: number; alreadyInTarget: number; target?: string | null; source?: string | null; parts: Row[]; unmatched?: Row[] | null }
interface FolderList { folders: { id: string; name: string; folders: number }[] }
type Mode = 'ids' | 'filter' | 'folder' | 'pattern';

/** Drill down the drawing folders: one list per level; the deepest choice is the folder. */
function FolderPicker({ value, onChange }: { value: string[]; onChange: (path: string[]) => void }) {
  const levels = [undefined, ...value];
  return <Stack direction="row" gap={1} flexWrap="wrap">
    {levels.map((parent, i) => <FolderLevel key={(parent ?? 'top') + i} parent={parent} value={value[i] ?? ''} label={i === 0 ? 'Drawing folder' : 'Subfolder (optional)'}
      onChange={(id) => onChange(id ? [...value.slice(0, i), id] : value.slice(0, i))} />)}
  </Stack>;
}

function FolderLevel({ parent, value, label, onChange }: { parent?: string; value: string; label: string; onChange: (id: string) => void }) {
  const q = useQuery({ queryKey: ['drawing-folders-pick', parent ?? ''], queryFn: () => get<FolderList>(`/api/drawings/folders${parent ? `?parentId=${parent}` : ''}`) });
  const list = q.data?.folders ?? [];
  if (parent && list.length === 0) return null;
  return <TextField select size="small" label={label} value={value} onChange={(e) => onChange(e.target.value)} sx={{ minWidth: 240 }} inputProps={{ 'data-testid': `folder-level-${parent ?? 'top'}` }}>
    <MenuItem value="">{parent ? 'All of this folder' : 'Choose…'}</MenuItem>
    {list.map((f) => <MenuItem key={f.id} value={f.id}>{f.name}</MenuItem>)}
  </TextField>;
}

function Numbers({ r, defs }: { r: Row; defs: AttrDef[] }) {
  const extra = defs.filter((d) => r.numbers[d.key]).map((d) => `${d.label}: ${r.numbers[d.key]}`);
  return <>{[r.drawingNumber ? `DWG #: ${r.drawingNumber}` : '', ...extra].filter(Boolean).join(' · ') || '—'}</>;
}

function Rows({ rows, defs, testid, drawing }: { rows: Row[]; defs: AttrDef[]; testid: string; drawing: boolean }) {
  return <TableContainer component={Paper} variant="outlined" sx={{ maxHeight: 280 }} data-testid={testid}>
    <Table size="small" stickyHeader>
      <TableHead><TableRow><TableCell>Part number</TableCell><TableCell>Name</TableCell><TableCell>Other numbers</TableCell><TableCell>Product family now</TableCell>{drawing && <TableCell>Matching drawing</TableCell>}</TableRow></TableHead>
      <TableBody>{rows.slice(0, 500).map((r) => <TableRow key={r.id}><TableCell><Mono>{r.partNumber}</Mono></TableCell><TableCell>{r.name}</TableCell>
        <TableCell sx={{ fontSize: 12 }}><Numbers r={r} defs={defs} /></TableCell><TableCell>{r.family ?? '—'}</TableCell>{drawing && <TableCell>{r.drawing ?? '—'}</TableCell>}</TableRow>)}</TableBody>
    </Table>
    {rows.length > 500 && <Typography variant="caption" sx={{ p: 1, display: 'block' }}>Showing the first 500 of {rows.length}.</Typography>}
  </TableContainer>;
}

/**
 * Move many parts to another product family. The parts can be the ticked ones, all matching the current filters, the parts of a family linked to drawings
 * in a drawing folder (and its subfolders), or those with a number matching a pattern. Shows what will change before anything is saved.
 */
export function ChangeFamilyDialog({ open, onClose, families, defs, ticked, filters, currentFamily, onDone }: {
  open: boolean; onClose: () => void; families: Family[]; defs: AttrDef[]; ticked: string[]; filters: Record<string, string>; currentFamily?: string; onDone: () => void;
}) {
  const qc = useQueryClient();
  const [mode, setMode] = useState<Mode>(ticked.length ? 'ids' : 'folder');
  const [path, setPath] = useState<string[]>([]);
  const [source, setSource] = useState(currentFamily ?? '');
  const [target, setTarget] = useState('');
  const [pattern, setPattern] = useState('');
  const [patternMode, setPatternMode] = useState('starts');
  const [preview, setPreview] = useState<Preview | null>(null);
  const [err, setErr] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<string | null>(null);
  useEffect(() => { if (open) { setMode(ticked.length ? 'ids' : 'folder'); setPreview(null); setErr(null); setDone(null); setSource(currentFamily ?? ''); } }, [open]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => setPreview(null), [mode, path, source, target, pattern, patternMode]);

  const body = () => ({
    mode, targetTypeId: target || null, sourceTypeId: source || null, pattern: pattern || null, patternMode,
    ...(mode === 'ids' ? { partIds: ticked } : {}),
    ...(mode === 'filter' ? { q: filters.q || null, partType: filters.partType || null, status: filters.status || null, tag: filters.tag || null } : {}),
    ...(mode === 'folder' ? { folderId: path[path.length - 1] ?? null } : {}),
  });
  const run = async (fn: () => Promise<void>) => { setBusy(true); setErr(null); try { await fn(); } catch (e) { setErr(e); } finally { setBusy(false); } };
  const doPreview = () => run(async () => setPreview(await post<Preview>(`${S}/parts/change-family/preview`, body())));
  const apply = () => run(async () => {
    const r = await post<{ changed: number; target: string }>(`${S}/parts/change-family`, body());
    setDone(`Moved ${r.changed} part${r.changed === 1 ? '' : 's'} to ${r.target}.`); setPreview(null);
    await qc.invalidateQueries({ queryKey: ['sqf-parts'] }); await qc.invalidateQueries({ queryKey: ['sqf-parts-untyped'] }); onDone();
  });
  const exportUnmatched = () => run(async () => {
    const a = document.createElement('a'); a.href = URL.createObjectURL(await getBlob(`${S}/parts/change-family/unmatched.xlsx?sourceTypeId=${source}`));
    a.download = 'parts-without-drawings.xlsx'; a.click(); URL.revokeObjectURL(a.href);
  });
  const ready = !!target && (mode === 'ids' ? ticked.length > 0 : mode === 'folder' ? path.length > 0 && !!source : mode === 'pattern' ? !!pattern.trim() : true);
  const familyPick = (label: string, value: string, set: (v: string) => void, testid: string, activeOnly: boolean, optional = false) =>
    <TextField select size="small" label={label} value={value} onChange={(e) => set(e.target.value)} sx={{ minWidth: 220 }} inputProps={{ 'data-testid': testid }}>
      {optional && <MenuItem value="">Any product family</MenuItem>}
      {families.filter((f) => !activeOnly || f.isActive).map((f) => <MenuItem key={f.id} value={f.id}>{f.label}{f.isActive ? '' : ' (inactive)'}</MenuItem>)}
    </TextField>;

  return <Dialog open={open} onClose={busy ? undefined : onClose} maxWidth="lg" fullWidth>
    <FormDialogTitle>Change product family</FormDialogTitle>
    <DialogContent>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
        Only the product family changes. Numbers kept for another family stay stored, so moving parts back loses nothing. Every change is recorded in the Audit Trail.</Typography>
      <Tabs value={mode} onChange={(_, v) => setMode(v)} variant="scrollable" sx={{ mb: 2 }}>
        <Tab value="ids" label={`Ticked parts (${ticked.length})`} disabled={!ticked.length} />
        <Tab value="filter" label="All matching the list filters" />
        <Tab value="folder" label="Parts linked to drawings in folder" />
        <Tab value="pattern" label="Part number pattern" />
      </Tabs>
      <Stack gap={2}>
        {mode === 'filter' && <Typography variant="body2">Every part matching the search and filters on the list now{filters.q ? ` (search “${filters.q}”)` : ''}.</Typography>}
        {mode === 'folder' && <>
          <Typography variant="body2">Selects the parts of the chosen family linked to any drawing in the folder or its subfolders.</Typography>
          <FolderPicker value={path} onChange={setPath} />
        </>}
        <Stack direction="row" gap={1} flexWrap="wrap">
          {(mode === 'folder' || mode === 'pattern') && familyPick('Product family now', source, setSource, 'cf-source', false, mode === 'pattern')}
          <TextField size="small" label={mode === 'pattern' ? 'Part number pattern' : 'And part number pattern (optional)'} value={pattern} onChange={(e) => setPattern(e.target.value)}
            helperText="Any of the part's numbers; capitals do not matter" inputProps={{ 'data-testid': 'cf-pattern' }} />
          <TextField select size="small" label="Match" value={patternMode} onChange={(e) => setPatternMode(e.target.value)} sx={{ minWidth: 130 }}>
            <MenuItem value="starts">Starts with</MenuItem><MenuItem value="contains">Contains</MenuItem><MenuItem value="ends">Ends with</MenuItem><MenuItem value="equals">Is exactly</MenuItem>
          </TextField>
          {familyPick('Move to product family', target, setTarget, 'cf-target', true)}
        </Stack>
        {!!err && <ErrorState error={err} />}
        {done && <Alert severity="success" data-testid="cf-done">{done}</Alert>}
        {preview && <Box data-testid="cf-preview">
          <Alert severity={preview.count ? 'info' : 'warning'} sx={{ mb: 1 }} data-testid="cf-count">
            {preview.count} part{preview.count === 1 ? '' : 's'} will move to {preview.target}.{preview.alreadyInTarget ? ` ${preview.alreadyInTarget} already in ${preview.target} will be left as they are.` : ''}</Alert>
          <Rows rows={preview.parts} defs={defs} testid="cf-parts" drawing />
          {preview.unmatched && <Box sx={{ mt: 2 }}>
            <Stack direction="row" alignItems="center" justifyContent="space-between">
              <Typography variant="subtitle2" data-testid="cf-unmatched-title">Unmatched — check these: {preview.unmatched.length} {preview.source} part{preview.unmatched.length === 1 ? '' : 's'} with no linked drawing</Typography>
              {preview.unmatched.length > 0 && <Button size="small" onClick={exportUnmatched} disabled={busy}>Export to Excel</Button>}
            </Stack>
            {preview.unmatched.length > 0 && <Rows rows={preview.unmatched} defs={defs} testid="cf-unmatched" drawing={false} />}
          </Box>}
        </Box>}
      </Stack>
    </DialogContent>
    <DialogActions>
      <Button onClick={onClose} disabled={busy}>Close</Button>
      <Button variant="outlined" onClick={doPreview} disabled={!ready || busy}>Preview</Button>
      <Button variant="contained" onClick={apply} disabled={!preview || !preview.count || busy}>Apply to {preview?.count ?? 0} part{preview?.count === 1 ? '' : 's'}</Button>
    </DialogActions>
  </Dialog>;
}
