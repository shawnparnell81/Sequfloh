import { useRef, useState, type CSSProperties, type InputHTMLAttributes, type ReactNode } from 'react';
import { FormDialogTitle } from './FullScreen';
import { Alert, Box, Button, Chip, Dialog, DialogActions, DialogContent, LinearProgress, Stack, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Typography } from '@mui/material';
import UploadFileOutlined from '@mui/icons-material/UploadFileOutlined';
import DriveFolderUploadOutlined from '@mui/icons-material/DriveFolderUploadOutlined';
import type { UploadSurface } from './uploadSurfaces';

type State = 'skipped' | 'waiting' | 'importing' | 'imported' | 'failed';
interface Item { file: File; path: string; state: State; note?: string }

const SYSTEM = ['.ds_store', 'thumbs.db', 'desktop.ini'];
const extOf = (name: string) => { const i = name.lastIndexOf('.'); return i < 0 ? '' : name.slice(i).toLowerCase(); };
const mb = (b: number) => `${Math.round(b / 1024 / 1024)} MB`;

/** Sorts picked files into ones to import and ones skipped, with the reason. Hidden and system files are skipped. */
export function classify(files: File[], accept: string[], maxBytes?: number): Item[] {
  return files.map((file) => {
    const path = (file as File & { webkitRelativePath?: string }).webkitRelativePath || file.name;
    const name = file.name.toLowerCase();
    const why = SYSTEM.includes(name) || name.startsWith('~$') || path.split('/').some((p) => p.startsWith('.')) ? 'system or hidden file'
      : !accept.includes(extOf(name)) ? `file type not accepted here (${extOf(name) || 'no extension'})`
        : file.size === 0 ? 'empty file' : maxBytes && file.size > maxBytes ? `larger than ${mb(maxBytes)}` : null;
    return { file, path, state: why ? 'skipped' : 'waiting', note: why ?? undefined } as Item;
  }).sort((a, b) => a.path.localeCompare(b.path));
}

const offscreen: CSSProperties = { position: 'absolute', width: 1, height: 1, padding: 0, margin: -1, overflow: 'hidden', clipPath: 'inset(50%)', whiteSpace: 'nowrap', border: 0 };
/** Kept on the element React renders. Setting it afterwards lets a later render turn the folder picker back into a file picker, and the next click does nothing. */
const folderAttrs = { webkitdirectory: '', directory: '' } as InputHTMLAttributes<HTMLInputElement>;
const label: Record<State, string> = { skipped: 'Skipped', waiting: 'Waiting', importing: 'Importing…', imported: 'Imported', failed: 'Failed' };
const color: Record<State, 'default' | 'success' | 'error' | 'warning' | 'info'> = { skipped: 'warning', waiting: 'default', importing: 'info', imported: 'success', failed: 'error' };

/**
 * The Import file and Import folder buttons used on every page that takes uploads. Import file takes one or many files; Import folder takes a whole
 * folder with its subfolders. Files of other types are skipped with the reason, each accepted file is sent on its own (so each is recorded in the
 * Audit Trail on its own) with its progress, and a summary shows how many were imported, skipped and failed.
 * Pages with their own import dialog (Drawings) take the picked files with onFiles instead.
 */
export function ImportButtons({ surface, accept, maxBytes, upload, onFiles, onDone, onStart, what = 'file', settings, ready = true, size = 'medium', variant = 'outlined' }: {
  surface: UploadSurface; accept: string[]; maxBytes?: number; upload?: (file: File, path: string) => Promise<unknown>; onFiles?: (files: File[], folder: boolean) => void;
  onDone?: () => void; onStart?: (count: number) => void; what?: string; settings?: ReactNode | ((files: File[]) => ReactNode); ready?: boolean; size?: 'small' | 'medium'; variant?: 'outlined' | 'contained' | 'text';
}) {
  const fileInput = useRef<HTMLInputElement>(null);
  const dirInput = useRef<HTMLInputElement>(null);
  const [fileEpoch, setFileEpoch] = useState(0);
  const [dirEpoch, setDirEpoch] = useState(0);
  const [items, setItems] = useState<Item[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [finished, setFinished] = useState(false);
  const [pickError, setPickError] = useState<string | null>(null);
  const openPicker = (el: HTMLInputElement | null) => { if (!el) return; el.value = ''; el.click(); };
  const take = (list: FileList | null, folder: boolean, input: HTMLInputElement) => {
    try {
      const files = list ? Array.from(list) : [];
      if (!files.length) { setPickError('No files were read from that selection. Nothing was imported.'); return; }
      // read each relative path now; clearing the input can make the browser forget it, or throw
      files.forEach((f) => { void (folder ? f.webkitRelativePath : f.name); });
      setPickError(null);
      if (onFiles) { onFiles(files, folder); return; }
      setItems(classify(files, accept, maxBytes)); setFinished(false);
    } catch (e) {
      setPickError((e as Error).message || 'Those files could not be read. Nothing was imported.');
    } finally {
      input.value = '';
      if (folder) setDirEpoch((n) => n + 1); else setFileEpoch((n) => n + 1);
    }
  };
  const start = async () => {
    if (!items || !upload) return;
    setBusy(true);
    try {
      onStart?.(items.filter((x) => x.state === 'waiting').length);
      const next = [...items];
      for (let i = 0; i < next.length; i++) {
        if (next[i].state !== 'waiting') continue;
        next[i] = { ...next[i], state: 'importing' }; setItems([...next]);
        try { await upload(next[i].file, next[i].path); next[i] = { ...next[i], state: 'imported' }; }
        catch (e) { next[i] = { ...next[i], state: 'failed', note: (e as Error).message || 'Could not import this file.' }; }
        setItems([...next]);
      }
      setFinished(true); onDone?.();
    } catch (e) {
      setPickError((e as Error).message || 'The import failed.');
    } finally { setBusy(false); }
  };
  const count = (s: State) => items?.filter((x) => x.state === s).length ?? 0;
  const toDo = count('waiting');
  const done = count('imported') + count('failed');
  const total = done + toDo + count('importing');
  const plural = (n: number) => `${n} ${what}${n === 1 ? '' : 's'}`;
  return <>
    <Stack direction="row" gap={1} data-upload-surface={surface} component="span" sx={{ display: 'inline-flex' }}>
      <Button type="button" variant={variant} size={size} startIcon={<UploadFileOutlined />} onClick={() => openPicker(fileInput.current)}>Import file</Button>
      <Button type="button" variant={variant} size={size} startIcon={<DriveFolderUploadOutlined />} onClick={() => openPicker(dirInput.current)}>Import folder</Button>
      <input key={`f${fileEpoch}`} ref={fileInput} type="file" multiple accept={accept.join(',')} data-testid={`${surface}-files-input`} style={offscreen} onChange={(e) => take(e.target.files, false, e.currentTarget)} />
      <input key={`d${dirEpoch}`} ref={dirInput} {...folderAttrs} type="file" multiple data-testid={`${surface}-folder-input`} style={offscreen}
        onChange={(e) => take(e.target.files, true, e.currentTarget)} />
    </Stack>
    {pickError && <Alert severity="error" sx={{ mt: 1 }} data-testid="import-pick-error" onClose={() => setPickError(null)}>{pickError}</Alert>}
    {items && <Dialog open onClose={busy ? undefined : () => setItems(null)} maxWidth="md" fullWidth data-testid="bulk-import">
      <FormDialogTitle>Import {what}s</FormDialogTitle>
      <DialogContent>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>Accepted here: {accept.join(', ')}{maxBytes ? `, up to ${mb(maxBytes)} each` : ''}. Every file is added on its own; nothing already here is replaced.</Typography>
        {!finished && !busy && settings && <Box sx={{ mb: 2 }}>{typeof settings === 'function' ? settings(items.filter((x) => x.state === 'waiting').map((x) => x.file)) : settings}</Box>}
        <Stack direction="row" gap={1} sx={{ mb: 1 }} data-testid="bulk-summary" flexWrap="wrap">
          <Chip size="small" color="success" label={`Imported ${count('imported')}`} /><Chip size="small" color="warning" label={`Skipped ${count('skipped')}`} />
          <Chip size="small" color="error" label={`Failed ${count('failed')}`} />{toDo > 0 && <Chip size="small" label={`To import ${toDo}`} />}
        </Stack>
        {(busy || finished) && total > 0 && <LinearProgress variant="determinate" value={(done / total) * 100} sx={{ mb: 1 }} />}
        {finished && <Alert severity={count('failed') ? 'warning' : 'success'} sx={{ mb: 1 }} data-testid="bulk-done">
          Finished: {plural(count('imported'))} imported, {count('skipped')} skipped, {count('failed')} failed.</Alert>}
        <TableContainer sx={{ maxHeight: 360 }}><Table size="small" stickyHeader>
          <TableHead><TableRow><TableCell>File</TableCell><TableCell>Result</TableCell><TableCell>Reason</TableCell></TableRow></TableHead>
          <TableBody>{items.map((x) => <TableRow key={x.path} data-testid="bulk-row">
            <TableCell sx={{ wordBreak: 'break-all' }}>{x.path}</TableCell><TableCell><Chip size="small" color={color[x.state]} label={label[x.state]} /></TableCell>
            <TableCell sx={{ fontSize: 12 }}>{x.note ?? ''}</TableCell></TableRow>)}</TableBody>
        </Table></TableContainer>
      </DialogContent>
      <DialogActions>
        <Button onClick={() => setItems(null)} disabled={busy}>{finished ? 'Close' : 'Cancel'}</Button>
        {!finished && <Button variant="contained" onClick={start} data-testid="bulk-import-start" disabled={busy || toDo === 0 || !ready}>Import {plural(toDo)}</Button>}
      </DialogActions>
    </Dialog>}
  </>;
}
