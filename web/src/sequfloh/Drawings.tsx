import { useState } from 'react';
import { FormDialogTitle } from '../components/FullScreen';
import { Link as RouterLink, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { Alert, Autocomplete, Box, Breadcrumbs, Button, Checkbox, Chip, Dialog, Tab, Tabs, DialogActions, DialogContent, FormControlLabel, LinearProgress, Link, Paper, Stack, TextField, Typography } from '@mui/material';
import OpenInNew from '@mui/icons-material/OpenInNew';
import FolderOutlined from '@mui/icons-material/FolderOutlined';
import Article from '@mui/icons-material/Article';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put } from '../api';
import { otherNumbers, usePartSearch } from './partSearch';
import { fmtDateTime } from '../components/Status';
import { Confirm, DeleteButton, Empty, ErrorState, Loading, Mono, SqfPage, qs } from './common';
import { partOption } from './partLabel';
import { SimpleTable } from './Parts';
import { ImportButtons } from '../components/ImportButtons';
import { RecordActions } from '../components/RecordActions';
import { FullScreenButton } from '../components/FullScreen';
import UploadFileOutlined from '@mui/icons-material/UploadFileOutlined';
import { ImportDrawingsDialog } from './DrawingsImport';
import { ListToolbar, RecordHeader } from '../layout/PageHeader';
import { SPACE } from '../layout/spacing';

const D = '/api/drawings';
const crumbs = (extra?: string) => [{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Parts Drawings', to: extra ? '/sequfloh/drawings' : undefined }, ...(extra ? [{ label: extra }] : [])];
export const ACCEPT = '.pdf,.png,.jpg,.jpeg,.tif,.tiff,.dwg,.dxf,.step,.stp,.iges,.igs,.slddrw,.sldprt,.docx,.doc';
/** The in-app viewer for a drawing revision; always opened in a new browser tab. */
export const viewerUrl = (id: string, rev?: string) => `/drawings/view/${id}${rev ? `?revision=${encodeURIComponent(rev)}` : ''}`;
const fileUrl = (id: string, rev?: string, view = false) => `${D}/${id}/file?${qs({ revision: rev, view: view || undefined })}`;
const size = (b?: number) => (b === undefined ? '' : b > 1048576 ? `${(b / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(b / 1024))} KB`);

export const isWord = (name?: string | null) => /\.docx?$/i.test(name ?? '');
/** A file name and size, with a Word tag for Word files (they download rather than open in the browser). */
function FileName({ name, sizeBytes }: { name: string; sizeBytes?: number }) {
  return <Stack direction="row" gap={1} alignItems="center" component="span">
    {isWord(name) && <Chip size="small" color="primary" variant="outlined" icon={<Article fontSize="small" />} label="Word" data-testid="word-label" />}
    <span>{name} · {size(sizeBytes)}</span></Stack>;
}

export interface PartDrawing { id: string; title: string; drawingNumber?: string; currentRevision: string; fileName?: string; viewable: boolean; attachedRevision?: string }
const label = (d: { title: string; drawingNumber?: string | null }) => (d.drawingNumber ? `${d.drawingNumber} · ${d.title}` : d.title);

/** Opens the current revision in a new tab: the viewer for PDF and images, the download for everything else. */
export function OpenDrawingButton({ d, size: sz = 'small' }: { d: PartDrawing; size?: 'small' | 'medium' }) {
  return <Button size={sz} variant="outlined" startIcon={<OpenInNew />} component="a" href={d.viewable ? viewerUrl(d.id) : fileUrl(d.id)} target="_blank" rel="noopener"
    data-testid="open-drawing">Open drawing {d.drawingNumber ?? d.title} (rev {d.currentRevision})</Button>;
}

/**
 * The drawings for a part number entered on a validation: an 'Open drawing' button for each, or a quiet 'No drawing linked'. With checkboxes, ticking
 * one opens that drawing in a new window (the technician's checklist style); the button stays next to it.
 */
export function PartDrawingLinks({ partId, partNumber, checkboxes }: { partId?: string | null; partNumber?: string | null; checkboxes?: boolean }) {
  const n = (partNumber ?? '').trim();
  const q = useQuery({ queryKey: ['drawings-for-part', partId ?? '', n], enabled: !!partId || n.length > 0, staleTime: 15_000,
    queryFn: () => get<PartDrawing[]>(`${D}/for-part?${qs({ partId: partId ?? undefined, partNumber: partId ? undefined : n })}`) });
  const [ticked, setTicked] = useState<Record<string, boolean>>({});
  if (!partId && !n) return null;
  if (q.isLoading) return <Typography variant="caption" color="text.secondary">Looking for drawings…</Typography>;
  const list = q.data ?? [];
  if (list.length === 0) return <Typography variant="caption" color="text.secondary" data-testid="no-drawing">No drawing linked</Typography>;
  return <Stack gap={0.75} data-testid="part-drawings">
    {list.map((d) => <Stack key={d.id} direction="row" alignItems="center" gap={1} flexWrap="wrap">
      {checkboxes && <FormControlLabel label={`Open drawing ${label(d)} (rev ${d.currentRevision})`}
        control={<Checkbox checked={!!ticked[d.id]} inputProps={{ 'aria-label': `Open drawing ${d.drawingNumber ?? d.title}` }}
          onChange={(_, c) => { setTicked({ ...ticked, [d.id]: c }); if (c) window.open(d.viewable ? viewerUrl(d.id) : fileUrl(d.id), '_blank', 'noopener'); }} />} />}
      {!checkboxes && <Typography variant="body2">{label(d)}</Typography>}
      <OpenDrawingButton d={d} />
    </Stack>)}
  </Stack>;
}

/** One click from a part number to its drawing: opens the current revision in the viewer (or the file, for types the browser can't show). Nothing when no drawing is linked. */
export function ViewDrawingButton({ partId, partNumber }: { partId?: string | null; partNumber?: string | null }) {
  const n = (partNumber ?? '').trim();
  const q = useQuery({ queryKey: ['drawings-for-part', partId ?? '', n], enabled: !!partId || n.length > 0, staleTime: 15_000,
    queryFn: () => get<PartDrawing[]>(`${D}/for-part?${qs({ partId: partId ?? undefined, partNumber: partId ? undefined : n })}`) });
  const list = q.data ?? [];
  if (list.length === 0) return null;
  return <Stack direction="row" gap={0.5} component="span" sx={{ display: 'inline-flex', ml: 1, verticalAlign: 'middle' }}>{list.map((d) =>
    <Button key={d.id} size="small" variant="text" startIcon={<OpenInNew fontSize="small" />} component="a" href={d.viewable ? viewerUrl(d.id) : fileUrl(d.id)} target="_blank" rel="noopener"
      data-testid="view-drawing" sx={{ py: 0 }}>{list.length === 1 ? 'View drawing' : `View ${d.drawingNumber || d.title}`}</Button>)}</Stack>;
}

/** Drawings attached to a validation work order: the revision attached when the order was made and the current one. */
export function RecordDrawings({ recordId }: { recordId: string }) {
  const q = useQuery({ queryKey: ['drawings-for-record', recordId], queryFn: () => get<PartDrawing[]>(`${D}/for-record/${recordId}`) });
  if (!q.data?.length) return null;
  return <Paper variant="outlined" sx={{ p: 2 }} data-testid="record-drawings">
    <Typography variant="subtitle2" sx={{ mb: 1 }}>Drawings</Typography>
    <Stack gap={1}>{q.data.map((d) => <Stack key={d.id} direction="row" alignItems="center" gap={1} flexWrap="wrap">
      <Typography variant="body2">{label(d)}</Typography>
      <Chip size="small" label={d.attachedRevision === d.currentRevision ? `Rev ${d.currentRevision} (attached)` : `Attached rev ${d.attachedRevision} · now rev ${d.currentRevision}`}
        color={d.attachedRevision === d.currentRevision ? 'default' : 'warning'} variant="outlined" />
      <OpenDrawingButton d={d} />
    </Stack>)}</Stack>
  </Paper>;
}

interface Opt { id: string; label: string; extra?: string }
interface SearchRow { id: string; title: string; drawingNumber?: string; currentRevision: string; viewable?: boolean; folder?: string; partNumber?: string; isArchived?: boolean; updatedAt?: string;
  file?: { fileName: string; sizeBytes: number; createdAt: string } }

/** Picks drawings by part number, drawing number or title. */
export function DrawingPicker({ value, onChange, label: l = 'Link drawing(s)' }: { value: Opt[]; onChange: (v: Opt[]) => void; label?: string }) {
  const [text, setText] = useState('');
  const q = useQuery({ queryKey: ['drawing-pick', text], enabled: text.trim().length > 0, queryFn: () => get<SearchRow[]>(`${D}/search?${qs({ q: text })}`) });
  const opts = (q.data ?? []).map((d) => ({ id: d.id, label: `${label(d)} (rev ${d.currentRevision})` }));
  return <Autocomplete multiple options={opts} value={value} filterOptions={(x) => x} isOptionEqualToValue={(a, b) => a.id === b.id} getOptionLabel={(o) => o.label}
    onInputChange={(_, t, r) => { if (r === 'input') setText(t); }} onChange={(_, v) => onChange(v)} noOptionsText={text ? 'No drawings match' : 'Type a part number, drawing number or title'}
    renderInput={(p) => <TextField {...p} label={l} placeholder="Search drawings" />} />;
}

/** Picks parts from Parts and Items. */
export function PartsPicker({ value, onChange, label: l = 'Link to part number(s)' }: { value: Opt[]; onChange: (v: Opt[]) => void; label?: string }) {
  const [text, setText] = useState('');
  const q = usePartSearch<{ id: string; partNumber: string; name: string; description?: string | null; drawingNumber?: string | null; attributes?: string | null }>(text);
  const opts = q.items.map((p) => ({ id: p.id, label: partOption(p), extra: otherNumbers(p) }));
  return <Autocomplete multiple options={opts} value={value} filterOptions={(x) => x} isOptionEqualToValue={(a, b) => a.id === b.id} getOptionLabel={(o) => o.label} loading={q.loading}
    renderOption={(props, o) => <li {...props} key={o.id}><Box><Typography variant="body2">{o.label}</Typography>{o.extra && <Typography variant="caption" color="text.secondary">{o.extra}</Typography>}</Box></li>}
    noOptionsText={text ? 'No part matches' : 'Type a part number'}
    onInputChange={(_, t, r) => { if (r === 'input') setText(t); }} onChange={(_, v) => onChange(v)} renderInput={(p) => <TextField {...p} label={l} placeholder="Search by part number, DWG #, NAPA, DMA or JH PN" helperText={q.hint} />} />;
}

/** The file, title, number, revision and notes of a new drawing (also used inline from a part). */
export function DrawingFileFields({ f, set }: { f: NewDrawing; set: (f: NewDrawing) => void }) {
  return <Stack gap={2}>
    <Button variant="outlined" component="label">{f.file ? f.file.name : 'Choose file'}
      <input hidden type="file" accept={ACCEPT} data-testid="drawing-file" onChange={(e) => { const file = e.target.files?.[0] ?? null; set({ ...f, file, title: f.title || (file ? file.name.replace(/\.[^.]+$/, '') : '') }); }} /></Button>
    <Typography variant="caption" color="text.secondary">PDF, PNG, JPG, TIF, DWG, DXF, STEP, IGES, SolidWorks or Word (DOCX, DOC) files up to 50 MB. PDF and images open in the browser; other files download.</Typography>
    <TextField label="Title" required value={f.title} onChange={(e) => set({ ...f, title: e.target.value })} />
    <Stack direction="row" gap={2}><TextField label="Drawing number" value={f.drawingNumber} onChange={(e) => set({ ...f, drawingNumber: e.target.value })} sx={{ flex: 2 }} />
      <TextField label="Revision" required value={f.revision} onChange={(e) => set({ ...f, revision: e.target.value.toUpperCase() })} sx={{ flex: 1 }} /></Stack>
    <TextField label="Notes" multiline minRows={2} value={f.notes} onChange={(e) => set({ ...f, notes: e.target.value })} />
  </Stack>;
}
export interface NewDrawing { file: File | null; title: string; drawingNumber: string; revision: string; notes: string }
export const blankDrawing = (): NewDrawing => ({ file: null, title: '', drawingNumber: '', revision: 'A', notes: '' });
export function drawingForm(f: NewDrawing, extra: Record<string, string> = {}) {
  const fd = new FormData();
  fd.append('file', f.file!); fd.append('title', f.title); fd.append('drawingNumber', f.drawingNumber); fd.append('revision', f.revision); fd.append('notes', f.notes);
  Object.entries(extra).forEach(([k, v]) => fd.append(k, v));
  return fd;
}

// ------------------------------------------------------------------ pages
interface FolderRow { id: string; name: string; isArchived: boolean; updatedAt: string; part?: { id: string; partNumber: string; name: string } | null; folders: number; drawings: number }
interface FolderView { parent?: { id: string; name: string; parentId?: string; isArchived: boolean; part?: FolderRow['part']; canManage: boolean } | null; path: { id: string; name: string }[];
  folders: FolderRow[]; drawings: SearchRow[]; canCreate: boolean }

/** A family tab label: the top-level folder name without a trailing " Drawings". */
export const familyLabel = (name: string) => name.replace(/\s+drawings\s*$/i, '');

export function DrawingsPage() {
  const nav = useNavigate();
  const qc = useQueryClient();
  const [sp, setSp] = useSearchParams();
  const folder = sp.get('folder') ?? ''; const archived = sp.get('archived') === '1'; const search = sp.get('q') ?? '';
  const set = (k: string, v: string) => { const n = new URLSearchParams(sp); if (v) n.set(k, v); else n.delete(k); setSp(n); };
  const q = useQuery({ queryKey: ['drawing-folders', folder, archived], queryFn: () => get<FolderView>(`${D}/folders?${qs({ parentId: folder || undefined, archived: archived || undefined })}`) });
  const roots = useQuery({ queryKey: ['drawing-folders', '', archived], queryFn: () => get<FolderView>(`${D}/folders?${qs({ archived: archived || undefined })}`) });
  const family = folder ? (q.data?.path?.[0]?.id ?? (q.data?.parent && !q.data.parent.parentId ? q.data.parent.id : '')) : '';
  const everywhere = sp.get('all') === '1';
  const scope = family && !everywhere ? family : undefined;
  const found = useQuery({ queryKey: ['drawing-search', search, scope], enabled: !!search, queryFn: () => get<SearchRow[]>(`${D}/search?${qs({ q: search, family: scope })}`) });
  const familyName = roots.data?.folders.find((f) => f.id === family)?.name;
  const [text, setText] = useState(search);
  const [dlg, setDlg] = useState<null | 'new' | 'edit' | 'upload' | 'import' | 'relink'>(null);
  const [confirm, setConfirm] = useState(false);
  const [picked, setPicked] = useState<{ files: File[]; folder: boolean } | null>(null);
  const v = q.data; const here = v?.parent;
  const canManage = here ? here.canManage : !!v?.canCreate;
  const refresh = () => qc.invalidateQueries({ queryKey: ['drawing-folders'] });
  const open = (id: string) => { const n = new URLSearchParams(); n.set('folder', id); setSp(n); };
  return (
    <SqfPage title="Parts Drawings" crumbs={crumbs()} permission="sequfloh.view_drawings" description="Parts drawings kept in folders, one tab per family. Every revision is kept."
      actions={canManage && !here?.isArchived && <>
        <Button variant="outlined" onClick={() => setDlg('new')}>New folder</Button>
        <ImportButtons surface="parts-drawings" accept={ACCEPT.split(',')} what="drawing" onFiles={(files, folder) => { setPicked({ files, folder }); setDlg('import'); }} />
        <Button variant="outlined" onClick={() => { setPicked(null); setDlg('import'); }}>Import drawings</Button>
        {!here && <Button variant="outlined" onClick={() => setDlg('relink')}>Relink drawings</Button>}
      </>}>
      <Tabs value={folder && !family && q.isLoading ? false : family} variant="scrollable" scrollButtons="auto" allowScrollButtonsMobile data-testid="family-tabs" sx={{ mb: 2, borderBottom: 1, borderColor: 'divider' }}
        onChange={(_, id: string) => { const n = new URLSearchParams(); if (id) n.set('folder', id); if (archived) n.set('archived', '1'); if (search) n.set('q', search); if (everywhere) n.set('all', '1'); setSp(n); }}>
        <Tab value="" label="All" data-testid="family-tab-all" />
        {(roots.data?.folders ?? []).map((f) => <Tab key={f.id} value={f.id} label={familyLabel(f.name)} data-testid="family-tab" data-family={f.name} />)}
      </Tabs>
      <ListToolbar onSubmit={() => set('q', text.trim())}
        end={<FormControlLabel control={<Checkbox checked={archived} onChange={(_, c) => set('archived', c ? '1' : '')} />} label="Show archived" />}>
        <TextField size="small" label={family && !everywhere ? `Search ${familyLabel(familyName ?? '')} drawings` : 'Search drawings'} placeholder="Part number, drawing number or title" value={text} onChange={(e) => setText(e.target.value)} sx={{ width: { xs: '100%', sm: 360 } }} />
        <Button type="submit" variant="outlined">Search</Button>{search && <Button onClick={() => { setText(''); set('q', ''); }}>Clear</Button>}
        {family && <FormControlLabel control={<Checkbox checked={everywhere} inputProps={{ 'data-testid': 'search-all-families' } as object} onChange={(_, c) => set('all', c ? '1' : '')} />} label="Search all families" />}
      </ListToolbar>
      {search ? (found.isLoading ? <Loading /> : found.error ? <ErrorState error={found.error} /> : !found.data?.length ? <Empty text="No drawings match." /> :
        <DrawingTable rows={found.data} onRow={(id) => nav(`/sequfloh/drawings/d/${id}`)} withFolder />) : <>
        <Breadcrumbs sx={{ mb: SPACE.block }} data-testid="folder-crumbs">
          <Link component="button" onClick={() => setSp(archived ? { archived: '1' } : {})}>All folders</Link>
          {(v?.path ?? []).map((p) => <Link key={p.id} component="button" onClick={() => open(p.id)}>{p.name}</Link>)}
          {here && <Typography color="text.primary">{here.name}</Typography>}
        </Breadcrumbs>
        {here && <RecordHeader testid="folder-header" title={<Stack direction="row" gap={1} alignItems="center" flexWrap="wrap"><FolderOutlined color="action" />{here.name}{here.isArchived && <Chip size="small" color="warning" label="Archived" />}</Stack>}
          meta={here.part ? <Chip size="small" variant="outlined" label={`Part ${here.part.partNumber} — ${here.part.name}`} component={RouterLink} to={`/sequfloh/parts/${here.part.id}`} clickable />
            : <Typography variant="body2" color="text.secondary">Not linked to a part</Typography>}
          actions={<RecordActions surface="drawing-folder" edit={here.canManage && !here.isArchived && { onClick: () => setDlg('edit') }}
            importNode={here.canManage && !here.isArchived && <Button variant="contained" startIcon={<UploadFileOutlined />} onClick={() => setDlg('upload')}>Upload drawing</Button>}
            del={{ what: `folder ${here.name}`, url: `${D}/folders/${here.id}`, label: 'Delete folder', extra: 'Only empty folders can be deleted.', onDone: () => setSp(here.parentId ? { folder: here.parentId } : {}) }}
            more={[here.canManage && { label: here.isArchived ? 'Restore folder' : 'Archive folder', danger: !here.isArchived, onClick: () => setConfirm(true) }]} />} />}
        {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : <>
          {v!.folders.length > 0 && <SimpleTable head={['Folder', 'Part', 'Contents', 'Updated']} rows={v!.folders.map((f) => [
            <Link key="n" component="button" onClick={() => open(f.id)} sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.75 }}><FolderOutlined fontSize="small" />{f.name}</Link>,
            f.part ? <Mono key="p">{f.part.partNumber}</Mono> : '—', `${f.folders} folder${f.folders === 1 ? '' : 's'} · ${f.drawings} drawing${f.drawings === 1 ? '' : 's'}`, fmtDateTime(f.updatedAt)])} />}
          {here && v!.drawings.length > 0 && <Box sx={{ mt: 2 }}><DrawingTable rows={v!.drawings} onRow={(id) => nav(`/sequfloh/drawings/d/${id}`)} /></Box>}
          {here && v!.drawings.length === 1 && v!.folders.length === 0 && /\.pdf$/i.test(v!.drawings[0].file?.fileName ?? '') &&
            <Box component="iframe" title={v!.drawings[0].title} src={fileUrl(v!.drawings[0].id, undefined, true)} data-testid="folder-inline-drawing"
              sx={{ mt: 2, width: '100%', height: '75vh', border: 1, borderColor: 'divider' }} />}
          {v!.folders.length === 0 && (!here || v!.drawings.length === 0) && <Empty text={here ? (archived ? 'Nothing archived here.' : 'This folder is empty.') : archived ? 'No archived folders.' : 'No drawing folders yet. Make one for each part number, for example.'} />}
        </>}
      </>}
      <FolderDialog open={dlg === 'new' || dlg === 'edit'} edit={dlg === 'edit' ? here ?? undefined : undefined} parentId={dlg === 'new' ? (here?.id ?? null) : here?.parentId ?? null}
        onClose={() => setDlg(null)} onSaved={(id) => { refresh(); if (dlg === 'new') open(id); }} />
      {here && <UploadDialog open={dlg === 'upload'} folderId={here.id} defaultParts={here.part ? [{ id: here.part.id, label: `${here.part.partNumber} — ${here.part.name}` }] : []} onClose={() => setDlg(null)}
        onSaved={(id) => { refresh(); nav(`/sequfloh/drawings/d/${id}`); }} />}
      <RelinkDialog open={dlg === 'relink'} onClose={() => setDlg(null)} />
      {<ImportDrawingsDialog open={dlg === 'import'} folderId={here?.id ?? null} folderName={here?.name ?? 'All folders'} initial={picked} onClose={() => { setDlg(null); setPicked(null); }} onDone={refresh} />}
      {here && <Confirm open={confirm} title={here.isArchived ? 'Restore folder' : 'Archive folder'} confirm={here.isArchived ? 'Restore' : 'Archive'} danger={!here.isArchived}
        text={here.isArchived ? 'The folder and what is in it show again.' : 'The folder and what is in it are hidden from the list and from search. Nothing is deleted; you can restore it.'}
        onClose={() => setConfirm(false)} onConfirm={async () => { await post(`${D}/folders/${here.id}/archive`, { archived: !here.isArchived }); refresh(); }} />}
    </SqfPage>
  );
}

function DrawingTable({ rows, onRow, withFolder }: { rows: SearchRow[]; onRow: (id: string) => void; withFolder?: boolean }) {
  const view = (d: SearchRow) => (/\.(pdf|png|jpe?g)$/i.test(d.file?.fileName ?? '') ? viewerUrl(d.id) : fileUrl(d.id));
  return <SimpleTable head={['Drawing number', 'Title', 'Current rev', ...(withFolder ? ['Folder', 'Part'] : []), 'File', 'Updated', '']} rows={rows.map((d) => [
    <Link key="n" href={view(d)} target="_blank" rel="noopener" data-testid="drawing-open"><Mono>{d.drawingNumber || '—'}</Mono></Link>, d.title, <Chip key="r" size="small" label={`Rev ${d.currentRevision}`} />,
    ...(withFolder ? [d.folder ?? '—', d.partNumber ?? '—'] : []), d.file ? <FileName key="f" name={d.file.fileName} sizeBytes={d.file.sizeBytes} /> : '—', fmtDateTime(d.updatedAt ?? d.file?.createdAt),
    <Button key="d" size="small" data-testid="drawing-details" onClick={() => onRow(d.id)}>Details</Button>])} />;
}

function FolderDialog({ open, edit, parentId, onClose, onSaved }: { open: boolean; edit?: FolderView['parent']; parentId: string | null; onClose: () => void; onSaved: (id: string) => void }) {
  const [name, setName] = useState(''); const [part, setPart] = useState<Opt[]>([]); const [err, setErr] = useState<string | null>(null);
  const init = () => { setErr(null); setName(edit?.name ?? ''); setPart(edit?.part ? [{ id: edit.part.id, label: `${edit.part.partNumber} — ${edit.part.name}` }] : []); };
  const save = async () => {
    setErr(null);
    try {
      const body = { name, parentId, partId: part[0]?.id ?? null };
      if (edit) { await put(`${D}/folders/${edit.id}`, body); onSaved(edit.id); } else { const r = await post<{ id: string }>(`${D}/folders`, body); onSaved(r.id); }
      onClose();
    } catch (e) { setErr((e as Error).message); }
  };
  return <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" TransitionProps={{ onEnter: init }}>
    <FormDialogTitle>{edit ? 'Rename folder' : 'New drawing folder'}</FormDialogTitle>
    <DialogContent><Stack gap={2} sx={{ pt: 1 }}>
      {err && <Alert severity="error">{err}</Alert>}
      <TextField label="Folder name" required autoFocus value={name} onChange={(e) => setName(e.target.value)} helperText="For example the part number." />
      <PartsPicker label="Linked part (optional)" value={part} onChange={(v) => setPart(v.slice(-1))} />
    </Stack></DialogContent>
    <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" onClick={save} disabled={!name.trim()}>Save</Button></DialogActions>
  </Dialog>;
}

function UploadDialog({ open, folderId, defaultParts, onClose, onSaved }: { open: boolean; folderId: string; defaultParts: Opt[]; onClose: () => void; onSaved: (id: string) => void }) {
  const [f, setF] = useState(blankDrawing()); const [parts, setParts] = useState<Opt[]>([]); const [err, setErr] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  const save = async () => {
    setErr(null); setBusy(true);
    try { const r = await post<{ id: string }>(`${D}/folders/${folderId}/drawings`, drawingForm(f, { partIds: parts.map((p) => p.id).join(',') })); onSaved(r.id); onClose(); }
    catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" TransitionProps={{ onEnter: () => { setF(blankDrawing()); setParts(defaultParts); setErr(null); } }}>
    <FormDialogTitle>Upload drawing</FormDialogTitle>
    <DialogContent><Stack gap={2} sx={{ pt: 1 }}>
      {err && <Alert severity="error">{err}</Alert>}
      <DrawingFileFields f={f} set={setF} />
      <PartsPicker value={parts} onChange={setParts} />
    </Stack></DialogContent>
    <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" onClick={save} disabled={busy || !f.file || !f.title.trim() || !f.revision.trim()}>Upload</Button></DialogActions>
  </Dialog>;
}


interface DrawingDetail { id: string; title: string; drawingNumber?: string; notes?: string; currentRevision: string; isArchived: boolean; createdAt: string; updatedAt: string; canManage: boolean;
  folder: { id: string; name: string; isArchived: boolean; part?: FolderRow['part'] }; parts: { id: string; partNumber: string; name: string; numbers?: string }[];
  revisions: { id: string; revision: string; fileName: string; sizeBytes: number; notes?: string; createdAt: string; viewable: boolean; current: boolean }[] }

export function DrawingPage() {
  const { id } = useParams();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['drawing', id], queryFn: () => get<DrawingDetail>(`${D}/${id}`) });
  const [tab, setTab] = useState('revisions');
  const [dlg, setDlg] = useState<null | 'rev' | 'edit' | 'parts'>(null);
  const [confirm, setConfirm] = useState(false);
  const d = q.data;
  const refresh = () => { qc.invalidateQueries({ queryKey: ['drawing', id] }); qc.invalidateQueries({ queryKey: ['drawings-for-part'] }); };
  return (
    <SqfPage title={d ? label(d) : 'Drawing'} crumbs={crumbs(d?.drawingNumber || d?.title || '…')} permission="sequfloh.view_drawings"
      tabs={[{ label: 'Revisions', value: 'revisions' }]} tab={tab} onTab={setTab}
      actions={d && <RecordActions surface="drawing" edit={d.canManage && !d.isArchived && { onClick: () => setDlg('edit') }}
        print={{ href: fileUrl(d.id, undefined, true) }}
        importNode={d.canManage && !d.isArchived && <Button variant="contained" startIcon={<UploadFileOutlined />} onClick={() => setDlg('rev')}>New revision</Button>}
        del={{ what: `drawing ${d.drawingNumber || d.title}`, url: `${D}/${d.id}`, to: `/sequfloh/drawings?folder=${d.folder.id}`,
          extra: 'Every revision and its file is removed, and it is unlinked from parts and work orders.' }}
        more={[d.canManage && { label: d.isArchived ? 'Restore drawing' : 'Archive drawing', danger: !d.isArchived, onClick: () => setConfirm(true) }]}>
        <OpenDrawingButton d={{ ...d, viewable: !!d.revisions.find((r) => r.current)?.viewable }} size="medium" />
        {d.canManage && !d.isArchived && <Button variant="outlined" onClick={() => setDlg('parts')}>Link to parts</Button>}
      </RecordActions>}>
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : d && <>
        <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
          <Stack direction="row" gap={4} flexWrap="wrap">
            <Box><Typography variant="caption" color="text.secondary">Drawing number</Typography><Typography><Mono>{d.drawingNumber || '—'}</Mono></Typography></Box>
            <Box><Typography variant="caption" color="text.secondary">Current revision</Typography><Typography data-testid="current-rev">{d.currentRevision}</Typography></Box>
            <Box><Typography variant="caption" color="text.secondary">Folder</Typography><Typography><Link component={RouterLink} to={`/sequfloh/drawings?folder=${d.folder.id}`}>{d.folder.name}</Link></Typography></Box>
            <Box><Typography variant="caption" color="text.secondary">Linked parts</Typography><Stack direction="row" gap={0.5} flexWrap="wrap" data-testid="linked-parts">
              {[...(d.folder.part && !d.parts.some((p) => p.id === d.folder.part!.id) ? [d.folder.part] : []), ...d.parts].map((p) =>
                <Chip key={p.id} size="small" label={('numbers' in p && p.numbers ? p.numbers : p.partNumber) as string} component={RouterLink} to={`/sequfloh/parts/${p.id}`} clickable />)}
              {!d.parts.length && !d.folder.part && <Typography variant="body2" color="text.secondary">None</Typography>}</Stack></Box>
            {d.isArchived && <Chip color="warning" label="Archived" />}
          </Stack>
          {d.notes && <Typography variant="body2" sx={{ mt: 1.5, whiteSpace: 'pre-wrap' }}>{d.notes}</Typography>}
        </Paper>
        {tab === 'revisions' && <SimpleTable head={['Revision', 'File', 'Notes', 'Uploaded', '']} rows={d.revisions.map((r) => [
          <Stack key="r" direction="row" gap={1} alignItems="center"><b>{r.revision}</b>{r.current && <Chip size="small" color="primary" label="Current" />}</Stack>,
          <FileName key="f" name={r.fileName} sizeBytes={r.sizeBytes} />, r.notes ?? '—', fmtDateTime(r.createdAt),
          <Stack key="a" direction="row" gap={1}>{r.viewable && <Button size="small" component="a" href={viewerUrl(d.id, r.revision)} target="_blank" rel="noopener">View</Button>}
            <Button size="small" component="a" href={fileUrl(d.id, r.revision)}>Download</Button>
            <DeleteButton size="small" show={d.revisions.length > 1} what={`revision ${r.revision}`} url={`${D}/${d.id}/revisions/${encodeURIComponent(r.revision)}`} extra="The file for this revision is removed." /></Stack>])} />}
        <RevisionDialog open={dlg === 'rev'} d={d} onClose={() => setDlg(null)} onSaved={refresh} />
        <EditDialog open={dlg === 'edit'} d={d} onClose={() => setDlg(null)} onSaved={refresh} />
        <LinkPartsDialog open={dlg === 'parts'} d={d} onClose={() => setDlg(null)} onSaved={refresh} />
        <Confirm open={confirm} title={d.isArchived ? 'Restore drawing' : 'Archive drawing'} confirm={d.isArchived ? 'Restore' : 'Archive'} danger={!d.isArchived}
          text={d.isArchived ? 'The drawing shows in its folder and in search again.' : 'The drawing is hidden from its folder, search and parts. Nothing is deleted.'}
          onClose={() => setConfirm(false)} onConfirm={async () => { await post(`${D}/${d.id}/archive`, { archived: !d.isArchived }); refresh(); }} />
      </>}
    </SqfPage>
  );
}

const nextRev = (r: string) => (/^[A-Y]$/.test(r) ? String.fromCharCode(r.charCodeAt(0) + 1) : /^\d+$/.test(r) ? String(Number(r) + 1) : '');
function RevisionDialog({ open, d, onClose, onSaved }: { open: boolean; d: DrawingDetail; onClose: () => void; onSaved: () => void }) {
  const [file, setFile] = useState<File | null>(null); const [rev, setRev] = useState(''); const [notes, setNotes] = useState(''); const [err, setErr] = useState<string | null>(null);
  const save = async () => {
    setErr(null);
    const fd = new FormData(); fd.append('file', file!); fd.append('revision', rev); fd.append('notes', notes);
    try { await post(`${D}/${d.id}/revisions`, fd); onSaved(); onClose(); } catch (e) { setErr((e as Error).message); }
  };
  return <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" TransitionProps={{ onEnter: () => { setFile(null); setRev(nextRev(d.currentRevision)); setNotes(''); setErr(null); } }}>
    <FormDialogTitle>New revision of {label(d)}</FormDialogTitle>
    <DialogContent><Stack gap={2} sx={{ pt: 1 }}>
      {err && <Alert severity="error">{err}</Alert>}
      <Typography variant="body2" color="text.secondary">Revision {d.currentRevision} and earlier stay in the history.</Typography>
      <Button variant="outlined" component="label">{file ? file.name : 'Choose file'}<input hidden type="file" accept={ACCEPT} data-testid="revision-file" onChange={(e) => setFile(e.target.files?.[0] ?? null)} /></Button>
      <TextField label="Revision" required value={rev} onChange={(e) => setRev(e.target.value.toUpperCase())} />
      <TextField label="What changed" multiline minRows={2} value={notes} onChange={(e) => setNotes(e.target.value)} />
    </Stack></DialogContent>
    <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" onClick={save} disabled={!file || !rev.trim()}>Upload revision</Button></DialogActions>
  </Dialog>;
}

function EditDialog({ open, d, onClose, onSaved }: { open: boolean; d: DrawingDetail; onClose: () => void; onSaved: () => void }) {
  const [f, setF] = useState({ title: '', drawingNumber: '', notes: '' }); const [err, setErr] = useState<string | null>(null);
  const save = async () => { setErr(null); try { await put(`${D}/${d.id}`, { ...f, revision: null }); onSaved(); onClose(); } catch (e) { setErr((e as Error).message); } };
  return <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" TransitionProps={{ onEnter: () => { setF({ title: d.title, drawingNumber: d.drawingNumber ?? '', notes: d.notes ?? '' }); setErr(null); } }}>
    <FormDialogTitle>Edit drawing</FormDialogTitle>
    <DialogContent><Stack gap={2} sx={{ pt: 1 }}>
      {err && <Alert severity="error">{err}</Alert>}
      <TextField label="Title" required value={f.title} onChange={(e) => setF({ ...f, title: e.target.value })} />
      <TextField label="Drawing number" value={f.drawingNumber} onChange={(e) => setF({ ...f, drawingNumber: e.target.value })} />
      <TextField label="Notes" multiline minRows={3} value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} />
    </Stack></DialogContent>
    <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" onClick={save} disabled={!f.title.trim()}>Save</Button></DialogActions>
  </Dialog>;
}

function LinkPartsDialog({ open, d, onClose, onSaved }: { open: boolean; d: DrawingDetail; onClose: () => void; onSaved: () => void }) {
  const [parts, setParts] = useState<Opt[]>([]); const [err, setErr] = useState<string | null>(null);
  const save = async () => { setErr(null); try { await put(`${D}/${d.id}/parts`, { ids: parts.map((p) => p.id) }); onSaved(); onClose(); } catch (e) { setErr((e as Error).message); } };
  return <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" TransitionProps={{ onEnter: () => { setParts(d.parts.map((p) => ({ id: p.id, label: `${p.partNumber} — ${p.name}` }))); setErr(null); } }}>
    <FormDialogTitle>Link {label(d)} to parts</FormDialogTitle>
    <DialogContent><Stack gap={2} sx={{ pt: 1 }}>{err && <Alert severity="error">{err}</Alert>}<PartsPicker value={parts} onChange={setParts} />
      {d.folder.part && <Typography variant="caption" color="text.secondary">Its folder is linked to part {d.folder.part.partNumber}, so it shows for that part too.</Typography>}</Stack></DialogContent>
    <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" onClick={save}>Save</Button></DialogActions>
  </Dialog>;
}

/** The new-tab viewer: the revision's PDF or image filling the window, with what it is at the top. */
export function DrawingViewer() {
  const id = window.location.pathname.split('/').pop()!;
  const rev = new URLSearchParams(window.location.search).get('revision') ?? undefined;
  const q = useQuery({ queryKey: ['drawing', id], queryFn: () => get<DrawingDetail>(`${D}/${id}`) });
  const d = q.data; const r = d ? (rev ? d.revisions.find((x) => x.revision === rev) : d.revisions.find((x) => x.current)) : undefined;
  const isPdf = r?.fileName.toLowerCase().endsWith('.pdf');
  if (d) document.title = `${label(d)} rev ${r?.revision ?? ''}`;
  return <Box sx={{ height: '100vh', display: 'flex', flexDirection: 'column' }}>
    <Stack direction="row" alignItems="center" gap={2} sx={{ px: 2, py: 1, borderBottom: 1, borderColor: 'divider' }}>
      <span data-fullscreen-viewer="drawing-viewer" style={{ display: 'contents' }}><FullScreenButton size="small" /></span>
      <Typography sx={{ fontWeight: 700 }} data-testid="viewer-title">{d ? `${label(d)} · rev ${r?.revision ?? '?'}` : 'Drawing'}</Typography>
      {r && !r.current && <Chip size="small" color="warning" label={`Not current · current is rev ${d!.currentRevision}`} />}
      {r?.current && <Chip size="small" color="primary" label="Current revision" />}
      <Box sx={{ flex: 1 }} />{r && <Button size="small" component="a" href={fileUrl(id, r.revision)}>Download</Button>}
    </Stack>
    {q.isLoading ? <Loading /> : q.error ? <Box sx={{ p: 3 }}><ErrorState error={q.error} /></Box> : !r ? <Box sx={{ p: 3 }}><Empty text="That revision was not found." /></Box> :
      !r.viewable ? <Box sx={{ p: 3 }}><Empty text="This file type does not open in the browser. Use Download." /></Box> :
      isPdf ? <Box component="iframe" title="Drawing" src={fileUrl(id, r.revision, true)} sx={{ flex: 1, border: 0, width: '100%' }} data-testid="drawing-frame" />
        : <Box sx={{ flex: 1, overflow: 'auto', textAlign: 'center', bgcolor: 'grey.100' }}><Box component="img" alt={label(d!)} src={fileUrl(id, r.revision, true)} sx={{ maxWidth: '100%' }} data-testid="drawing-image" /></Box>}
  </Box>;
}

/** Runs the part matching again over every drawing and adds the links that are missing. Links are never removed. */
function RelinkDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const qc = useQueryClient();
  const [busy, setBusy] = useState(false);
  const [res, setRes] = useState<{ checked: number; added: number; drawings: number } | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const run = async () => {
    setBusy(true); setErr(null);
    try { setRes(await post(`${D}/relink`, {})); qc.invalidateQueries(); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return <Dialog open={open} onClose={busy ? undefined : onClose} fullWidth maxWidth="sm" TransitionProps={{ onEnter: () => { setRes(null); setErr(null); } }}>
    <FormDialogTitle>Relink drawings</FormDialogTitle>
    <DialogContent><Stack gap={2}>
      <Typography variant="body2">Checks every drawing against the parts and links each drawing to every part whose part number, DWG # or drawing number field matches the drawing title or its folder name
        (for fuel pumps the JH PN, so one drawing links to all the parts that share it). Case, spaces, dashes and underscores are ignored. Links that are already there, including ones made by hand, are kept.</Typography>
      {busy && <LinearProgress />}
      {err && <Alert severity="error">{err}</Alert>}
      {res && <Alert severity="success" data-testid="relink-result">Checked {res.checked.toLocaleString('en-US')} drawings. Added {res.added.toLocaleString('en-US')} part link{res.added === 1 ? '' : 's'}
        {res.added > 0 && ` to ${res.drawings.toLocaleString('en-US')} drawing${res.drawings === 1 ? '' : 's'}`}.</Alert>}
    </Stack></DialogContent>
    <DialogActions>
      <Button onClick={onClose} disabled={busy}>{res ? 'Close' : 'Cancel'}</Button>
      {!res && <Button variant="contained" onClick={run} disabled={busy}>Relink</Button>}
    </DialogActions>
  </Dialog>;
}
