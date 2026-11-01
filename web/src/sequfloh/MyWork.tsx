import { useState, type KeyboardEvent } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Box, Button, Checkbox, Chip, Collapse, Dialog, DialogActions, DialogContent, DialogTitle, IconButton, List, ListItem, ListItemButton, ListItemText, MenuItem, Paper, Stack,
  TextField, ToggleButton, ToggleButtonGroup, Tooltip, Typography } from '@mui/material';
import { alpha } from '@mui/material/styles';
import PushPin from '@mui/icons-material/PushPin';
import PushPinOutlined from '@mui/icons-material/PushPinOutlined';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import EditOutlined from '@mui/icons-material/EditOutlined';
import EventOutlined from '@mui/icons-material/EventOutlined';
import ExpandMore from '@mui/icons-material/ExpandMore';
import Restore from '@mui/icons-material/Restore';
import WarningAmberOutlined from '@mui/icons-material/WarningAmberOutlined';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post, put } from '../api';
import { PanelHead, Readout } from '../components/Kit';
import { fmtDate, localDate } from '../components/Status';
import { ErrorState, Loading, Mono, S, SqfStatus } from './common';

export interface WorkItem {
  id: string; number: string; typeLabel: string; status: string; priority: string; dueAt?: string | null; workOrder?: string | null; partNumber?: string | null;
  description: string; assignedBy?: string | null; assigned: boolean; inProgress: boolean; clockedOn: boolean; overdue: boolean; constraints: string[];
}
interface MyWorkData { counts: { assigned: number; inProgress: number; overdue: number; constrained: number; dueThisWeek: number }; items: WorkItem[] }
type Filter = 'all' | 'overdue' | 'progress' | 'constraints';

function WorkRow({ w }: { w: WorkItem }) {
  return (
    <ListItemButton component={RouterLink} to={`/sequfloh/record/${w.id}`} data-testid="my-work-item" divider alignItems="flex-start"
      sx={(t) => (w.overdue ? { borderLeft: `3px solid ${t.palette.error.main}`, bgcolor: alpha(t.palette.error.main, t.palette.mode === 'dark' ? 0.12 : 0.04) } : { borderLeft: '3px solid transparent' })}>
      <ListItemText disableTypography
        primary={<Stack direction="row" gap={1} alignItems="center" flexWrap="wrap">
          <Mono>{w.number}</Mono>
          {w.workOrder && w.workOrder !== w.number && <Typography variant="body2" color="text.secondary">WO {w.workOrder}</Typography>}
          <SqfStatus status={w.status} />
          {w.overdue && <Chip size="small" color="error" label="Overdue" />}
          {w.inProgress && !w.overdue && <Chip size="small" color="primary" variant="outlined" label={w.clockedOn ? 'Clocked on' : 'In progress'} />}
          {w.priority !== 'Normal' && <Chip size="small" variant="outlined" label={w.priority} />}
        </Stack>}
        secondary={<Box sx={{ mt: 0.5 }}>
          <Typography variant="body2">{w.description || w.typeLabel}</Typography>
          <Typography variant="caption" color="text.secondary" component="div">
            {[w.typeLabel, w.partNumber, `Due ${w.dueAt ? fmtDate(w.dueAt) : '—'}`, w.assignedBy ? `Assigned by ${w.assignedBy}` : null].filter(Boolean).join(' · ')}
          </Typography>
          {w.constraints.map((c) => <Chip key={c} size="small" color="warning" variant="outlined" icon={<WarningAmberOutlined />} label={c} sx={{ mt: 0.5, mr: 0.5 }} />)}
        </Box>} />
    </ListItemButton>
  );
}

/** The signed-in person's open work: assigned, in progress, overdue and held items, overdue first, then by due date. */
export function MyWorkPanel({ data }: { data: MyWorkData }) {
  const [filter, setFilter] = useState<Filter>('all');
  const c = data.counts;
  const rows = data.items.filter((w) => filter === 'all' || (filter === 'overdue' && w.overdue) || (filter === 'progress' && w.inProgress) || (filter === 'constraints' && w.constraints.length > 0));
  const empty = data.items.length === 0 ? 'Nothing is assigned to you right now. New work orders assigned to you show here.' : 'Nothing matches this filter.';
  return (
    <Paper variant="outlined" sx={{ overflow: 'hidden' }} component="section" aria-label="My work" data-testid="my-work">
      <PanelHead title="Assigned to me" />
      <Box sx={{ px: 2, py: 1, overflowX: 'auto', borderBottom: 1, borderColor: 'divider' }}>
        <ToggleButtonGroup size="small" exclusive value={filter} onChange={(_, v) => v && setFilter(v)} aria-label="Filter my work" sx={{ flexWrap: 'nowrap', '& .MuiToggleButton-root': { whiteSpace: 'nowrap' } }}>
          <ToggleButton value="all">All</ToggleButton>
          <ToggleButton value="overdue">Overdue</ToggleButton>
          <ToggleButton value="progress">In progress</ToggleButton>
          <ToggleButton value="constraints">Constraints</ToggleButton>
        </ToggleButtonGroup>
      </Box>
      {rows.length === 0 ? <Typography variant="body2" color="text.secondary" sx={{ px: 2, py: 3 }} data-testid="my-work-empty">{empty}</Typography> :
        <List dense disablePadding>{rows.map((w) => <WorkRow key={w.id} w={w} />)}</List>}
      <Typography variant="caption" color="text.secondary" component="p" sx={{ px: 2, py: 1 }}>{c.assigned} assigned · {c.dueThisWeek} due in the next 7 days</Typography>
    </Paper>
  );
}

export interface Note { id: string; text: string; isDone: boolean; doneAt?: string | null; remindOn?: string | null; pinned: boolean; priority: string; trackingRecordId?: string | null;
  link?: { id: string; number: string; workOrder?: string | null } | null }

const when = (d?: string | null) => { if (!d) return null; const t = localDate(); return d < t ? 'overdue' : d === t ? 'today' : 'upcoming'; };

function NoteEditor({ note, work, onClose }: { note: Note; work: WorkItem[]; onClose: (saved: boolean) => void }) {
  const [text, setText] = useState(note.text);
  const [remindOn, setRemindOn] = useState(note.remindOn ?? '');
  const [link, setLink] = useState(note.trackingRecordId ?? '');
  const [priority, setPriority] = useState(note.priority);
  const [busy, setBusy] = useState(false);
  const save = async () => {
    setBusy(true);
    try {
      await put(`${S}/my-notes/${note.id}`, { text, priority, remindOn: remindOn || null, clearRemindOn: !remindOn, trackingRecordId: link || null, clearLink: !link });
      onClose(true);
    } finally { setBusy(false); }
  };
  const options = work.some((w) => w.id === link) || !note.link ? work : [...work, { id: note.link.id, number: note.link.number, workOrder: note.link.workOrder } as WorkItem];
  return (
    <Dialog open onClose={() => onClose(false)} maxWidth="xs" fullWidth>
      <DialogTitle>Edit note</DialogTitle>
      <DialogContent>
        <Stack gap={2} sx={{ pt: 1 }}>
          <TextField label="Note" value={text} onChange={(e) => setText(e.target.value)} multiline minRows={2} inputProps={{ maxLength: 1000 }} autoFocus />
          <TextField label="Remind on" type="date" value={remindOn} onChange={(e) => setRemindOn(e.target.value)} InputLabelProps={{ shrink: true }} />
          <TextField select label="Work order" value={link} onChange={(e) => setLink(e.target.value)}>
            <MenuItem value="">None</MenuItem>
            {options.map((w) => <MenuItem key={w.id} value={w.id}>{w.number}{w.workOrder ? ` · WO ${w.workOrder}` : ''}</MenuItem>)}
          </TextField>
          <ToggleButtonGroup size="small" exclusive value={priority} onChange={(_, v) => v && setPriority(v)} aria-label="Priority">
            <ToggleButton value="Normal">Normal</ToggleButton>
            <ToggleButton value="Important">Important</ToggleButton>
          </ToggleButtonGroup>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={() => onClose(false)}>Cancel</Button>
        <Button variant="contained" disabled={busy || !text.trim()} onClick={save}>Save</Button>
      </DialogActions>
    </Dialog>
  );
}

/** My Notes and Reminders: private to the signed-in person. */
export function NotesPanel({ work }: { work: WorkItem[] }) {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['sqf-my-notes'], queryFn: () => get<Note[]>(`${S}/my-notes`) });
  const [text, setText] = useState('');
  const [editing, setEditing] = useState<Note | null>(null);
  const [deleting, setDeleting] = useState<Note | null>(null);
  const [showDone, setShowDone] = useState(false);
  const refresh = () => qc.invalidateQueries({ queryKey: ['sqf-my-notes'] });
  const change = async (n: Note, body: object) => { await put(`${S}/my-notes/${n.id}`, body); await refresh(); };
  const add = async (e: KeyboardEvent) => {
    if (e.key !== 'Enter' || !text.trim()) return;
    e.preventDefault();
    await post(`${S}/my-notes`, { text }); setText(''); await refresh();
  };
  const notes = q.data ?? [];
  const open = notes.filter((n) => !n.isDone);
  const done = notes.filter((n) => n.isDone);
  const tone = { overdue: 'error', today: 'warning', upcoming: 'default' } as const;
  const row = (n: Note) => {
    const w = when(n.remindOn);
    return (
      <ListItem key={n.id} divider data-testid="note" alignItems="flex-start" sx={(t) => ({ pr: 13, borderLeft: `3px solid ${n.priority === 'Important' ? t.palette.warning.main : 'transparent'}` })}
        secondaryAction={<Stack direction="row">
          {n.isDone ? <Tooltip title="Restore"><IconButton size="small" aria-label="Restore note" onClick={() => change(n, { isDone: false })}><Restore fontSize="small" /></IconButton></Tooltip> : <>
            <Tooltip title={n.pinned ? 'Unpin' : 'Pin to top'}><IconButton size="small" aria-label={n.pinned ? 'Unpin note' : 'Pin note'} onClick={() => change(n, { pinned: !n.pinned })}>
              {n.pinned ? <PushPin fontSize="small" color="primary" /> : <PushPinOutlined fontSize="small" />}</IconButton></Tooltip>
            <Tooltip title="Edit"><IconButton size="small" aria-label="Edit note" onClick={() => setEditing(n)}><EditOutlined fontSize="small" /></IconButton></Tooltip>
          </>}
          <Tooltip title="Delete"><IconButton size="small" aria-label="Delete note" onClick={() => setDeleting(n)}><DeleteOutline fontSize="small" /></IconButton></Tooltip>
        </Stack>}>
        <Checkbox size="small" edge="start" checked={n.isDone} onChange={(e) => change(n, { isDone: e.target.checked })} inputProps={{ 'aria-label': n.isDone ? 'Mark not done' : 'Mark done' }} sx={{ mt: -0.5 }} />
        <ListItemText disableTypography
          primary={<Typography variant="body2" sx={{ textDecoration: n.isDone ? 'line-through' : 'none', color: n.isDone ? 'text.secondary' : 'text.primary', wordBreak: 'break-word' }}>{n.text}</Typography>}
          secondary={(n.remindOn || n.link || n.priority === 'Important') && !n.isDone ? <Stack direction="row" gap={0.5} flexWrap="wrap" sx={{ mt: 0.5 }}>
            {n.remindOn && w && <Chip size="small" icon={<EventOutlined />} color={tone[w]} variant={w === 'upcoming' ? 'outlined' : 'filled'}
              label={w === 'today' ? 'Today' : w === 'overdue' ? `Overdue · ${fmtDate(n.remindOn)}` : fmtDate(n.remindOn)} />}
            {n.link && <Chip size="small" clickable component={RouterLink} to={`/sequfloh/record/${n.link.id}`} label={n.link.number} variant="outlined" color="primary" />}
            {n.priority === 'Important' && <Chip size="small" label="Important" color="warning" variant="outlined" />}
          </Stack> : null} />
      </ListItem>
    );
  };
  return (
    <Paper variant="outlined" sx={{ overflow: 'hidden' }} component="section" aria-label="My notes and reminders" data-testid="my-notes">
      <PanelHead title="My Notes & Reminders" />
      <Box sx={{ px: 2, pt: 1.5, pb: 1 }}>
        <TextField fullWidth size="small" placeholder="Add a note and press Enter" value={text} onChange={(e) => setText(e.target.value)} onKeyDown={add}
          inputProps={{ maxLength: 1000, 'aria-label': 'Add a note' }} helperText="Only you can see your notes." />
      </Box>
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : <>
        {open.length === 0 ? <Typography variant="body2" color="text.secondary" sx={{ px: 2, pb: 2 }} data-testid="notes-empty">No notes yet. Jot down a reminder, a to-do or something to follow up on.</Typography> :
          <List dense disablePadding>{open.map(row)}</List>}
        {done.length > 0 && <>
          <Button size="small" onClick={() => setShowDone(!showDone)} endIcon={<ExpandMore sx={{ transform: showDone ? 'rotate(180deg)' : 'none', transition: '0.2s' }} />} sx={{ m: 1 }}>Completed ({done.length})</Button>
          <Collapse in={showDone}><List dense disablePadding>{done.map(row)}</List></Collapse>
        </>}
      </>}
      {editing && <NoteEditor note={editing} work={work} onClose={async (saved) => { setEditing(null); if (saved) await refresh(); }} />}
      {deleting && <Dialog open onClose={() => setDeleting(null)} maxWidth="xs" fullWidth>
        <DialogTitle>Delete note?</DialogTitle>
        <DialogContent><Typography variant="body2">"{deleting.text}" will be deleted. This can't be undone.</Typography></DialogContent>
        <DialogActions>
          <Button onClick={() => setDeleting(null)}>Cancel</Button>
          <Button color="error" variant="contained" onClick={async () => { await del(`${S}/my-notes/${deleting.id}`); setDeleting(null); await refresh(); }}>Delete</Button>
        </DialogActions>
      </Dialog>}
    </Paper>
  );
}

/** My Work: counts, the person's open work and their private notes, at the top of SEQUFLOH Home. */
export function MyWork() {
  const q = useQuery({ queryKey: ['sqf-my-work'], queryFn: () => get<MyWorkData>(`${S}/my-work`), refetchInterval: 60_000 });
  const notes = useQuery({ queryKey: ['sqf-my-notes'], queryFn: () => get<Note[]>(`${S}/my-notes`) });
  if (q.isLoading) return <Loading />;
  if (q.error) return <ErrorState error={q.error} retry={q.refetch} />;
  const d = q.data!;
  const today = (notes.data ?? []).filter((n) => !n.isDone && n.remindOn && n.remindOn <= localDate()).length;
  return (
    <Box data-testid="my-work-page">
      <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: '1fr 1fr', md: 'repeat(5, minmax(0, 1fr))' }, mb: 2 }}>
        <Readout label="Assigned to me" value={d.counts.assigned} color="primary.main" />
        <Readout label="In progress" value={d.counts.inProgress} color="primary.main" />
        <Readout label="Overdue" value={d.counts.overdue} color={d.counts.overdue ? 'error.main' : 'primary.main'} />
        <Readout label="With constraints" value={d.counts.constrained} color={d.counts.constrained ? 'warning.main' : 'primary.main'} />
        <Readout label="Reminders due" value={today} color={today ? 'warning.main' : 'primary.main'} />
      </Box>
      <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: 'minmax(0, 1fr)', lg: 'minmax(0, 2fr) minmax(0, 1fr)' }, alignItems: 'start' }}>
        <MyWorkPanel data={d} />
        <NotesPanel work={d.items} />
      </Box>
    </Box>
  );
}
