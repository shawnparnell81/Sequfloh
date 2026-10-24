import { useEffect, useState } from 'react';
import { Alert, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, IconButton, Paper, Stack, Table, TableBody, TableCell, TableHead, TableRow,
  TextField, Tooltip, Typography } from '@mui/material';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import EditOutlined from '@mui/icons-material/EditOutlined';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post, put } from '../api';
import { fmtDateTime } from '../components/Status';
import { S } from './common';

interface Pause { from: string; to: string }
interface Session { id: string; crewSize: number; startedAt: string; stoppedAt?: string | null; entered: boolean; note?: string | null; elapsedSeconds: number; pausedSeconds: number; workedSeconds: number; laborSeconds: number; pauses: Pause[] }
export interface CrewSummary {
  serverTime: string; canEdit: boolean; running?: { id: string; crewSize: number; startedAt: string; pausedAt?: string | null; seconds: number; pausedSeconds: number } | null;
  crewWorkedSeconds: number; crewPausedSeconds: number; crewLaborSeconds: number; userSeconds: number; users: number; totalSeconds: number; personnel: number; maxCrew: number; sessions: Session[];
}

export const hrs = (s: number) => (s / 3600).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const pad = (n: number) => String(n).padStart(2, '0');
/** A date-time input value (local time) for an ISO instant. */
const toInput = (iso?: string | null) => { if (!iso) return ''; const d = new Date(iso); return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`; };
const toIso = (v: string) => new Date(v).toISOString();
const ymd = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;

function Big({ label, value, testid, strong }: { label: string; value: string; testid: string; strong?: boolean }) {
  return <Box sx={{ minWidth: 150, p: 1.5, border: 1, borderColor: strong ? 'primary.main' : 'divider', borderRadius: 1, bgcolor: strong ? 'primary.50' : undefined }}>
    <Typography variant="caption" color="text.secondary">{label}</Typography>
    <Typography variant="h3" data-testid={testid} sx={{ fontVariantNumeric: 'tabular-nums' }}>{value}</Typography>
  </Box>;
}

/**
 * Crew time: a number of workers with no names, clocked together. One Start / Pause / Continue / Stop applies to the whole crew.
 * Registered people keep using their own Clock on job buttons; their hours are shown next to the crew hours and added into the total.
 */
export function CrewPanel({ recordId }: { recordId: string }) {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['sqf-crew', recordId], queryFn: () => get<CrewSummary>(`${S}/tracking/${recordId}/crew`), refetchInterval: 30000 });
  const [crew, setCrew] = useState('3');
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [edit, setEdit] = useState<Session | 'new' | null>(null);
  const [bulk, setBulk] = useState(false);
  const [now, setNow] = useState(Date.now());
  useEffect(() => { const t = setInterval(() => setNow(Date.now()), 1000); return () => clearInterval(t); }, []);
  const d = q.data;
  const refresh = () => qc.invalidateQueries();
  const go = async (what: 'start' | 'pause' | 'continue' | 'stop') => {
    setBusy(true); setErr(null);
    try { await post(`${S}/tracking/${recordId}/crew/${what}`, what === 'start' ? { crewSize: Number(crew) } : undefined); await refresh(); }
    catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  const extra = d?.running && !d.running.pausedAt ? Math.max(0, (now - q.dataUpdatedAt) / 1000) : 0;
  const crewLabor = (d?.crewLaborSeconds ?? 0) + extra * (d?.running?.crewSize ?? 0);
  return (
    <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Crew time">
      <Stack direction="row" alignItems="center" gap={1.5} sx={{ mb: 1.5 }} flexWrap="wrap">
        <Typography variant="h3">Crew time</Typography>
        <Tooltip title="Workers with no login, such as day labor. One start, pause and stop applies to the whole crew. Registered people still clock on and off with their own buttons."><Typography variant="body2" color="text.secondary">(no names needed)</Typography></Tooltip>
        <Box sx={{ flex: 1 }} />
        {d?.canEdit && (d.running
          ? <>
            <Typography variant="body2">Crew of {d.running.crewSize} {d.running.pausedAt ? `paused since ${fmtDateTime(d.running.pausedAt)}` : `on since ${fmtDateTime(d.running.startedAt)}`}</Typography>
            {d.running.pausedAt
              ? <Button variant="contained" disabled={busy} data-testid="crew-continue" onClick={() => go('continue')}>Continue</Button>
              : <Button variant="outlined" disabled={busy} data-testid="crew-pause" onClick={() => go('pause')}>Pause crew</Button>}
            <Button variant="contained" color="error" disabled={busy} data-testid="crew-stop" onClick={() => go('stop')}>Stop crew</Button>
          </>
          : <>
            <TextField size="small" type="number" label="Crew size (workers)" value={crew} onChange={(e) => setCrew(e.target.value)} inputProps={{ min: 1, max: 500, 'data-testid': 'crew-size' }} sx={{ width: 170 }} />
            <Button variant="contained" color="success" disabled={busy || !(Number(crew) >= 1)} data-testid="crew-start" onClick={() => go('start')}>Start crew</Button>
          </>)}
        {d?.canEdit && <Button variant="outlined" data-testid="crew-add-past" onClick={() => setEdit('new')}>Add past time</Button>}
        {d?.canEdit && <Button variant="outlined" data-testid="crew-bulk" onClick={() => setBulk(true)}>Bulk entry</Button>}
      </Stack>
      {err && <Alert severity="error" sx={{ mb: 1.5 }} onClose={() => setErr(null)}>{err}</Alert>}
      {d && <Stack direction="row" gap={1.5} flexWrap="wrap" sx={{ mb: 1.5 }} data-testid="crew-totals">
        <Big label="Crew elapsed hours (paused left out)" value={hrs(d.crewWorkedSeconds)} testid="crew-elapsed" />
        <Big label="Crew size" value={d.running ? String(d.running.crewSize) : d.maxCrew ? `up to ${d.maxCrew}` : '—'} testid="crew-size-now" />
        <Big label="Crew labor hours" value={hrs(crewLabor)} testid="crew-labor" />
        <Big label="Registered people hours" value={hrs(d.userSeconds)} testid="crew-user-hours" />
        <Big label="Total labor hours" value={hrs(crewLabor + d.userSeconds)} testid="crew-total" strong />
        <Big label="Total personnel" value={String(d.personnel)} testid="crew-personnel" />
      </Stack>}
      {d && (d.sessions.length === 0 ? <Typography variant="body2" color="text.secondary">No crew time yet.</Typography> :
        <Table size="small" data-testid="crew-sessions">
          <TableHead><TableRow><TableCell>Start</TableCell><TableCell>Stop</TableCell><TableCell align="right">Crew</TableCell><TableCell align="right">Elapsed h</TableCell>
            <TableCell align="right">Paused h</TableCell><TableCell align="right">Labor h</TableCell><TableCell>Note</TableCell><TableCell /></TableRow></TableHead>
          <TableBody>{d.sessions.map((s) => <TableRow key={s.id}>
            <TableCell>{fmtDateTime(s.startedAt)}</TableCell><TableCell>{s.stoppedAt ? fmtDateTime(s.stoppedAt) : 'running'}</TableCell>
            <TableCell align="right">{s.crewSize}</TableCell><TableCell align="right">{hrs(s.elapsedSeconds)}</TableCell><TableCell align="right">{hrs(s.pausedSeconds)}</TableCell>
            <TableCell align="right"><b>{hrs(s.laborSeconds)}</b></TableCell><TableCell>{s.entered ? <i>entered</i> : ''}{s.note ? ` ${s.note}` : ''}</TableCell>
            <TableCell sx={{ whiteSpace: 'nowrap' }}>{d.canEdit && s.stoppedAt && <>
              <IconButton size="small" aria-label="Edit crew time" onClick={() => setEdit(s)}><EditOutlined fontSize="small" /></IconButton>
              <IconButton size="small" aria-label="Delete crew time" onClick={async () => { if (!confirm('Delete this crew time?')) return; try { await del(`${S}/tracking/crew/sessions/${s.id}`); await refresh(); } catch (e) { setErr((e as Error).message); } }}>
                <DeleteOutline fontSize="small" /></IconButton></>}</TableCell>
          </TableRow>)}</TableBody>
        </Table>)}
      <Typography variant="caption" color="text.secondary" component="div" sx={{ mt: 1 }}>
        Labor hours = (elapsed − paused) × crew size. Total personnel = largest crew + each registered person who clocked on. Every change is kept in History.
      </Typography>
      {edit && <SessionDialog recordId={recordId} session={edit === 'new' ? null : edit} onClose={() => setEdit(null)} onSaved={async () => { setEdit(null); await refresh(); }} />}
      {bulk && <BulkDialog recordId={recordId} onClose={() => setBulk(false)} onSaved={async () => { setBulk(false); await refresh(); }} />}
    </Paper>
  );
}

function SessionDialog({ recordId, session, onClose, onSaved }: { recordId: string; session: Session | null; onClose: () => void; onSaved: () => void }) {
  const [crew, setCrew] = useState(String(session?.crewSize ?? 3));
  const [start, setStart] = useState(toInput(session?.startedAt));
  const [stop, setStop] = useState(toInput(session?.stoppedAt));
  const [pauses, setPauses] = useState<{ from: string; to: string }[]>((session?.pauses ?? []).map((p) => ({ from: toInput(p.from), to: toInput(p.to) })));
  const [note, setNote] = useState(session?.note ?? '');
  const [err, setErr] = useState<string | null>(null);
  const save = async () => {
    setErr(null);
    if (!start || !stop) { setErr('Enter the start and stop.'); return; }
    if (pauses.some((p) => !p.from || !p.to)) { setErr('Fill in both times for each pause, or remove it.'); return; }
    const body = { crewSize: Number(crew), start: toIso(start), stop: toIso(stop), pauses: pauses.map((p) => ({ from: toIso(p.from), to: toIso(p.to) })), note: note || null };
    try { if (session) await put(`${S}/tracking/crew/sessions/${session.id}`, body); else await post(`${S}/tracking/${recordId}/crew/sessions`, body); onSaved(); }
    catch (e) { setErr((e as Error).message); }
  };
  return <Dialog open onClose={onClose} fullWidth maxWidth="sm">
    <DialogTitle>{session ? 'Edit crew time' : 'Add past crew time'}</DialogTitle>
    <DialogContent><Stack gap={2} sx={{ pt: 1 }}>
      <TextField type="number" label="Crew size (workers)" value={crew} onChange={(e) => setCrew(e.target.value)} inputProps={{ min: 1 }} />
      <Stack direction="row" gap={2}>
        <TextField type="datetime-local" label="Start" value={start} onChange={(e) => setStart(e.target.value)} InputLabelProps={{ shrink: true }} fullWidth />
        <TextField type="datetime-local" label="Stop" value={stop} onChange={(e) => setStop(e.target.value)} InputLabelProps={{ shrink: true }} fullWidth />
      </Stack>
      {pauses.map((p, i) => <Stack key={i} direction="row" gap={2} alignItems="center">
        <TextField type="datetime-local" label={`Pause ${i + 1}`} value={p.from} onChange={(e) => setPauses(pauses.map((x, j) => (j === i ? { ...x, from: e.target.value } : x)))} InputLabelProps={{ shrink: true }} fullWidth />
        <TextField type="datetime-local" label="Continue" value={p.to} onChange={(e) => setPauses(pauses.map((x, j) => (j === i ? { ...x, to: e.target.value } : x)))} InputLabelProps={{ shrink: true }} fullWidth />
        <IconButton aria-label="Remove pause" onClick={() => setPauses(pauses.filter((_, j) => j !== i))}><DeleteOutline /></IconButton>
      </Stack>)}
      <Box><Button size="small" onClick={() => setPauses([...pauses, { from: '', to: '' }])}>Add pause / continue</Button></Box>
      <TextField label="Note (optional)" value={note} onChange={(e) => setNote(e.target.value)} inputProps={{ maxLength: 300 }} />
      {err && <Alert severity="error">{err}</Alert>}
    </Stack></DialogContent>
    <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" onClick={save}>Save</Button></DialogActions>
  </Dialog>;
}

function BulkDialog({ recordId, onClose, onSaved }: { recordId: string; onClose: () => void; onSaved: () => void }) {
  const today = new Date();
  const [from, setFrom] = useState(ymd(new Date(today.getFullYear(), today.getMonth(), today.getDate() - 14)));
  const [to, setTo] = useState(ymd(new Date(today.getFullYear(), today.getMonth(), today.getDate() - 1)));
  const [skip, setSkip] = useState(true);
  const [crew, setCrew] = useState('3');
  const [hours, setHours] = useState('8');
  const [startTime, setStartTime] = useState('07:00');
  const [lunch, setLunch] = useState('0');
  const [off, setOff] = useState<string[]>([]);
  const [err, setErr] = useState<string | null>(null);
  const days: string[] = [];
  if (from && to) for (let d = new Date(`${from}T12:00`); d <= new Date(`${to}T12:00`) && days.length < 400; d.setDate(d.getDate() + 1))
    if (!skip || (d.getDay() !== 0 && d.getDay() !== 6)) days.push(ymd(d));
  const chosen = days.filter((d) => !off.includes(d));
  const labor = chosen.length * Number(crew || 0) * Number(hours || 0);
  const save = async () => {
    setErr(null);
    try {
      await post(`${S}/tracking/${recordId}/crew/bulk`, { from, to, crewSize: Number(crew), hoursPerDay: Number(hours), startTime, lunchMinutes: Number(lunch || 0), skipWeekends: skip, days: chosen });
      onSaved();
    } catch (e) { setErr((e as Error).message); }
  };
  return <Dialog open onClose={onClose} fullWidth maxWidth="md">
    <DialogTitle>Bulk crew time</DialogTitle>
    <DialogContent><Stack gap={2} sx={{ pt: 1 }}>
      <Stack direction="row" gap={2} flexWrap="wrap">
        <TextField type="date" label="From" value={from} onChange={(e) => setFrom(e.target.value)} InputLabelProps={{ shrink: true }} />
        <TextField type="date" label="To" value={to} onChange={(e) => setTo(e.target.value)} InputLabelProps={{ shrink: true }} />
        <FormControlLabel control={<Checkbox checked={skip} onChange={(e) => setSkip(e.target.checked)} />} label="Skip weekends" />
      </Stack>
      <Stack direction="row" gap={2} flexWrap="wrap">
        <TextField type="number" label="Crew size (workers)" value={crew} onChange={(e) => setCrew(e.target.value)} inputProps={{ min: 1 }} />
        <TextField type="number" label="Hours per day" value={hours} onChange={(e) => setHours(e.target.value)} inputProps={{ min: 0.25, step: 0.25 }} />
        <TextField type="time" label="Start time" value={startTime} onChange={(e) => setStartTime(e.target.value)} InputLabelProps={{ shrink: true }} />
        <TextField type="number" label="Unpaid lunch (minutes)" value={lunch} onChange={(e) => setLunch(e.target.value)} inputProps={{ min: 0 }} />
      </Stack>
      <Box>
        <Typography variant="subtitle2" sx={{ mb: 0.5 }}>Working days (untick any day off)</Typography>
        <Stack direction="row" flexWrap="wrap" gap={0.5} data-testid="bulk-days">
          {days.map((d) => <FormControlLabel key={d} sx={{ mr: 1 }} control={<Checkbox size="small" checked={!off.includes(d)} onChange={(e) => setOff(e.target.checked ? off.filter((x) => x !== d) : [...off, d])} />}
            label={new Date(`${d}T12:00`).toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' })} />)}
        </Stack>
      </Box>
      <Alert severity="info" data-testid="bulk-summary">
        <b>{crew || 0} workers × {chosen.length} working days × {hours || 0} h/day = {labor.toLocaleString('en-US')} labor hours</b>
      </Alert>
      {err && <Alert severity="error">{err}</Alert>}
    </Stack></DialogContent>
    <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" disabled={chosen.length === 0} data-testid="bulk-save" onClick={save}>Add {chosen.length} days</Button></DialogActions>
  </Dialog>;
}
