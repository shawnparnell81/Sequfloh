import { useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { Link as RouterLink, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { Alert, Box, Button, Checkbox, Chip, Link, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, Menu, MenuItem, Paper, Stack, Switch, Tab, Tabs, TextField, Tooltip, Typography } from '@mui/material';
import UploadFileOutlined from '@mui/icons-material/UploadFileOutlined';
import DownloadOutlined from '@mui/icons-material/DownloadOutlined';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, api, get, post, put } from '../api';
import { useAuth } from '../auth';
import { useSites } from '../components/Kit';
import { fmtDateTime } from '../components/Status';
import { Empty, ErrorState, Filters, Loading, Mono, Paged, PagedTable, SqfPage, qs, useUnsaved } from './common';
import { HistoryTable, type AuditRow } from './Parts';
import { PartDrawingLinks } from './Drawings';

const A = '/api/imported-files';
const GREEN = '#0E8550';
const crumbs = (label: string, extra?: { label: string }) => [{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Imported Files', to: '/sequfloh/imported-files' }, { label }, ...(extra ? [extra] : [])];

interface FileRow { id: string; number: string; title: string; kind: 'spreadsheet' | 'document'; sourceFormat: string; originalFileName: string; currentVersion: number; updatedAt: string; updatedBy?: string; isBlankForm: boolean; blankForm?: string | null }
interface Detail {
  id: string; number: string; title: string; kind: 'spreadsheet' | 'document'; sourceFormat: string; originalFileName: string; currentVersion: number; createdAt: string; updatedAt: string; canEdit: boolean;
  isBlankForm: boolean; partNumber?: string | null; blankForm?: { id: string; number: string; title: string; version?: number | null } | null;
  copies?: { id: string; number: string; title: string; blankFormVersion?: number | null; createdAt: string; updatedAt: string; createdBy?: string; updatedBy?: string }[] | null;
  summary: { formulaTotal?: number; formulaClasses?: Record<string, number>; warnings?: string[]; security?: string[]; sheets?: { name: string; charts: number; pivots: number }[]; paragraphs?: number; tables?: number; controls?: number };
  versions: { version: number; fileName: string; sizeBytes: number; note?: string; createdAt: string; createdByName: string }[];
}

const kindLabel = (k: string) => (k === 'spreadsheet' ? 'Spreadsheet' : 'Word document');
const formatLabel = (f: string) => f.toUpperCase();
const today = () => new Date().toLocaleDateString('en-CA');
const listTitles: Record<string, string> = { spreadsheet: 'Spreadsheets', document: 'Word Documents', blank: 'Blank Forms', filled: 'Filled Forms' };

/** Asks for the title of a new file made from this one (a filled copy of a blank form, or a saved copy). */
function CopyDialog({ open, heading, initial, action, busy, error, onClose, onConfirm, partNumber }: { open: boolean; heading: string; initial: string; action: string; busy: boolean; error?: string | null; onClose: () => void; onConfirm: (title: string, partNumber?: string) => void; partNumber?: boolean }) {
  const [title, setTitle] = useState(initial);
  const [pn, setPn] = useState('');
  const [typed, setTyped] = useState('');
  useEffect(() => { const t = setTimeout(() => setTyped(pn), 400); return () => clearTimeout(t); }, [pn]);
  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} fullWidth maxWidth="sm" TransitionProps={{ onEnter: () => { setTitle(initial); setPn(''); setTyped(''); } }}>
      <DialogTitle>{heading}</DialogTitle>
      <DialogContent>
        {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
        <TextField autoFocus fullWidth label="Title" value={title} onChange={(e) => setTitle(e.target.value)} sx={{ mt: 1 }} inputProps={{ 'data-testid': 'copy-title' }} />
        {partNumber && <>
          <TextField fullWidth label="Part number (optional)" value={pn} onChange={(e) => setPn(e.target.value)} sx={{ mt: 2 }} inputProps={{ 'data-testid': 'copy-part-number' }}
            helperText="The part this copy is for. Its drawings can be opened from here and from the filled copy." />
          {typed.trim() && <Box sx={{ mt: 1 }}><PartDrawingLinks partNumber={typed} checkboxes /></Box>}
        </>}
      </DialogContent>
      <DialogActions><Button onClick={onClose} disabled={busy}>Cancel</Button><Button variant="contained" disabled={!title.trim() || busy} onClick={() => onConfirm(title.trim(), pn.trim() || undefined)} data-testid="copy-confirm">{busy ? 'Working…' : action}</Button></DialogActions>
    </Dialog>
  );
}

/** The part number a filled copy is for, with a tick box (and button) to open each of its drawings in a new window. */
function CopyPartNumber({ d, onSaved }: { d: Detail; onSaved: () => void }) {
  const [edit, setEdit] = useState(false);
  const [pn, setPn] = useState(d.partNumber ?? '');
  const save = async () => { await put(`${A}/${d.id}/part-number`, { partNumber: pn }); setEdit(false); onSaved(); };
  if (edit || (!d.partNumber && d.canEdit)) return <Stack direction="row" gap={1} alignItems="center">
    <TextField size="small" label="Part number" value={pn} onChange={(e) => setPn(e.target.value)} inputProps={{ 'data-testid': 'copy-header-part' }} sx={{ width: 200 }} />
    <Button size="small" onClick={save} disabled={pn.trim() === (d.partNumber ?? '')} data-testid="copy-header-part-save">Set</Button>
    {edit && <Button size="small" onClick={() => { setPn(d.partNumber ?? ''); setEdit(false); }}>Cancel</Button>}</Stack>;
  return <Stack direction="row" gap={1.5} alignItems="center" flexWrap="wrap" data-testid="copy-part">
    <Typography variant="body2">Part <Mono>{d.partNumber}</Mono></Typography>
    {d.canEdit && <Button size="small" onClick={() => setEdit(true)}>Change</Button>}
    <PartDrawingLinks partNumber={d.partNumber} checkboxes />
  </Stack>;
}

/** "Save as new copy": saves the pending edits into a new file and opens it; the file being edited stays as it is. */
function useSaveAsCopy(file: Detail, body: () => object) {
  const nav = useNavigate();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const confirm = async (title: string, after: () => void) => {
    setBusy(true); setError(null);
    try { const r = await post<{ id: string }>(`${A}/${file.id}/save-as-copy`, { baseVersion: file.currentVersion, title, ...body() }); after(); setOpen(false); nav(`/sequfloh/imported-files/${r.id}`); }
    catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  };
  const base = file.blankForm?.title ?? file.title;
  return { open: () => { setError(null); setOpen(true); }, dialog: (after: () => void) => <CopyDialog open={open} heading="Save as new copy" initial={`${base} – ${today()}`} action="Save copy" busy={busy} error={error}
    onClose={() => setOpen(false)} onConfirm={(t) => confirm(t, after)} /> };
}

/** The list of imported files, with upload. */
export function ImportedFilesPage() {
  const { can } = useAuth();
  const nav = useNavigate();
  const [sp] = useSearchParams();
  const kind = sp.get('kind') ?? '';
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1); const [size, setSize] = useState(25);
  const q = useQuery({ queryKey: ['imported-files', kind, search, page, size], queryFn: () => get<Paged<FileRow>>(`${A}?${qs({ q: search, kind, page, pageSize: size })}`) });
  const [open, setOpen] = useState(false);
  const title = listTitles[kind] ?? 'All Imported Files';
  return (
    <SqfPage title={title} crumbs={crumbs(title)} permission="sequfloh.view_imported_files"
      description="Excel, CSV and Word files brought into the app exactly as they are, with formulas, merged cells and formatting. Open one to view and edit it; every save keeps a new version."
      actions={can('sequfloh.import_files') && <Button variant="contained" startIcon={<UploadFileOutlined />} onClick={() => setOpen(true)} data-testid="import-file">Import a file</Button>}>
      <Filters search={search} onSearch={(s) => { setSearch(s); setPage(1); }} values={{}} onChange={() => undefined} onClear={() => setSearch('')} filters={[]} />
      <PagedTable q={q} page={page} size={size} onPage={setPage} onSize={setSize} onRow={(r) => nav(`/sequfloh/imported-files/${r.id}`)} empty="No files imported yet."
        cols={[{ key: 'number', label: 'Number', render: (r) => <Mono>{r.number}</Mono> }, { key: 'title', label: 'Title', render: (r) => <Stack gap={0.5} alignItems="flex-start"><Stack direction="row" gap={1} alignItems="center" flexWrap="wrap"><span>{r.title}</span>
            {r.isBlankForm && <Chip size="small" color="primary" variant="outlined" label="Blank form" />}</Stack>{r.blankForm && <Chip size="small" variant="outlined" label={`Filled from ${r.blankForm}`} />}</Stack> }, { key: 'kind', label: 'Type', render: (r) => kindLabel(r.kind) },
          { key: 'sourceFormat', label: 'Imported from', render: (r) => <Chip size="small" variant="outlined" label={formatLabel(r.sourceFormat)} /> }, { key: 'currentVersion', label: 'Version' },
          { key: 'updatedAt', label: 'Last saved', render: (r) => fmtDateTime(r.updatedAt) }, { key: 'updatedBy', label: 'Saved by', render: (r) => r.updatedBy ?? '—' }]} />
      <UploadDialog open={open} onClose={() => setOpen(false)} onDone={(id) => nav(`/sequfloh/imported-files/${id}`)} />
    </SqfPage>
  );
}

function UploadDialog({ open, onClose, onDone }: { open: boolean; onClose: () => void; onDone: (id: string) => void }) {
  const sites = useSites();
  const [file, setFile] = useState<File | null>(null);
  const [title, setTitle] = useState('');
  const [site, setSite] = useState('');
  const [blank, setBlank] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const upload = async () => {
    if (!file) return;
    setBusy(true); setError(null);
    const fd = new FormData();
    fd.append('file', file);
    if (title.trim()) fd.append('title', title.trim());
    if (site) fd.append('siteId', site);
    fd.append('blankForm', blank ? 'true' : 'false');
    try { const r = await api<{ id: string }>('POST', A, fd); onClose(); onDone(r.id); } catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  };
  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} fullWidth maxWidth="sm" TransitionProps={{ onEnter: () => { setFile(null); setTitle(''); setError(null); setBlank(true); } }}>
      <DialogTitle>Import a file</DialogTitle>
      <DialogContent>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>Excel (.xlsx, .xlsm, .xls), CSV or Word (.docx, .doc), up to 20 MB. The file is kept exactly as uploaded; macros are never run.</Typography>
        {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
        <Stack gap={2}>
          <Button variant="outlined" component="label" startIcon={<UploadFileOutlined />}>{file ? file.name : 'Choose a file'}
            <input hidden type="file" accept=".xlsx,.xlsm,.xls,.csv,.docx,.doc" data-testid="import-input" onChange={(e) => { const f = e.target.files?.[0] ?? null; setFile(f); if (f && !title) setTitle(f.name.replace(/\.[^.]+$/, '')); }} />
          </Button>
          <TextField label="Title" value={title} onChange={(e) => setTitle(e.target.value)} />
          {(sites.data?.length ?? 0) > 1 && <TextField select label="Site" value={site} onChange={(e) => setSite(e.target.value)}>
            <MenuItem value="">My default site</MenuItem>{sites.data!.filter((s) => !s.isArchived).map((s) => <MenuItem key={s.id} value={s.id}>{s.code} · {s.name}</MenuItem>)}</TextField>}
          <Box>
            <FormControlLabel control={<Checkbox checked={blank} onChange={(e) => setBlank(e.target.checked)} inputProps={{ 'data-testid': 'import-blank' } as object} />} label="Keep as a blank form" />
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', ml: 4 }}>The file stays empty and protected. People use Fill out a copy to fill it in; the blank never changes.</Typography>
          </Box>
        </Stack>
      </DialogContent>
      <DialogActions><Button onClick={onClose} disabled={busy}>Cancel</Button><Button variant="contained" disabled={!file || busy} onClick={upload} data-testid="import-upload">{busy ? 'Importing…' : 'Import'}</Button></DialogActions>
    </Dialog>
  );
}

/** One imported file: the sheet or document editor, versions and history. */
export function ImportedFilePage() {
  const { id } = useParams();
  const q = useQuery({ queryKey: ['imported-file', id], queryFn: () => get<Detail>(`${A}/${id}`) });
  const history = useQuery({ queryKey: ['imported-file-history', id], queryFn: () => get<AuditRow[]>(`${A}/${id}/history`) });
  const [tab, setTab] = useState('file');
  const [version, setVersion] = useState<number | null>(null);
  const [menu, setMenu] = useState<HTMLElement | null>(null);
  const [editBlank, setEditBlank] = useState(false);
  const [fillOpen, setFillOpen] = useState(false);
  const [fillBusy, setFillBusy] = useState(false);
  const [fillError, setFillError] = useState<string | null>(null);
  const [upBusy, setUpBusy] = useState(false);
  const [upMsg, setUpMsg] = useState<{ kind: 'success' | 'error'; text: string } | null>(null);
  const nav = useNavigate();
  useEffect(() => { setEditBlank(false); setTab('file'); setVersion(null); }, [id]);
  const d = q.data;
  const shown = version ?? d?.currentVersion ?? 1;
  const latest = !d || shown === d.currentVersion;
  const refresh = () => { q.refetch(); history.refetch(); };
  const qc = useQueryClient();
  const fillOut = async (title: string, partNumber?: string) => {
    setFillBusy(true); setFillError(null);
    try { const r = await post<{ id: string }>(`${A}/${d!.id}/fill-out`, { title, partNumber }); setFillOpen(false); nav(`/sequfloh/imported-files/${r.id}`); }
    catch (e) { setFillError((e as Error).message); } finally { setFillBusy(false); }
  };
  const uploadVersion = async (f: File) => {
    setUpBusy(true); setUpMsg(null);
    const fd = new FormData(); fd.append('file', f);
    try {
      const r = await api<{ version: number }>('POST', `${A}/${d!.id}/versions`, fd);
      await qc.invalidateQueries({ queryKey: ['imported-sheet', d!.id] }); await qc.invalidateQueries({ queryKey: ['imported-doc', d!.id] });
      setVersion(null); refresh();
      setUpMsg({ kind: 'success', text: `Uploaded as version ${r.version}. Filled copies made earlier are not changed.` });
    } catch (e) { setUpMsg({ kind: 'error', text: (e as Error).message }); } finally { setUpBusy(false); }
  };
  const protectedBlank = !!d?.isBlankForm && !editBlank;
  const editable = !!d && d.canEdit && latest && !protectedBlank;
  return (
    <SqfPage title={d ? `${d.number} · ${d.title}` : 'Imported file'} crumbs={crumbs(d?.number ?? 'File')} permission="sequfloh.view_imported_files"
      recent={d ? { label: `${d.number} ${d.title}`, itemType: 'ImportedFile', recordId: d.id } : null}
      tabs={[{ label: d?.kind === 'document' ? 'Document' : 'Sheet', value: 'file' }, { label: `Versions${d ? ` (${d.versions.length})` : ''}`, value: 'versions' },
        ...(d?.isBlankForm ? [{ label: `Filled copies (${d.copies?.length ?? 0})`, value: 'copies' }] : []), { label: 'History', value: 'history' }]} tab={tab} onTab={setTab}
      actions={d && <>
        {d.isBlankForm && d.canEdit && <Button variant="contained" onClick={() => { setFillError(null); setFillOpen(true); }} data-testid="fill-out">Fill out a copy</Button>}
        {d.canEdit && (!d.isBlankForm || editBlank) && <Button component="label" startIcon={<UploadFileOutlined />} disabled={upBusy} data-testid="upload-version">{upBusy ? 'Uploading…' : 'Upload new version'}
          <input hidden type="file" accept=".xlsx,.xlsm,.xls,.csv,.docx,.doc" data-testid="upload-version-input" onChange={(e) => { const f = e.target.files?.[0]; e.target.value = ''; if (f) uploadVersion(f); }} /></Button>}
        <Button startIcon={<DownloadOutlined />} onClick={(e) => setMenu(e.currentTarget)}>Download</Button>
        <Menu open={!!menu} anchorEl={menu} onClose={() => setMenu(null)}>
          <MenuItem component="a" href={`${A}/${d.id}/download${latest ? '' : `?version=${shown}`}`} onClick={() => setMenu(null)}>{latest ? 'Current version' : `Version ${shown}`} ({d.kind === 'spreadsheet' ? 'Excel' : 'Word'})</MenuItem>
          <MenuItem component="a" href={`${A}/${d.id}/download?original=true`} onClick={() => setMenu(null)}>As uploaded ({d.originalFileName})</MenuItem>
        </Menu>
      </>}>
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : d && <>
        <Stack direction="row" gap={1} flexWrap="wrap" sx={{ mb: 1.5 }} alignItems="center">
          <Chip size="small" label={kindLabel(d.kind)} />
          <Chip size="small" variant="outlined" label={`Imported from ${formatLabel(d.sourceFormat)}`} />
          <Chip size="small" variant="outlined" color={latest ? 'primary' : 'warning'} label={latest ? `Version ${d.currentVersion}` : `Viewing version ${shown} of ${d.currentVersion}`} />
          {!latest && <Button size="small" onClick={() => setVersion(null)}>Back to the current version</Button>}
          {d.isBlankForm && <Chip size="small" color="primary" label="Blank form" data-testid="blank-chip" />}
          {d.blankForm && <Typography variant="body2" data-testid="blank-link">Filled from blank form{' '}
            <Link component={RouterLink} to={`/sequfloh/imported-files/${d.blankForm.id}`}>{d.blankForm.number} · {d.blankForm.title}</Link>{d.blankForm.version ? ` (version ${d.blankForm.version})` : ''}</Typography>}
          {d.isBlankForm && d.canEdit && <FormControlLabel sx={{ ml: 'auto' }} control={<Switch size="small" checked={editBlank} onChange={(e) => setEditBlank(e.target.checked)} inputProps={{ 'data-testid': 'edit-blank' } as object} />} label="Edit blank" />}
          {d.kind === 'spreadsheet' && (d.summary.formulaTotal ?? 0) > 0 && <Chip size="small" variant="outlined" sx={{ borderColor: GREEN, color: GREEN }} label={`${d.summary.formulaTotal} formulas`} />}
          {d.kind === 'document' && <Chip size="small" variant="outlined" label={`${d.summary.paragraphs ?? 0} paragraphs · ${d.summary.tables ?? 0} tables`} />}
        </Stack>
        {!d.isBlankForm && (d.partNumber || d.canEdit) && <Paper variant="outlined" sx={{ px: 2, py: 1.25, mb: 1.5 }}><CopyPartNumber d={d} onSaved={refresh} /></Paper>}
        {upMsg && <Alert severity={upMsg.kind} sx={{ mb: 1 }} onClose={() => setUpMsg(null)}>{upMsg.text}</Alert>}
        {d.isBlankForm && tab === 'file' && <Alert severity={editBlank ? 'warning' : 'info'} sx={{ mb: 1 }}>{editBlank
          ? 'You are editing the blank form itself. Saved changes become a new version of the blank; filled copies made earlier stay as they are.'
          : 'This is a blank form, so it is protected. Use Fill out a copy to start a filled one; turn on Edit blank to change the blank itself.'}</Alert>}
        {[...(d.summary.security ?? []), ...(d.summary.warnings ?? [])].slice(0, 4).map((w) => <Alert key={w} severity="info" sx={{ mb: 1 }}>{w}</Alert>)}
        {tab === 'file' && (d.kind === 'spreadsheet'
          ? <SheetEditor key={d.id} file={d} version={shown} editable={editable} editBlank={editBlank} onSaved={refresh} />
          : <DocumentEditor key={d.id} file={d} version={shown} editable={editable} editBlank={editBlank} onSaved={refresh} />)}
        {tab === 'copies' && (d.copies?.length ? <Paper variant="outlined" data-testid="copies">
          {d.copies.map((c) => <Stack key={c.id} direction="row" gap={2} alignItems="center" sx={{ px: 2, py: 1.25, borderBottom: 1, borderColor: 'divider', cursor: 'pointer', '&:hover': { bgcolor: 'action.hover' } }}
            onClick={() => nav(`/sequfloh/imported-files/${c.id}`)}>
            <Mono>{c.number}</Mono>
            <Box sx={{ flex: 1 }}><Typography variant="body2" sx={{ fontWeight: 600 }}>{c.title}</Typography>
              <Typography variant="caption" color="text.secondary">Started {fmtDateTime(c.createdAt)} by {c.createdBy ?? '—'}{c.blankFormVersion ? ` from version ${c.blankFormVersion}` : ''}</Typography></Box>
            <Box sx={{ textAlign: 'right' }}><Typography variant="body2">Saved {fmtDateTime(c.updatedAt)}</Typography><Typography variant="caption" color="text.secondary">{c.updatedBy ?? c.createdBy ?? '—'}</Typography></Box>
          </Stack>)}
        </Paper> : <Empty text="No filled copies yet. Use Fill out a copy to start one." />)}
        {tab === 'versions' && <Paper variant="outlined">
          {d.versions.map((v) => <Stack key={v.version} direction="row" gap={2} alignItems="center" sx={{ px: 2, py: 1.25, borderBottom: 1, borderColor: 'divider' }}>
            <Typography sx={{ width: 90, fontWeight: 600 }}>Version {v.version}</Typography>
            <Box sx={{ flex: 1 }}><Typography variant="body2">{v.note ?? '—'}</Typography><Typography variant="caption" color="text.secondary">{v.createdByName} · {fmtDateTime(v.createdAt)} · {(v.sizeBytes / 1024).toFixed(0)} KB</Typography></Box>
            <Button size="small" onClick={() => { setVersion(v.version === d.currentVersion ? null : v.version); setTab('file'); }}>Open</Button>
            <Button size="small" component="a" href={`${A}/${d.id}/download?version=${v.version}`}>Download</Button>
          </Stack>)}
        </Paper>}
        {tab === 'history' && <HistoryTable q={history} />}
      </>}
      {d && <CopyDialog open={fillOpen} heading="Fill out a copy" initial={`${d.title} – ${today()}`} action="Create and open" busy={fillBusy} error={fillError} onClose={() => setFillOpen(false)} onConfirm={fillOut} partNumber />}
    </SqfPage>
  );
}

// ---- spreadsheet ---------------------------------------------------------------------------------------------------------------

interface CellStyle { font?: string; size?: number; bold?: boolean; italic?: boolean; underline?: boolean; colour?: string; fill?: string; align?: string; vAlign?: string; wrap?: boolean;
  borderTop?: string; borderRight?: string; borderBottom?: string; borderLeft?: string; borderColour?: string; numberFormat?: string }
interface GridCell { r: number; c: number; text?: string | null; formula?: string | null; cached?: string | null; validation: boolean; name?: string | null; rowSpan: number; colSpan: number; style?: CellStyle | null; locked: boolean; comment?: string | null }
interface Sheet { version: number; sheet: string; r1: number; c1: number; r2: number; c2: number; widths: number[]; heights: number[]; cells: GridCell[]; truncated: boolean; sheets: string[];
  freezeRows: number; freezeCols: number; formulas: { r: number; c: number; classes: string[]; warning?: string }[]; conditional?: Look[] }
interface Look { r: number; c: number; fill?: string | null; colour?: string | null; bold: boolean }
interface Preview { values: { r: number; c: number; value: string }[]; conditional: Look[] }

const letters = (n: number) => { let s = ''; while (n > 0) { const m = (n - 1) % 26; s = String.fromCharCode(65 + m) + s; n = Math.floor((n - 1) / 26); } return s; };
const key = (r: number, c: number) => `${r}:${c}`;
/** Shows a stored number the way its number format asks (decimals, percent, thousands); other formats show the value as stored. */
function formatted(v: string | null | undefined, nf?: string) {
  if (v === null || v === undefined || v === '' || !nf || nf === 'General' || !/^-?\d+(\.\d+)?(E[-+]?\d+)?$/i.test(v)) return v ?? '';
  const section = nf.split(';')[0].replace(/"[^"]*"|\[[^\]]*\]|[_*\\]./g, '');
  if (/[dmyhs]/i.test(section) && !/[0#]/.test(section)) return v;
  const n = Number(v);
  const pct = section.includes('%');
  const dec = (section.split('.')[1]?.match(/[0#]/g) ?? []).length;
  const x = pct ? n * 100 : n;
  const s = section.includes(',') ? x.toLocaleString('en-US', { minimumFractionDigits: dec, maximumFractionDigits: dec }) : x.toFixed(dec);
  const prefix = (section.match(/^[^0#.,%]*/)?.[0] ?? '').replace(/\\/g, '');
  return `${prefix}${s}${pct ? '%' : ''}`;
}
const pt = (v: number) => Math.max(0, Math.round(v * 4 / 3));
const faces: Record<string, string> = { sans: 'Calibri, Arial, Helvetica, sans-serif', serif: 'Cambria, "Times New Roman", serif', mono: 'Consolas, "Courier New", monospace' };
const borderCss = (b?: string, colour?: string) => !b ? undefined : `${b === 'thick' ? 2.5 : b === 'medium' ? 1.75 : 1}px ${b === 'double' ? 'double' : b === 'dashed' ? 'dashed' : b === 'dotted' ? 'dotted' : 'solid'} ${colour ?? '#000'}`;

function cellCss(s?: CellStyle | null): CSSProperties {
  if (!s) return {};
  return {
    fontFamily: s.font ? faces[s.font] : undefined, fontSize: s.size ? `${s.size}pt` : undefined, fontWeight: s.bold ? 700 : undefined, fontStyle: s.italic ? 'italic' : undefined,
    textDecoration: s.underline ? 'underline' : undefined, color: s.colour, background: s.fill, textAlign: (s.align as CSSProperties['textAlign']) ?? undefined,
    verticalAlign: s.vAlign === 'middle' ? 'middle' : s.vAlign === 'top' ? 'top' : 'bottom', whiteSpace: s.wrap ? 'pre-wrap' : 'pre',
    borderTop: borderCss(s.borderTop, s.borderColour), borderRight: borderCss(s.borderRight, s.borderColour), borderBottom: borderCss(s.borderBottom, s.borderColour), borderLeft: borderCss(s.borderLeft, s.borderColour),
  };
}

function SheetEditor({ file, version, editable, editBlank, onSaved }: { file: Detail; version: number; editable: boolean; editBlank: boolean; onSaved: () => void }) {
  const qc = useQueryClient();
  const [sheet, setSheet] = useState<string | undefined>(undefined);
  const q = useQuery({ queryKey: ['imported-sheet', file.id, version, sheet], queryFn: () => get<Sheet>(`${A}/${file.id}/sheet?${qs({ name: sheet, version })}`) });
  const [edits, setEdits] = useState<Record<string, Record<string, string>>>({});
  const [sel, setSel] = useState<{ r: number; c: number } | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [bar, setBar] = useState('');
  const [showFormulas, setShowFormulas] = useState(false);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ kind: 'success' | 'error' | 'info'; text: string } | null>(null);
  const pendingCount = Object.values(edits).reduce((n, m) => n + Object.keys(m).length, 0);
  useUnsaved(pendingCount > 0);
  const g = q.data;
  const cells = useMemo(() => new Map((g?.cells ?? []).map((c) => [key(c.r, c.c), c])), [g]);
  const covered = useMemo(() => {
    const s = new Set<string>();
    for (const c of g?.cells ?? []) if (c.rowSpan > 1 || c.colSpan > 1) for (let r = c.r; r < c.r + c.rowSpan; r++) for (let cc = c.c; cc < c.c + c.colSpan; cc++) if (r !== c.r || cc !== c.c) s.add(key(r, cc));
    return s;
  }, [g]);
  const fInfo = useMemo(() => new Map((g?.formulas ?? []).map((f) => [key(f.r, f.c), f])), [g]);
  const name = g?.sheet ?? '';
  const pend = edits[name] ?? {};
  const allEdits = useMemo(() => Object.entries(edits).flatMap(([s, m]) => Object.entries(m).map(([k, value]) => { const [r, c] = k.split(':').map(Number); return { sheet: s, r, c, value }; })), [edits]);
  // with unsaved edits, ask the server for the recalculated results and conditional colours without saving anything
  const [preview, setPreview] = useState<{ sheet: string; data: Preview } | null>(null);
  useEffect(() => {
    if (!allEdits.length || !name) { setPreview(null); return; }
    let live = true;
    const t = setTimeout(() => { post<Preview>(`${A}/${file.id}/preview`, { sheet: name, edits: allEdits }).then((d) => { if (live) setPreview({ sheet: name, data: d }); }).catch(() => { if (live) setPreview(null); }); }, 250);
    return () => { live = false; clearTimeout(t); };
  }, [allEdits, name, file.id]);
  const pv = preview?.sheet === name ? preview.data : null;
  const looks = useMemo(() => new Map((pv?.conditional ?? g?.conditional ?? []).map((l) => [key(l.r, l.c), l])), [pv, g]);
  const pvValues = useMemo(() => new Map((pv?.values ?? []).map((v) => [key(v.r, v.c), v.value])), [pv]);
  const raw = (r: number, c: number) => { const p = pend[key(r, c)]; if (p !== undefined) return p; const x = cells.get(key(r, c)); return x?.formula ?? x?.text ?? ''; };
  const select = (r: number, c: number) => { setSel({ r, c }); setBar(raw(r, c)); };
  const commit = (r: number, c: number, value: string) => {
    const x = cells.get(key(r, c));
    const original = x?.formula ?? x?.text ?? '';
    setEdits((e) => { const m = { ...(e[name] ?? {}) }; if (value === original) delete m[key(r, c)]; else m[key(r, c)] = value; return { ...e, [name]: m }; });
  };
  const inputRef = useRef<HTMLInputElement>(null);
  useEffect(() => { if (editing) inputRef.current?.focus(); }, [editing]);
  const save = async () => {
    setBusy(true); setMsg(null);
    const list = Object.entries(edits).flatMap(([s, m]) => Object.entries(m).map(([k, value]) => { const [r, c] = k.split(':').map(Number); return { sheet: s, r, c, value }; }));
    try {
      const r = await put<{ version: number; notice?: string }>(`${A}/${file.id}/cells`, { baseVersion: file.currentVersion, edits: list, editBlank });
      setEdits({});
      await qc.invalidateQueries({ queryKey: ['imported-sheet', file.id] });
      onSaved();
      setMsg({ kind: 'success', text: `Saved as version ${r.version}.${r.notice ? ' ' + r.notice : ''}` });
    } catch (e) { setMsg({ kind: 'error', text: e instanceof ApiError ? e.message : 'The changes could not be saved.' }); } finally { setBusy(false); }
  };
  const copy = useSaveAsCopy(file, () => ({ cells: allEdits }));
  if (q.isLoading) return <Loading />;
  if (q.error) return <ErrorState error={q.error} retry={q.refetch} />;
  if (!g) return null;
  const selCell = sel ? cells.get(key(sel.r, sel.c)) : undefined;
  const selInfo = sel ? fInfo.get(key(sel.r, sel.c)) : undefined;
  const rows: ReactNode[] = [];
  for (let r = g.r1; r <= g.r2; r++) {
    const tds: ReactNode[] = [<th key="h" style={{ ...headCss, position: 'sticky', left: 0, zIndex: 2, minWidth: 40 }}>{r}</th>];
    for (let c = g.c1; c <= g.c2; c++) {
      const k = key(r, c);
      if (covered.has(k)) continue;
      const x = cells.get(k);
      const p = pend[k];
      const isSel = sel?.r === r && sel?.c === c;
      const live = pvValues.get(k);
      const value = p !== undefined ? (p.startsWith('=') && !showFormulas && live !== undefined ? formatted(live, x?.style?.numberFormat) : p)
        : showFormulas && x?.formula ? x.formula : formatted(live ?? x?.text, x?.style?.numberFormat);
      const look = looks.get(k);
      const isFormula = !!(p !== undefined ? p.startsWith('=') : x?.formula);
      const info = fInfo.get(k);
      tds.push(<td key={k} rowSpan={x?.rowSpan ?? 1} colSpan={x?.colSpan ?? 1} data-cell={`${letters(c)}${r}`} data-formula={x?.formula ?? undefined} data-fill={look?.fill ?? undefined}
        onClick={() => select(r, c)} onDoubleClick={() => { if (editable) { select(r, c); setEditing(k); } }}
        title={info?.warning ?? (x?.formula ? `${x.formula}` : x?.comment ?? undefined)}
        style={{ ...baseCell, ...cellCss(x?.style), height: pt(g.heights[r - g.r1] ?? 15), outline: isSel ? '2px solid #1257C4' : undefined, outlineOffset: -2,
          ...(look?.colour ? { color: look.colour } : {}), ...(look?.bold ? { fontWeight: 700 } : {}),
          background: p !== undefined ? '#FFF6D6' : look?.fill ?? x?.style?.fill ?? '#fff', position: 'relative', fontFamily: showFormulas && isFormula ? faces.mono : cellCss(x?.style).fontFamily }}>
        {editing === k
          ? <input ref={inputRef} defaultValue={raw(r, c)} aria-label={`Edit ${letters(c)}${r}`} style={{ width: '100%', border: 0, outline: 0, font: 'inherit', background: 'transparent', padding: 0 }}
              onBlur={(e) => { commit(r, c, e.currentTarget.value); setEditing(null); }}
              onKeyDown={(e) => { if (e.key === 'Enter') { commit(r, c, e.currentTarget.value); setEditing(null); select(Math.min(r + (x?.rowSpan ?? 1), g.r2), c); } if (e.key === 'Escape') setEditing(null); }} />
          : value}
        {isFormula && !showFormulas && <span aria-hidden style={{ position: 'absolute', top: 0, left: 0, width: 0, height: 0, borderTop: `6px solid ${GREEN}`, borderRight: '6px solid transparent' }} />}
      </td>);
    }
    rows.push(<tr key={r}>{tds}</tr>);
  }
  return (
    <Stack gap={1}>
      {msg && <Alert severity={msg.kind} onClose={() => setMsg(null)}>{msg.text}</Alert>}
      <Paper variant="outlined" sx={{ p: 1 }}>
        <Stack direction="row" gap={1} alignItems="center">
          <Box sx={{ width: 70, fontFamily: faces.mono, fontWeight: 600, textAlign: 'center' }} data-testid="cell-address">{sel ? `${letters(sel.c)}${sel.r}` : ''}</Box>
          <Typography sx={{ fontStyle: 'italic', color: GREEN, fontWeight: 700, px: 0.5 }}>fx</Typography>
          <TextField size="small" fullWidth value={bar} disabled={!sel || !editable} placeholder={editable ? 'Select a cell, then type a value or a formula starting with =' : 'Viewing only'}
            inputProps={{ 'data-testid': 'formula-bar', style: { fontFamily: faces.mono } }}
            onChange={(e) => setBar(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter' && sel) commit(sel.r, sel.c, bar); }} onBlur={() => { if (sel && editable && bar !== raw(sel.r, sel.c)) commit(sel.r, sel.c, bar); }} />
          <FormControlLabel sx={{ whiteSpace: 'nowrap', ml: 1, flexShrink: 0 }} control={<Switch size="small" checked={showFormulas} onChange={(e) => setShowFormulas(e.target.checked)} />} label="Show formulas" />
          {editable && <Button variant="contained" disabled={!pendingCount || busy} onClick={save} data-testid="save-cells" sx={{ whiteSpace: 'nowrap', flexShrink: 0 }}>{busy ? 'Saving…' : pendingCount ? `Save ${pendingCount} change${pendingCount === 1 ? '' : 's'}` : 'Saved'}</Button>}
          {editable && pendingCount > 0 && <Button variant="outlined" onClick={copy.open} disabled={busy} data-testid="save-as-copy" sx={{ whiteSpace: 'nowrap', flexShrink: 0 }}>Save as new copy</Button>}
          {pendingCount > 0 && <Button onClick={() => setEdits({})} disabled={busy} sx={{ flexShrink: 0 }}>Discard</Button>}
        </Stack>
        <Typography variant="caption" color="text.secondary" noWrap sx={{ display: 'block', mt: 0.5, ml: 1, minHeight: 20 }}>
          {selCell?.formula ? <>Result: <b>{formatted(selCell.cached, selCell.style?.numberFormat)}</b>{selInfo?.warning ? ` · ${selInfo.warning}` : ' · updates as you edit; saved when you save'}</> : sel && editable ? 'Double-click a cell or type in the bar above. Start with = for a formula.' : ''}</Typography>
      </Paper>
      <Paper variant="outlined" sx={{ overflow: 'auto', maxHeight: 'calc(100vh - 330px)', minHeight: 300 }}>
        <table style={{ borderCollapse: 'collapse', tableLayout: 'fixed' }} data-testid="sheet-grid">
          <colgroup><col style={{ width: 40 }} />{g.widths.map((w, i) => <col key={i} style={{ width: pt(w) }} />)}</colgroup>
          <thead><tr><th style={{ ...headCss, position: 'sticky', top: 0, left: 0, zIndex: 3 }} />{g.widths.map((_, i) => <th key={i} style={{ ...headCss, position: 'sticky', top: 0, zIndex: 1 }}>{letters(g.c1 + i)}</th>)}</tr></thead>
          <tbody>{rows}</tbody>
        </table>
      </Paper>
      {g.truncated && <Typography variant="caption" color="text.secondary">Only the first rows and columns are shown here; download the file to see all of it.</Typography>}
      {copy.dialog(() => setEdits({}))}
      {g.sheets.length > 1 && <Tabs value={g.sheet} onChange={(_, v) => { setSheet(v); setSel(null); }} variant="scrollable" sx={{ minHeight: 36, '& .MuiTab-root': { minHeight: 36, textTransform: 'none' } }}>
        {g.sheets.map((s) => <Tab key={s} value={s} label={`${s}${Object.keys(edits[s] ?? {}).length ? ' •' : ''}`} />)}
      </Tabs>}
    </Stack>
  );
}
const headCss: CSSProperties = { background: '#EEF2F7', color: '#5E6A7A', fontWeight: 500, fontSize: 12, border: '1px solid #D5DBE3', padding: '2px 4px', textAlign: 'center' };
const baseCell: CSSProperties = { border: '1px solid #E3E7ED', padding: '1px 4px', fontSize: '11pt', fontFamily: faces.sans, overflow: 'hidden', textOverflow: 'clip', cursor: 'cell', whiteSpace: 'pre', verticalAlign: 'bottom' };

// ---- Word document -------------------------------------------------------------------------------------------------------------

interface WStyle { bold: boolean; italic: boolean; underline: boolean; colour?: string; highlight?: string; size?: number; font?: string }
interface WControl { type: string; tag?: string; alias?: string; items: string[]; checked: boolean; text: string; showingPlaceholder: boolean }
interface WSeg { kind: string; text: string; style?: WStyle; control?: WControl; link?: string }
interface WPara { segs: WSeg[]; styleName?: string; heading?: number | null; align?: string; indent: number; before: number; after: number; listLabel?: string; listLevel: number; style?: WStyle; ref?: number | null; editable: boolean }
interface WCell { paras: WPara[]; nested: WTable[]; gridSpan: number; vMerge?: string; width?: number; shading?: string; borderTop?: string; borderBottom?: string; borderLeft?: string; borderRight?: string; vAlign?: string }
interface WTable { grid: number[]; rows: { cells: WCell[]; height?: number; header: boolean }[] }
interface WBlock { kind: string; p?: WPara; t?: WTable; section?: { orientation: string } }
interface Doc { version: number; title?: string; blocks: WBlock[]; header: WPara[]; footer: WPara[]; finalSection: { orientation: string; widthPt: number; heightPt: number; marginsPt: number[] } }

const hl: Record<string, string> = { yellow: '#FFF59D', green: '#C8E6C9', cyan: '#B2EBF2', magenta: '#F8BBD0', blue: '#BBDEFB', red: '#FFCDD2', lightGray: '#EEEEEE', darkGray: '#BDBDBD' };
const segCss = (s?: WStyle): CSSProperties => !s ? {} : ({ fontWeight: s.bold ? 700 : undefined, fontStyle: s.italic ? 'italic' : undefined, textDecoration: s.underline ? 'underline' : undefined,
  color: s.colour, background: s.highlight ? (s.highlight.startsWith('#') ? s.highlight : hl[s.highlight] ?? undefined) : undefined, fontSize: s.size ? `${s.size}pt` : undefined });

function DocumentEditor({ file, version, editable, editBlank, onSaved }: { file: Detail; version: number; editable: boolean; editBlank: boolean; onSaved: () => void }) {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['imported-doc', file.id, version], queryFn: () => get<Doc>(`${A}/${file.id}/document?${qs({ version })}`) });
  const [edits, setEdits] = useState<Record<number, string>>({});
  const [editing, setEditing] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ kind: 'success' | 'error'; text: string } | null>(null);
  const count = Object.keys(edits).length;
  useUnsaved(count > 0);
  const save = async () => {
    setBusy(true); setMsg(null);
    try {
      const r = await put<{ version: number }>(`${A}/${file.id}/document`, { baseVersion: file.currentVersion, edits: Object.entries(edits).map(([ref, text]) => ({ ref: Number(ref), text })), editBlank });
      setEdits({}); setEditing(null);
      await qc.invalidateQueries({ queryKey: ['imported-doc', file.id] });
      onSaved();
      setMsg({ kind: 'success', text: `Saved as version ${r.version}.` });
    } catch (e) { setMsg({ kind: 'error', text: (e as Error).message }); } finally { setBusy(false); }
  };
  const copy = useSaveAsCopy(file, () => ({ paragraphs: Object.entries(edits).map(([ref, text]) => ({ ref: Number(ref), text })) }));
  if (q.isLoading) return <Loading />;
  if (q.error) return <ErrorState error={q.error} retry={q.refetch} />;
  const d = q.data;
  if (!d) return null;
  const textOf = (p: WPara) => p.segs.map((s) => (s.kind === 'tab' ? '\t' : s.kind === 'break' ? '\n' : s.text)).join('');
  const para = (p: WPara, i: number | string, inCell = false) => {
    const ref = p.ref ?? null;
    const edited = ref !== null && edits[ref] !== undefined;
    const size = p.heading === 0 ? 20 : p.heading === 1 ? 15 : p.heading === 2 ? 13 : undefined;
    const css: CSSProperties = { margin: 0, marginTop: inCell ? 0 : Math.min(p.before, 24) * 4 / 3, marginBottom: inCell ? 0 : Math.max(4, Math.min(p.after, 18) * 4 / 3), paddingLeft: p.indent * 4 / 3,
      textAlign: (p.align as CSSProperties['textAlign']) ?? undefined, fontSize: size ? `${size}pt` : undefined, fontWeight: p.heading !== null && p.heading !== undefined ? 700 : undefined,
      color: p.heading !== null && p.heading !== undefined ? p.style?.colour ?? undefined : undefined, whiteSpace: 'pre-wrap', minHeight: '1.2em', borderRadius: 2,
      cursor: editable && p.editable ? 'text' : undefined, background: edited ? '#FFF6D6' : undefined, outline: editable && p.editable ? undefined : undefined };
    if (editing !== null && ref === editing) {
      return <TextField key={i} multiline fullWidth autoFocus size="small" defaultValue={edits[ref] ?? textOf(p)} inputProps={{ 'data-testid': 'paragraph-input' }}
        onBlur={(e) => { const v = e.target.value; setEdits((x) => { const n = { ...x }; if (v === textOf(p)) delete n[ref]; else n[ref] = v; return n; }); setEditing(null); }}
        onKeyDown={(e) => { if (e.key === 'Escape') setEditing(null); }} sx={{ my: 0.5 }} />;
    }
    return <p key={i} style={css} data-ref={ref ?? undefined} data-editable={p.editable || undefined}
      onClick={() => { if (editable && p.editable && ref !== null) setEditing(ref); }} className={editable && p.editable ? 'sqf-editable' : undefined}>
      {p.listLabel ? <span style={{ display: 'inline-block', minWidth: 24 + p.listLevel * 18 }}>{p.listLabel} </span> : null}
      {edited ? edits[ref!] : p.segs.map((s, j) => seg(s, j))}
    </p>;
  };
  const seg = (s: WSeg, j: number): ReactNode => {
    if (s.kind === 'tab') return <span key={j} style={{ display: 'inline-block', width: 36 }} />;
    if (s.kind === 'break') return <br key={j} />;
    if (s.kind === 'pagebreak') return <hr key={j} style={{ border: 0, borderTop: '1px dashed #C8CFD8' }} />;
    if (s.kind === 'page') return <span key={j}>#</span>;
    if (s.kind === 'pages') return <span key={j}>##</span>;
    if (s.kind === 'formula') return <Tooltip key={j} title={s.text}><span style={{ color: GREEN, fontFamily: faces.mono }}>{'{fx}'}</span></Tooltip>;
    if (s.kind === 'control' && s.control) {
      const c = s.control;
      if (c.type === 'checkbox') return <span key={j} style={{ fontSize: '1.1em' }}>{c.checked ? '☒' : '☐'}</span>;
      return <Tooltip key={j} title={`${c.alias ?? c.tag ?? 'Field'}${c.items.length ? ': ' + c.items.join(', ') : ''}`}>
        <span style={{ border: '1px solid #9DB7E6', background: '#EEF4FD', borderRadius: 3, padding: '0 4px', color: c.showingPlaceholder ? '#5E6A7A' : undefined }}>{c.text || c.alias || '…'}</span></Tooltip>;
    }
    return <span key={j} style={segCss(s.style)}>{s.link ? <a href={s.link.startsWith('#') ? undefined : s.link} target="_blank" rel="noreferrer noopener">{s.text}</a> : s.text}</span>;
  };
  const table = (t: WTable, i: number | string): ReactNode => {
    // vertical merges: a restart cell spans the continue cells under it
    const spans = new Map<string, number>();
    t.rows.forEach((row, ri) => { let col = 0; row.cells.forEach((cell) => { if (cell.vMerge === 'restart') { let n = 1; for (let k = ri + 1; k < t.rows.length; k++) { let cc = 0; const below = t.rows[k].cells.find((x) => { const hit = cc === col; cc += x.gridSpan; return hit; }); if (below?.vMerge === 'continue') n++; else break; } spans.set(`${ri}:${col}`, n); } col += cell.gridSpan; }); });
    return <table key={i} style={{ borderCollapse: 'collapse', width: '100%', margin: '6px 0 10px', tableLayout: 'fixed' }}>
      <colgroup>{t.grid.map((w, k) => <col key={k} style={{ width: `${w * 4 / 3}px` }} />)}</colgroup>
      <tbody>{t.rows.map((row, ri) => { let col = 0; return <tr key={ri} style={{ height: row.height ? row.height * 4 / 3 : undefined }}>{row.cells.map((cell, ci) => {
        const at = col; col += cell.gridSpan;
        if (cell.vMerge === 'continue') return null;
        const b = (x?: string) => borderCss(x, '#7A8594');
        return <td key={ci} colSpan={cell.gridSpan} rowSpan={spans.get(`${ri}:${at}`) ?? 1} style={{ border: '1px solid #D5DBE3', borderTop: b(cell.borderTop), borderBottom: b(cell.borderBottom), borderLeft: b(cell.borderLeft), borderRight: b(cell.borderRight),
          background: cell.shading, verticalAlign: cell.vAlign === 'middle' ? 'middle' : cell.vAlign === 'bottom' ? 'bottom' : 'top', padding: '3px 6px', fontWeight: row.header ? 600 : undefined }}>
          {cell.paras.map((p, k) => para(p, `${ri}-${ci}-${k}`, true))}{cell.nested.map((n, k) => table(n, `n${k}`))}</td>;
      })}</tr>; })}</tbody>
    </table>;
  };
  const fs = d.finalSection;
  return (
    <Stack gap={1}>
      {msg && <Alert severity={msg.kind} onClose={() => setMsg(null)}>{msg.text}</Alert>}
      {editable && <Paper variant="outlined" sx={{ p: 1 }}><Stack direction="row" gap={1} alignItems="center">
        <Typography variant="body2" color="text.secondary" sx={{ flex: 1, px: 1 }}>Click a paragraph or table cell to change its text. Fields, form controls and pictures stay as they are in the file.</Typography>
        {count > 0 && <Button onClick={() => { setEdits({}); setEditing(null); }} disabled={busy} sx={{ flexShrink: 0 }}>Discard</Button>}
        {count > 0 && <Button variant="outlined" onClick={copy.open} disabled={busy} data-testid="save-doc-as-copy" sx={{ whiteSpace: 'nowrap', flexShrink: 0 }}>Save as new copy</Button>}
        <Button variant="contained" disabled={!count || busy} onClick={save} data-testid="save-document" sx={{ whiteSpace: 'nowrap', flexShrink: 0 }}>{busy ? 'Saving…' : count ? `Save ${count} change${count === 1 ? '' : 's'}` : 'Saved'}</Button>
      </Stack></Paper>}
      <Box sx={{ bgcolor: '#E9EDF2', p: 3, borderRadius: 1, overflow: 'auto', maxHeight: 'calc(100vh - 300px)', '& .sqf-editable:hover': { background: '#EEF4FD' } }}>
        <Paper elevation={2} data-testid="document-page" sx={{ mx: 'auto', bgcolor: '#fff', color: '#1B2430', width: Math.min(fs.widthPt * 4 / 3, 1100),
          px: `${(fs.marginsPt[0] ?? 72) * 4 / 3 * 0.7}px`, py: `${(fs.marginsPt[2] ?? 72) * 4 / 3 * 0.6}px`, fontFamily: faces.sans, fontSize: '10.5pt', lineHeight: 1.35 }}>
          {d.header.length > 0 && <Box sx={{ borderBottom: '1px solid #E3E7ED', mb: 2, pb: 0.5, color: '#5E6A7A', fontSize: '9pt' }}>{d.header.map((p, i) => para(p, `h${i}`, true))}</Box>}
          {d.blocks.map((b, i) => b.p ? para(b.p, i) : b.t ? table(b.t, i) : b.kind === 'section' ? <hr key={i} style={{ border: 0, borderTop: '1px dashed #C8CFD8', margin: '14px 0' }} title="Section break" /> : null)}
          {d.footer.length > 0 && <Box sx={{ borderTop: '1px solid #E3E7ED', mt: 2, pt: 0.5, color: '#5E6A7A', fontSize: '9pt' }}>{d.footer.map((p, i) => para(p, `f${i}`, true))}</Box>}
        </Paper>
      </Box>
      {copy.dialog(() => { setEdits({}); setEditing(null); })}
      {d.blocks.length === 0 && <Empty text="The document is empty." />}
    </Stack>
  );
}
