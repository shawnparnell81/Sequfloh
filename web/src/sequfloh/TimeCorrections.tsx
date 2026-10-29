import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Alert, Autocomplete, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, Paper, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post } from '../api';
import { FormDialogTitle } from '../components/FullScreen';
import { fmtDateTime } from '../components/Status';
import { S, qs } from './common';
import { hrs } from './CrewPanel';

interface Entry { kind: 'crew' | 'dayWorker' | 'user'; id: string; who: string; startedAt: string; stoppedAt?: string | null; seconds: number; note?: string | null; voidedAt?: string | null; voidedBy?: string | null; voidReason?: string | null }
interface Data { canTransfer: boolean; canRemove: boolean; entries: Entry[] }
interface WoOpt { id: string; number: string; workOrder?: string | null }

/** The selection a "transfer to a new work order" carries to the New work order screen. */
export const MOVE_KEY = 'sqf-move-time';
export interface MoveSelection { from: string; fromNumber: string; crewSessionIds: string[]; timeEntryIds: string[]; reason: string | null }

const KIND: Record<Entry['kind'], string> = { crew: 'Crew', dayWorker: 'Day worker', user: 'Clocked person' };

/** Correct recorded time: move entries to another work order (existing or new) or remove them with a reason. Shown only to people with those permissions. */
export function TimeCorrections({ recordId, number }: { recordId: string; number: string }) {
  const qc = useQueryClient();
  const nav = useNavigate();
  const q = useQuery({ queryKey: ['sqf-time-fix', recordId], queryFn: () => get<Data>(`${S}/tracking/${recordId}/time-corrections`) });
  const [sel, setSel] = useState<string[]>([]);
  const [mode, setMode] = useState<'move' | 'remove' | null>(null);
  const [reason, setReason] = useState('');
  const [target, setTarget] = useState<WoOpt | null>(null);
  const [text, setText] = useState('');
  const [err, setErr] = useState<string | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const wos = useQuery({ queryKey: ['sqf-time-fix-wo', text], enabled: mode === 'move', queryFn: () => get<{ items: WoOpt[] }>(`${S}/tracking?${qs({ q: text || undefined, pageSize: 20, sort: 'createdAt:desc' })}`) });
  const d = q.data;
  if (!d || (!d.canTransfer && !d.canRemove)) return null;
  const live = d.entries.filter((e) => !e.voidedAt && e.stoppedAt);
  const removed = d.entries.filter((e) => e.voidedAt);
  const picked = live.filter((e) => sel.includes(e.id));
  const ids = { crewSessionIds: picked.filter((e) => e.kind !== 'user').map((e) => e.id), timeEntryIds: picked.filter((e) => e.kind === 'user').map((e) => e.id) };
  const total = hrs(picked.reduce((a, e) => a + e.seconds, 0));
  const done = async (text: string) => { setSel([]); setMode(null); setReason(''); setTarget(null); setMsg(text); await qc.invalidateQueries(); };
  const run = async () => {
    setBusy(true); setErr(null);
    try {
      if (mode === 'remove') {
        if (!reason.trim()) throw new Error('Type the reason for removing this time.');
        await post(`${S}/tracking/${recordId}/time-corrections/remove`, { ...ids, reason });
        await done(`Removed ${total} h. It no longer counts but stays in the history below.`);
      } else {
        if (!target) throw new Error('Choose the work order to move the time to.');
        const r = await post<{ to: string }>(`${S}/tracking/${recordId}/time-corrections/transfer`, { targetId: target.id, ...ids, reason: reason.trim() || null });
        await done(`Moved ${total} h to ${r.to}.`);
      }
    } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  const toNew = () => {
    const m: MoveSelection = { from: recordId, fromNumber: number, ...ids, reason: reason.trim() || null };
    sessionStorage.setItem(MOVE_KEY, JSON.stringify(m));
    nav(`/sequfloh/tracking/new?from=${recordId}`);
  };
  return <Paper variant="outlined" sx={{ p: 2 }} data-testid="time-corrections">
    <Typography variant="subtitle1" fontWeight={600}>Correct recorded time</Typography>
    <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>Select time that was put on the wrong work order, then move it or remove it. Every change is in the audit trail, and chargeback reports of the order are flagged to be regenerated.</Typography>
    {msg && <Alert severity="success" onClose={() => setMsg(null)} sx={{ mb: 1 }}>{msg}</Alert>}
    {live.length === 0 ? <Typography variant="body2" color="text.secondary">No finished time on this work order.</Typography> : <>
      <Table size="small" data-testid="time-fix-list">
        <TableHead><TableRow>
          <TableCell padding="checkbox"><Checkbox checked={sel.length > 0 && sel.length === live.length} indeterminate={sel.length > 0 && sel.length < live.length}
            onChange={(e) => setSel(e.target.checked ? live.map((x) => x.id) : [])} inputProps={{ 'aria-label': 'Select all', 'data-testid': 'time-fix-all' } as object} /></TableCell>
          <TableCell>Type</TableCell><TableCell>Who</TableCell><TableCell>Start</TableCell><TableCell>Stop</TableCell><TableCell align="right">Hours</TableCell><TableCell>Note</TableCell>
        </TableRow></TableHead>
        <TableBody>{live.map((e) => <TableRow key={e.id} data-testid="time-fix-row">
          <TableCell padding="checkbox"><Checkbox checked={sel.includes(e.id)} onChange={(x) => setSel(x.target.checked ? [...sel, e.id] : sel.filter((s) => s !== e.id))} /></TableCell>
          <TableCell>{KIND[e.kind]}</TableCell><TableCell>{e.who}</TableCell><TableCell>{fmtDateTime(e.startedAt)}</TableCell><TableCell>{fmtDateTime(e.stoppedAt!)}</TableCell>
          <TableCell align="right">{hrs(e.seconds)}</TableCell><TableCell>{e.note ?? ''}</TableCell>
        </TableRow>)}</TableBody>
      </Table>
      <Stack direction="row" gap={1} sx={{ mt: 1 }} flexWrap="wrap">
        {d.canTransfer && <Button variant="outlined" disabled={!picked.length} onClick={() => { setMode('move'); setErr(null); }} data-testid="time-fix-move">Transfer to another work order</Button>}
        {d.canTransfer && <Button variant="outlined" disabled={!picked.length} onClick={toNew} data-testid="time-fix-new">Transfer to a new work order</Button>}
        {d.canRemove && <Button variant="outlined" color="error" disabled={!picked.length} onClick={() => { setMode('remove'); setErr(null); }} data-testid="time-fix-remove">Remove time</Button>}
        {picked.length > 0 && <Typography variant="body2" sx={{ alignSelf: 'center' }}>{picked.length} selected, {total} h</Typography>}
      </Stack>
    </>}
    {removed.length > 0 && <Box sx={{ mt: 2 }}>
      <Typography variant="subtitle2">Removed time (kept for history, not counted)</Typography>
      <Table size="small" data-testid="time-fix-removed"><TableBody>{removed.map((e) => <TableRow key={e.id}>
        <TableCell>{KIND[e.kind]}</TableCell><TableCell>{e.who}</TableCell><TableCell>{fmtDateTime(e.startedAt)} – {e.stoppedAt ? fmtDateTime(e.stoppedAt) : ''}</TableCell>
        <TableCell align="right">{hrs(e.seconds)} h</TableCell><TableCell>Removed {fmtDateTime(e.voidedAt!)} by {e.voidedBy}: {e.voidReason}</TableCell>
      </TableRow>)}</TableBody></Table>
    </Box>}
    <Dialog open={!!mode} onClose={() => setMode(null)} maxWidth="sm" fullWidth>
      <FormDialogTitle>{mode === 'remove' ? `Remove ${total} h from ${number}` : `Move ${total} h from ${number}`}</FormDialogTitle>
      <DialogContent>
        <Stack gap={2} sx={{ pt: 1 }}>
          {err && <Alert severity="error">{err}</Alert>}
          {mode === 'move' && <Autocomplete options={(wos.data?.items ?? []).filter((w) => w.id !== recordId)} value={target} onChange={(_, o) => setTarget(o)} filterOptions={(x) => x}
            onInputChange={(_, v) => setText(v)} getOptionLabel={(o) => `${o.number}${o.workOrder ? ` (${o.workOrder})` : ''}`} isOptionEqualToValue={(a, b) => a.id === b.id}
            renderInput={(p) => <TextField {...p} label="Move to work order" inputProps={{ ...p.inputProps, 'data-testid': 'time-fix-target' }} />} />}
          <TextField label={mode === 'remove' ? 'Reason (required)' : 'Reason (optional)'} value={reason} onChange={(e) => setReason(e.target.value)} multiline minRows={2} required={mode === 'remove'} inputProps={{ 'data-testid': 'time-fix-reason', maxLength: 500 }} />
          {mode === 'remove' && <Typography variant="body2" color="text.secondary">Removed time stops counting on totals, the job report and chargebacks. It is not deleted: it stays in the history on this tab and in the audit trail.</Typography>}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={() => setMode(null)}>Cancel</Button>
        <Button variant="contained" color={mode === 'remove' ? 'error' : 'primary'} disabled={busy} onClick={() => void run()} data-testid="time-fix-confirm">{mode === 'remove' ? 'Remove time' : 'Move time'}</Button>
      </DialogActions>
    </Dialog>
  </Paper>;
}
