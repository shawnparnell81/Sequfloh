import { setupOptions } from '../components/SetupLink';
import { FormDialogTitle } from '../components/FullScreen';
import { useEffect, useMemo, useState } from 'react';
import { Link as RouterLink, Navigate, useParams, useSearchParams } from 'react-router-dom';
import { Alert, Box, Button, Checkbox, Chip, Dialog, DialogActions, DialogContent, FormControlLabel, IconButton, Link, MenuItem, Paper, Stack, Switch, Table, TableBody, TableCell,
  TableContainer, TableHead, TableRow, TextField, Tooltip, Typography } from '@mui/material';
import ArrowUpward from '@mui/icons-material/ArrowUpward';
import ArrowDownward from '@mui/icons-material/ArrowDownward';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put } from '../api';
import { useAuth } from '../auth';
import { useSites } from '../components/Kit';
import { fmtDateTime } from '../components/Status';
import type { ValidationForms } from './Tracking';
import { Confirm, Empty, ErrorState, Loading, Mono, S, SqfPage, SqfStatus, useUnsaved, words } from './common';

// ------------------------------------------------------------------ settings
type FieldType = 'bool' | 'text' | 'multiline' | 'number' | 'csv' | 'lines' | 'site' | 'department' | 'select' | 'permissions';
interface Field { key: string; label: string; type: FieldType; options?: string[]; helper?: string }
const sections: Record<string, { title: string; description: string; fields: (Field & { section?: string })[]; lists?: string[]; also?: string }> = {
  general: { title: 'General Settings', description: 'Module identity, landing page, paging, default site and department, recent items and favorites.', fields: [
    { key: 'moduleEnabled', label: 'Module enabled', type: 'bool', helper: 'When off, only SEQUFLOH settings administrators can open the module.' },
    { key: 'displayName', label: 'Module display name', type: 'text' }, { key: 'description', label: 'Module description', type: 'multiline' },
    { key: 'defaultLandingPage', label: 'Default landing page', type: 'select', options: ['/sequfloh', '/sequfloh/execution/active', '/sequfloh/execution/mine', '/sequfloh/instructions/all', '/sequfloh/tracking/open'] },
    { key: 'recordsPerPage', label: 'Records per page', type: 'number' }, { key: 'defaultSiteId', label: 'Default site', type: 'site' }, { key: 'defaultDepartmentId', label: 'Default department', type: 'department' },
    { key: 'recentItemsLimit', label: 'Recent-items limit', type: 'number' }, { key: 'favoritesEnabled', label: 'Favorites enabled', type: 'bool' }] },
  workInstructions: { title: 'Work Instruction Settings', description: 'Effective dates, revision and training requirements, retention and print footer. Who may release is set by permissions in Module Access.', lists: ['work-instruction-types', 'lifecycle-statuses', 'lifecycle-actions', 'document-types'], fields: [
    { key: 'effectiveDateRequired', label: 'Effective date required for release', type: 'bool' }, { key: 'revisionReasonRequired', label: 'Reason required for a new revision', type: 'bool' },
    { key: 'trainingAcknowledgementRequired', label: 'Training acknowledgement required', type: 'bool' }, { key: 'obsoleteRetentionYears', label: 'Obsolete-document retention (years)', type: 'number' },
    { key: 'printFooter', label: 'Print footer', type: 'multiline' }] },
  tracking: { title: 'Tracking Settings', description: 'Variance thresholds, default benchmark times, and overdue thresholds.', lists: ['activity-statuses', 'priorities', 'cancellation-reasons', 'status-tags', 'discovery-methods', 'inspection-reasons', 'inspection-priorities', 'inspection-sub-types'], fields: [
    { key: 'varianceWarningPercent', label: 'Variance warning threshold (%)', type: 'number' }, { key: 'varianceCriticalPercent', label: 'Variance critical threshold (%)', type: 'number' },
    { key: 'defaultMinimumMinutes', label: 'Default minimum time (minutes)', type: 'number' }, { key: 'defaultMaximumMinutes', label: 'Default maximum time (minutes)', type: 'number' },
    { key: 'overdueThresholdHours', label: 'Overdue threshold (hours after due date)', type: 'number' }, { key: 'approachingDueHours', label: 'Approaching due date warning (hours)', type: 'number' },
    { key: 'varianceReasonRequired', label: 'Variance reason required outside the benchmark range', type: 'bool' }] },
  validation: { title: 'Validation / FAI Settings', description: 'Evidence requirements for Validation / FAI work orders.', also: 'fai', lists: ['validation-types', 'validation-results', 'activity-statuses', 'fai-types', 'inspection-results', 'characteristic-results'], fields: [
    { key: 'evidenceRequired', label: 'Evidence required for each result', type: 'bool' },
    { key: 'evidenceRequiredForFailures', label: 'Evidence required for failed characteristics', type: 'bool', section: 'fai' }] },
  repair: { title: 'Repair / Rework Settings', description: 'Dropdowns used on Repair / Rework work orders.', lists: ['repair-types', 'repair-categories', 'repair-descriptions', 'repair-test-results', 'repair-failure-reasons', 'qualification-labels'], fields: [] },
  notifications: { title: 'Notification Settings', description: 'SEQUFLOH notifications sent through the in-app notification system.', fields: [
    { key: 'assignedWork', label: 'Assigned work', type: 'bool' }, { key: 'overdueWork', label: 'Overdue work', type: 'bool' }, { key: 'failedRecords', label: 'Failed records requiring attention', type: 'bool' },
    { key: 'releasedInstructions', label: 'Released instructions', type: 'bool' }, { key: 'supersededInstructions', label: 'Superseded instructions', type: 'bool' },
    { key: 'approachingDueDates', label: 'Approaching due dates', type: 'bool' }] },
  display: { title: 'Display Preferences', description: 'Compact tables, dashboard cards, default filters and folder view, status colours.', fields: [
    { key: 'compactTables', label: 'Compact tables', type: 'bool' },
    { key: 'openFormsInNewWindow', label: 'Fill out a copy opens the form in a new window', type: 'bool', helper: 'Techs get the form editor on its own, with nothing else on the screen.' }, { key: 'dashboardCards', label: 'Visible dashboard cards (comma separated)', type: 'csv' },
    { key: 'defaultTrackingFilter', label: 'Default tracking filter', type: 'select', options: ['open', 'all', 'completed'] },
    { key: 'defaultFolderView', label: 'Default folder view', type: 'select', options: ['current', 'all'] },
    { key: 'statusColors', label: 'Status colours (status=colour per line: default, primary, success, warning, error, info)', type: 'lines' }] },
  export: { title: 'Export Settings', description: 'File names, date format, organization heading, included columns and report titles.', fields: [
    { key: 'filenamePattern', label: 'Filename pattern ({report}, {date}, {user})', type: 'text' }, { key: 'dateFormat', label: 'Export date format', type: 'text' },
    { key: 'organizationHeading', label: 'Organization heading (blank: organization name)', type: 'text' },
    { key: 'reportTitles', label: 'Default report titles (report=title per line)', type: 'lines' },
    { key: 'includedColumns', label: 'Included columns (report=column,column per line; blank: all)', type: 'lines' }] },
  security: { title: 'Security Settings', description: 'Override reasons, export protection and suggested SEQUFLOH permission sets for existing roles.', fields: [
    { key: 'requireReasonForOverride', label: 'Reason required for administrative overrides', type: 'bool' }, { key: 'exportsRequirePermission', label: 'Exports require sequfloh.export_records', type: 'bool' }] },
};

type Obj = Record<string, unknown>;
const toLines = (v: unknown) => Object.entries((v ?? {}) as Record<string, unknown>).map(([k, x]) => `${k}=${Array.isArray(x) ? x.join(',') : String(x)}`).join('\n');
const fromLines = (s: string, arrays: boolean) => Object.fromEntries(s.split('\n').map((l) => l.trim()).filter((l) => l.includes('=')).map((l) => {
  const [k, ...rest] = l.split('='); const v = rest.join('=').trim(); return [k.trim(), arrays ? v.split(',').map((x) => x.trim()).filter(Boolean) : v];
}));

interface Settings { [k: string]: unknown; rowVersion: number }
export const useSettings = () => useQuery({ queryKey: ['sqf-settings'], queryFn: () => get<Settings>(`${S}/settings`) });

export function SettingsPage() {
  const { section = 'general' } = useParams();
  if (section === 'fai') return <Navigate to="/sequfloh/settings/validation" replace />;
  if (section === 'numbering') return <NumberingPage />;
  if (section === 'validation-forms') return <ValidationFormsPage />;
  return <SettingsSection key={section} section={section} />;
}

function SettingsSection({ section }: { section: string }) {
  const def = sections[section];
  const q = useSettings();
  const qc = useQueryClient();
  const sites = useSites();
  const perms = useQuery({ queryKey: ['permission-catalog'], queryFn: () => get<string[]>('/api/admin/roles/permissions').catch(() => [] as string[]) });
  const [draft, setDraft] = useState<Obj | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const guard = useUnsaved(!!draft);
  const crumbs = [{ label: 'SEQUFLOH Settings' }, { label: def?.title ?? section }];
  if (!def) return <SqfPage title="Settings" crumbs={crumbs}><Alert severity="warning">Unknown settings area.</Alert></SqfPage>;
  const stored = q.data?.[section] as Obj | undefined;
  const current = (draft ?? (stored && def?.also ? { ...(q.data?.[def.also] as Obj), ...stored } : stored)) ?? null;
  const set = (k: string, v: unknown) => { setSaved(false); setDraft({ ...(current ?? {}), [k]: v }); };
  const save = async () => {
    setError(null);
    try {
      if (def.also && current) {
        // one page for two stored sections: each field is saved to the section it belongs to
        const otherKeys = def.fields.filter((x) => x.section).map((x) => x.key);
        const own = Object.fromEntries(Object.entries(current).filter(([k]) => !otherKeys.includes(k)));
        const other = Object.fromEntries(Object.entries(current).filter(([k]) => otherKeys.includes(k)));
        await put(`${S}/settings/${section}`, { value: own, rowVersion: q.data?.rowVersion ?? 0 });
        const fresh = await get<Settings>(`${S}/settings`);
        await put(`${S}/settings/${def.also}`, { value: { ...(fresh[def.also] as Obj), ...other }, rowVersion: fresh.rowVersion });
      } else await put(`${S}/settings/${section}`, { value: current, rowVersion: q.data?.rowVersion ?? 0 });
      setDraft(null); setSaved(true); await qc.invalidateQueries({ queryKey: ['sqf-settings'] }); qc.invalidateQueries({ queryKey: ['sqf-context'] }); }
    catch (e) { setError((e as Error).message); }
  };
  const siteId = current?.defaultSiteId as string | null;
  return (
    <SqfPage title={def.title} description={def.description} crumbs={crumbs} permission="sequfloh.manage_settings"
      actions={<>{draft && <Button onClick={() => guard(() => setDraft(null))}>Discard changes</Button>}<Button variant="contained" disabled={!draft} onClick={save}>Save settings</Button></>}>
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : current && <Stack gap={2}>
        {error && <Alert severity="error">{error}</Alert>}
        {saved && <Alert severity="success">Settings saved. The change is recorded in the audit trail.</Alert>}
        <Paper variant="outlined" sx={{ p: 2.5 }}>
          <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: 'minmax(0, 1fr)', md: '1fr 1fr' } }}>
            {def.fields.map((f) => {
              const v = current[f.key];
              const full = f.type === 'multiline' || f.type === 'lines' || f.type === 'permissions' || f.type === 'csv';
              const sx = full ? { gridColumn: '1 / -1' } : undefined;
              switch (f.type) {
                case 'bool': return <FormControlLabel key={f.key} sx={sx} control={<Switch checked={!!v} onChange={(e) => set(f.key, e.target.checked)} />} label={<Box>{f.label}{f.helper && <Typography variant="caption" display="block" color="text.secondary">{f.helper}</Typography>}</Box>} />;
                case 'number': return <TextField key={f.key} type="number" label={f.label} value={v ?? ''} onChange={(e) => set(f.key, e.target.value === '' ? 0 : Number(e.target.value))} />;
                case 'select': return <TextField key={f.key} select label={f.label} value={v ?? ''} onChange={(e) => set(f.key, e.target.value)}>{f.options!.map((o) => <MenuItem key={o} value={o}>{o}</MenuItem>)}</TextField>;
                case 'site': return <TextField key={f.key} select label={f.label} value={v ?? ''} onChange={(e) => { setSaved(false); setDraft({ ...current, defaultSiteId: e.target.value || null, defaultDepartmentId: null }); }}>
                  <MenuItem value="">None</MenuItem>{(sites.data ?? []).filter((s) => !s.isArchived).map((s) => <MenuItem key={s.id} value={s.id}>{s.code} — {s.name}</MenuItem>)}{setupOptions('site', sites.data)}</TextField>;
                case 'department': return <TextField key={f.key} select label={f.label} value={v ?? ''} disabled={!siteId} onChange={(e) => set(f.key, e.target.value || null)}>
                  <MenuItem value="">None</MenuItem>{(sites.data?.find((s) => s.id === siteId)?.departments ?? []).map((d) => <MenuItem key={d.id} value={d.id}>{d.code} — {d.name}</MenuItem>)}{setupOptions('department', sites.data?.find((s) => s.id === siteId)?.departments)}</TextField>;
                case 'csv': return <TextField key={f.key} sx={sx} label={f.label} value={((v as string[]) ?? []).join(', ')} onChange={(e) => set(f.key, e.target.value.split(',').map((x) => x.trim()).filter(Boolean))} />;
                case 'lines': return <TextField key={f.key} sx={sx} multiline minRows={4} label={f.label} defaultValue={toLines(v)} onChange={(e) => set(f.key, fromLines(e.target.value, f.key === 'includedColumns'))} />;
                case 'permissions': return <TextField key={f.key} sx={sx} select label={f.label} helperText={f.helper} value={(v as string[]) ?? []} slotProps={{ select: { multiple: true } }}
                  onChange={(e) => set(f.key, ([] as string[]).concat(e.target.value as unknown as string[]))}>{(perms.data ?? []).map((p) => <MenuItem key={p} value={p}>{p}</MenuItem>)}</TextField>;
                default: return <TextField key={f.key} sx={sx} label={f.label} multiline={f.type === 'multiline'} minRows={f.type === 'multiline' ? 2 : undefined} value={(v as string) ?? ''} onChange={(e) => set(f.key, e.target.value)} />;
              }
            })}
          </Box>
        </Paper>
        {section === 'security' && <RoleSuggestions value={(current.roleSuggestions as Suggestion[]) ?? []} onChange={(v) => set('roleSuggestions', v)} />}
        {def.lists && <Paper variant="outlined" sx={{ p: 2.5 }}>
          <Typography variant="h3" sx={{ mb: 1 }}>Dropdown values</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>These dropdowns are administered as SEQUFLOH reference lists. Seeded values stay editable.</Typography>
          <Stack direction="row" gap={1} flexWrap="wrap">{def.lists.map((l) => <Chip key={l} clickable component={RouterLink} to={`/sequfloh/admin/dropdowns?list=${l}`} label={words(l.replace(/-/g, ' '))} variant="outlined" color="primary" />)}</Stack>
        </Paper>}
      </Stack>}
    </SqfPage>
  );
}

interface Suggestion { name: string; description: string; permissions: string[] }
function RoleSuggestions({ value, onChange }: { value: Suggestion[]; onChange: (v: Suggestion[]) => void }) {
  const ctx = useQuery({ queryKey: ['sqf-permissions'], queryFn: () => get<{ permissions: string[] }>(`${S}/module-access`).then((x) => x.permissions).catch(() => [] as string[]) });
  const upd = (i: number, p: Partial<Suggestion>) => onChange(value.map((s, k) => (k === i ? { ...s, ...p } : s)));
  return (
    <Paper variant="outlined" sx={{ p: 2.5 }}>
      <Stack direction="row" justifyContent="space-between" sx={{ mb: 1 }}><Typography variant="h3">Suggested permission sets</Typography>
        <Button onClick={() => onChange([...value, { name: 'New set', description: '', permissions: ['sequfloh.access'] }])}>Add set</Button></Stack>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>Suggestions an administrator can apply to an existing role on the Module Access page. They are not roles.</Typography>
      <Stack gap={2}>{value.map((s, i) => <Box key={i} sx={{ display: 'grid', gap: 1, gridTemplateColumns: { xs: 'minmax(0, 1fr)', md: '200px 1fr 2fr auto' }, alignItems: 'start' }}>
        <TextField size="small" label="Name" value={s.name} onChange={(e) => upd(i, { name: e.target.value })} />
        <TextField size="small" label="Description" value={s.description} onChange={(e) => upd(i, { description: e.target.value })} />
        <TextField size="small" select label="Permissions" value={s.permissions} slotProps={{ select: { multiple: true, renderValue: (x) => `${(x as string[]).length} permissions` } }}
          onChange={(e) => upd(i, { permissions: ([] as string[]).concat(e.target.value as unknown as string[]) })}>{(ctx.data ?? []).map((p) => <MenuItem key={p} value={p}><Checkbox size="small" checked={s.permissions.includes(p)} />{p}</MenuItem>)}</TextField>
        <Button color="error" onClick={() => onChange(value.filter((_, k) => k !== i))}>Remove</Button>
      </Box>)}</Stack>
    </Paper>
  );
}

function NumberingPage() {
  const st = useSettings();
  const qc = useQueryClient();
  const [error, setError] = useState<string | null>(null);
  const fmt = ((st.data?.numbering as Obj | undefined)?.revisionFormat as string) ?? 'Letters';
  const saveFormat = async (v: string) => { setError(null); try { await put(`${S}/settings/numbering`, { value: { revisionFormat: v }, rowVersion: st.data?.rowVersion ?? 0 }); qc.invalidateQueries({ queryKey: ['sqf-settings'] }); } catch (e) { setError((e as Error).message); } };
  return (
    <SqfPage title="Revision Settings" description="How revision letters or digits step up on work instructions and parts lists. Record numbers and file names are always your own: you type them on every form and can edit them later."
      crumbs={[{ label: 'SEQUFLOH Settings' }, { label: 'Revision Settings' }]} permission="sequfloh.manage_settings">
      {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
      <Paper variant="outlined" sx={{ p: 2.5, mb: 2 }}>
        <TextField select label="Revision format" value={fmt} onChange={(e) => saveFormat(e.target.value)} sx={{ minWidth: 260 }} helperText="Applies to new work instructions and parts lists. Released revisions are never overwritten.">
          <MenuItem value="Letters">A, B, C</MenuItem><MenuItem value="TwoDigits">00, 01, 02</MenuItem></TextField>
      </Paper>
    </SqfPage>
  );
}

// ------------------------------------------------------------------ reference data
interface ListInfo { key: string; label: string; description: string; total: number; active: number }
interface RefValue { id: string; listKey: string; code: string; label: string; description: string | null; sortOrder: number; isActive: boolean; isDefault: boolean; isSeeded: boolean; rowVersion: number; updatedAt: string; hiddenFields?: string | null }
const useLists = () => useQuery({ queryKey: ['sqf-lists'], queryFn: () => get<ListInfo[]>(`${S}/reference`) });

export function ReferenceDataPage() {
  const q = useLists();
  const [filter, setFilter] = useState('');
  const rows = (q.data ?? []).filter((l) => !filter || (l.label + ' ' + l.description).toLowerCase().includes(filter.toLowerCase()));
  return (
    <SqfPage title="Reference Data" description="SEQUFLOH reference lists that feed every controlled dropdown. Open a list to create, edit, activate, deactivate, reorder and mark defaults."
      crumbs={[{ label: 'SEQUFLOH Administration' }, { label: 'Reference Data' }]} permission="sequfloh.manage_dropdowns">
      <TextField size="small" label="Search lists" value={filter} onChange={(e) => setFilter(e.target.value)} sx={{ mb: 2, width: 320 }} />
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : rows.length === 0 ? <Empty text="No lists match the search." /> :
        <TableContainer component={Paper} variant="outlined"><Table size="small">
          <TableHead><TableRow><TableCell>List</TableCell><TableCell>Purpose</TableCell><TableCell align="right">Active</TableCell><TableCell align="right">Total</TableCell></TableRow></TableHead>
          <TableBody>{rows.map((l) => <TableRow key={l.key} hover><TableCell><Link component={RouterLink} to={`/sequfloh/admin/dropdowns?list=${l.key}`}>{l.label}</Link></TableCell>
            <TableCell>{l.description}</TableCell><TableCell align="right">{l.active}</TableCell><TableCell align="right">{l.total}</TableCell></TableRow>)}</TableBody>
        </Table></TableContainer>}
    </SqfPage>
  );
}

export function DropdownsPage() {
  const [sp, setSp] = useSearchParams();
  const lists = useLists();
  const key = sp.get('list') ?? 'work-instruction-types';
  const [search, setSearch] = useState('');
  const [active, setActive] = useState('');
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['sqf-values', key, search, active], queryFn: () => get<RefValue[]>(`${S}/reference/${key}?${new URLSearchParams({ ...(search ? { q: search } : {}), ...(active ? { active } : {}) })}`) });
  const [edit, setEdit] = useState<RefValue | 'new' | null>(null);
  const [history, setHistory] = useState<RefValue | null>(null);
  const [naFor, setNaFor] = useState<RefValue | null>(null);
  const [confirm, setConfirm] = useState<{ title: string; text: string; run: () => Promise<unknown> } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const refresh = () => { qc.invalidateQueries({ queryKey: ['sqf-values', key] }); qc.invalidateQueries({ queryKey: ['sqf-lists'] }); qc.invalidateQueries({ queryKey: ['sqf-options', key] }); };
  const act = async (fn: () => Promise<unknown>) => { setError(null); try { await fn(); refresh(); } catch (e) { setError((e as Error).message); } };
  const rows = q.data ?? [];
  const move = (i: number, d: number) => act(async () => {
    const all = await get<RefValue[]>(`${S}/reference/${key}`);
    const ids = all.map((x) => x.id); const from = ids.indexOf(rows[i].id); const to = ids.indexOf(rows[i + d].id);
    [ids[from], ids[to]] = [ids[to], ids[from]];
    await post(`${S}/reference/${key}/reorder`, { ids });
  });
  const info = lists.data?.find((l) => l.key === key);
  return (
    <SqfPage title="Dropdown Management" description={info?.description ?? 'Administer the values of a SEQUFLOH dropdown.'} crumbs={[{ label: 'SEQUFLOH Administration' }, { label: 'Dropdown Management' }]} permission="sequfloh.manage_dropdowns"
      actions={<><Button onClick={() => setConfirm({ title: 'Reset seeded values', text: 'Restore the seeded labels and order of this list and reactivate seeded values? Values added by administrators are kept.', run: () => post(`${S}/reference/${key}/reset`, { confirmed: true }) })}>Reset seeded values</Button>
        <Button variant="contained" onClick={() => setEdit('new')}>New value</Button></>}>
      <Stack direction="row" gap={1} flexWrap="wrap" sx={{ mb: 2 }}>
        <TextField select size="small" label="List" value={key} onChange={(e) => setSp({ list: e.target.value })} sx={{ minWidth: 260 }}>
          {(lists.data ?? []).map((l) => <MenuItem key={l.key} value={l.key}>{l.label}</MenuItem>)}</TextField>
        <TextField size="small" label="Search values" value={search} onChange={(e) => setSearch(e.target.value)} />
        <TextField select size="small" label="Status" value={active} onChange={(e) => setActive(e.target.value)} sx={{ minWidth: 140 }}>
          <MenuItem value="">All</MenuItem><MenuItem value="true">Active</MenuItem><MenuItem value="false">Inactive</MenuItem></TextField>
        <Button onClick={() => { setSearch(''); setActive(''); }}>Clear filters</Button>
      </Stack>
      {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : rows.length === 0 ? <Empty text="No values match." /> :
        <TableContainer component={Paper} variant="outlined"><Table size="small">
          <TableHead><TableRow><TableCell>Order</TableCell><TableCell>Label</TableCell><TableCell>Code</TableCell><TableCell>Status</TableCell><TableCell>Default</TableCell><TableCell>Origin</TableCell><TableCell /></TableRow></TableHead>
          <TableBody>{rows.map((v, i) => <TableRow key={v.id}>
            <TableCell sx={{ whiteSpace: 'nowrap' }}><Tooltip title="Move up"><span><IconButton size="small" aria-label={`Move ${v.label} up`} disabled={i === 0 || !!search || !!active} onClick={() => move(i, -1)}><ArrowUpward fontSize="small" /></IconButton></span></Tooltip>
              <Tooltip title="Move down"><span><IconButton size="small" aria-label={`Move ${v.label} down`} disabled={i === rows.length - 1 || !!search || !!active} onClick={() => move(i, 1)}><ArrowDownward fontSize="small" /></IconButton></span></Tooltip></TableCell>
            <TableCell>{v.label}{v.description && <Typography variant="caption" display="block" color="text.secondary">{v.description}</Typography>}</TableCell>
            <TableCell><Mono>{v.code}</Mono></TableCell><TableCell><SqfStatus status={v.isActive ? 'Active' : 'Inactive'} /></TableCell>
            <TableCell>{v.isDefault ? <Chip size="small" color="primary" label="Default" /> : v.isActive && <Button size="small" onClick={() => act(() => post(`${S}/reference/values/${v.id}/default`))}>Make default</Button>}</TableCell>
            <TableCell>{v.isSeeded ? 'Seeded' : 'Added'}</TableCell>
            <TableCell align="right" sx={{ whiteSpace: 'nowrap' }}>
              <Button size="small" onClick={() => setEdit(v)}>Edit</Button>
              {v.isActive ? <Button size="small" color="warning" onClick={() => setConfirm({ title: 'Deactivate value', text: `Deactivate "${v.label}"? It stays on records that already use it but is no longer offered.`, run: () => post(`${S}/reference/values/${v.id}/deactivate`) })}>Deactivate</Button>
                : <Button size="small" onClick={() => act(() => post(`${S}/reference/values/${v.id}/activate`))}>Activate</Button>}
              {key === 'product-families' && <Button size="small" data-testid="family-na-fields" onClick={() => setNaFor(v)}>Not applicable fields</Button>}
              <Button size="small" onClick={() => setHistory(v)}>History</Button>
            </TableCell></TableRow>)}</TableBody>
        </Table></TableContainer>}
      <ValueDialog listKey={key} value={edit} onClose={() => setEdit(null)} onSaved={refresh} />
      <HistoryDialog value={history} onClose={() => setHistory(null)} />
      <FamilyFieldsDialog value={naFor} onClose={() => setNaFor(null)} onSaved={() => { setNaFor(null); refresh(); }} />
      <Confirm open={!!confirm} title={confirm?.title ?? ''} text={confirm?.text ?? ''} confirm="Confirm" danger onClose={() => setConfirm(null)} onConfirm={async () => { await confirm!.run(); refresh(); }} />
    </SqfPage>
  );
}

function ValueDialog({ listKey, value, onClose, onSaved }: { listKey: string; value: RefValue | 'new' | null; onClose: () => void; onSaved: () => void }) {
  const isNew = value === 'new';
  const [v, setV] = useState({ code: '', label: '', description: '', isActive: true, isDefault: false });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { setError(null); setV(value && value !== 'new' ? { code: value.code, label: value.label, description: value.description ?? '', isActive: value.isActive, isDefault: value.isDefault } : { code: '', label: '', description: '', isActive: true, isDefault: false }); }, [value]);
  return (
    <Dialog open={!!value} onClose={onClose} fullWidth maxWidth="sm">
      <FormDialogTitle>{isNew ? 'New value' : 'Edit value'}</FormDialogTitle>
      <DialogContent><Stack gap={2} sx={{ pt: 1 }}>
        {error && <Alert severity="error">{error}</Alert>}
        <TextField label="Label" required value={v.label} onChange={(e) => setV({ ...v, label: e.target.value })} />
        <TextField label="Code" value={v.code} disabled={!isNew} helperText={isNew ? 'Leave blank to derive from the label.' : 'The code cannot change.'} onChange={(e) => setV({ ...v, code: e.target.value })} />
        <TextField label="Description" multiline minRows={2} value={v.description} onChange={(e) => setV({ ...v, description: e.target.value })} />
        <FormControlLabel control={<Switch checked={v.isActive} onChange={(e) => setV({ ...v, isActive: e.target.checked })} />} label="Active" />
        <FormControlLabel control={<Switch checked={v.isDefault} onChange={(e) => setV({ ...v, isDefault: e.target.checked })} />} label="Default value" />
      </Stack></DialogContent>
      <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" disabled={busy || !v.label.trim()} onClick={async () => {
        setBusy(true); setError(null);
        try {
          if (isNew) await post(`${S}/reference/${listKey}`, v); else await put(`${S}/reference/values/${(value as RefValue).id}`, { ...v, rowVersion: (value as RefValue).rowVersion });
          onSaved(); onClose();
        } catch (e) { setError((e as Error).message); } finally { setBusy(false); }
      }}>Save</Button></DialogActions>
    </Dialog>
  );
}

function HistoryDialog({ value, onClose }: { value: RefValue | null; onClose: () => void }) {
  const q = useQuery({ queryKey: ['sqf-value-history', value?.id], queryFn: () => get<{ occurredAt: string; userName: string; action: string; description: string }[]>(`${S}/reference/values/${value!.id}/history`), enabled: !!value });
  return (
    <Dialog open={!!value} onClose={onClose} fullWidth maxWidth="md">
      <FormDialogTitle>History of {value?.label}</FormDialogTitle>
      <DialogContent>{q.isLoading ? <Loading /> : (q.data ?? []).length === 0 ? <Empty text="No history yet." /> :
        <Table size="small"><TableHead><TableRow><TableCell>When</TableCell><TableCell>Who</TableCell><TableCell>Action</TableCell><TableCell>Details</TableCell></TableRow></TableHead>
          <TableBody>{q.data!.map((h, i) => <TableRow key={i}><TableCell sx={{ whiteSpace: 'nowrap' }}>{fmtDateTime(h.occurredAt)}</TableCell><TableCell>{h.userName}</TableCell><TableCell>{h.action}</TableCell><TableCell>{h.description}</TableCell></TableRow>)}</TableBody></Table>}
      </DialogContent>
      <DialogActions><Button onClick={onClose}>Close</Button></DialogActions>
    </Dialog>
  );
}

// ------------------------------------------------------------------ module access
interface AccessRole { id: string; name: string; description: string | null; isSystem: boolean; rowVersion: number; permissions: string[]; users: number }
interface AccessData { permissions: string[]; roles: AccessRole[]; suggestions: Suggestion[] }
const useAccess = () => useQuery({ queryKey: ['sqf-access'], queryFn: () => get<AccessData>(`${S}/module-access`) });
const capLabel = (p: string) => words(p.replace('sequfloh.', '').split('_').map((w, i) => (i === 0 ? w[0].toUpperCase() + w.slice(1) : w)).join(' '));

export function ModuleAccessPage() {
  const q = useAccess();
  const qc = useQueryClient();
  const [roleId, setRoleId] = useState('');
  const [sel, setSel] = useState<string[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [ok, setOk] = useState(false);
  const role = q.data?.roles.find((r) => r.id === roleId);
  const chosen = sel ?? role?.permissions ?? [];
  useUnsaved(!!sel);
  const toggle = (p: string) => { setOk(false); setSel(chosen.includes(p) ? chosen.filter((x) => x !== p) : [...chosen, p]); };
  return (
    <SqfPage title="Module Access" description="Grant SEQUFLOH capabilities to existing roles. Only SEQUFLOH permissions are changed here; people and roles are managed under Administration."
      crumbs={[{ label: 'SEQUFLOH Administration' }, { label: 'Module Access' }]} permission="sequfloh.manage_module_access"
      actions={role && !role.isSystem && <Button variant="contained" disabled={!sel} onClick={async () => {
        setError(null);
        try { await put(`${S}/module-access/roles/${role.id}`, { permissions: chosen, rowVersion: role.rowVersion }); setSel(null); setOk(true); qc.invalidateQueries({ queryKey: ['sqf-access'] }); }
        catch (e) { setError((e as Error).message); }
      }}>Save role access</Button>}>
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : <Stack gap={2}>
        <Stack direction={{ xs: 'column', md: 'row' }} gap={2}>
          <TextField select label="Role" value={roleId} onChange={(e) => { setRoleId(e.target.value); setSel(null); setOk(false); }} sx={{ minWidth: 320 }}>
            {q.data!.roles.map((r) => <MenuItem key={r.id} value={r.id}>{r.name} ({r.users} assignments)</MenuItem>)}</TextField>
          {role && !role.isSystem && <TextField select label="Apply a suggested permission set" value="" sx={{ minWidth: 320 }} onChange={(e) => { const s = q.data!.suggestions.find((x) => x.name === e.target.value); if (s) { setOk(false); setSel([...new Set(s.permissions)]); } }}>
            {q.data!.suggestions.map((s) => <MenuItem key={s.name} value={s.name}>{s.name} — {s.description}</MenuItem>)}</TextField>}
        </Stack>
        {error && <Alert severity="error">{error}</Alert>}
        {ok && <Alert severity="success">Role access saved. The change is recorded in the audit trail.</Alert>}
        {role?.isSystem && <Alert severity="info">The built-in administrator role always holds every permission.</Alert>}
        {role && <Paper variant="outlined" sx={{ p: 2 }}>
          <Stack direction="row" gap={1} sx={{ mb: 1 }}><Button size="small" disabled={role.isSystem} onClick={() => setSel([...q.data!.permissions])}>Select all</Button><Button size="small" disabled={role.isSystem} onClick={() => setSel([])}>Clear all</Button></Stack>
          <Box sx={{ display: 'grid', gridTemplateColumns: { xs: 'minmax(0, 1fr)', sm: '1fr 1fr', lg: '1fr 1fr 1fr' } }}>
            {q.data!.permissions.map((p) => <FormControlLabel key={p} control={<Checkbox size="small" disabled={role.isSystem} checked={role.isSystem || chosen.includes(p)} onChange={() => toggle(p)} />}
              label={<Box><Typography variant="body2">{capLabel(p)}</Typography><Typography variant="caption" color="text.secondary"><Mono>{p}</Mono></Typography></Box>} />)}
          </Box>
        </Paper>}
      </Stack>}
    </SqfPage>
  );
}

export function RolesMatrixPage() {
  const q = useAccess();
  const { can } = useAuth();
  const roles = useMemo(() => q.data?.roles ?? [], [q.data]);
  return (
    <SqfPage title="Roles and Permissions" description="SEQUFLOH permission matrix of the existing roles. Change a role on Module Access; create roles and assign people under Administration."
      crumbs={[{ label: 'SEQUFLOH Administration' }, { label: 'Roles and Permissions' }]} permission="sequfloh.manage_module_access"
      actions={<><Button component={RouterLink} to="/sequfloh/admin/module-access" variant="contained">Module Access</Button>{can('admin.roles') && <Button component={RouterLink} to="/admin/roles">All roles</Button>}</>}>
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> :
        <TableContainer component={Paper} variant="outlined" sx={{ maxHeight: '70vh' }}><Table size="small" stickyHeader>
          <TableHead><TableRow><TableCell>Capability</TableCell>{roles.map((r) => <TableCell key={r.id} align="center" sx={{ whiteSpace: 'nowrap' }}>{r.name}</TableCell>)}</TableRow></TableHead>
          <TableBody>{q.data!.permissions.map((p) => <TableRow key={p} hover><TableCell sx={{ whiteSpace: 'nowrap' }}>{capLabel(p)} <Typography component="span" variant="caption" color="text.secondary"><Mono>{p}</Mono></Typography></TableCell>
            {roles.map((r) => <TableCell key={r.id} align="center">{r.isSystem || r.permissions.includes(p) ? '✓' : ''}</TableCell>)}</TableRow>)}</TableBody>
        </Table></TableContainer>}
    </SqfPage>
  );
}

export function ConfigurationPage() {
  const ctx = useQuery({ queryKey: ['sqf-context'], queryFn: () => get<{ module: { displayName: string; subtitle: string; description: string; moduleEnabled: boolean } }>(`${S}/context`) });
  const lists = useLists();
  const [result, setResult] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const qc = useQueryClient();
  return (
    <SqfPage title="Application Configuration" description="Module registration, initialization status and the idempotent SEQUFLOH seed. Seeding adds only missing settings and reference values; administrator changes are never overwritten."
      crumbs={[{ label: 'SEQUFLOH Administration' }, { label: 'Application Configuration' }]} permission="sequfloh.manage_settings"
      actions={<Button variant="contained" onClick={async () => { setError(null); try { const r = await post<{ added: number }>(`${S}/seed`); setResult(`${r.added} missing item(s) added.`); qc.invalidateQueries(); } catch (e) { setError((e as Error).message); } }}>Run SEQUFLOH seed</Button>}>
      {result && <Alert severity="success" sx={{ mb: 2 }}>{result}</Alert>}
      {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
      <Paper variant="outlined" sx={{ p: 2.5, mb: 2 }}>
        <Typography variant="h3" sx={{ mb: 1 }}>Module registration</Typography>
        <Table size="small"><TableBody>
          <TableRow><TableCell>Logical module identifier</TableCell><TableCell><Mono>sequfloh</Mono></TableCell></TableRow>
          <TableRow><TableCell>Display name</TableCell><TableCell>{ctx.data?.module.displayName}</TableCell></TableRow>
          <TableRow><TableCell>Subtitle</TableCell><TableCell>{ctx.data?.module.subtitle}</TableCell></TableRow>
          <TableRow><TableCell>Description</TableCell><TableCell>{ctx.data?.module.description}</TableCell></TableRow>
          <TableRow><TableCell>Status</TableCell><TableCell><SqfStatus status={ctx.data?.module.moduleEnabled ? 'Active' : 'Inactive'} /></TableCell></TableRow>
          <TableRow><TableCell>Reference lists</TableCell><TableCell>{lists.data?.length ?? '—'} lists, {lists.data?.reduce((a, l) => a + l.total, 0) ?? '—'} values</TableCell></TableRow>
        </TableBody></Table>
      </Paper>
      <Alert severity="info">SEQUFLOH is designed to support controlled records, traceability, workflow execution, and audit-history requirements. Organizational compliance depends on configuration, validation,
        procedures, security, deployment, training, implementation, and organizational use.</Alert>
    </SqfPage>
  );
}

/** The fields hidden as not applicable on new parts of a product family (and on imported parts that have no own setting). */
function FamilyFieldsDialog({ value, onClose, onSaved }: { value: RefValue | null; onClose: () => void; onSaved: () => void }) {
  const fields = useQuery({ queryKey: ['sqf-part-fields'], queryFn: () => get<{ key: string; label: string }[]>(`${S}/parts/fields`), enabled: !!value });
  const [sel, setSel] = useState<string[]>([]); const [err, setErr] = useState<string | null>(null);
  useEffect(() => { setSel(value?.hiddenFields ? value.hiddenFields.split(',').filter(Boolean) : []); setErr(null); }, [value]);
  const save = async () => { try { await put(`${S}/reference/values/${value!.id}/hidden-fields`, { fields: sel }); onSaved(); } catch (e) { setErr((e as Error).message); } };
  return <Dialog open={!!value} onClose={onClose} fullWidth maxWidth="sm">
    <FormDialogTitle>Not applicable fields · {value?.label}</FormDialogTitle>
    <DialogContent>{err && <Alert severity="error" sx={{ mb: 1 }}>{err}</Alert>}
      <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>New parts of this family start with these fields hidden. Each part can change its own setting.</Typography>
      <Stack direction="row" flexWrap="wrap" columnGap={2}>{(fields.data ?? []).map((d) => <FormControlLabel key={d.key} label={d.label}
        control={<Checkbox size="small" checked={sel.includes(d.key)} onChange={(_, c) => setSel(c ? [...sel, d.key] : sel.filter((x) => x !== d.key))} />} />)}</Stack></DialogContent>
    <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" onClick={save}>Save</Button></DialogActions>
  </Dialog>;
}

/** Validation forms by product: one row per product family. A blank whose title has the family name and "validation" is suggested; nothing is used until an administrator saves it. */
function ValidationFormsPage() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['sqf-validation-forms'], queryFn: () => get<ValidationForms>(`${S}/validation-forms`) });
  const [pick, setPick] = useState<Record<string, string>>({});
  const [msg, setMsg] = useState<{ sev: 'success' | 'error'; text: string } | null>(null);
  const save = async (familyId: string, label: string, blankFormId: string, test = false) => {
    setMsg(null);
    try { await put(test ? `${S}/validation-forms/tests/${familyId}` : `${S}/validation-forms/${familyId}`, { blankFormId: blankFormId || null }); setMsg({ sev: 'success', text: `Saved the validation form for ${label}.` }); setPick((p) => { const n = { ...p }; delete n[familyId]; return n; }); await qc.invalidateQueries({ queryKey: ['sqf-validation-forms'] }); }
    catch (e) { setMsg({ sev: 'error', text: (e as Error).message }); }
  };
const vfRow = (f: { id: string; label: string; blankFormId?: string | null; suggestedId?: string | null }, test: boolean) => {
            const value = pick[f.id] ?? f.blankFormId ?? f.suggestedId ?? '';
            const confirmed = !!f.blankFormId && value === f.blankFormId;
            return <TableRow key={f.id} data-testid={`vf-row-${f.label}`}>
              <TableCell>{f.label}</TableCell>
              <TableCell sx={{ minWidth: 320 }}><TextField select size="small" fullWidth label="Blank form" value={value} onChange={(e) => setPick({ ...pick, [f.id]: e.target.value })} inputProps={{ 'data-testid': `vf-select-${f.label}` }}>
                <MenuItem value=""><em>None</em></MenuItem>{q.data!.blanks.map((b) => <MenuItem key={b.id} value={b.id}>{b.number} {b.title}</MenuItem>)}</TextField></TableCell>
              <TableCell>{confirmed ? <Chip size="small" color="success" label="Confirmed" /> : !f.blankFormId && value && value === f.suggestedId && pick[f.id] === undefined ? <Chip size="small" color="warning" label="Suggested, not saved" />
                : value !== (f.blankFormId ?? '') ? <Chip size="small" label="Not saved" /> : <Chip size="small" label="No form" />}</TableCell>
              <TableCell align="right"><Button size="small" variant="contained" disabled={confirmed || (!value && !f.blankFormId)} onClick={() => void save(f.id, f.label, value, test)} data-testid={`vf-save-${f.label}`}>{value ? 'Confirm and save' : 'Clear'}</Button></TableCell>
            </TableRow>;
          };
  const crumbs = [{ label: 'SEQUFLOH Settings' }, { label: 'Validation forms by product and test' }];
  return (
    <SqfPage title="Validation forms by product and test" crumbs={crumbs} permission="sequfloh.manage_settings"
      description="Choose the blank form each product family uses for validation. A new Validation / FAI work order for the family gets a copy of the confirmed form to fill out. A suggestion is only used once you save it.">
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : q.data && <Stack gap={2}>
        {msg && <Alert severity={msg.sev} onClose={() => setMsg(null)}>{msg.text}</Alert>}
        {q.data.families.length === 0 ? <Alert severity="info">No product families yet. Add them under Dropdown Management, Product families.</Alert> :
        <Paper variant="outlined"><Table size="small" data-testid="validation-forms-table">
          <TableHead><TableRow><TableCell>Product family</TableCell><TableCell>Blank form</TableCell><TableCell>Status</TableCell><TableCell /></TableRow></TableHead>
          <TableBody>{q.data.families.map((f) => vfRow(f, false))}</TableBody></Table></Paper>}
        <Typography variant="h2" sx={{ mt: 1 }}>Tests</Typography>
        <Typography variant="body2" color="text.secondary">Tests are not product families. A new work order of the test, for any product, gets a copy of the confirmed form to fill out.</Typography>
        <Paper variant="outlined"><Table size="small" data-testid="test-forms-table">
          <TableHead><TableRow><TableCell>Test</TableCell><TableCell>Blank form</TableCell><TableCell>Status</TableCell><TableCell /></TableRow></TableHead>
          <TableBody>{(q.data.tests ?? []).map((t) => vfRow({ ...t, id: t.key }, true))}</TableBody></Table></Paper>
      </Stack>}
    </SqfPage>
  );
}
