import { useRef, useState } from 'react';
import { Alert, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, LinearProgress, Stack, TextField, Typography } from '@mui/material';
import { post } from '../api';
import { Mono } from './common';
import { SimpleTable } from './Parts';

const D = '/api/drawings/import';
const KNOWN = ['.pdf', '.png', '.jpg', '.jpeg', '.tif', '.tiff', '.dwg', '.dxf', '.step', '.stp', '.iges', '.igs', '.slddrw', '.sldprt'];
const BATCH = 1000;
const PARALLEL = 3;
const ext = (n: string) => (n.match(/\.[^./]+$/)?.[0] ?? '').toLowerCase();
export const gb = (b: number) => (b >= 1 << 30 ? `${(b / (1 << 30)).toFixed(2)} GB` : b >= 1 << 20 ? `${(b / (1 << 20)).toFixed(1)} MB` : `${Math.max(1, Math.round(b / 1024))} KB`);
const dirOf = (p: string) => p.split('/').slice(0, -1).join('/');

interface Item { file: File; path: string; kind: 'pdf' | 'other' | 'unsupported'; exists?: boolean; parts?: string[] }
interface Plan { files: { path: string; exists: boolean; parts: string[] }[]; folders: { path: string; exists: boolean; parts: string[] }[] }
interface Start { token: string; url?: string; uploadId?: string; partSize: number; partUrls: string[]; direct: boolean; contentType: string }
interface Progress { done: number; total: number; bytes: number; totalBytes: number; skipped: number; failed: string[]; linked: number; folders: number; doneBytes: number; cancelled: boolean }

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
export function ImportDrawingsDialog({ open, folderId, folderName, onClose, onDone }: { open: boolean; folderId: string | null; folderName: string; onClose: () => void; onDone: () => void }) {
  const [items, setItems] = useState<Item[]>([]);
  const [folders, setFolders] = useState<Plan['folders']>([]);
  const [revision, setRevision] = useState('A');
  const [linkParts, setLinkParts] = useState(true);
  const [others, setOthers] = useState(false);
  const [busy, setBusy] = useState<'' | 'reading' | 'importing'>('');
  const [err, setErr] = useState<string | null>(null);
  const [prog, setProg] = useState<Progress | null>(null);
  const stop = useRef<AbortController | null>(null);
  const dirInput = useRef<HTMLInputElement>(null);

  const top = folderId === null;
  const reset = () => { setItems([]); setFolders([]); setRevision('A'); setLinkParts(true); setOthers(false); setErr(null); setProg(null); setBusy(''); };

  const pick = async (list: FileList | null, folderPick: boolean) => {
    if (!list?.length) return;
    setErr(null); setProg(null); setBusy('reading');
    try {
      const all: Item[] = [...list].filter((f) => !f.name.startsWith('.')).map((file) => {
        let path = (folderPick && file.webkitRelativePath) || file.name;
        // at the top level the chosen folder's subfolders become top-level folders; files loose in the chosen folder keep it as their folder
        if (top && path.split('/').length > 2) path = path.split('/').slice(1).join('/');
        const e = ext(file.name);
        return { file, path, kind: e === '.pdf' ? 'pdf' : !top && KNOWN.includes(e) ? 'other' : 'unsupported' };
      });
      await plan(all, linkParts);
    } catch (e) { setErr((e as Error).message); } finally { setBusy(''); }
  };

  const plan = async (all: Item[], link: boolean) => {
    const dirs = [...new Set(all.filter((i) => i.kind !== 'unsupported').flatMap((i) => { const p = dirOf(i.path).split('/').filter(Boolean); return p.map((_, n) => p.slice(0, n + 1).join('/')); }))];
    const byPath = new Map<string, Plan['files'][number]>();
    const fl: Plan['folders'] = [];
    const known = all.filter((i) => i.kind !== 'unsupported');
    for (let n = 0; n < Math.max(known.length, dirs.length); n += BATCH) {
      const r = await post<Plan>(`${D}/plan`, { folderId, linkParts: link, files: known.slice(n, n + BATCH).map((i) => ({ path: i.path, size: i.file.size })), folders: dirs.slice(n, n + BATCH) });
      r.files.forEach((f) => byPath.set(f.path, f)); fl.push(...r.folders);
    }
    setItems(all.map((i) => ({ ...i, exists: byPath.get(i.path)?.exists, parts: byPath.get(i.path)?.parts })));
    setFolders(fl);
  };

  const todo = items.filter((i) => (i.kind === 'pdf' || (others && i.kind === 'other')) && !i.exists);
  const run = async () => {
    setErr(null); setBusy('importing');
    const ac = new AbortController(); stop.current = ac;
    const p: Progress = { done: 0, total: todo.length, bytes: 0, totalBytes: todo.reduce((s, i) => s + i.file.size, 0), skipped: 0, failed: [], linked: 0, doneBytes: 0, cancelled: false, folders: folders.filter((f) => !f.exists).length };
    const tick = () => setProg({ ...p, failed: [...p.failed] });
    tick();
    try {
      // folders first, parents before children, so files can go up side by side
      const dirs = [...new Set(todo.map((i) => dirOf(i.path)).filter(Boolean))];
      const ids: Record<string, string> = folderId ? { '': folderId } : {};
      for (let n = 0; n < dirs.length; n += BATCH) Object.assign(ids, await post<Record<string, string>>(`${D}/folders`, { folderId, linkParts, paths: dirs.slice(n, n + BATCH) }));
      const queue = [...todo];
      const one = async (i: Item) => {
        const dir = dirOf(i.path);
        let s: Start | null = null;
        try {
          s = await post<Start>(`${D}/start`, { folderId: ids[dir], fileName: i.file.name, size: i.file.size });
          const add = (n: number) => { p.bytes += n; tick(); };
          let parts: { partNumber: number; eTag: string }[] = [];
          if (s.uploadId) {
            for (let n = 0; n < s.partUrls.length; n++) {
              if (ac.signal.aborted) throw new DOMException('Stopped', 'AbortError');
              const tag = await putBlob(s.partUrls[n], i.file.slice(n * s.partSize, (n + 1) * s.partSize), null, add, ac.signal);
              parts.push({ partNumber: n + 1, eTag: tag });
            }
          } else await putBlob(s.url!, i.file, s.contentType, add, ac.signal);
          const r = await post<{ skipped: boolean; linked: number }>(`${D}/finish`, { token: s.token, uploadId: s.uploadId ?? null, parts, revision, linkParts, matchNames: dir.split('/').filter(Boolean) });
          parts = [];
          if (r.skipped) p.skipped++; else { p.done++; p.doneBytes += i.file.size; p.linked += r.linked; }
        } catch (e) {
          if (s) post(`${D}/abort`, { token: s.token, uploadId: s.uploadId ?? null }).catch(() => {});
          if ((e as Error).name === 'AbortError') return;
          p.failed.push(`${i.path}: ${(e as Error).message}`);
        }
        tick();
      };
      await Promise.all(Array.from({ length: PARALLEL }, async () => { while (queue.length && !ac.signal.aborted) await one(queue.shift()!); }));
    } catch (e) { setErr((e as Error).message); }
    await post(`${D}/summary`, { folderId, imported: p.done, folders: p.folders, bytes: p.doneBytes,
      skipped: p.skipped + items.filter((i) => i.exists).length, failed: p.failed.length, linked: p.linked, cancelled: ac.signal.aborted }).catch(() => {});
    p.cancelled = ac.signal.aborted; stop.current = null; setBusy(''); tick(); onDone();
  };

  const pdfs = items.filter((i) => i.kind === 'pdf'), other = items.filter((i) => i.kind === 'other'), bad = items.filter((i) => i.kind === 'unsupported');
  const already = items.filter((i) => i.exists).length;
  const matches = [...folders.filter((f) => f.parts.length).map((f) => ({ name: f.path, kind: 'Folder', parts: f.parts })),
    ...items.filter((i) => i.parts?.length && (i.kind === 'pdf' || others)).map((i) => ({ name: i.path, kind: 'Drawing', parts: i.parts! }))];
  const pct = prog && prog.totalBytes ? Math.min(100, (prog.bytes / prog.totalBytes) * 100) : 0;
  const finished = prog && busy === '';

  return <Dialog open={open} onClose={busy ? undefined : onClose} fullWidth maxWidth="md" TransitionProps={{ onEnter: reset }}>
    <DialogTitle>Import drawings into {folderName}</DialogTitle>
    <DialogContent><Stack gap={2} sx={{ pt: 1 }} data-testid="import-drawings">
      {err && <Alert severity="error">{err}</Alert>}
      {!prog && <>
        {top ? <Typography variant="body2" color="text.secondary">Choose a folder. Its subfolders are made as drawing folders here, with the same names and nesting, and each PDF goes into its matching folder,
          titled with its file name. Only PDF files are imported; other files are skipped. Choosing the same folder again skips files already imported, so a stopped import can carry on.</Typography>
        : <Typography variant="body2" color="text.secondary">Choose a folder to bring in everything in it, with its subfolders, or choose several files. Each file becomes a drawing titled with its file name.
          Files go straight to file storage. Choosing the same folder again skips files already here (same folder, name and size), so a stopped import can carry on.</Typography>}
        <Stack direction="row" gap={1}>
          <Button variant="outlined" component="label" disabled={!!busy}>Choose folder
            <input hidden type="file" ref={(el) => { dirInput.current = el; el?.setAttribute('webkitdirectory', ''); }} multiple data-testid="import-folder-input" onChange={(e) => { pick(e.target.files, true); e.target.value = ''; }} /></Button>
          {!top && <Button variant="outlined" component="label" disabled={!!busy}>Choose files
            <input hidden type="file" multiple data-testid="import-files-input" onChange={(e) => { pick(e.target.files, false); e.target.value = ''; }} /></Button>}
        </Stack>
        <Stack direction="row" gap={2} alignItems="center" flexWrap="wrap">
          <TextField size="small" label="Revision for all" required value={revision} onChange={(e) => setRevision(e.target.value.toUpperCase())} sx={{ width: 160 }} helperText="Given to every new drawing" />
          <FormControlLabel control={<Checkbox checked={linkParts} disabled={!!busy} onChange={(_, c) => { setLinkParts(c); if (items.length) { setBusy('reading'); plan(items, c).catch((e) => setErr(e.message)).finally(() => setBusy('')); } }} />}
            label="Link to parts by matching name" />
        </Stack>
        {busy === 'reading' && <LinearProgress />}
        {items.length > 0 && <>
          <Stack direction="row" gap={3} flexWrap="wrap" data-testid="import-preview">
            <Fact label="Folders" value={`${folders.length.toLocaleString('en-US')} (${folders.filter((f) => !f.exists).length.toLocaleString('en-US')} new)`} />
            <Fact label="Files" value={items.length.toLocaleString('en-US')} />
            <Fact label="Total size" value={gb(items.reduce((s, i) => s + i.file.size, 0))} />
            <Fact label="PDF files" value={pdfs.length.toLocaleString('en-US')} />
            {top && <Fact label="Skipped (not PDF)" value={bad.length.toLocaleString('en-US')} />}
            <Fact label="Already imported" value={already.toLocaleString('en-US')} />
            <Fact label="To import" value={`${todo.length.toLocaleString('en-US')} · ${gb(todo.reduce((s, i) => s + i.file.size, 0))}`} />
          </Stack>
          {other.length > 0 && <Alert severity="warning" data-testid="non-pdf">{other.length} file{other.length === 1 ? ' is' : 's are'} not PDF ({[...new Set(other.map((i) => ext(i.file.name)))].join(', ')}), for example {other[0].path}.
            <FormControlLabel sx={{ display: 'block' }} control={<Checkbox checked={others} onChange={(_, c) => setOthers(c)} />} label="Import these too (they download instead of opening in the browser)" /></Alert>}
          {bad.length > 0 && !top && <Alert severity="info">{bad.length} file{bad.length === 1 ? ' is' : 's are'} not a drawing type and will be skipped, for example {bad[0].path}.</Alert>}
          {linkParts && <Box>
            <Typography variant="subtitle2" sx={{ mb: 1 }}>Part matches ({matches.length.toLocaleString('en-US')})</Typography>
            {matches.length === 0 ? <Typography variant="body2" color="text.secondary">No folder or file name matches an OR # or SENSEN #.</Typography>
              : <SimpleTable head={['Folder or drawing', '', 'Links to part']} rows={matches.slice(0, 50).map((m) => [<Mono key="n">{m.name}</Mono>, m.kind, m.parts.join(', ')])} />}
            {matches.length > 50 && <Typography variant="caption" color="text.secondary">Showing the first 50.</Typography>}
          </Box>}
        </>}
      </>}
      {prog && <Box data-testid="import-progress">
        <Typography variant="body2" sx={{ mb: 1 }}>{finished ? (prog.cancelled ? 'Stopped. ' : 'Finished. ') : 'Importing… '}
          {prog.done.toLocaleString('en-US')} of {prog.total.toLocaleString('en-US')} files · {gb(prog.bytes)} of {gb(prog.totalBytes)}</Typography>
        <LinearProgress variant="determinate" value={pct} />
        <Typography variant="caption" color="text.secondary" component="div" sx={{ mt: 1 }}>{prog.folders} new folders · {prog.linked} part links · {prog.skipped} already here · {prog.failed.length} failed</Typography>
        {prog.failed.length > 0 && <Alert severity="error" sx={{ mt: 1 }}>{prog.failed.slice(0, 10).map((f) => <div key={f}>{f}</div>)}{prog.failed.length > 10 && <div>…and {prog.failed.length - 10} more. Choose the folder again to retry them.</div>}</Alert>}
      </Box>}
    </Stack></DialogContent>
    <DialogActions>
      {busy === 'importing' ? <Button color="error" onClick={() => stop.current?.abort()}>Cancel import</Button> : <Button onClick={onClose}>{finished ? 'Close' : 'Cancel'}</Button>}
      {!prog && <Button variant="contained" disabled={!!busy || todo.length === 0 || !revision.trim()} onClick={run}>Import {todo.length.toLocaleString('en-US')} drawing{todo.length === 1 ? '' : 's'}</Button>}
    </DialogActions>
  </Dialog>;
}

function Fact({ label, value }: { label: string; value: string }) {
  return <Box><Typography variant="caption" color="text.secondary">{label}</Typography><Typography variant="body1">{value}</Typography></Box>;
}
