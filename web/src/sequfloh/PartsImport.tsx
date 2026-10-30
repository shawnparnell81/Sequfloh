import { useState } from 'react';
import CloseIcon from '@mui/icons-material/Close';
import { FormDialogTitle } from '../components/FullScreen';
import { Alert, Box, Button, Checkbox, Chip, Dialog, DialogActions, DialogContent, FormControlLabel, IconButton, ListSubheader, MenuItem, Radio, RadioGroup, Stack, Table, TableBody, TableCell,
  TableContainer, TableHead, TableRow, TextField, Typography } from '@mui/material';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post, put } from '../api';
import { useSites } from '../components/Kit';
import { LOT_CHOICES, S, UNITS } from './common';

interface ImportTable { fileName: string; headers: string[]; rows: string[][]; mapping: Record<string, number>; warnings: string[]; headerRow: number; rowNumbers?: number[]; top?: { row: number; cells: string[] }[]; tabs?: ImportTab[] | null; skippedTabs?: string[] | null }
interface ImportTab { name: string; familyId: string; family: string; headers: string[]; rows: string[][]; mapping: Record<string, number>; headerRow: number; rowNumbers: number[] }
interface TabCount { tab: string; family: string; rows: number; add: number; update: number; skip: number; errors: number }
interface PlanRow { row: number; action: 'Add' | 'Update' | 'Skip' | 'Error'; partNumber?: string; name?: string; messages: string[]; repeat?: boolean; tab?: string | null }
interface Plan { rows: PlanRow[]; add: number; update: number; skip: number; errors: number; tabs?: TabCount[] | null; skippedTabs?: string[] | null }
interface LayoutData { headerRow?: number; headers?: string[]; columns?: Record<string, { title: string; index: number }>; fixed?: Record<string, string>; updateExisting?: boolean; keepRepeats?: boolean; removed?: string[] }
interface SavedLayout { id: string; name: string; layout: LayoutData }
/** Fields that are always imported and cannot be removed from the mapping. Part number can be removed: the part number then comes from the other number columns. */
const KEEP = ['name'];
const norm = (h: string) => h.trim().toLowerCase().replace(/\s+/g, ' ');
const actionColor = { Add: 'success', Update: 'info', Skip: 'default', Error: 'error' } as const;

/** Bulk import into the Part Master: choose a file, check the column mapping, preview, then confirm. */
/** preset: a product family label set as the Name and Product family for all rows (from a family's parts list). */
export function ImportPartsDialog({ open, onClose, preset }: { open: boolean; onClose: () => void; preset?: string }) {
  const sites = useSites();
  const types = useQuery({ queryKey: ['sqf-part-types'], queryFn: () => get<{ id: string; label: string; isActive: boolean }[]>(`${S}/reference/product-families`), enabled: open });
  const fields = useQuery({ queryKey: ['sqf-part-import-fields'], queryFn: () => get<{ key: string; label: string; choices?: 'families' | 'units' | 'yesNo' | 'lot' | null; isNumber?: boolean }[]>(`${S}/parts/import/fields`), enabled: open });
  const qc = useQueryClient();
  const [file, setFile] = useState<File | null>(null);
  const [allTables, setAllTables] = useState(false);
  const [allTabs, setAllTabs] = useState(false);
  const [table, setTable] = useState<ImportTable | null>(null);
  const [mapping, setMapping] = useState<Record<string, number>>({});
  const [siteId, setSiteId] = useState('');
  const [update, setUpdate] = useState(false);
  const [plan, setPlan] = useState<Plan | null>(null);
  const [done, setDone] = useState<Plan | null>(null);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);
  const [fixed, setFixed] = useState<Record<string, string>>({});
  const [keepRepeats, setKeepRepeats] = useState(false);
  const [removed, setRemoved] = useState<string[]>([]);
  const [layoutId, setLayoutId] = useState('');
  const [naming, setNaming] = useState<{ mode: 'save' | 'rename'; name: string } | null>(null);
  const layouts = useQuery({ queryKey: ['sqf-part-import-layouts'], queryFn: () => get<SavedLayout[]>(`${S}/parts/import/layouts`), enabled: open });
  const site = siteId || sites.data?.[0]?.id || '';
  const reset = () => { setFile(null); setTable(null); setMapping({}); setPlan(null); setDone(null); setErr(''); setUpdate(false); setAllTables(false); setAllTabs(false); setFixed({}); setKeepRepeats(false); setRemoved([]); setLayoutId(''); setNaming(null); };
  const run = async (fn: () => Promise<void>) => { setBusy(true); setErr(''); try { await fn(); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); } };
  /** Fills the mapping from a saved layout: each column is found by its title, or by its position when no title matches. */
  const applyLayout = (t: ImportTable, l: LayoutData) => {
    const titles = t.headers.map(norm); const m: Record<string, number> = {};
    for (const [key, c] of Object.entries(l.columns ?? {})) {
      const i = titles.indexOf(norm(c.title));
      if (i >= 0) m[key] = i; else if (c.index < t.headers.length) m[key] = c.index;
    }
    setMapping(m); setFixed({ ...(l.fixed ?? {}) }); setUpdate(!!l.updateExisting); setKeepRepeats(!!l.keepRepeats); setRemoved((l.removed ?? []).filter((k) => !KEEP.includes(k))); setPlan(null);
  };
  const read = (headerRow?: number, layout?: LayoutData) => run(async () => {
    const fd = new FormData(); fd.append('file', file!);
    const t = await post<ImportTable>(`${S}/parts/import/read?allTables=${allTables}${allTabs ? '&allTabs=true' : ''}${headerRow ? `&headerRow=${headerRow}` : ''}`, fd);
    setTable(t);
    if (layout) { applyLayout(t, layout); return; }
    const { name: _n, ...m } = t.mapping; if (preset) delete m.typeName; setMapping(m); setFixed(preset ? { name: preset, typeName: preset } : {}); setPlan(null);
  });
  const chooseLayout = (id: string) => {
    setLayoutId(id); const l = layouts.data?.find((x) => x.id === id); if (!l || !table) return;
    if (l.layout.headerRow && l.layout.headerRow !== table.headerRow && table.top?.some((x) => x.row === l.layout.headerRow)) read(l.layout.headerRow, l.layout); else applyLayout(table, l.layout);
  };
  const currentLayout = (): LayoutData => ({
    headerRow: table!.headerRow, headers: table!.headers, updateExisting: update, keepRepeats, removed,
    columns: Object.fromEntries(Object.entries(mapping).filter(([k]) => !removed.includes(k)).map(([k, i]) => [k, { title: table!.headers[i] ?? '', index: i }])),
    fixed: Object.fromEntries(Object.entries(fixed).filter(([k]) => !removed.includes(k))),
  });
  const saveName = () => run(async () => {
    const name = naming!.name.trim(); if (!name) return;
    const x = naming!.mode === 'save' ? await post<SavedLayout>(`${S}/parts/import/layouts`, { name, layout: currentLayout() })
      : await put<SavedLayout>(`${S}/parts/import/layouts/${layoutId}/name`, { name });
    await qc.invalidateQueries({ queryKey: ['sqf-part-import-layouts'] }); setLayoutId(x.id); setNaming(null);
  });
  const deleteLayout = () => run(async () => {
    const l = layouts.data?.find((x) => x.id === layoutId); if (!l || !window.confirm(`Delete the layout ${l.name}?`)) return;
    await del(`${S}/parts/import/layouts/${layoutId}`); await qc.invalidateQueries({ queryKey: ['sqf-part-import-layouts'] }); setLayoutId('');
  });
  const removeField = (key: string) => { setRemoved([...removed, key]); setPlan(null); };
  const multi = !!table?.tabs;
  const nameOk = multi || !!fixed.name;
  const keep = <T,>(o: Record<string, T>) => Object.fromEntries(Object.entries(o).filter(([k]) => !removed.includes(k)));
  const body = (upd = update, map = mapping) => ({ fileName: table!.fileName, headers: table!.headers, rows: table!.rows, mapping: keep(map), siteId: site, updateExisting: upd,
    rowNumbers: table!.rowNumbers, keepRepeats, fixed: keep(fixed), ...(multi ? { tabs: table!.tabs, skippedTabs: table!.skippedTabs ?? [], fixed: {} } : {}) });
  const preview = (upd = update, map = mapping) => run(async () => setPlan(await post<Plan>(`${S}/parts/import/preview`, body(upd, map))));
  const confirm = () => run(async () => { setDone(await post<Plan>(`${S}/parts/import`, body())); qc.invalidateQueries({ queryKey: ['sqf-parts'] }); });
  /** A field's dropdown value: '' not imported, 'col:<index>' a file column, 'fix:<choice>' the same choice for all rows. */
  const valueOf = (key: string) => (fixed[key] ? `fix:${fixed[key]}` : mapping[key] !== undefined ? `col:${mapping[key]}` : '');
  const setMap = (key: string, v: string) => {
    const n = { ...mapping }; const x = { ...fixed }; delete n[key]; delete x[key];
    if (v.startsWith('col:')) n[key] = Number(v.slice(4)); else if (v.startsWith('fix:')) x[key] = v.slice(4);
    setMapping(n); setFixed(x); setPlan(null);
  };
  const choiceList = (kind?: string | null): string[] => kind === 'families' ? (types.data ?? []).filter((t) => t.isActive).map((t) => t.label)
    : kind === 'units' ? UNITS : kind === 'yesNo' ? ['Yes', 'No'] : kind === 'lot' ? LOT_CHOICES : [];
  const columnItems = (key: string) => table!.headers.map((h, i) => <MenuItem key={`${key}-c${i}`} value={`col:${i}`} sx={{ pl: 4 }}>{h || `Column ${i + 1}`}</MenuItem>);
  const noNumbers = !!fields.data && !(fields.data ?? []).some((f) => f.isNumber && !removed.includes(f.key) && mapping[f.key] !== undefined);
  const isDocx = /\.docx?$/i.test(file?.name ?? '');
  const isBook = /\.xls[xm]?$/i.test(file?.name ?? '');
  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="lg" TransitionProps={{ onEnter: reset }}>
      <FormDialogTitle>Import parts</FormDialogTitle>
      <DialogContent>
        {err && <Alert severity="error" sx={{ mb: 2 }}>{err}</Alert>}
        {done ? <Alert severity="success" data-testid="import-done">Imported from {table?.fileName}: {done.add} added, {done.update} updated, {done.skip} skipped{done.errors ? `, ${done.errors} row${done.errors === 1 ? '' : 's'} with errors not imported` : ''}.</Alert> : <>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
            Excel (.xlsx, .xlsm, .xls), CSV or a Word (.docx, .doc) table with a header row. The file is only read; macros never run. Serial numbers are not imported.
          </Typography>
          <Stack direction="row" gap={2} alignItems="center" flexWrap="wrap">
            <Button variant="outlined" component="label">Choose file<input hidden type="file" data-testid="import-file" accept=".xlsx,.xlsm,.xls,.csv,.docx,.doc"
              onChange={(e) => { setFile(e.target.files?.[0] ?? null); setTable(null); setPlan(null); e.target.value = ''; }} /></Button>
            <Typography variant="body2">{file?.name ?? 'No file chosen'}</Typography>
            {isBook && <FormControlLabel control={<Checkbox checked={allTabs} data-testid="all-tabs" onChange={(e) => { setAllTabs(e.target.checked); setTable(null); setPlan(null); }} />} label="Import every tab: tab name is the product family" />}
            {isDocx && <FormControlLabel control={<Checkbox checked={allTables} onChange={(e) => setAllTables(e.target.checked)} />} label="Read all tables (same headings)" />}
            <TextField select size="small" label="Site for new parts" value={site} onChange={(e) => { setSiteId(e.target.value); setPlan(null); }} sx={{ minWidth: 220 }}>
              {(sites.data ?? []).map((x) => <MenuItem key={x.id} value={x.id}>{x.code} · {x.name}</MenuItem>)}
            </TextField>
            <Button variant="contained" disabled={!file || busy} onClick={() => read()}>Read file</Button>
          </Stack>
          {table && <Box sx={{ mt: 3 }}>
            {table.warnings.map((w) => <Alert key={w} severity="info" sx={{ mb: 1 }}>{w}</Alert>)}
            {multi && <TabsSummary table={table} />}
            {!multi && <>
            {table.top && table.top.length > 1 && <Stack direction="row" gap={2} alignItems="center" sx={{ mb: 2 }}>
              <TextField select size="small" label="Header row" value={table.headerRow} onChange={(e) => read(Number(e.target.value))} sx={{ minWidth: 420 }} inputProps={{ 'data-testid': 'header-row' }}>
                {table.top.map((t) => <MenuItem key={t.row} value={t.row}>Row {t.row}: {t.cells.filter(Boolean).join(' · ').slice(0, 80)}</MenuItem>)}
              </TextField>
              <Typography variant="body2" color="text.secondary">Rows above the header row are not imported.</Typography>
            </Stack>}
            <Stack direction="row" gap={1} alignItems="center" flexWrap="wrap" sx={{ mb: 2 }}>
              <TextField select size="small" label="Layout" value={layoutId} onChange={(e) => chooseLayout(e.target.value)} sx={{ minWidth: 240 }} inputProps={{ 'data-testid': 'import-layout' }}
                SelectProps={{ displayEmpty: true }} InputLabelProps={{ shrink: true }}>
                <MenuItem value=""><em>None</em></MenuItem>
                {(layouts.data ?? []).map((l) => <MenuItem key={l.id} value={l.id}>{l.name}</MenuItem>)}
              </TextField>
              <Button size="small" variant="outlined" disabled={busy} onClick={() => setNaming({ mode: 'save', name: layouts.data?.find((x) => x.id === layoutId)?.name ?? '' })}>Save layout</Button>
              <Button size="small" disabled={busy || !layoutId} onClick={() => setNaming({ mode: 'rename', name: layouts.data?.find((x) => x.id === layoutId)?.name ?? '' })}>Rename</Button>
              <Button size="small" color="error" disabled={busy || !layoutId} onClick={deleteLayout}>Delete</Button>
              {naming && <>
                <TextField size="small" autoFocus label={naming.mode === 'save' ? 'Layout name' : 'New name'} placeholder="Fuel Injector list" value={naming.name}
                  onChange={(e) => setNaming({ ...naming, name: e.target.value })} onKeyDown={(e) => { if (e.key === 'Enter') saveName(); }} inputProps={{ maxLength: 200 }} />
                <Button size="small" variant="contained" disabled={busy || !naming.name.trim()} onClick={saveName}>{naming.mode === 'save' ? 'Save' : 'Rename'}</Button>
                <Button size="small" onClick={() => setNaming(null)}>Cancel</Button>
              </>}
            </Stack>
            <Typography variant="subtitle2" sx={{ mb: 1 }}>Column mapping · {table.rows.length} rows found</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>
              For each field, pick the column in your file that fills it. Name is picked here for every row and never comes from the file. Fields with set choices (Product family, Part class, Unit of measure, Assembly, Repairable, Lot controlled)
              can also use one choice for every row. Values from a column must match a choice; rows that do not are listed as errors in the preview.
            </Typography>
            <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(230px, 1fr))', gap: 1.5 }}>
              {(fields.data ?? []).filter((f) => !removed.includes(f.key)).map((f) => (
                <Stack key={f.key} direction="row" alignItems="center" gap={0.5}>
                <TextField fullWidth select size="small" label={f.key === 'name' ? 'Name (required, same for all rows)' : f.label} error={f.key === 'name' && !fixed.name} value={valueOf(f.key)} onChange={(e) => setMap(f.key, e.target.value)} inputProps={{ 'data-field': f.key }}
                  SelectProps={{ renderValue: (v) => { const x = String(v); return x.startsWith('fix:') ? `All rows: ${x.slice(4)}` : x.startsWith('col:') ? `Column: ${table.headers[Number(x.slice(4))] || `Column ${Number(x.slice(4)) + 1}`}` : f.key === 'name' ? 'Choose a name' : 'Not imported'; }, displayEmpty: true }}
                  InputLabelProps={{ shrink: true }}>
                  {f.key !== 'name' && <MenuItem value=""><em>Not imported</em></MenuItem>}
                  {f.choices ? [
                    <ListSubheader key="h1">Same for all rows</ListSubheader>,
                    ...choiceList(f.choices).map((c) => <MenuItem key={`${f.key}-f${c}`} value={`fix:${c}`} sx={{ pl: 4 }}>{c}</MenuItem>),
                    ...(f.key === 'name' ? [] : [<ListSubheader key="h2">From file column</ListSubheader>, ...columnItems(f.key)]),
                  ] : columnItems(f.key)}
                </TextField>
                {!KEEP.includes(f.key) && <IconButton size="small" aria-label={`Remove ${f.label}`} title="Remove this field; it is not imported" onClick={() => removeField(f.key)}><CloseIcon fontSize="small" /></IconButton>}
                </Stack>
              ))}
            </Box>
            {removed.length > 0 && <TextField select size="small" label="Add field" value="" onChange={(e) => { setRemoved(removed.filter((k) => k !== e.target.value)); setPlan(null); }} sx={{ mt: 1.5, minWidth: 230 }}
              SelectProps={{ displayEmpty: true, renderValue: () => 'Bring back a removed field' }} InputLabelProps={{ shrink: true }}>
              {(fields.data ?? []).filter((f) => removed.includes(f.key)).map((f) => <MenuItem key={f.key} value={f.key}>{f.label}</MenuItem>)}
            </TextField>}
            </>}
            <Stack direction="row" gap={2} alignItems="center" sx={{ mt: 2 }}>
              <Typography variant="body2">Parts already in the Part Master:</Typography>
              <RadioGroup row value={update ? 'update' : 'skip'} onChange={(e) => { const u = e.target.value === 'update'; setUpdate(u); if (plan) preview(u); }}>
                <FormControlLabel value="skip" control={<Radio size="small" />} label="Skip existing" />
                <FormControlLabel value="update" control={<Radio size="small" />} label={multi ? 'Fill in blanks of existing' : 'Update existing'} />
              </RadioGroup>
              <FormControlLabel control={<Checkbox size="small" checked={keepRepeats} data-testid="keep-repeats" onChange={(e) => { setKeepRepeats(e.target.checked); setPlan(null); }} />}
                label="Keep rows that repeat a Part number but differ" title="A row with the same Part number as another row but other values (for example another Position) is added as its own part. Exact duplicate rows are always skipped." />
              <Button variant="outlined" disabled={busy || !nameOk || (!multi && noNumbers)} onClick={() => preview()}>Preview</Button>
            </Stack>
            {!multi && noNumbers && <Alert severity="warning" sx={{ mt: 2 }} data-testid="number-required">Map at least one number column (Part number, OR #, DWG # or a cross-reference number such as NAPA) so each part gets a part number.</Alert>}
            {!nameOk && <Alert severity="warning" sx={{ mt: 2 }} data-testid="name-required">Choose the Name for all rows before you preview or import. The name is never taken from the file.</Alert>}
          </Box>}
          {plan && <Box sx={{ mt: 2 }}>
            <Stack direction="row" gap={1} sx={{ mb: 1 }} data-testid="import-counts">
              <Chip color="success" label={`Add ${plan.add}`} /><Chip color="info" label={`Update ${plan.update}`} /><Chip label={`Skip ${plan.skip}`} /><Chip color="error" label={`Errors ${plan.errors}`} />
            </Stack>
            {(plan.skippedTabs ?? []).map((m) => <Alert key={m} severity="warning" sx={{ mb: 1 }} data-testid="tab-skipped">{m}</Alert>)}
            {plan.tabs && <Table size="small" sx={{ mb: 2 }} data-testid="tab-counts">
              <TableHead><TableRow><TableCell>Tab</TableCell><TableCell>Product family</TableCell><TableCell align="right">Rows</TableCell><TableCell align="right">Add</TableCell><TableCell align="right">Update</TableCell><TableCell align="right">Skip</TableCell><TableCell align="right">Errors</TableCell></TableRow></TableHead>
              <TableBody>{plan.tabs.map((t) => <TableRow key={t.tab}><TableCell>{t.tab}</TableCell><TableCell>{t.family}</TableCell><TableCell align="right">{t.rows}</TableCell><TableCell align="right">{t.add}</TableCell>
                <TableCell align="right">{t.update}</TableCell><TableCell align="right">{t.skip}</TableCell><TableCell align="right" sx={t.errors ? { color: 'error.main', fontWeight: 600 } : undefined}>{t.errors}</TableCell></TableRow>)}</TableBody></Table>}
            <TableContainer sx={{ maxHeight: 340 }}><Table size="small" stickyHeader>
              <TableHead><TableRow><TableCell>Row</TableCell><TableCell>Action</TableCell><TableCell>Part number</TableCell><TableCell>Name</TableCell><TableCell>Notes</TableCell></TableRow></TableHead>
              <TableBody>{plan.rows.map((r) => (
                <TableRow key={`${r.tab ?? ''}-${r.row}`}><TableCell data-testid="plan-row">{r.tab ? `Tab ${r.tab}, row ${r.row}` : r.row}</TableCell><TableCell><Chip size="small" color={actionColor[r.action]} label={r.repeat ? 'Add (repeat Part number, different values)' : r.action} /></TableCell>
                  <TableCell>{r.partNumber}</TableCell><TableCell>{r.name}</TableCell><TableCell>{r.messages.join(' ')}</TableCell></TableRow>))}
              </TableBody></Table></TableContainer>
          </Box>}
        </>}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>{done ? 'Close' : 'Cancel'}</Button>
        {!done && <Button variant="contained" disabled={!plan || busy || !nameOk || plan.add + plan.update === 0} onClick={confirm}>Import {plan ? plan.add + plan.update : ''} parts</Button>}
      </DialogActions>
    </Dialog>
  );
}

/** The tabs an every-tab import found: each tab's product family, its header row, rows and the columns it maps; tabs that match no family are listed as skipped. */
function TabsSummary({ table }: { table: ImportTable }) {
  return <Box sx={{ mb: 2 }} data-testid="tabs-summary">
    {(table.skippedTabs ?? []).map((m) => <Alert key={m} severity="warning" sx={{ mb: 1 }}>{m}</Alert>)}
    <Typography variant="subtitle2" sx={{ mb: 1 }}>{table.tabs!.length} tabs to import · {table.tabs!.reduce((n, t) => n + t.rows.length, 0)} rows</Typography>
    <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>Each tab is read on its own: its header row and columns are found for it, and its parts get the tab's product family as Name and Product family.
      Parts already in the Part Master are only filled in where a field is empty; nothing they have is replaced.</Typography>
    <Table size="small"><TableHead><TableRow><TableCell>Tab</TableCell><TableCell>Product family</TableCell><TableCell align="right">Header row</TableCell><TableCell align="right">Rows</TableCell><TableCell>Columns used</TableCell></TableRow></TableHead>
      <TableBody>{table.tabs!.map((t) => <TableRow key={t.name}><TableCell>{t.name}</TableCell><TableCell>{t.family}</TableCell><TableCell align="right">{t.headerRow}</TableCell><TableCell align="right">{t.rows.length}</TableCell>
        <TableCell>{Object.values(t.mapping).map((i) => t.headers[i]).filter(Boolean).join(', ')}</TableCell></TableRow>)}</TableBody></Table>
  </Box>;
}
