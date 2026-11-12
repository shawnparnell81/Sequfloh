import { useEffect, useRef, useState, type CSSProperties, type InputHTMLAttributes } from 'react';
import { FormDialogTitle } from '../components/FullScreen';
import { Alert, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, FormControlLabel, LinearProgress, Stack, TextField, Typography } from '@mui/material';
import { post } from '../api';
import { Mono } from './common';
import { SimpleTable } from './Parts';

const D = '/api/drawings/import';
const KNOWN = ['.pdf', '.png', '.jpg', '.jpeg', '.tif', '.tiff', '.dwg', '.dxf', '.step', '.stp', '.iges', '.igs', '.slddrw', '.sldprt', '.docx', '.doc'];
const WORD = ['.docx', '.doc'];
/** Names sent per plan or folder request. The server accepts 2000; this stays well under that and under the request size limit. */
const BATCH = 1000;
const PARALLEL = 3;
const ext = (n: string) => (n.match(/\.[^./]+$/)?.[0] ?? '').toLowerCase();
export const gb = (b: number) => (b >= 1 << 30 ? `${(b / (1 << 30)).toFixed(2)} GB` : b >= 1 << 20 ? `${(b / (1 << 20)).toFixed(1)} MB` : `${Math.max(1, Math.round(b / 1024))} KB`);
const dirOf = (p: string) => p.split('/').slice(0, -1).join('/');
const offscreen: CSSProperties = { position: 'absolute', width: 1, height: 1, padding: 0, margin: -1, overflow: 'hidden', clipPath: 'inset(50%)', whiteSpace: 'nowrap', border: 0 };
const folderAttrs = { webkitdirectory: '', directory: '' } as InputHTMLAttributes<HTMLInputElement>;
const fail = (e: unknown) => {
  const m = e instanceof Error ? e.message : '';
  if (!m || m === 'Failed to fetch') return 'The import could not reach the server. Nothing was uploaded.';
  return m;
};

/**
 * The path a picked file is stored under. Backslashes are treated as separators, and spaces at the ends of a folder name are dropped, so a Windows
 * folder matches the folders already here. At the top of Drawings the chosen folder's own name is dropped when the file sits in a subfolder; a file
 * loose in the chosen folder keeps that folder.
 */
export function pickedPath(file: File, folderPick: boolean, top: boolean, preset?: string) {
  const raw = preset ?? ((folderPick && file.webkitRelativePath) || file.name);
  const parts = raw.replace(/\\/g, '/').split('/').map((s) => s.trim()).filter(Boolean);
  if (top && parts.length > 2) parts.shift();
  return parts.join('/');
}

const main = (i: { kind: string }) => i.kind === 'pdf' || i.kind === 'word';
/** Compared like part numbers on the server: upper case, without spaces, dashes, underscores or slashes. */
const norm = (s: string) => s.toUpperCase().replace(/[\s\-_/\\]/g, '');
const stem = (n: string) => n.replace(/\.[^./]+$/, '').trim();

/** A revision the import can store: letters, numbers, '-' or '.', up to 20 characters. Anything else, including a blank, becomes A. */
export function importRevision(value: string | undefined | null) {
  const rev = (value ?? '').trim().toUpperCase();
  return /^[A-Z0-9.-]{1,20}$/.test(rev) ? rev : 'A';
}


/**
 * The picked files that are their folder's primary drawing: named like the folder (ignoring case, spaces, dashes and underscores), or the only PDF or
 * Word file in it. Only these are linked to the part a folder is named for; the other files go into the same folder for reference.
 */
export function primaryPaths(items: { path: string; kind: string }[]) {
  const byDir = new Map<string, string[]>();
  for (const i of items) if (main(i)) { const d = dirOf(i.path); byDir.set(d, [...(byDir.get(d) ?? []), i.path]); }
  const out = new Set<string>();
  for (const [dir, paths] of byDir) {
    const folder = norm(dir.split('/').pop() ?? '');
    for (const p of paths) if ((folder && norm(stem(p.split('/').pop() ?? '')) === folder) || paths.length === 1) out.add(p);
  }
  return out;
}
interface Item { file: File; path: string; kind: 'pdf' | 'word' | 'other' | 'unsupported'; exists?: boolean; conflict?: boolean; rejected?: boolean; reason?: string | null; parts?: string[] }
interface Plan { files: { path: string; exists: boolean; conflict?: boolean; rejected?: boolean; reason?: string | null; parts: string[] }[]; folders: { path: string; exists: boolean; conflict?: boolean; rejected?: boolean; reason?: string | null; parts: string[] }[] }
interface Start { token: string; url?: string; uploadId?: string; partSize: number; partUrls: string[]; direct: boolean; contentType: string }
interface Progress { done: number; total: number; bytes: number; totalBytes: number; skipped: number; failed: string[]; linked: number; folders: number; doneBytes: number; cancelled: boolean; conflicts: number }

/** One PUT with upload progress; returns the ETag the storage answers with. */
function putBlob(url: string, body: Blob, contentType: string | null, onBytes: (n: number) => void, signal: AbortSignal): Promise<string> {
  return new Promise((resolve, reject) => {
    const x = new XMLHttpRequest();
    x.open('PUT', url);
    if (contentType) x.setRequestHeader('Content-Type', contentType);
    let sent = 0;
    x.upload.onprogress = (e) => { onBytes(e.loaded - sent); sent = e.loaded; };
    x.onload = () => { if (x.status >= 200 && x.status < 300) { onBytes(body.size - sent); resolve(x.getResponseHeader('ETag') ?? ''); } else reject(new Error(`The upload was refused (HTTP ${x.status}).`)); };
    x.onerror = () => reject(new Error('The upload could not reach file storage. If this keeps happening, file storage may need its CORS settings (see changes.md).'));
    x.onabort = () => reject(new DOMException('Stopped', 'AbortError'));
    signal.addEventListener('abort', () => x.abort());
    x.send(body);
  });
}

/**
 * Import a whole folder of drawings (or several files) into the current folder: the folder tree is recreated, every file becomes a drawing titled
 * with its file name, and the files go straight to file storage. Picking the same folder again skips what is already here.
 */
export function ImportDrawingsDialog({ open, folderId, folderName, onClose, onDone, initial }: { open: boolean; folderId: string | null; folderName: string; onClose: () => void; onDone: () => void;
  /** Files already picked with Import file or Import folder on the page. paths are read at pick time, before the input is cleared. */ initial?: { files: File[]; paths?: string[]; folder: boolean } | null }) {
  const [items, setItems] = useState<Item[]>([]);
  const [folders, setFolders] = useState<Plan['folders']>([]);
  const [revision, setRevision] = useState('A');
  const [linkParts, setLinkParts] = useState(true);
  const [others, setOthers] = useState(false);
  const [busy, setBusy] = useState<'' | 'reading' | 'importing'>('');
  const [err, setErr] = useState<string | null>(null);
  const [prog, setProg] = useState<Progress | null>(null);
  const stop = useRef<AbortController | null>(null);
  const gen = useRef(0);
  const taken = useRef<typeof initial>(null);
  const openedAt = useRef(0);

  const top = folderId === null;
  const reset = () => { setItems([]); setFolders([]); setRevision('A'); setLinkParts(true); setOthers(false); setErr(null); setProg(null); setBusy(''); };
  const close = () => { gen.current++; setBusy(''); onClose(); };

  const pick = async (list: FileList | File[] | null, folderPick: boolean, presets?: string[]) => {
    const mine = ++gen.current;
    if (!list?.length) { setErr('No files were read from that folder. Nothing was imported.'); setBusy(''); return; }
    setErr(null); setProg(null); setBusy('reading');
    try {
      const unread: string[] = [];
      const all: Item[] = [];
      [...list].forEach((file, index) => {
        try {
          if (!file.name || file.name.startsWith('.')) return;
          const path = presets?.[index] ?? pickedPath(file, folderPick, top);
          if (!path) return;
          const e = ext(file.name);
          all.push({ file, path, kind: e === '.pdf' ? 'pdf' : WORD.includes(e) ? 'word' : !top && KNOWN.includes(e) ? 'other' : 'unsupported' });
        } catch (e) { unread.push(`${file.name || 'A file'}: ${fail(e)}`); }
      });
      if (mine !== gen.current) return;
      if (all.length === 0) { setErr(unread[0] ?? 'No files were read from that folder. Nothing was imported.'); return; }
      const planned = await plan(all, linkParts);
      if (mine !== gen.current) return;
      setItems(planned.items); setFolders(planned.folders);
      if (unread.length) setErr(unread.slice(0, 5).join(' '));
    } catch (e) { if (mine === gen.current) setErr(fail(e)); }
    finally { if (mine === gen.current) setBusy(''); }
  };
  useEffect(() => { if (open) openedAt.current = Date.now(); }, [open]);
  useEffect(() => {
    if (!open) { taken.current = null; return; }
    if (!initial || taken.current === initial) return;
    taken.current = initial;
    reset();
    void pick(initial.files, initial.folder, initial.paths);
  }, [open, initial]); // eslint-disable-line react-hooks/exhaustive-deps


  const plan = async (all: Item[], link: boolean) => {
    const dirs = [...new Set(all.filter((i) => i.kind !== 'unsupported').flatMap((i) => { const p = dirOf(i.path).split('/').filter(Boolean); return p.map((_, n) => p.slice(0, n + 1).join('/')); }))];
    const byPath = new Map<string, Plan['files'][number]>();
    const fl: Plan['folders'] = [];
    const known = all.filter((i) => i.kind !== 'unsupported');
    for (let n = 0; n < Math.max(known.length, dirs.length); n += BATCH) {
      const r = await post<Plan>(`${D}/plan`, { folderId, linkParts: link, files: known.slice(n, n + BATCH).map((i) => ({ path: i.path, size: i.file.size })), folders: dirs.slice(n, n + BATCH) });
      r.files.forEach((f) => byPath.set(f.path, f)); fl.push(...r.folders);
    }
    return { items: all.map((i) => { const hit = byPath.get(i.path); return { ...i, exists: hit?.exists, conflict: hit?.conflict, rejected: hit?.rejected, reason: hit?.reason, parts: hit?.parts }; }), folders: fl };
  };

  const todo = items.filter((i) => (main(i) || (others && i.kind === 'other')) && !i.exists && !i.conflict && !i.rejected);
  const run = async () => {
    setErr(null); setBusy('importing');
    const ac = new AbortController(); stop.current = ac;
    const conflicts = items.filter((i) => i.conflict).length;
    const p: Progress = { done: 0, total: todo.length, bytes: 0, totalBytes: todo.reduce((s, i) => s + i.file.size, 0), skipped: 0, failed: [], linked: 0, doneBytes: 0, cancelled: false, conflicts, folders: folders.filter((f) => !f.exists && !f.rejected).length };
    const tick = () => setProg({ ...p, failed: [...p.failed] });
    const revisionToStore = importRevision(revision);
    tick();
    try {
      // folders first, parents before children, so files can go up side by side
      const dirs = [...new Set(todo.map((i) => dirOf(i.path)).filter(Boolean))];
      const ids: Record<string, string> = folderId ? { '': folderId } : {};
      for (let n = 0; n < dirs.length; n += BATCH) Object.assign(ids, await post<Record<string, string>>(`${D}/folders`, { folderId, linkParts, paths: dirs.slice(n, n + BATCH) }));
      const primary = primaryPaths(items);
      const queue = [...todo];
      const one = async (i: Item) => {
        const dir = dirOf(i.path);
        const dest = ids[dir];
        let s: Start | null = null;
        try {
          if (!dest) throw new Error('No matching folder for this file. Nothing was uploaded for it.');
          s = await post<Start>(`${D}/start`, { folderId: dest, fileName: i.file.name, size: i.file.size });
          const add = (n: number) => { p.bytes += n; tick(); };
          let parts: { partNumber: number; eTag: string }[] = [];
          if (s.uploadId) {
            for (let n = 0; n < s.partUrls.length; n++) {
              if (ac.signal.aborted) throw new DOMException('Stopped', 'AbortError');
              const tag = await putBlob(s.partUrls[n], i.file.slice(n * s.partSize, (n + 1) * s.partSize), null, add, ac.signal);
              parts.push({ partNumber: n + 1, eTag: tag });
            }
          } else await putBlob(s.url!, i.file, s.contentType, add, ac.signal);
          const leaf = dir.split('/').filter(Boolean).pop();
          const r = await post<{ skipped: boolean; linked: number }>(`${D}/finish`, { token: s.token, uploadId: s.uploadId ?? null, parts, revision: revisionToStore, linkParts, matchNames: leaf ? [leaf] : [], primary: primary.has(i.path) });
          parts = [];
          if (r.skipped) p.skipped++; else { p.done++; p.doneBytes += i.file.size; p.linked += r.linked; }
        } catch (e) {
          if (s) post(`${D}/abort`, { token: s.token, uploadId: s.uploadId ?? null }).catch(() => {});
          if ((e as Error).name === 'AbortError') return;
          p.failed.push(`${i.path}: ${fail(e)}`);
        }
        tick();
      };
      await Promise.all(Array.from({ length: PARALLEL }, async () => {
        try {
          for (;;) {
            if (ac.signal.aborted) return;
            const next = queue.shift();
            if (!next) return;
            await one(next);
          }
        } catch (e) { if ((e as Error).name !== 'AbortError') { p.failed.push(fail(e)); tick(); } }
      }));
    } catch (e) { if ((e as Error).name !== 'AbortError') setErr(fail(e)); }
    finally {
      await post(`${D}/summary`, { folderId, imported: p.done, folders: p.folders, bytes: p.doneBytes,
        skipped: p.skipped + items.filter((i) => i.exists).length, failed: p.failed.length, linked: p.linked, cancelled: ac.signal.aborted, conflicts: p.conflicts }).catch(() => {});
      p.cancelled = ac.signal.aborted; stop.current = null; setBusy(''); tick(); onDone();
    }
  };

  const pdfs = items.filter((i) => i.kind === 'pdf'), words = items.filter((i) => i.kind === 'word'), other = items.filter((i) => i.kind === 'other'), bad = items.filter((i) => i.kind === 'unsupported');
  const already = items.filter((i) => i.exists).length;
  const matches = [...folders.filter((f) => f.parts?.length).map((f) => ({ name: f.path, kind: 'Folder', parts: f.parts ?? [] })),
    ...items.filter((i) => i.parts?.length && (main(i) || others)).map((i) => ({ name: i.path, kind: 'Drawing', parts: i.parts! }))];
  const pct = prog && prog.totalBytes ? Math.min(100, (prog.bytes / prog.totalBytes) * 100) : 0;
  const finished = prog && busy === '';

  return <Dialog open={open} onClose={(_, reason) => { if (busy === 'importing') return; if (reason === 'backdropClick' && Date.now() - openedAt.current < 600) return; close(); }} fullWidth maxWidth="md" TransitionProps={{ onEnter: () => { if (!initial) reset(); } }}>
    <FormDialogTitle>Import drawings into {folderName}</FormDialogTitle>
    <DialogContent><Stack gap={2} sx={{ pt: 1 }} data-testid="import-drawings">
      {err && <Alert severity="error">{err}</Alert>}
      {!prog && <>
        {top ? <Typography variant="body2" color="text.secondary">Choose a folder. Its subfolders are made as drawing folders here, with the same names and nesting, and each PDF or Word file goes into its matching folder,
          titled with its file name. Only PDF and Word (DOCX, DOC) files are imported; other files are skipped. A folder already here is used again, including when the only difference is capital letters or spaces at the ends of a name. Choosing the same folder again skips a file already imported with the same name and size. A file whose contents differ is left alone and is not saved as another drawing or another revision. A path that steps out of the folder is rejected.</Typography>
        : <Typography variant="body2" color="text.secondary">Choose a folder to bring in everything in it, with its subfolders, or choose several files. Each file becomes a drawing titled with its file name.
          Files go straight to file storage. A folder already here is used again, including when the only difference is capital letters or spaces at the ends of a name. Choosing the same folder again skips a file that is already here with the same name and size. A file whose contents differ is left alone and is not saved as another drawing or another revision. A path that steps out of the folder is rejected.</Typography>}
        <Stack direction="row" gap={1}>
          <Button variant="outlined" component="label" disabled={!!busy}>Choose folder
            <input type="file" {...folderAttrs} multiple data-testid="import-folder-input" style={offscreen} onChange={(e) => { const input = e.currentTarget; const files = input.files ? Array.from(input.files) : []; input.value = ''; void pick(files, true); }} /></Button>
          {!top && <Button variant="outlined" component="label" disabled={!!busy}>Choose files
            <input type="file" multiple data-testid="import-files-input" style={offscreen} onChange={(e) => { const input = e.currentTarget; const files = input.files ? Array.from(input.files) : []; input.value = ''; void pick(files, false); }} /></Button>}
        </Stack>
        <Stack direction="row" gap={2} alignItems="center" flexWrap="wrap">
          <TextField size="small" label="Revision when the Part Master has none" required value={revision} autoComplete="off" onChange={(e) => { const v = e.target.value.toUpperCase().trim(); if (/^[A-Z0-9.-]{0,20}$/.test(v)) setRevision(v); }} sx={{ width: 280 }} helperText="Used only if the Part Master has no usable drawing revision. Letters, numbers, '-' or '.' only. Never read from the file name." />
          <FormControlLabel control={<Checkbox checked={linkParts} disabled={!!busy} onChange={(_, c) => { setLinkParts(c); if (items.length) { setBusy('reading'); plan(items, c).then((planned) => { setItems(planned.items); setFolders(planned.folders); }).catch((e) => setErr(fail(e))).finally(() => setBusy('')); } }} />}
            label="Link to parts by matching name" />
        </Stack>
        {busy === 'reading' && <Box><Typography variant="body2" sx={{ mb: 1 }}>Reading the folder…</Typography><LinearProgress /></Box>}
        {items.length > 0 && <>
          <Stack direction="row" gap={3} flexWrap="wrap" data-testid="import-preview">
            <Fact label="Folders" value={`${folders.length.toLocaleString('en-US')} (${folders.filter((f) => !f.exists).length.toLocaleString('en-US')} new)`} />
            <Fact label="Files" value={items.length.toLocaleString('en-US')} />
            <Fact label="Total size" value={gb(items.reduce((s, i) => s + i.file.size, 0))} />
            <Fact label="PDF files" value={pdfs.length.toLocaleString('en-US')} />
            <Fact label="Word files" value={words.length.toLocaleString('en-US')} />
            {top && <Fact label="Skipped (not PDF or Word)" value={bad.length.toLocaleString('en-US')} />}
            <Fact label="Already imported" value={already.toLocaleString('en-US')} />
            <Fact label="Left in place" value={items.filter((i) => i.conflict).length.toLocaleString('en-US')} />
            <Fact label="Rejected paths" value={items.filter((i) => i.rejected).length.toLocaleString('en-US')} />
            <Fact label="To import" value={`${todo.length.toLocaleString('en-US')} · ${gb(todo.reduce((s, i) => s + i.file.size, 0))}`} />
          </Stack>
          {items.some((i) => i.conflict) && <Alert severity="warning" data-testid="import-conflicts">{items.filter((i) => i.conflict).slice(0, 8).map((i) => <div key={i.path}>{i.path}: {i.reason}</div>)}</Alert>}
          {items.some((i) => i.rejected) && <Alert severity="error" data-testid="import-rejected">{items.filter((i) => i.rejected).slice(0, 8).map((i) => <div key={i.path}>{i.path}: {i.reason}</div>)}</Alert>}
          {other.length > 0 && <Alert severity="warning" data-testid="non-pdf">{other.length} file{other.length === 1 ? ' is' : 's are'} not PDF or Word ({[...new Set(other.map((i) => ext(i.file.name)))].join(', ')}), for example {other[0].path}.
            <FormControlLabel sx={{ display: 'block' }} control={<Checkbox checked={others} onChange={(_, c) => setOthers(c)} />} label="Import these too (they download instead of opening in the browser)" /></Alert>}
          {bad.length > 0 && !top && <Alert severity="info">{bad.length} file{bad.length === 1 ? ' is' : 's are'} not a drawing type and will be skipped, for example {bad[0].path}.</Alert>}
          {linkParts && <Box>
            <Typography variant="subtitle2" sx={{ mb: 1 }}>Part matches ({matches.length.toLocaleString('en-US')})</Typography>
            {matches.length === 0 ? <Typography variant="body2" color="text.secondary">No folder or file name matches a part number, DWG # or JH PN.</Typography>
              : <SimpleTable head={['Folder or drawing', '', 'Parts', 'Links to part']} rows={matches.slice(0, 50).map((m) => [<Mono key="n">{m.name}</Mono>, m.kind,
                <span key="c" data-testid="match-count">{`${m.parts.length.toLocaleString('en-US')} part${m.parts.length === 1 ? '' : 's'}`}</span>,
                m.parts.slice(0, 6).join(', ') + (m.parts.length > 6 ? ` and ${(m.parts.length - 6).toLocaleString('en-US')} more` : '')])} />}
            {matches.length > 50 && <Typography variant="caption" color="text.secondary">Showing the first 50.</Typography>}
          </Box>}
        </>}
      </>}
      {prog && <Box data-testid="import-progress">
        <Typography variant="body2" sx={{ mb: 1 }}>{finished ? (prog.cancelled ? 'Stopped. ' : 'Finished. ') : 'Importing… '}
          {(prog.done + prog.skipped + prog.failed.length).toLocaleString('en-US')} of {prog.total.toLocaleString('en-US')} files · {gb(prog.bytes)} of {gb(prog.totalBytes)}</Typography>
        <LinearProgress variant="determinate" value={pct} />
        <Typography variant="caption" color="text.secondary" component="div" sx={{ mt: 1 }}>{prog.folders} new folders · {prog.linked} part links · {prog.skipped} already here · {prog.conflicts} left in place · {prog.failed.length} failed</Typography>
        {prog.failed.length > 0 && <Alert severity="error" sx={{ mt: 1 }}>{prog.failed.slice(0, 10).map((f) => <div key={f}>{f}</div>)}{prog.failed.length > 10 && <div>…and {prog.failed.length - 10} more. Choose the folder again to retry them.</div>}</Alert>}
      </Box>}
    </Stack></DialogContent>
    <DialogActions>
      {busy === 'importing' ? <Button color="error" onClick={() => stop.current?.abort()}>Cancel import</Button> : <Button onClick={close}>{finished ? 'Close' : 'Cancel'}</Button>}
      {!prog && <Button variant="contained" disabled={!!busy || todo.length === 0 || !revision.trim()} onClick={run}>Import {todo.length.toLocaleString('en-US')} drawing{todo.length === 1 ? '' : 's'}</Button>}
    </DialogActions>
  </Dialog>;
}

function Fact({ label, value }: { label: string; value: string }) {
  return <Box><Typography variant="caption" color="text.secondary">{label}</Typography><Typography variant="body1">{value}</Typography></Box>;
}
