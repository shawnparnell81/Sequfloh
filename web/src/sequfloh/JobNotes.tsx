import { useState } from 'react';
import { Alert, Box, Button, Collapse, Dialog, DialogActions, DialogContent, IconButton, Paper, Stack, TextField, Typography } from '@mui/material';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import EditOutlined from '@mui/icons-material/EditOutlined';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post, put } from '../api';
import { ImportButtons } from '../components/ImportButtons';
import { FormDialogTitle } from '../components/FullScreen';
import { S } from './common';

export interface JobNote { id: string; text: string; authorName: string; createdAt: string; editedAt?: string | null; editedBy?: string | null; mine: boolean }

/** Date and time in New York, the plant's time, with the zone shown. */
export const nyTime = (iso: string) => new Date(iso).toLocaleString('en-US', { timeZone: 'America/New_York', month: 'numeric', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit', timeZoneName: 'short' });

export const addJobNote = (id: string, text: string) => post(`${S}/tracking/${id}/job/notes`, { text });

const SHOWN = 3;

/** The running job notes log, shown at the top of the work order so whoever clocks on next sees it straight away. Notes are added, never deleted. */
export function JobNotesPanel({ id }: { id: string }) {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['sqf-job-notes', id], queryFn: () => get<JobNote[]>(`${S}/tracking/${id}/job/notes`) });
  const [text, setText] = useState(''); const [busy, setBusy] = useState(false); const [err, setErr] = useState<string | null>(null);
  const [all, setAll] = useState(false); const [edit, setEdit] = useState<JobNote | null>(null); const [editText, setEditText] = useState('');
  const notes = q.data ?? [];
  const refresh = () => qc.invalidateQueries({ queryKey: ['sqf-job-notes', id] });
  const run = async (fn: () => Promise<unknown>) => { setBusy(true); setErr(null); try { await fn(); await refresh(); return true; } catch (e) { setErr((e as Error).message); return false; } finally { setBusy(false); } };
  const shown = all ? notes : notes.slice(0, SHOWN);
  return <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Job notes" data-testid="job-notes">
    <Typography variant="h3" sx={{ mb: 0.5 }}>Job notes</Typography>
    <Typography variant="caption" color="text.secondary" component="div" sx={{ mb: 1.5 }}>What was done, what is left, anything the next technician should know. Newest first. Notes stay on the work order and print on the job report.</Typography>
    {err && <Alert severity="error" sx={{ mb: 1.5 }} onClose={() => setErr(null)}>{err}</Alert>}
    <Stack direction={{ xs: 'column', sm: 'row' }} gap={1} alignItems={{ sm: 'flex-start' }} sx={{ mb: 1.5 }}>
      <TextField fullWidth multiline minRows={2} size="small" label="Add a job note" value={text} onChange={(e) => setText(e.target.value)} inputProps={{ maxLength: 4000, 'data-testid': 'job-note-text' }} />
      <Button variant="contained" disabled={busy || !text.trim()} data-testid="job-note-add" onClick={async () => { if (await run(() => addJobNote(id, text))) setText(''); }}>Add note</Button>
    </Stack>
    {q.isLoading ? null : notes.length === 0 ? <Typography variant="body2" color="text.secondary">No job notes yet.</Typography>
      : <Stack gap={1} data-testid="job-note-list">{shown.map((n) => <Box key={n.id} data-testid="job-note" sx={{ borderLeft: 3, borderColor: 'primary.main', pl: 1.5, py: 0.25 }}>
        <Stack direction="row" alignItems="center" gap={1}>
          <Typography variant="caption" color="text.secondary"><b>{n.authorName}</b> · {nyTime(n.createdAt)}{n.editedAt ? ` · edited ${nyTime(n.editedAt)}` : ''}</Typography>
          {n.mine && <IconButton size="small" aria-label="Edit note" onClick={() => { setEdit(n); setEditText(n.text); }}><EditOutlined fontSize="inherit" /></IconButton>}
        </Stack>
        <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>{n.text}</Typography></Box>)}
        {notes.length > SHOWN && <Box><Button size="small" onClick={() => setAll(!all)} data-testid="job-notes-all">{all ? 'Show fewer' : `Show all ${notes.length} notes`}</Button></Box>}
      </Stack>}
    <Dialog open={!!edit} onClose={() => setEdit(null)} fullWidth maxWidth="sm">
      <FormDialogTitle>Edit job note</FormDialogTitle>
      <DialogContent><Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>The earlier wording is kept in the work order history.</Typography>
        <TextField fullWidth multiline minRows={3} size="small" value={editText} onChange={(e) => setEditText(e.target.value)} /></DialogContent>
      <DialogActions><Button onClick={() => setEdit(null)}>Cancel</Button>
        <Button variant="contained" disabled={busy || !editText.trim()} onClick={async () => { if (edit && await run(() => put(`${S}/tracking/job/notes/${edit.id}`, { text: editText }))) setEdit(null); }}>Save</Button></DialogActions>
    </Dialog>
  </Paper>;
}

interface FileRow { id: string; kind: string; fileName: string; uploadedAt: string; uploadedBy?: string }
interface Removed { id: string; fileName: string; uploadedByName: string; uploadedAt: string; removedAt: string; removedBy: string; removeReason?: string | null }
interface JobData { files: FileRow[]; removedFiles: Removed[]; canRemoveFiles: boolean; canDeleteFilesPermanently?: boolean }

export const FILE_ACCEPT = '.jpg,.jpeg,.png,.gif,.webp,.heic,.heif,.pdf,.docx,.doc,.xlsx,.xls,.csv,.pptx,.txt';
const isPicture = (name: string) => /\.(jpe?g|png|gif|webp)$/i.test(name);
const fileUrl = (id: string) => `${S}/tracking/job/test-files/${id}`;
export const uploadRecordFile = (id: string, file: File) => { const fd = new FormData(); fd.append('files', file); fd.append('kind', 'attachment'); return post(`${S}/tracking/${id}/job/test-files`, fd); };

/** Pictures and files on the work order (added when it was created or later), shown next to the job notes. */
export function RecordFilesPanel({ id, canWork }: { id: string; canWork: boolean }) {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['sqf-job', id], queryFn: () => get<JobData>(`${S}/tracking/${id}/job`) });
  const [view, setView] = useState<FileRow | null>(null); const [remove, setRemove] = useState<FileRow | null>(null); const [reason, setReason] = useState('');
  const [err, setErr] = useState<string | null>(null); const [history, setHistory] = useState(false);
  const refresh = () => qc.invalidateQueries({ queryKey: ['sqf-job', id] });
  const files = (q.data?.files ?? []).filter((x) => x.kind === 'attachment' || x.kind === 'evidence');
  const pics = files.filter((x) => isPicture(x.fileName)); const docs = files.filter((x) => !isPicture(x.fileName));
  const removed = q.data?.removedFiles ?? [];
  const rm = (x: FileRow) => q.data?.canRemoveFiles && <IconButton size="small" aria-label={`Remove ${x.fileName}`} data-testid="record-file-remove" onClick={() => { setRemove(x); setReason(''); }}><DeleteOutline fontSize="small" /></IconButton>;
  return <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Pictures and files" data-testid="record-files">
    <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ mb: 1 }} gap={1} flexWrap="wrap">
      <Typography variant="h3">Pictures and files</Typography>
      {canWork && <ImportButtons surface="work-order-attachments" size="small" accept={FILE_ACCEPT.split(',')} what="file" upload={(f) => uploadRecordFile(id, f)} onDone={() => void refresh()} />}
    </Stack>
    {err && <Alert severity="error" sx={{ mb: 1 }} onClose={() => setErr(null)}>{err}</Alert>}
    {files.length === 0 && <Typography variant="body2" color="text.secondary">No pictures or files yet.</Typography>}
    {pics.length > 0 && <Stack direction="row" gap={1.5} flexWrap="wrap" sx={{ mb: 1.5 }}>{pics.map((x) => <Box key={x.id} data-testid="record-picture" sx={{ width: 140 }}>
      <Box component="img" src={fileUrl(x.id)} alt={x.fileName} onClick={() => setView(x)} sx={{ width: 140, height: 105, objectFit: 'cover', border: 1, borderColor: 'divider', borderRadius: 1, cursor: 'zoom-in', display: 'block' }} />
      <Stack direction="row" alignItems="center"><Typography variant="caption" noWrap sx={{ flex: 1 }} title={x.fileName}>{x.fileName}</Typography>{rm(x)}</Stack></Box>)}</Stack>}
    {docs.length > 0 && <Stack gap={0.5}>{docs.map((x) => <Stack key={x.id} direction="row" alignItems="center" gap={1} data-testid="record-file">
      <a href={fileUrl(x.id)} target="_blank" rel="noreferrer">{x.fileName}</a><Typography variant="caption" color="text.secondary">{nyTime(x.uploadedAt)}</Typography>{rm(x)}</Stack>)}</Stack>}
    {removed.length > 0 && <Box sx={{ mt: 1 }}><Button size="small" onClick={() => setHistory(!history)}>{history ? 'Hide' : 'Show'} removed files ({removed.length})</Button>
      <Collapse in={history}><Stack gap={0.5} sx={{ mt: 0.5 }} data-testid="record-files-removed">{removed.map((x) => <Typography key={x.id} variant="caption" color="text.secondary">
        <a href={fileUrl(x.id)} target="_blank" rel="noreferrer">{x.fileName}</a> removed by {x.removedBy} {nyTime(x.removedAt)}{x.removeReason ? ` (${x.removeReason})` : ''}
        {q.data?.canDeleteFilesPermanently && <Button size="small" color="error" sx={{ ml: 1, py: 0, minWidth: 0 }} data-testid="record-file-delete-permanently"
          onClick={async () => { if (!window.confirm(`Delete ${x.fileName} permanently? The stored file is removed for good; the audit trail keeps a note of it.`)) return;
            try { await del(`${fileUrl(x.id)}/permanently`); await refresh(); } catch (e) { setErr((e as Error).message); } }}>Delete permanently</Button>}</Typography>)}</Stack></Collapse></Box>}
    <Dialog open={!!view} onClose={() => setView(null)} maxWidth="lg">
      {view && <><FormDialogTitle>{view.fileName}</FormDialogTitle><DialogContent><Box component="img" src={fileUrl(view.id)} alt={view.fileName} sx={{ maxWidth: '100%', maxHeight: '75vh', display: 'block' }} /></DialogContent>
        <DialogActions><Button href={fileUrl(view.id)} target="_blank">Open full size</Button><Button onClick={() => setView(null)}>Close</Button></DialogActions></>}
    </Dialog>
    <Dialog open={!!remove} onClose={() => setRemove(null)} fullWidth maxWidth="xs">
      <FormDialogTitle>Remove {remove?.fileName}?</FormDialogTitle>
      <DialogContent><Typography variant="body2" sx={{ mb: 1.5 }}>It is taken off the work order and the job report, but kept in the history.</Typography>
        <TextField fullWidth size="small" label="Reason" value={reason} onChange={(e) => setReason(e.target.value)} inputProps={{ 'data-testid': 'record-file-reason' }} /></DialogContent>
      <DialogActions><Button onClick={() => setRemove(null)}>Cancel</Button>
        <Button color="error" variant="contained" data-testid="record-file-remove-confirm" onClick={async () => {
          try { await del(`${S}/tracking/job/test-files/${remove!.id}?reason=${encodeURIComponent(reason)}`); setRemove(null); await refresh(); } catch (e) { setErr((e as Error).message); setRemove(null); }
        }}>Remove</Button></DialogActions>
    </Dialog>
  </Paper>;
}
