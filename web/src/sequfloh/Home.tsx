import { Link as RouterLink } from 'react-router-dom';
import { Box, Button, Link, List, ListItemButton, ListItemText, Paper, Stack, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { get } from '../api';
import { useAuth } from '../auth';
import { fmtDateTime } from '../components/Status';
import { Empty, S, SqfPage, useSqf } from './common';
import { NewInspectionRequestButton } from './InspectionRequests';
import { Dashboard } from './Dashboard';

interface Item { id: string; itemType: string; path: string; label: string; lastOpenedAt: string }

/** SEQUFLOH Home: permission-aware dashboard, quick actions, pinned and recent SEQUFLOH items. */
export function SequflohHome() {
  const { can } = useAuth();
  const ctx = useSqf();
  const items = useQuery({ queryKey: ['sqf-my-items'], queryFn: () => get<{ recent: Item[]; favorites: Item[] }>(`${S}/my-items`) });
  const quick: [string, string, string][] = [
    ['New work instruction', '/sequfloh/instructions/new', 'sequfloh.create_work_instruction'],
    ['Active Station', '/sequfloh/execution/active', 'sequfloh.view_live_execution'],
    ['Dispatch Queue', '/sequfloh/execution/dispatch', 'sequfloh.view_live_execution'],
    ['New production task', '/sequfloh/tracking/new?type=Production', 'sequfloh.create_production_task'],
    ['New repair record', '/sequfloh/tracking/new?type=Repair', 'sequfloh.create_repair_record'],
    ['Parts Lists', '/sequfloh/parts-lists', 'sequfloh.access'],
    ['Reports', '/sequfloh/reports/task-performance', 'sequfloh.view_reports'],
    ['Search', '/sequfloh/search', 'sequfloh.access'],
  ];
  const List2 = ({ title, rows, empty }: { title: string; rows?: Item[]; empty: string }) => (
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Typography variant="h3" sx={{ mb: 1 }}>{title}</Typography>
      {(rows ?? []).length === 0 ? <Typography variant="body2" color="text.secondary">{empty}</Typography> :
        <List dense disablePadding>{rows!.map((i) => <ListItemButton key={i.id} component={RouterLink} to={i.path}><ListItemText primary={i.label} secondary={`${i.itemType} · ${fmtDateTime(i.lastOpenedAt)}`} /></ListItemButton>)}</List>}
    </Paper>
  );
  return (
    <SqfPage title={ctx.data?.module.displayName ?? 'SEQUFLOH'} crumbs={[{ label: 'SEQUFLOH Home' }]} permission="sequfloh.view_dashboard"
      description={`${ctx.data?.module.subtitle ?? 'Enterprise Execution Engine'} — ${ctx.data?.module.description ?? ''}`}>
      <Stack direction="row" gap={1} flexWrap="wrap" sx={{ mb: 3 }} aria-label="Quick actions">
        <NewInspectionRequestButton />
        {quick.filter(([, , p]) => can(p)).map(([l, to]) => <Button key={to} component={RouterLink} to={to} variant="outlined" size="small">{l}</Button>)}
      </Stack>
      <Dashboard />
      <Box sx={{ display: 'grid', gap: 2, mt: 3, gridTemplateColumns: { xs: 'minmax(0, 1fr)', md: '1fr 1fr' } }}>
        <List2 title="Pinned" rows={items.data?.favorites} empty="Pin SEQUFLOH pages and records with the pin button in their header." />
        <List2 title="Recently opened" rows={items.data?.recent} empty="SEQUFLOH pages and records you open appear here." />
      </Box>
      {!ctx.data && !ctx.isLoading && <Empty text="SEQUFLOH context could not be loaded." />}
      <Typography variant="caption" color="text.secondary" component="p" sx={{ mt: 3 }}>
        Designed to support controlled records, traceability, workflow execution, and audit-history requirements. Organizational compliance depends on configuration, validation, procedures,
        security, deployment, training, implementation, and organizational use. <Link component={RouterLink} to="/sequfloh/admin/configuration">About SEQUFLOH</Link>
      </Typography>
    </SqfPage>
  );
}
