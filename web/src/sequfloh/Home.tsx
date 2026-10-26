import { useState, type ReactNode } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, IconButton, Link, List, ListItem, ListItemButton, ListItemText, Paper, Stack, Typography } from '@mui/material';
import ArrowDownward from '@mui/icons-material/ArrowDownward';
import ArrowUpward from '@mui/icons-material/ArrowUpward';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, put } from '../api';
import { useAuth } from '../auth';
import { fmtDateTime } from '../components/Status';
import { Empty, S, SqfPage, useSqf } from './common';
import { NewInspectionRequestButton } from './InspectionRequests';
import { Dashboard } from './Dashboard';
import { NewWorkOrderButton } from './Tracking';

interface Section { key: string; visible: boolean }
const SECTIONS: [string, string][] = [['quick', 'Quick actions'], ['dashboard', 'Dashboard'], ['pinned', 'Pinned'], ['recent', 'Recently opened'], ['projects', 'Open projects']];
const DEFAULT: Section[] = SECTIONS.map(([key]) => ({ key, visible: true }));

/** Lets the person show, hide and reorder their own Home sections. */
function CustomizeHome({ current, onClose }: { current: Section[]; onClose: () => void }) {
  const qc = useQueryClient();
  const [rows, setRows] = useState(current);
  const [busy, setBusy] = useState(false);
  const label = (k: string) => SECTIONS.find(([x]) => x === k)?.[1] ?? k;
  const move = (i: number, d: number) => { const n = [...rows]; [n[i], n[i + d]] = [n[i + d], n[i]]; setRows(n); };
  const done = async (fn: () => Promise<unknown>) => { setBusy(true); try { await fn(); await qc.invalidateQueries({ queryKey: ['sqf-my-home'] }); onClose(); } finally { setBusy(false); } };
  return (
    <Dialog open onClose={onClose} maxWidth="xs" fullWidth>
      <DialogTitle>Customize Home</DialogTitle>
      <DialogContent>
        <List dense data-testid="home-sections">{rows.map((r, i) => (
          <ListItem key={r.key} disableGutters secondaryAction={<>
            <IconButton size="small" aria-label={`Move ${label(r.key)} up`} disabled={i === 0} onClick={() => move(i, -1)}><ArrowUpward fontSize="small" /></IconButton>
            <IconButton size="small" aria-label={`Move ${label(r.key)} down`} disabled={i === rows.length - 1} onClick={() => move(i, 1)}><ArrowDownward fontSize="small" /></IconButton>
          </>}>
            <Checkbox size="small" checked={r.visible} onChange={(e) => setRows(rows.map((x) => (x.key === r.key ? { ...x, visible: e.target.checked } : x)))} inputProps={{ 'aria-label': `Show ${label(r.key)}` }} />
            <ListItemText primary={label(r.key)} />
          </ListItem>))}
        </List>
      </DialogContent>
      <DialogActions>
        <Button disabled={busy} onClick={() => done(() => del(`${S}/my-home`))} sx={{ mr: 'auto' }}>Reset to default</Button>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={busy} onClick={() => done(() => put(`${S}/my-home`, rows))}>Save</Button>
      </DialogActions>
    </Dialog>
  );
}

interface OpenProject { id: string; number: string; name: string; workOrders: number; inspectionRequests: number; items: number; hours: number; totalCost: number }

/** Open projects with their linked item count, man hours and total cost; each row opens the project. */
function OpenProjects() {
  const q = useQuery({ queryKey: ['sqf-projects-open-summary'], queryFn: () => get<OpenProject[]>(`${S}/projects/open-summary`) });
  const rows = q.data ?? [];
  return (
    <Paper variant="outlined" sx={{ p: 2 }} data-testid="home-open-projects">
      <Typography variant="h3" sx={{ mb: 1 }}>Open projects</Typography>
      {q.isLoading ? <Typography variant="body2" color="text.secondary">Loading…</Typography> : rows.length === 0 ?
        <Typography variant="body2" color="text.secondary">No open projects. <Link component={RouterLink} to="/sequfloh/projects">Go to Projects</Link></Typography> :
        <List dense disablePadding>{rows.map((p) => (
          <ListItemButton key={p.id} component={RouterLink} to={`/sequfloh/project/${p.id}`}>
            <ListItemText primary={`${p.number} · ${p.name}`}
              secondary={`${p.items} linked item${p.items === 1 ? '' : 's'} · ${p.hours.toLocaleString(undefined, { maximumFractionDigits: 2 })} man hours · ${p.totalCost.toLocaleString(undefined, { style: 'currency', currency: 'USD' })}`} />
          </ListItemButton>))}
        </List>}
    </Paper>
  );
}

interface Item { id: string; itemType: string; path: string; label: string; lastOpenedAt: string }

/** SEQUFLOH Home: permission-aware dashboard, quick actions, pinned and recent SEQUFLOH items. */
export function SequflohHome() {
  const { can } = useAuth();
  const ctx = useSqf();
  const home = useQuery({ queryKey: ['sqf-my-home'], queryFn: () => get<{ sections: Section[] | null }>(`${S}/my-home`) });
  const [customizing, setCustomizing] = useState(false);
  const saved = home.data?.sections ?? DEFAULT;
  const sections = [...saved.filter((x) => SECTIONS.some(([k]) => k === x.key)), ...DEFAULT.filter((d) => !saved.some((x) => x.key === d.key))];
  const items = useQuery({ queryKey: ['sqf-my-items'], queryFn: () => get<{ recent: Item[]; favorites: Item[] }>(`${S}/my-items`) });
  const quick: [string, string, string][] = [
    ['New work instruction', '/sequfloh/instructions/new', 'sequfloh.create_work_instruction'],
    ['Active Station', '/sequfloh/execution/active', 'sequfloh.view_live_execution'],
    ['Dispatch Queue', '/sequfloh/execution/dispatch', 'sequfloh.view_live_execution'],
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
  const parts: Record<string, ReactNode> = {
    quick: <Stack direction="row" gap={1} flexWrap="wrap" aria-label="Quick actions">
      <NewWorkOrderButton />
      <NewInspectionRequestButton />
      {quick.filter(([, , p]) => can(p)).map(([l, to]) => <Button key={to} component={RouterLink} to={to} variant="outlined" size="small">{l}</Button>)}
    </Stack>,
    dashboard: <Dashboard />,
    pinned: <List2 title="Pinned" rows={items.data?.favorites} empty="Pin SEQUFLOH pages and records with the pin button in their header." />,
    recent: <List2 title="Recently opened" rows={items.data?.recent} empty="SEQUFLOH pages and records you open appear here." />,
    projects: can('sequfloh.access') ? <OpenProjects /> : null,
  };
  return (
    <SqfPage title={ctx.data?.module.displayName ?? 'SEQUFLOH'} crumbs={[{ label: 'SEQUFLOH Home' }]} permission="sequfloh.view_dashboard"
      description={`${ctx.data?.module.subtitle ?? 'Enterprise Execution Engine'} — ${ctx.data?.module.description ?? ''}`}>
      <Stack direction="row" justifyContent="flex-end" sx={{ mb: 1 }}>
        <Button size="small" onClick={() => setCustomizing(true)} data-testid="customize-home">Customize Home</Button>
      </Stack>
      {sections.filter((x) => x.visible && parts[x.key]).map((x) => <Box key={x.key} sx={{ mb: 3 }}>{parts[x.key]}</Box>)}
      {customizing && <CustomizeHome current={sections} onClose={() => setCustomizing(false)} />}
      {!ctx.data && !ctx.isLoading && <Empty text="SEQUFLOH context could not be loaded." />}
      <Typography variant="caption" color="text.secondary" component="p" sx={{ mt: 3 }}>
        Designed to support controlled records, traceability, workflow execution, and audit-history requirements. Organizational compliance depends on configuration, validation, procedures,
        security, deployment, training, implementation, and organizational use. <Link component={RouterLink} to="/sequfloh/admin/configuration">About SEQUFLOH</Link>
      </Typography>
    </SqfPage>
  );
}
