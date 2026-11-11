import { useMemo, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Alert, Box, Button, Chip, Dialog, DialogActions, DialogContent, MenuItem, Paper, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post } from '../api';
import { useAuth } from '../auth';
import { FormDialogTitle } from '../components/FullScreen';
import { useUsers } from '../components/Kit';
import { S, SqfPage, fmtSeconds } from './common';

const et = 'America/New_York';
const fmtEt = (d?: string | null) => d ? new Intl.DateTimeFormat('en-US', { timeZone: et, month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }).format(new Date(d)) : '—';
const fmtClock = (d?: string | null) => d ? new Intl.DateTimeFormat('en-US', { timeZone: et, hour: 'numeric', minute: '2-digit' }).format(new Date(d)) : '—';

function etToday() {
  return new Intl.DateTimeFormat('en-CA', { timeZone: et, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
}
function addDays(iso: string, n: number) {
  const [y, m, d] = iso.split('-').map(Number);
  const dt = new Date(Date.UTC(y, m - 1, d + n));
  return dt.toISOString().slice(0, 10);
}
function weekStart(iso: string) {
  const [y, m, d] = iso.split('-').map(Number);
  const dt = new Date(Date.UTC(y, m - 1, d));
  const weekday = dt.getUTCDay();
  return addDays(iso, weekday === 0 ? -6 : 1 - weekday);
}
/** A datetime-local value is a clock time in America/New_York, which is the shop day. */
function fromEt(local: string) {
  const [date, time] = local.split('T');
  const [y, mo, da] = date.split('-').map(Number);
  const [h, mi] = time.split(':').map(Number);
  const utcGuess = Date.UTC(y, mo - 1, da, h, mi);
  const dtf = new Intl.DateTimeFormat('en-US', { timeZone: et, hour12: false, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' });
  const asParts = (ms: number) => {
    const map: Record<string, string> = {};
    for (const p of dtf.formatToParts(new Date(ms))) if (p.type !== 'literal') map[p.type] = p.value;
    return Date.UTC(+map.year, +map.month - 1, +map.day, +map.hour % 24, +map.minute, +map.second);
  };
  return new Date(utcGuess - (asParts(utcGuess) - utcGuess)).toISOString();
}

interface Task { id: string; label: string }
interface Activity { kind: string; name: string; since: string; note?: string | null; trackingRecordId?: string | null; taskId?: string | null; resumeName?: string | null }
interface Status {
  clockedIn: boolean; startedAt?: string | null; workingLate: boolean; scheduledEnd?: string | null; hardCapAt?: string | null; offerLate: boolean; endLabel: string; serverTime: string;
  activity?: Activity | null; tasks: Task[];
  can: { own: boolean; team: boolean; all: boolean; correct: boolean; manage: boolean };
}
interface Seg { id: string; kind: string; name: string; note?: string | null; startedAt: string; endedAt?: string | null; seconds: number; endKind?: string | null }
interface Day {
  date: string; arrival?: string | null; clockOut?: string | null; clockOutKind?: string | null; automatic: boolean;
  workSeconds: number; indirectSeconds: number; breakSeconds: number; lunchSeconds: number; unaccountedSeconds: number;
  indirect: { task: string; seconds: number }[]; workOrders: { number: string; seconds: number }[]; segments: Seg[];
}
interface Sheet { userId: string; userName: string; from: string; to: string; days: Day[]; workSeconds: number; indirectSeconds: number; breakSeconds: number; lunchSeconds: number; unaccountedSeconds: number }
interface TeamPerson { userId: string; name: string; department?: string | null; since: string; activity: string; activitySince: string; kind: string; workingLate: boolean }

const useStatus = () => useQuery({ queryKey: ['day-clock'], queryFn: () => get<Status>(`${S}/day-clock`), refetchInterval: 20_000 });

export function DayClockBar() {
  const { can } = useAuth();
  const qc = useQueryClient();
  const q = useStatus();
  const [task, setTask] = useState('');
  const [note, setNote] = useState('');
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  if (!can('sequfloh.access')) return null;
  const d = q.data;
  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true); setErr(null);
    try { await fn(); setNote(''); await qc.invalidateQueries({ queryKey: ['day-clock'] }); await qc.invalidateQueries({ queryKey: ['sqf-time'] }); }
    catch (e) { setErr((e as Error).message); }
    finally { setBusy(false); }
  };
  if (q.isLoading || !d) return null;
  if (!d.clockedIn) return (
    <Alert severity="warning" variant="filled" data-testid="day-clock-banner" sx={{ borderRadius: 0, py: 0.5, alignItems: 'center', '& .MuiAlert-message': { width: '100%' } }}
      action={<Button color="inherit" variant="outlined" data-testid="day-clock-in" disabled={busy} sx={{ whiteSpace: 'nowrap' }} onClick={() => run(() => post(`${S}/day-clock/in`))}>Clock in</Button>}>
      <Typography component="span" sx={{ fontWeight: 800 }}>Clock in for the day.</Typography>{' '}This records when you arrived and lets you start a job or an indirect task. It is not a payroll clock.
    </Alert>
  );
  const onBreak = d.activity?.kind === 'Break' || d.activity?.kind === 'Lunch';
  const resume = d.activity?.resumeName;
  return (
    <Paper square elevation={0} data-testid="day-clock-widget" sx={{ px: 2, py: 1, borderBottom: 1, borderColor: 'divider' }}>
      {err && <Alert severity="error" sx={{ mb: 1 }} onClose={() => setErr(null)}>{err}</Alert>}
      <Stack direction="row" gap={1} alignItems="center" flexWrap="wrap">
        <Chip color="success" label={`In since ${fmtClock(d.startedAt)}`} data-testid="day-clock-since" />
        <Chip color={onBreak ? 'warning' : d.activity ? 'primary' : 'default'} variant={d.activity ? 'filled' : 'outlined'} data-testid="day-clock-activity"
          label={d.activity ? `${d.activity.name}${d.activity.note ? ` · ${d.activity.note}` : ''} since ${fmtClock(d.activity.since)}` : 'Not on a task'} />
        {onBreak
          ? <Button variant="contained" disabled={busy} data-testid="day-clock-resume" onClick={() => run(() => post(`${S}/day-clock/resume`))}>{resume ? `Resume ${resume}` : 'End break'}</Button>
          : <>
            <TextField select size="small" label="Indirect task" value={task} onChange={(e) => setTask(e.target.value)} sx={{ minWidth: 280 }} data-testid="day-clock-task" disabled={busy}>
              <MenuItem value="">Choose a task</MenuItem>
              {d.tasks.map((t) => <MenuItem key={t.id} value={t.id}>{t.label}</MenuItem>)}
            </TextField>
            <TextField size="small" label="Note" placeholder="Optional" value={note} onChange={(e) => setNote(e.target.value)} sx={{ width: 180 }} slotProps={{ htmlInput: { 'data-testid': 'day-clock-note', maxLength: 300 } }} disabled={busy} />
            <Button variant="contained" disabled={busy || !task} data-testid="day-clock-start-task" onClick={() => run(() => post(`${S}/day-clock/indirect`, { taskId: task, note: note || null }))}>Start</Button>
            <Button variant="outlined" disabled={busy} data-testid="day-clock-break" onClick={() => run(() => post(`${S}/day-clock/pause`, { reason: 'Break' }))}>Break</Button>
            <Button variant="outlined" disabled={busy} data-testid="day-clock-lunch" onClick={() => run(() => post(`${S}/day-clock/pause`, { reason: 'Lunch' }))}>Lunch</Button>
          </>}
        {d.offerLate && !d.workingLate && <Button variant="contained" color="warning" disabled={busy} data-testid="day-clock-late" onClick={() => run(() => post(`${S}/day-clock/late`))}>Working late</Button>}
        {d.workingLate && <Chip color="warning" label={`Working late until ${fmtClock(d.hardCapAt)}`} />}
        <Typography variant="caption" color="text.secondary">Day ends {d.endLabel} ET</Typography>
        <Box sx={{ flex: 1 }} />
        <Button size="small" component={RouterLink} to="/sequfloh/day-clock" data-testid="day-clock-timesheet-link">Timesheet</Button>
        <Button variant="contained" color="error" disabled={busy} data-testid="day-clock-out" onClick={() => run(() => post(`${S}/day-clock/out`))}>Clock out</Button>
      </Stack>
    </Paper>
  );
}

function Cards({ day }: { day: Day }) {
  const cells: [string, string][] = [
    ['Arrived', fmtClock(day.arrival)],
    ['Clocked out', day.clockOut ? `${fmtClock(day.clockOut)}${day.automatic ? ` · ${day.clockOutKind === 'HardCap' ? 'hard cap' : 'automatic'}` : ''}` : day.arrival ? 'Still in' : '—'],
    ['Work orders', fmtSeconds(day.workSeconds)],
    ['Indirect', fmtSeconds(day.indirectSeconds)],
    ['Break', fmtSeconds(day.breakSeconds)],
    ['Lunch', fmtSeconds(day.lunchSeconds)],
    ['Unaccounted', fmtSeconds(day.unaccountedSeconds)],
  ];
  return (
    <Box sx={{ display: 'grid', gap: 1, gridTemplateColumns: { xs: '1fr 1fr', md: 'repeat(7, 1fr)' } }}>
      {cells.map(([k, v]) => <Paper key={k} variant="outlined" sx={{ p: 1.25 }}><Typography variant="caption" color="text.secondary">{k}</Typography><Typography sx={{ fontWeight: 800, fontVariantNumeric: 'tabular-nums' }}>{v}</Typography></Paper>)}
    </Box>
  );
}

export function DayClockPage({ view = 'today' }: { view?: 'today' | 'week' | 'team' }) {
  const { can } = useAuth();
  const status = useStatus();
  const today = etToday();
  const from = view === 'week' ? weekStart(today) : today;
  const to = view === 'week' ? addDays(from, 6) : today;
  const mine = can('sequfloh.view_own_day_clock') || can('sequfloh.view_team_day_clock') || can('sequfloh.view_all_day_clock');
  const teamOk = can('sequfloh.view_team_day_clock') || can('sequfloh.view_all_day_clock');
  const sheet = useQuery({ queryKey: ['day-clock-sheet', from, to], queryFn: () => get<Sheet>(`${S}/day-clock/timesheet?from=${from}&to=${to}`), enabled: mine && view !== 'team' });
  const team = useQuery({ queryKey: ['day-clock-team'], queryFn: () => get<{ serverTime: string; people: TeamPerson[] }>(`${S}/day-clock/team`), enabled: teamOk && view === 'team', refetchInterval: 20_000 });
  const [correct, setCorrect] = useState<{ segment?: Seg; day?: Day } | null>(null);
  const day = sheet.data?.days[0];
  return (
    <SqfPage title="Day clock" description="When you arrived, what you worked on, and the time that was not on a task. Salaried staff: this is not a payroll clock. Times are America/New_York."
      crumbs={[{ label: 'Day clock', to: '/sequfloh/day-clock' }, { label: view === 'team' ? 'Team' : view === 'week' ? 'This week' : 'Today' }]}
      actions={<>
        {mine && <Button component="a" href={`${S}/day-clock/export.csv?from=${from}&to=${to}`} data-testid="day-clock-csv">Export CSV</Button>}
        {can('sequfloh.correct_day_clock') && <Button variant="outlined" data-testid="correction-open" onClick={() => setCorrect({})}>Correct time</Button>}
        {can('sequfloh.manage_day_clock') && <Button component={RouterLink} to="/sequfloh/settings/dayClock">Day clock settings</Button>}
      </>}>
      {!mine && view !== 'team' && <Alert severity="info">An administrator can grant “view your own day clock” so this timesheet is available. Clocking in does not need that permission.</Alert>}
      {view === 'team' && !teamOk && <Alert severity="info">Team status needs the department or all-day clock permission.</Alert>}
      {view !== 'team' && sheet.data && (
        <Stack gap={2} data-testid={view === 'week' ? 'timesheet-weekly' : 'timesheet-daily'}>
          <Typography variant="body2" color="text.secondary">{sheet.data.userName} · {from} to {to}</Typography>
          {view === 'today' && day && <Cards day={day} />}
          {view === 'today' && day && <SegmentTable day={day} onCorrect={can('sequfloh.correct_day_clock') ? (s) => setCorrect({ segment: s, day }) : undefined} />}
          {view === 'week' && <Table size="small" data-testid="week-table"><TableHead><TableRow>
            <TableCell>Day</TableCell><TableCell>Arrived</TableCell><TableCell>Clocked out</TableCell><TableCell align="right">Work orders</TableCell><TableCell align="right">Indirect</TableCell><TableCell align="right">Break</TableCell><TableCell align="right">Lunch</TableCell><TableCell align="right">Unaccounted</TableCell>
          </TableRow></TableHead><TableBody>
            {sheet.data.days.map((r) => <TableRow key={r.date}><TableCell>{r.date}</TableCell><TableCell>{fmtClock(r.arrival)}</TableCell>
              <TableCell>{r.clockOut ? fmtClock(r.clockOut) : r.arrival ? 'Still in' : '—'}{r.automatic ? ' · automatic' : ''}</TableCell>
              <TableCell align="right">{fmtSeconds(r.workSeconds)}</TableCell><TableCell align="right">{fmtSeconds(r.indirectSeconds)}</TableCell>
              <TableCell align="right">{fmtSeconds(r.breakSeconds)}</TableCell><TableCell align="right">{fmtSeconds(r.lunchSeconds)}</TableCell>
              <TableCell align="right">{fmtSeconds(r.unaccountedSeconds)}</TableCell></TableRow>)}
          </TableBody></Table>}
          {view === 'week' && <Paper variant="outlined" sx={{ p: 1.5 }}>
            <Typography variant="body2">Week totals · work orders {fmtSeconds(sheet.data.workSeconds)} · indirect {fmtSeconds(sheet.data.indirectSeconds)} · break {fmtSeconds(sheet.data.breakSeconds)} · lunch {fmtSeconds(sheet.data.lunchSeconds)} · unaccounted {fmtSeconds(sheet.data.unaccountedSeconds)}</Typography>
            {sheet.data.days.flatMap((r) => r.indirect).length > 0 && <Typography variant="body2" sx={{ mt: 0.5 }}>Indirect: {Object.entries(sheet.data.days.flatMap((r) => r.indirect).reduce<Record<string, number>>((a, t) => ({ ...a, [t.task]: (a[t.task] ?? 0) + t.seconds }), {})).map(([k, v]) => `${k} ${fmtSeconds(v)}`).join(' · ')}</Typography>}
          </Paper>}
        </Stack>
      )}
      {view === 'team' && team.data && (
        <Stack gap={1} data-testid="team-live">
          <Typography variant="body2" color="text.secondary">As of {fmtEt(team.data.serverTime)}. People who are clocked in right now.</Typography>
          {team.data.people.length === 0 ? <Typography>Nobody is clocked in.</Typography> : (
            <Table size="small"><TableHead><TableRow><TableCell>Person</TableCell><TableCell>Department</TableCell><TableCell>In since</TableCell><TableCell>On</TableCell><TableCell>Since</TableCell></TableRow></TableHead>
              <TableBody>{team.data.people.map((p) => <TableRow key={p.userId} data-testid="team-row"><TableCell>{p.name}{p.workingLate ? ' · late' : ''}</TableCell><TableCell>{p.department ?? '—'}</TableCell><TableCell>{fmtEt(p.since)}</TableCell><TableCell>{p.activity}</TableCell><TableCell>{fmtEt(p.activitySince)}</TableCell></TableRow>)}</TableBody></Table>
          )}
        </Stack>
      )}
      {sheet.error && <Alert severity="error">{(sheet.error as Error).message}</Alert>}
      {team.error && <Alert severity="error">{(team.error as Error).message}</Alert>}
      <History />
      {correct && status.data && <CorrectionDialog tasks={status.data.tasks} initial={correct} onClose={() => setCorrect(null)} />}
    </SqfPage>
  );
}

function SegmentTable({ day, onCorrect }: { day: Day; onCorrect?: (s: Seg) => void }) {
  return (
    <Stack gap={1}>
      {day.indirect.length > 0 && <Typography variant="body2">Indirect by task: {day.indirect.map((t) => `${t.task} ${fmtSeconds(t.seconds)}`).join(' · ')}</Typography>}
      {day.workOrders.length > 0 && <Typography variant="body2">Work orders: {day.workOrders.map((t) => `${t.number} ${fmtSeconds(t.seconds)}`).join(' · ')}</Typography>}
      <Table size="small" data-testid="day-segments"><TableHead><TableRow><TableCell>Task</TableCell><TableCell>From</TableCell><TableCell>To</TableCell><TableCell align="right">Time</TableCell><TableCell>Note</TableCell>{onCorrect && <TableCell />}</TableRow></TableHead>
        <TableBody>{day.segments.length === 0 ? <TableRow><TableCell colSpan={6}>No task yet. Time while clocked in and not on a task counts as unaccounted.</TableCell></TableRow> : day.segments.map((s) => <TableRow key={s.id}>
          <TableCell>{s.name}</TableCell><TableCell>{fmtEt(s.startedAt)}</TableCell><TableCell>{s.endedAt ? fmtEt(s.endedAt) : 'Now'}</TableCell>
          <TableCell align="right">{fmtSeconds(s.seconds)}</TableCell><TableCell>{s.note ?? (s.endKind === 'Automatic' || s.endKind === 'HardCap' ? 'Stopped at end of day' : '—')}</TableCell>
          {onCorrect && <TableCell><Button size="small" onClick={() => onCorrect(s)}>Correct</Button></TableCell>}
        </TableRow>)}</TableBody></Table>
    </Stack>
  );
}

function History() {
  const q = useQuery({ queryKey: ['day-clock-history'], queryFn: () => get<{ entries: { occurredAt: string; userName?: string | null; description: string; reason?: string | null }[] }>(`${S}/day-clock/history`) });
  const rows = q.data?.entries ?? [];
  if (rows.length === 0) return null;
  return (
    <Box sx={{ mt: 3 }} data-testid="day-clock-history">
      <Typography variant="h3" sx={{ mb: 1 }}>History</Typography>
      <Table size="small"><TableHead><TableRow><TableCell>When</TableCell><TableCell>Who</TableCell><TableCell>What</TableCell></TableRow></TableHead>
        <TableBody>{rows.slice(0, 30).map((r, i) => <TableRow key={i}><TableCell>{fmtEt(r.occurredAt)}</TableCell><TableCell>{r.userName ?? '—'}</TableCell><TableCell>{r.description}</TableCell></TableRow>)}</TableBody></Table>
    </Box>
  );
}

function CorrectionDialog({ tasks, initial, onClose }: { tasks: Task[]; initial: { segment?: Seg; day?: Day }; onClose: () => void }) {
  const qc = useQueryClient();
  const users = useUsers();
  const me = useAuth().me;
  const [userId, setUserId] = useState(me?.id ?? '');
  const [action, setAction] = useState(initial.segment ? 'adjust-segment' : 'add-clock');
  const [start, setStart] = useState('');
  const [end, setEnd] = useState('');
  const [kind, setKind] = useState('Indirect');
  const [taskId, setTaskId] = useState(tasks[0]?.id ?? '');
  const [note, setNote] = useState('');
  const [reason, setReason] = useState('');
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const save = async () => {
    setBusy(true); setErr(null);
    try {
      const body: Record<string, unknown> = { reason, userId: userId || undefined, action, note: note || null };
      if (action === 'add-clock' || action === 'add-segment') {
        if (!start || !end) throw new Error('Enter the start and the end.');
        body.startedAt = fromEt(start); body.endedAt = fromEt(end);
      }
      if (action === 'adjust-segment' && initial.segment) {
        body.segmentId = initial.segment.id;
        if (start) body.startedAt = fromEt(start);
        if (end) body.endedAt = fromEt(end);
      }
      if (action === 'void-segment') body.segmentId = initial.segment?.id;
      if (action === 'add-segment') { body.kind = kind; if (kind === 'Indirect') body.indirectValueId = taskId; }
      await post(`${S}/day-clock/corrections`, body);
      await qc.invalidateQueries({ queryKey: ['day-clock'] });
      await qc.invalidateQueries({ queryKey: ['day-clock-sheet'] });
      await qc.invalidateQueries({ queryKey: ['day-clock-history'] });
      onClose();
    } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  const actions = useMemo(() => [
    ['add-clock', 'Add a missed clock-in and clock-out'],
    ['add-segment', 'Add a missed task, break, or lunch'],
    ...(initial.segment ? [['adjust-segment', `Change ${initial.segment.name}`], ['void-segment', `Remove ${initial.segment.name}`]] : []),
  ] as [string, string][], [initial.segment]);
  return (
    <Dialog open onClose={busy ? undefined : onClose} fullWidth maxWidth="sm" data-testid="correction-dialog">
      <FormDialogTitle>Correct the day clock</FormDialogTitle>
      <DialogContent>
        <Stack gap={2} sx={{ mt: 1 }}>
          {err && <Alert severity="error">{err}</Alert>}
          <Typography variant="body2">Every correction needs a reason and is written to the audit trail in plain language. This does not change payroll.</Typography>
          <TextField select label="Person" value={userId} onChange={(e) => setUserId(e.target.value)}>
            {(users.data ?? [{ id: me?.id ?? '', displayName: me?.displayName ?? 'Me' }]).map((u) => <MenuItem key={u.id} value={u.id}>{u.displayName}</MenuItem>)}
          </TextField>
          <TextField select label="What to correct" value={action} onChange={(e) => setAction(e.target.value)} data-testid="correction-action">
            {actions.map(([k, label]) => <MenuItem key={k} value={k}>{label}</MenuItem>)}
          </TextField>
          {(action === 'add-clock' || action === 'add-segment' || action === 'adjust-segment') && <>
            <TextField type="datetime-local" label={action === 'adjust-segment' ? 'New start (blank keeps it)' : 'Start (Eastern time)'} value={start} onChange={(e) => setStart(e.target.value)} slotProps={{ inputLabel: { shrink: true } }} />
            <TextField type="datetime-local" label={action === 'adjust-segment' ? 'New end (blank keeps it)' : 'End (Eastern time)'} value={end} onChange={(e) => setEnd(e.target.value)} slotProps={{ inputLabel: { shrink: true } }} />
          </>}
          {action === 'add-segment' && <>
            <TextField select label="Kind" value={kind} onChange={(e) => setKind(e.target.value)}>
              <MenuItem value="Indirect">Indirect task</MenuItem><MenuItem value="Break">Break</MenuItem><MenuItem value="Lunch">Lunch</MenuItem>
            </TextField>
            {kind === 'Indirect' && <TextField select label="Task" value={taskId} onChange={(e) => setTaskId(e.target.value)}>{tasks.map((t) => <MenuItem key={t.id} value={t.id}>{t.label}</MenuItem>)}</TextField>}
            <TextField label="Note" value={note} onChange={(e) => setNote(e.target.value)} />
          </>}
          <TextField label="Reason" required value={reason} onChange={(e) => setReason(e.target.value)} helperText="Required. It is stored with the audit entry." slotProps={{ htmlInput: { 'data-testid': 'correction-reason' } }} />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>Cancel</Button>
        <Button variant="contained" disabled={busy || reason.trim().length < 3} data-testid="correction-save" onClick={save}>Save correction</Button>
      </DialogActions>
    </Dialog>
  );
}
