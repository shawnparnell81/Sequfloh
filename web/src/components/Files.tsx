import { useState } from 'react';
import { Alert, Checkbox, FormControlLabel, IconButton, Link, Stack, Table, TableBody, TableCell, TableHead, TableRow, Tooltip, Typography } from '@mui/material';
import LockOutlined from '@mui/icons-material/LockOutlined';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api, get, post } from '../api';
import { fmtDateTime } from './Status';
import { mono } from '../theme';
import { ImportButtons } from './ImportButtons';
import type { UploadSurface } from './uploadSurfaces';

/** The file types record attachments take (the same list the server checks). */
export const ATTACHMENT_TYPES = ['.png', '.jpg', '.jpeg', '.gif', '.webp', '.pdf', '.txt', '.csv', '.xlsx', '.docx'];

const base = (type: string, id: string) => `/api/records/${type}/${id}`;

interface FileRow { id: string; fileName: string; sizeBytes: number; sha256: string; version: number; isLocked: boolean; isArchived: boolean; createdAt: string; contentType: string }

export function FilesPanel({ type, id, canEdit, onChanged, surface }: { type: string; id: string; canEdit: boolean; onChanged?: () => void; surface: UploadSurface }) {
  const qc = useQueryClient();
  const [history, setHistory] = useState(false);
  const error: string | null = null;
  const files = useQuery({ queryKey: ['files', type, id, history], queryFn: () => get<FileRow[]>(`${base(type, id)}/files?history=${history}`) });
  const upload = async (f: File) => { const form = new FormData(); form.append('file', f); await api('POST', `${base(type, id)}/files`, form); };
  const rows = files.data ?? [];
  return (
    <Stack gap={1.5}>
      <Stack direction="row" gap={1} alignItems="center">
        {canEdit && <ImportButtons surface={surface} accept={ATTACHMENT_TYPES} maxBytes={10 * 1024 * 1024} upload={upload} onDone={() => { qc.invalidateQueries({ queryKey: ['files', type, id] }); onChanged?.(); }} />}
        <FormControlLabel control={<Checkbox size="small" checked={history} onChange={(e) => setHistory(e.target.checked)} />} label="Show removed files" />
      </Stack>
      {error && <Alert severity="error">{error}</Alert>}
      {rows.length === 0 ? <Typography variant="body2" color="text.secondary">No files attached.</Typography> :
        <Table size="small">
          <TableHead><TableRow><TableCell>File</TableCell><TableCell>Size</TableCell><TableCell>SHA-256</TableCell><TableCell>Uploaded</TableCell><TableCell /></TableRow></TableHead>
          <TableBody>{rows.map((r) => (
            <TableRow key={r.id}>
              <TableCell><Stack direction="row" gap={1} alignItems="center">
                {r.isLocked && <Tooltip title="Locked: part of a released revision"><LockOutlined fontSize="inherit" color="action" /></Tooltip>}
                <Link href={`/api/files/${r.id}?inline=true`} target="_blank" rel="noopener">{r.fileName}</Link></Stack></TableCell>
              <TableCell>{(r.sizeBytes / 1024).toFixed(1)} KB</TableCell>
              <TableCell><Tooltip title={r.sha256}><span style={{ fontFamily: mono, fontSize: 12 }}>{r.sha256.slice(0, 12)}…</span></Tooltip></TableCell>
              <TableCell>{fmtDateTime(r.createdAt)}</TableCell>
              <TableCell align="right">{canEdit && !r.isLocked && !r.isArchived &&
                <IconButton size="small" aria-label="Remove file" onClick={async () => { await post(`/api/files/${r.id}/archive`); qc.invalidateQueries({ queryKey: ['files', type, id] }); onChanged?.(); }}><DeleteOutline fontSize="small" /></IconButton>}</TableCell>
            </TableRow>))}
          </TableBody>
        </Table>}
    </Stack>
  );
}
