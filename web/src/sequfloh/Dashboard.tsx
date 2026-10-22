import { Link as RouterLink } from 'react-router-dom';
import { Box, List, ListItemButton, ListItemText, Paper, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { get } from '../api';
import { fmtDateTime } from '../components/Status';
import { ErrorState, Loading, S, words } from './common';
import { MiniList, Stat, Stats } from './Personnel';

interface Mini { id: string; number: string; revision?: string; title?: string; type: string; status: string; result?: string; partNumber?: string; releasedAt?: string; updatedAt?: string }
interface Home {
  live: { awaitingDispatch: number; active: number; paused: number; overdue: number; validationInProgress: number; faiInProgress: number; repairInProgress: number; productionInProgress: number;
    failed: number; assignedToMe: number; myWork: Mini[] } | null;
  instructions: { drafts: number; released: number; recentlyReleased: Mini[]; recentlyRevised: Mini[] };
  recentActivity: { id: string; at: string; number: string; kind: string; text: string; user: string }[];
}

/** SEQUFLOH Home dashboard: only the sections and records the signed-in user is allowed to see (the server leaves out the rest). */
export function Dashboard() {
  const q = useQuery({ queryKey: ['sqf-dash', 'home'], queryFn: () => get<Home>(`${S}/dashboard`), refetchInterval: 60_000 });
  if (q.isLoading) return <Loading />;
  if (q.error) return <ErrorState error={q.error} retry={q.refetch} />;
  const d = q.data!;
  return <>
    {d.live && <Stats>
      <Stat label="Tasks awaiting dispatch" value={d.live.awaitingDispatch} to="/sequfloh/execution/dispatch" />
      <Stat label="Active tasks" value={d.live.active} to="/sequfloh/execution/active" />
      <Stat label="Paused tasks" value={d.live.paused} to="/sequfloh/execution/paused" />
      <Stat label="Overdue tasks" value={d.live.overdue} to="/sequfloh/execution/overdue" />
      <Stat label="Validation records in progress" value={d.live.validationInProgress} to="/sequfloh/tracking/validation" />
      <Stat label="FAI records in progress" value={d.live.faiInProgress} to="/sequfloh/tracking/fai" />
      <Stat label="Repair records in progress" value={d.live.repairInProgress} to="/sequfloh/tracking/repair" />
      <Stat label="Production tasks in progress" value={d.live.productionInProgress} to="/sequfloh/tracking/production" />
      <Stat label="Failed records" value={d.live.failed} to="/sequfloh/execution/completed?status=Failed" />
      <Stat label="Assigned to me" value={d.live.assignedToMe} to="/sequfloh/execution/mine" />
    </Stats>}
    <Stats>
      <Stat label="Draft instructions" value={d.instructions.drafts} to="/sequfloh/instructions/draft" />
      <Stat label="Released instructions" value={d.instructions.released} to="/sequfloh/instructions/released" />
    </Stats>
    <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: 'minmax(0, 1fr)', md: '1fr 1fr' } }}>
      {d.live && <MiniList title="Assigned work" rows={d.live.myWork} empty="Nothing is assigned to you." kind="record" />}
      <MiniList title="Recently released instructions" rows={d.instructions.recentlyReleased} empty="No instructions released yet." kind="instruction" />
      <MiniList title="Recently revised instructions" rows={d.instructions.recentlyRevised} empty="No revised instructions released yet." kind="instruction" />
      <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Recent activity">
        <Typography variant="h3" sx={{ mb: 1 }}>Recent activity</Typography>
        {d.recentActivity.length === 0 ? <Typography variant="body2" color="text.secondary">No activity yet.</Typography> :
          <List dense disablePadding>{d.recentActivity.map((a, i) => <ListItemButton key={i} component={RouterLink} to={a.kind === 'Tracking' ? `/sequfloh/record/${a.id}?tab=events` : `/sequfloh/instruction/${a.id}?tab=history`}>
            <ListItemText primary={`${a.number} — ${words(a.text.replace(/(^|-)([a-z])/g, (_, __, c: string) => c.toUpperCase()))}`} secondary={`${a.kind} · ${a.user} · ${fmtDateTime(a.at)}`} /></ListItemButton>)}</List>}
      </Paper>
    </Box>
  </>;
}
