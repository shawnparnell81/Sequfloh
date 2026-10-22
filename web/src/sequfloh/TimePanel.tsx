import { useEffect, useState } from 'react';
import { Alert, Autocomplete, Box, Button, Chip, Dialog, DialogActions, DialogContent, DialogTitle, Paper, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put } from '../api';
import { useUsers } from '../components/Kit';
import { fmtDateTime } from '../components/Status';
import { S, fmtSeconds } from './common';

export interface Person { id: string; displayName: string }
interface TimePerson { userId: string; name: string; seconds: number; entries: number; runningSince?: string | null }
interface TimeSummary { totalSeconds: number; people: TimePerson[]; serverTime: string; mineRunning: boolean }

/** People on a work order. The first one is the lead. */
export function AssigneesPanel({ recordId, people, canAssign }: { recordId: string; people: Person[]; canAssign: boolean }) {
  const qc = useQueryClient();
  const users = useUsers();
  const [open, setOpen] = useState(false);
  const [pick, setPick] = useState<Person[]>(people);
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const save = async () => {
    setBusy(true); setErr(null);
    try { await put(`${S}/tracking/${recordId}/assignees`, { userIds: pick.map((p) => p.id) }); setOpen(false); await qc.invalidateQueries(); }
    catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return (
    <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Assigned people">
      <Stack direction="row" alignItems="center" gap={1} sx={{ mb: 1 }}>
        <Typography variant="h3">Assigned people</Typography><Box sx={{ flex: 1 }} />
        {canAssign && <Button size="small" variant="outlined" data-testid="edit-assignees" onClick={() => { setPick(people); setErr(null); setOpen(true); }}>Change</Button>}
      </Stack>
      <Stack direction="row" gap={1} flexWrap="wrap" data-testid="assignees">
        {people.length ? people.map((p) => <Chip key={p.id} label={p.displayName} />) : <Typography variant="body2" color="text.secondary">Nobody yet.</Typography>}
      </Stack>
      <Dialog open={open} onClose={busy ? undefined : () => setOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle>Assigned people</DialogTitle>
        <DialogContent>
          {err && <Alert severity="error" sx={{ mb: 2 }}>{err}</Alert>}
          <Autocomplete multiple options={users.data ?? []} value={pick} getOptionLabel={(o) => o.displayName} isOptionEqualToValue={(a, b) => a.id === b.id}
            onChange={(_, v) => setPick(v)} renderInput={(p) => <TextField {...p} label="People" sx={{ mt: 1 }} />} />
        </DialogContent>
        <DialogActions><Button onClick={() => setOpen(false)} disabled={busy}>Cancel</Button><Button variant="contained" disabled={busy} onClick={save}>Save</Button></DialogActions>
      </Dialog>
    </Paper>
  );
}

/** Live time per person. Anyone signed in can start or stop their own timer. */
export function TimePanel({ recordId }: { recordId: string }) {
  const qc = useQueryClient();
  const key = ['sqf-time', recordId];
  const q = useQuery({ queryKey: key, queryFn: () => get<TimeSummary>(`${S}/tracking/${recordId}/time`), refetchInterval: 30000 });
  const [now, setNow] = useState(Date.now());
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { const t = setInterval(() => setNow(Date.now()), 1000); return () => clearInterval(t); }, []);
  const d = q.data;
  const skew = d ? q.dataUpdatedAt - new Date(d.serverTime).getTime() : 0;
  const live = (p: TimePerson) => p.seconds + (p.runningSince ? Math.max(0, (now - skew - new Date(p.runningSince).getTime()) / 1000) : 0);
  const total = d ? d.people.reduce((a, p) => a + live(p), 0) : 0;
  const go = async (what: 'start' | 'stop') => {
    setBusy(true); setErr(null);
    try { await post(`${S}/tracking/${recordId}/time/${what}`); await qc.invalidateQueries(); }
    catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return (
    <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Time on this job">
      <Stack direction="row" alignItems="center" gap={2} sx={{ mb: 1.5 }} flexWrap="wrap">
        <Typography variant="h3">Time on this job</Typography>
        <Typography variant="h3" data-testid="time-total" sx={{ fontVariantNumeric: 'tabular-nums' }}>{fmtSeconds(total)}</Typography>
        <Box sx={{ flex: 1 }} />
        {d && (d.mineRunning
          ? <Button variant="contained" color="error" disabled={busy} data-testid="stop-timer" onClick={() => go('stop')}>Stop my timer</Button>
          : <Button variant="contained" color="success" disabled={busy} data-testid="start-timer" onClick={() => go('start')}>Start my timer</Button>)}
      </Stack>
      {err && <Alert severity="error" sx={{ mb: 1.5 }} onClose={() => setErr(null)}>{err}</Alert>}
      {d && (d.people.length === 0 ? <Typography variant="body2" color="text.secondary">No time logged yet. Anyone can start a timer.</Typography> :
        <Table size="small" data-testid="time-people">
          <TableHead><TableRow><TableCell>Person</TableCell><TableCell align="right">Time</TableCell><TableCell align="right">Sessions</TableCell><TableCell>Now</TableCell></TableRow></TableHead>
          <TableBody>{d.people.map((p) => <TableRow key={p.userId}>
            <TableCell>{p.name}</TableCell><TableCell align="right" sx={{ fontVariantNumeric: 'tabular-nums' }}>{fmtSeconds(live(p))}</TableCell>
            <TableCell align="right">{p.entries}</TableCell>
            <TableCell>{p.runningSince ? <Chip size="small" color="success" label={`Running since ${fmtDateTime(p.runningSince)}`} /> : '—'}</TableCell></TableRow>)}</TableBody>
        </Table>)}
    </Paper>
  );
}
