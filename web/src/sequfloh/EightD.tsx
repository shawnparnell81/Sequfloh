import { useRef, useState } from 'react';
import { Alert, Button, FormControlLabel, Link, Paper, Radio, RadioGroup, Stack, Table, TableBody, TableCell, TableHead, TableRow, Typography } from '@mui/material';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post, put } from '../api';
import { S } from './common';
import { fmtDateTime } from '../components/Status';

export type EightDKind = 'work-orders' | 'inspection-requests' | 'projects';
export const EIGHT_D_ACCEPT = '.pdf,.doc,.docx,.xls,.xlsx,.xlsm,.csv';
/** Validation / FAI work orders have no 8D report. */
export const noEightD = (type?: string | null) => type === 'Validation' || type === 'Fai';
export const eightDUrl = (id: string, download = false) => `${S}/8d/files/${id}${download ? '?download=true' : ''}`;
export interface EightDFile { id: string; fileName: string; uploadedBy?: string | null; uploadedAt: string; ownerNumber?: string | null }

export const uploadEightD = (kind: EightDKind, id: string, file: File) => { const fd = new FormData(); fd.append('file', file); return post(`${S}/8d/${kind}/${id}`, fd); };

/** "Include 8D Report?" No / Yes. */
export function EightDAsk({ yes, onYes, disabled, testId = 'eightd-ask' }: { yes: boolean; onYes: (yes: boolean) => void; disabled?: boolean; testId?: string }) {
  return <Stack direction="row" alignItems="center" gap={2}>
    <Typography variant="body2">Include 8D Report?</Typography>
    <RadioGroup row value={yes ? 'yes' : 'no'} onChange={(e) => onYes(e.target.value === 'yes')} data-testid={testId}>
      <FormControlLabel value="no" control={<Radio size="small" />} label="No" disabled={disabled} />
      <FormControlLabel value="yes" control={<Radio size="small" />} label="Yes" disabled={disabled} />
    </RadioGroup>
  </Stack>;
}

/** The create-form question with an optional file picker; the files are uploaded after the record is saved. */
export function EightDCreate({ yes, onYes, files, onFiles }: { yes: boolean; onYes: (yes: boolean) => void; files: File[]; onFiles: (f: File[]) => void }) {
  return <Stack gap={1}>
    <EightDAsk yes={yes} onYes={(y) => { onYes(y); if (!y) onFiles([]); }} />
    {yes && <Stack direction="row" alignItems="center" gap={1} flexWrap="wrap">
      <Button size="small" variant="outlined" component="label">Choose 8D file (optional)
        <input hidden type="file" multiple accept={EIGHT_D_ACCEPT} data-testid="eightd-create-file" onChange={(e) => onFiles([...files, ...Array.from(e.target.files ?? [])])} /></Button>
      {files.length === 0 ? <Typography variant="caption" color="text.secondary">PDF, Word or Excel. You can also add it later.</Typography>
        : files.map((f) => <Typography key={f.name} variant="body2">{f.name}</Typography>)}
    </Stack>}
  </Stack>;
}

/** The 8D Report block on a record page. Shown when the record has "Include 8D Report?" Yes or already has 8D files. */
export function EightDBlock({ kind, id }: { kind: EightDKind; id: string }) {
  const qc = useQueryClient();
  const key = ['sqf-8d', kind, id];
  const q = useQuery({ queryKey: key, queryFn: () => get<{ include: boolean; editable: boolean; lockReason?: string | null; files: EightDFile[] }>(`${S}/8d/${kind}/${id}`) });
  const [err, setErr] = useState<string | null>(null);
  const input = useRef<HTMLInputElement>(null);
  if (!q.data) return null;
  const d = q.data;
  const run = async (fn: () => Promise<unknown>) => { setErr(null); try { await fn(); } catch (e) { setErr((e as Error).message); } finally { await qc.invalidateQueries({ queryKey: key }); } };
  const toggle = (y: boolean) => run(() => put(`${S}/8d/${kind}/${id}/include`, { include: y }));
  if (!d.include) return d.editable ? <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="8D Report"><EightDAsk yes={false} onYes={toggle} />{err && <Alert severity="error">{err}</Alert>}</Paper> : null;
  return <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="8D Report" data-testid="eightd-block">
    <Stack direction="row" alignItems="center" justifyContent="space-between" flexWrap="wrap" gap={1} sx={{ mb: 1 }}>
      <Typography variant="h3">8D Report</Typography>
      {d.editable && <EightDAsk yes onYes={toggle} />}
    </Stack>
    {err && <Alert severity="error" sx={{ mb: 1 }}>{err}</Alert>}
    {d.files.length === 0 ? <Typography variant="body2" color="text.secondary">No 8D report yet.</Typography> :
      <Table size="small"><TableHead><TableRow><TableCell>File</TableCell><TableCell>Uploaded by</TableCell><TableCell>When</TableCell><TableCell /></TableRow></TableHead>
        <TableBody>{d.files.map((f) => <TableRow key={f.id}>
          <TableCell>{f.fileName}</TableCell><TableCell>{f.uploadedBy ?? '—'}</TableCell><TableCell>{fmtDateTime(f.uploadedAt)}</TableCell>
          <TableCell align="right"><Stack direction="row" gap={1} justifyContent="flex-end">
            <Button size="small" href={eightDUrl(f.id)} target="_blank" rel="noopener">Open</Button>
            <Button size="small" href={eightDUrl(f.id, true)}>Download</Button>
            {d.editable && <Button size="small" color="error" onClick={() => { if (window.confirm(`Remove ${f.fileName}?`)) void run(() => del(`${S}/8d/files/${f.id}`)); }}>Remove</Button>}
          </Stack></TableCell>
        </TableRow>)}</TableBody></Table>}
    {d.editable ? <Button sx={{ mt: 1 }} size="small" variant="outlined" onClick={() => input.current?.click()} data-testid="eightd-upload">Upload 8D report</Button>
      : d.lockReason && <Typography variant="caption" color="text.secondary" component="p" sx={{ mt: 1 }}>{d.lockReason}</Typography>}
    <input ref={input} hidden type="file" multiple accept={EIGHT_D_ACCEPT} onChange={(e) => { const list = Array.from(e.target.files ?? []); e.target.value = ''; void run(async () => { for (const x of list) await uploadEightD(kind, id, x); }); }} />
  </Paper>;
}

/** The 8D files in a report: one link per file. */
export function EightDLinks({ files }: { files?: { id: string; fileName: string }[] | null }) {
  if (!files?.length) return null;
  return <Stack gap={0.5} data-testid="eightd-links">{files.map((f) => <Link key={f.id} href={eightDUrl(f.id)} target="_blank" rel="noopener">{f.fileName}</Link>)}</Stack>;
}
