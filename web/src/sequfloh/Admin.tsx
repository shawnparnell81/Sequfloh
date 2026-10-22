import { setupOptions } from '../components/SetupLink';
import { useEffect, useMemo, useState } from 'react';
import { Link as RouterLink, useParams, useSearchParams } from 'react-router-dom';
import { Alert, Box, Button, Checkbox, Chip, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, IconButton, Link, MenuItem, Paper, Stack, Switch, Table, TableBody, TableCell,
  TableContainer, TableHead, TableRow, TextField, Tooltip, Typography } from '@mui/material';
import ArrowUpward from '@mui/icons-material/ArrowUpward';
import ArrowDownward from '@mui/icons-material/ArrowDownward';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put } from '../api';
import { useAuth } from '../auth';
import { useSites } from '../components/Kit';
import { fmtDateTime } from '../components/Status';
import { Confirm, Empty, ErrorState, Loading, Mono, S, SqfPage, SqfStatus, useUnsaved, words } from './common';

// ------------------------------------------------------------------ settings
type FieldType = 'bool' | 'text' | 'multiline' | 'number' | 'csv' | 'lines' | 'site' | 'department' | 'select' | 'permissions';
interface Field { key: string; label: string; type: FieldType; options?: string[]; helper?: string }
const sections: Record<string, { title: string; description: string; fields: Field[]; lists?: string[] }> = {
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
  tracking: { title: 'Tracking Settings', description: 'Variance thresholds, default benchmark times, and overdue thresholds.', lists: ['activity-statuses', 'priorities', 'cancellation-reasons', 'status-tags', 'discovery-methods'], fields: [
    { key: 'varianceWarningPercent', label: 'Variance warning threshold (%)', type: 'number' }, { key: 'varianceCriticalPercent', label: 'Variance critical threshold (%)', type: 'number' },
    { key: 'defaultMinimumMinutes', label: 'Default minimum time (minutes)', type: 'number' }, { key: 'defaultMaximumMinutes', label: 'Default maximum time (minutes)', type: 'number' },
    { key: 'overdueThresholdHours', label: 'Overdue threshold (hours after due date)', type: 'number' }, { key: 'approachingDueHours', label: 'Approaching due date warning (hours)', type: 'number' },
    { key: 'varianceReasonRequired', label: 'Variance reason required outside the benchmark range', type: 'bool' }] },
  validation: { title: 'Validation Settings', description: 'Evidence requirements for validation records.', lists: ['validation-types', 'validation-results', 'activity-statuses'], fields: [
    { key: 'evidenceRequired', label: 'Evidence required for each result', type: 'bool' }] },
  fai: { title: 'FAI Settings', description: 'Evidence requirements for FAI records.', lists: ['fai-types', 'inspection-results', 'characteristic-results'], fields: [
    { key: 'evidenceRequiredForFailures', label: 'Evidence required for failed characteristics', type: 'bool' }] },
  repair: { title: 'Repair Settings', description: 'Test requirements for repairs.', lists: ['repair-types', 'repair-categories', 'repair-descriptions', 'repair-test-results', 'repair-failure-reasons', 'qualification-labels'], fields: [
    { key: 'repairTestRequired', label: 'Repair test required before completion', type: 'bool' }] },
  notifications: { title: 'Notification Settings', description: 'SEQUFLOH notifications sent through the in-app notification system.', fields: [
    { key: 'assignedWork', label: 'Assigned work', type: 'bool' }, { key: 'overdueWork', label: 'Overdue work', type: 'bool' }, { key: 'failedRecords', label: 'Failed records requiring attention', type: 'bool' },
    { key: 'releasedInstructions', label: 'Released instructions', type: 'bool' }, { key: 'supersededInstructions', label: 'Superseded instructions', type: 'bool' },
    { key: 'approachingDueDates', label: 'Approaching due dates', type: 'bool' }] },
  display: { title: 'Display Preferences', description: 'Compact tables, dashboard cards, default filters and folder view, status colours.', fields: [
    { key: 'compactTables', label: 'Compact tables', type: 'bool' }, { key: 'dashboardCards', label: 'Visible dashboard cards (comma separated)', type: 'csv' },
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
  if (section === 'numbering') return <NumberingPage />;
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
  const current = (draft ?? (q.data?.[section] as Obj | undefined)) ?? null;
  const set = (k: string, v: unknown) => { setSaved(false); setDraft({ ...(current ?? {}), [k]: v }); };
  const save = async () => {
    setError(null);
    try { await put(`${S}/settings/${section}`, { value: current, rowVersion: q.data?.rowVersion ?? 0 }); setDraft(null); setSaved(true); await qc.invalidateQueries({ queryKey: ['sqf-settings'] }); qc.invalidateQueries({ queryKey: ['sqf-context'] }); }
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

interface Sequence { recordType: string; label: string; prefix: string; padding: number; pattern: string; nextValue: number; example: string }
function NumberingPage() {
  const q = useQuery({ queryKey: ['sqf-numbering'], queryFn: () => get<Sequence[]>(`${S}/numbering`) });
  const st = useSettings();
  const qc = useQueryClient();
  const [edit, setEdit] = useState<Sequence | null>(null);
  const [error, setError] = useState<string | null>(null);
  const fmt = ((st.data?.numbering as Obj | undefined)?.revisionFormat as string) ?? 'Letters';
  const saveFormat = async (v: string) => { setError(null); try { await put(`${S}/settings/numbering`, { value: { revisionFormat: v }, rowVersion: st.data?.rowVersion ?? 0 }); qc.invalidateQueries({ queryKey: ['sqf-settings'] }); } catch (e) { setError((e as Error).message); } };
  return (
    <SqfPage title="Numbering Settings" description="Prefixes, sequence padding and numbering pattern of every SEQUFLOH record type (numbers are allocated atomically inside the saving transaction)."
      crumbs={[{ label: 'SEQUFLOH Settings' }, { label: 'Numbering Settings' }]} permission="sequfloh.manage_settings">
      {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
      <Paper variant="outlined" sx={{ p: 2.5, mb: 2 }}>
        <TextField select label="Revision format" value={fmt} onChange={(e) => saveFormat(e.target.value)} sx={{ minWidth: 260 }} helperText="Applies to new work instructions and parts lists. Released revisions are never overwritten.">
          <MenuItem value="Letters">A, B, C</MenuItem><MenuItem value="TwoDigits">00, 01, 02</MenuItem></TextField>
      </Paper>
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> :
        <TableContainer component={Paper} variant="outlined"><Table size="small">
          <TableHead><TableRow><TableCell>Record type</TableCell><TableCell>Prefix</TableCell><TableCell>Padding</TableCell><TableCell>Pattern</TableCell><TableCell>Next number</TableCell><TableCell /></TableRow></TableHead>
          <TableBody>{(q.data ?? []).map((s) => <TableRow key={s.recordType}><TableCell>{s.label}</TableCell><TableCell><Mono>{s.prefix}</Mono></TableCell><TableCell>{s.padding}</TableCell>
            <TableCell>{s.pattern}</TableCell><TableCell><Mono>{s.example}</Mono></TableCell><TableCell align="right"><Button size="small" onClick={() => setEdit(s)}>Edit</Button></TableCell></TableRow>)}</TableBody>
        </Table></TableContainer>}
      <SequenceDialog seq={edit} onClose={() => setEdit(null)} onSaved={() => qc.invalidateQueries({ queryKey: ['sqf-numbering'] })} />
    </SqfPage>
  );
}

function SequenceDialog({ seq, onClose, onSaved }: { seq: Sequence | null; onClose: () => void; onSaved: () => void }) {
  const [v, setV] = useState<Sequence | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { setV(seq); setError(null); }, [seq]);
  if (!v) return null;
  return (
    <Dialog open={!!seq} onClose={onClose} fullWidth maxWidth="xs">
      <DialogTitle>{v.label}</DialogTitle>
      <DialogContent><Stack gap={2} sx={{ pt: 1 }}>
        {error && <Alert severity="error">{error}</Alert>}
        <TextField label="Prefix" value={v.prefix} onChange={(e) => setV({ ...v, prefix: e.target.value })} />
        <TextField label="Sequence padding" type="number" value={v.padding} onChange={(e) => setV({ ...v, padding: Number(e.target.value) })} />
        <TextField select label="Numbering pattern" value={v.pattern} onChange={(e) => setV({ ...v, pattern: e.target.value })}>
          <MenuItem value="PREFIX-YEAR-SEQUENCE">PREFIX-YEAR-SEQUENCE</MenuItem><MenuItem value="PREFIX-SEQUENCE">PREFIX-SEQUENCE</MenuItem></TextField>
      </Stack></DialogContent>
      <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" onClick={async () => {
        try { await put(`${S}/numbering`, { recordType: v.recordType, prefix: v.prefix, padding: v.padding, pattern: v.pattern }); onSaved(); onClose(); } catch (e) { setError((e as Error).message); }
      }}>Save</Button></DialogActions>
    </Dialog>
  );
}

// ------------------------------------------------------------------ reference data
interface ListInfo { key: string; label: string; description: string; total: number; active: number }
interface RefValue { id: string; listKey: string; code: string; label: string; description: string | null; sortOrder: number; isActive: boolean; isDefault: boolean; isSeeded: boolean; rowVersion: number; updatedAt: string }
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
              <Button size="small" onClick={() => setHistory(v)}>History</Button>
            </TableCell></TableRow>)}</TableBody>
        </Table></TableContainer>}
      <ValueDialog listKey={key} value={edit} onClose={() => setEdit(null)} onSaved={refresh} />
      <HistoryDialog value={history} onClose={() => setHistory(null)} />
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
      <DialogTitle>{isNew ? 'New value' : 'Edit value'}</DialogTitle>
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
      <DialogTitle>History of {value?.label}</DialogTitle>
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
    <SqfPage title="Application Configuration" description="Module registration, initialization status and the idempotent SEQUFLOH seed. Seeding adds only missing settings, numbering sequences and reference values; administrator changes are never overwritten."
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
