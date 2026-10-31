import { useEffect, useState } from 'react';
import { Alert, Autocomplete, Box, Button, Dialog, DialogActions, DialogContent, IconButton, Stack, TextField, Typography } from '@mui/material';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post } from '../api';
import { useAuth } from '../auth';
import { useUsers } from '../components/Kit';
import { FormDialogTitle } from '../components/FullScreen';
import { fmtDateTime } from '../components/Status';
import { S } from './common';

interface FixData { canAddPast?: boolean; useCrewTime?: boolean }
interface Overlap { trackingNumber: string; startedAt: string; stoppedAt?: string | null }
type Person = { id: string; displayName: string };

const pad = (n: number) => String(n).padStart(2, '0');
const toInput = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
const toIso = (v: string) => new Date(v).toISOString();
export const REASON_HELP = "Explain what work was done and why it wasn't clocked";
const CREW_HINT = 'This work order uses crew time, so nobody can clock on by name or as a day worker. Set Use crew time to No to log named time instead.';

/** Same rule as the server: at least 20 characters, several different words, not one character or word repeated, not a placeholder. */
export function reasonProblem(v: string): string | null {
  const t = v.trim().split(/\s+/).filter(Boolean).join(' ');
  const msg = "Type a detailed reason (at least 20 characters): explain what work was done and why it wasn't clocked.";
  if (t.length < 20) return msg;
  if (new Set(t.toLowerCase().replace(/[^\p{L}\p{N}]/gu, '')).size < 5) return msg;
  const words = t.toLowerCase().split(' ').map((w) => w.replace(/[^\p{L}\p{N}]/gu, '')).filter(Boolean);
  if (words.length < 4 || new Set(words).size < 3) return msg;
  return null;
}

/** "Add past time" for registered people on a work order's Time tab, for people with the Add past time permission. */
export function AddPastTimeButton({ recordId }: { recordId: string }) {
  const q = useQuery({ queryKey: ['sqf-time-fix', recordId], queryFn: () => get<FixData>(`${S}/tracking/${recordId}/time-corrections`) });
  const [open, setOpen] = useState(false);
  if (!q.data?.canAddPast) return null;
  const off = !!q.data.useCrewTime;
  return <>
    <Button variant="outlined" disabled={off} title={off ? CREW_HINT : 'Add time a registered person worked on this job but did not clock'} data-testid="add-past-time" onClick={() => setOpen(true)}>Add past time</Button>
    {open && <PastTimeDialog recordId={recordId} onClose={() => setOpen(false)} />}
  </>;
}

function PastTimeDialog({ recordId, onClose }: { recordId: string; onClose: () => void }) {
  const qc = useQueryClient();
  const { me } = useAuth();
  const users = useUsers();
  const [person, setPerson] = useState<Person | null>(me ? { id: me.id, displayName: me.displayName } : null);
  const [start, setStart] = useState('');
  const [stop, setStop] = useState('');
  const [pauses, setPauses] = useState<{ from: string; to: string }[]>([]);
  const [reason, setReason] = useState('');
  const [touched, setTouched] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [overlaps, setOverlaps] = useState<Overlap[]>([]);
  const max = toInput(new Date());

  useEffect(() => {
    setOverlaps([]);
    if (!person || !start || !stop || new Date(stop) <= new Date(start)) return;
    const t = setTimeout(() => {
      const p = new URLSearchParams({ userId: person.id, start: toIso(start), stop: toIso(stop) });
      get<Overlap[]>(`${S}/tracking/${recordId}/time/past/overlaps?${p}`).then(setOverlaps).catch(() => setOverlaps([]));
    }, 300);
    return () => clearTimeout(t);
  }, [person, start, stop, recordId]);

  const reasonErr = reasonProblem(reason);
  const save = async () => {
    setErr(null); setTouched(true);
    if (!person) { setErr('Choose the person.'); return; }
    if (!start || !stop) { setErr('Enter the start and stop.'); return; }
    if (new Date(stop) <= new Date(start)) { setErr('The stop time must be after the start time.'); return; }
    if (new Date(stop).getTime() > Date.now() + 60_000) { setErr("Past time can't end in the future. Use Clock on job for time happening now."); return; }
    if (pauses.some((p) => !p.from || !p.to)) { setErr('Fill in both times for each pause, or remove it.'); return; }
    if (reasonErr) { setErr(reasonErr); return; }
    setBusy(true);
    try {
      await post(`${S}/tracking/${recordId}/time/past`, { userId: person.id, start: toIso(start), stop: toIso(stop), pauses: pauses.map((p) => ({ from: toIso(p.from), to: toIso(p.to) })), reason: reason.trim() });
      await qc.invalidateQueries(); onClose();
    } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return <Dialog open onClose={busy ? undefined : onClose} fullWidth maxWidth="sm">
    <FormDialogTitle>Add past time</FormDialogTitle>
    <DialogContent><Stack gap={2} sx={{ pt: 1 }} data-testid="past-time-dialog">
      <Typography variant="body2" color="text.secondary">For time a registered person worked on this job but did not clock. It counts like clocked time and is marked in the log as added after the fact.</Typography>
      <Autocomplete options={users.data ?? []} value={person} getOptionLabel={(o) => o.displayName} isOptionEqualToValue={(a, b) => a.id === b.id}
        onChange={(_, v) => setPerson(v)} renderInput={(p) => <TextField {...p} label="Person" inputProps={{ ...p.inputProps, 'data-testid': 'past-person' }} />} />
      <Stack direction="row" gap={2}>
        <TextField type="datetime-local" label="Start" inputProps={{ 'data-testid': 'past-start', max }} value={start} onChange={(e) => setStart(e.target.value)} InputLabelProps={{ shrink: true }} fullWidth />
        <TextField type="datetime-local" label="Stop" inputProps={{ 'data-testid': 'past-stop', max }} value={stop} onChange={(e) => setStop(e.target.value)} InputLabelProps={{ shrink: true }} fullWidth />
      </Stack>
      {pauses.map((p, i) => <Stack key={i} direction="row" gap={2} alignItems="center">
        <TextField type="datetime-local" label={`Pause ${i + 1}`} inputProps={{ 'data-testid': `past-pause-${i}` }} value={p.from} onChange={(e) => setPauses(pauses.map((x, j) => (j === i ? { ...x, from: e.target.value } : x)))} InputLabelProps={{ shrink: true }} fullWidth />
        <TextField type="datetime-local" label="Continue" inputProps={{ 'data-testid': `past-continue-${i}` }} value={p.to} onChange={(e) => setPauses(pauses.map((x, j) => (j === i ? { ...x, to: e.target.value } : x)))} InputLabelProps={{ shrink: true }} fullWidth />
        <IconButton aria-label="Remove pause" onClick={() => setPauses(pauses.filter((_, j) => j !== i))}><DeleteOutline /></IconButton>
      </Stack>)}
      <Box><Button size="small" data-testid="past-add-pause" onClick={() => setPauses([...pauses, { from: '', to: '' }])}>Add pause / continue</Button></Box>
      <TextField label="Reason" required multiline minRows={2} value={reason} onChange={(e) => { setReason(e.target.value); setErr(null); }} onBlur={() => setTouched(true)}
        error={touched && !!reasonErr} helperText={touched && reasonErr ? reasonErr : `${REASON_HELP} (at least 20 characters).`} inputProps={{ maxLength: 1000, 'data-testid': 'past-reason' }} />
      {overlaps.length > 0 && <Alert severity="warning" data-testid="past-overlap-warning">
        {person?.displayName} also has time on other work orders in these hours: {overlaps.map((o) => `${o.trackingNumber} (${fmtDateTime(o.startedAt)} – ${o.stoppedAt ? fmtDateTime(o.stoppedAt) : 'still clocked on'})`).join('; ')}. Check this is not the same time twice.
      </Alert>}
      {err && <Alert severity="error" data-testid="past-error">{err}</Alert>}
    </Stack></DialogContent>
    <DialogActions><Button onClick={onClose} disabled={busy}>Cancel</Button><Button variant="contained" disabled={busy} onClick={save} data-testid="past-save">Save</Button></DialogActions>
  </Dialog>;
}
