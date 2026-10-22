import { SetupHint, setupOptions, useSetupListbox } from '../components/SetupLink';
import { useEffect, useMemo, useState } from 'react';
import { Link as RouterLink, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { Alert, Autocomplete, Box, Button, Card, CardActions, CardContent, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, IconButton, Link, List,
  ListItem, ListItemText, Menu, MenuItem, Paper, Stack, TextField, Typography } from '@mui/material';
import MoreVert from '@mui/icons-material/MoreVert';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put } from '../api';
import { useAuth } from '../auth';
import { Facts, useSites, useUsers } from '../components/Kit';
import { fmtDate, fmtDateTime } from '../components/Status';
import { Confirm, DeleteButton, Empty, ErrorState, Filters, Grid, Loading, Mono, Paged, PagedTable, S, SqfPage, SqfStatus, nul, qs, useOptions, useUnsaved, words } from './common';
import { StatusBadge, StatusEditor, useStatusFilter } from './Job';
import { type AuditRow, HistoryTable, SimpleTable } from './Parts';
import { DevelopmentPanel } from './Personnel';
import { type Block, BlocksEditor, LayoutTemplates, TemplateActions, TemplatePicker } from './Blocks';
import { type DSchema, FieldInput, ProblemList, type Row, RowsEditor, SectionCard, type Val, sequenceProblems, typeLabels, types, useSchemas } from './Designer';

export const wiCrumbs = (label: string, folder?: { label: string; to: string }) =>
  [{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Work Instructions', to: '/sequfloh/instructions/all' }, ...(folder ? [folder] : []), { label }];
const statuses = ['Draft', 'Released', 'Superseded', 'Obsolete'];
const folderStatuses = ['Draft', 'Released', 'Superseded', 'Obsolete'];

// ------------------------------------------------------------------ folders
interface WiRow { statusTag?: string | null; statusNote?: string | null; id: string; number: string; title: string; type: string; revision: string; status: string; partId?: string; partNumber?: string; validationType?: string; faiType?: string;
  repairCategory?: string; repairType?: string; repairDescription?: string; resultStatus?: string; effectiveDate?: string; updatedAt: string; owner?: string; releasedBy?: string }
type Kind = 'generic' | 'validation' | 'repair';
/** Navigation folder -> API folder, title, column set. */
const folders: Record<string, { api: string; title: string; kind: Kind; description: string; newType?: string }> = {
  all: { api: 'all', title: 'All Work Instructions', kind: 'generic', description: 'Every SEQUFLOH work instruction you can see, all types and statuses.' },
  validation: { api: 'validation', title: 'Validation Work Instructions', kind: 'validation', newType: 'Validation', description: 'Validation folder: Validation, Validation / FAI and FAI instructions, kept separate from repair instructions.' },
  fai: { api: 'fai', title: 'FAI Work Instructions', kind: 'validation', newType: 'Fai', description: 'First article inspection instructions with their characteristics.' },
  'repair-test': { api: 'repair-test', title: 'Repair Test Instructions', kind: 'repair', newType: 'RepairTest', description: 'Repair test instructions: test steps, parameters and pass / fail criteria.' },
  repair: { api: 'repair', title: 'Repair Work Instructions', kind: 'repair', newType: 'RepairWork', description: 'Repair folder: Repair Work Instructions and Repair Test instructions, kept separate from validation instructions.' },
  draft: { api: 'drafts', title: 'Draft Instructions', kind: 'generic', description: 'Instructions that can still be edited. Release one when it is ready.' },
  released: { api: 'released', title: 'Released Instructions', kind: 'generic', description: 'Current released revisions. Released revisions are read-only; changes go through a new revision.' },
  obsolete: { api: 'obsolete', title: 'Obsolete Instructions', kind: 'generic', description: 'Instructions marked obsolete. They stay on file and cannot be used for new work.' },
};

export function InstructionsFolderPage({ fixedFolder, crumbsOverride, titleOverride, descriptionOverride, permission, tabs, tab, onTab }: {
  fixedFolder?: string; crumbsOverride?: { label: string; to?: string }[]; titleOverride?: string; descriptionOverride?: string; permission?: string;
  tabs?: { label: string; value: string }[]; tab?: string; onTab?: (t: string) => void } = {}) {
  const params = useParams();
  const folderKey = fixedFolder ?? params.folder ?? 'all';
  const cfg = folders[folderKey] ?? folders.all;
  const { can } = useAuth();
  const nav = useNavigate();
  const [sp, setSp] = useSearchParams();
  const v = (k: string) => sp.get(k) ?? '';
  const [page, setPage] = useState(1); const [size, setSize] = useState(25); const [sort, setSort] = useState('updatedAt:desc');
  useEffect(() => setPage(1), [folderKey]);
  const set = (k: string, val: string) => { const n = new URLSearchParams(sp); if (val) n.set(k, val); else n.delete(k); setSp(n); setPage(1); };
  const repairCats = useOptions('repair-categories'); const repairTypes = useOptions('repair-types'); const valTypes = useOptions('validation-types');
  const q = useQuery({ queryKey: ['sqf-wi', cfg.api, sp.toString(), page, size, sort], queryFn: () => get<Paged<WiRow>>(`${S}/instructions?${qs({ folder: cfg.api, q: v('q'), status: v('status'), type: v('type'),
    part: v('part'), repairCategory: v('repairCategory'), repairType: v('repairType'), validationType: v('validationType'), resultStatus: v('resultStatus'), tag: v('tag'), sort, page, pageSize: size })}`) });
  const tagFilter = useStatusFilter();
  const title = titleOverride ?? cfg.title;
  const filters: { key: string; label: string; options: [string, string][] }[] = [{ key: 'status', label: 'Status', options: (cfg.kind === 'generic' ? statuses : folderStatuses).map((s) => [s, words(s)]) }];
  if (cfg.kind === 'generic') filters.push({ key: 'type', label: 'Type', options: types.map((t) => [t, typeLabels[t]]) });
  if (cfg.kind === 'validation') {
    filters.push({ key: 'resultStatus', label: 'Result', options: [['Pass', 'Pass'], ['Fail', 'Fail'], ['Conditional', 'Conditional']] });
    if (folderKey === 'validation') filters.push({ key: 'validationType', label: 'Validation type', options: (valTypes.data ?? []).map((o) => [o, o]) });
  }
  if (cfg.kind === 'repair') {
    filters.push({ key: 'repairCategory', label: 'Repair category', options: (repairCats.data ?? []).map((o) => [o, o]) });
    filters.push({ key: 'repairType', label: 'Repair type', options: (repairTypes.data ?? []).map((o) => [o, o]) });
  }
  filters.push(tagFilter);
  const num = { key: 'number', label: 'Instruction number', sort: true, render: (r: WiRow) => <Mono>{r.number}</Mono> };
  const people = [{ key: 'owner', label: 'Owner', render: (r: WiRow) => r.owner ?? '—' }, { key: 'releasedBy', label: 'Released by', render: (r: WiRow) => r.releasedBy ?? '—' }];
  const tail = [{ key: 'effectiveDate', label: 'Effective', sort: true, render: (r: WiRow) => fmtDate(r.effectiveDate) }, { key: 'updatedAt', label: 'Last updated', sort: true, render: (r: WiRow) => fmtDateTime(r.updatedAt) }];
  const actions = { key: 'actions', label: 'Actions', render: (r: WiRow) => <RowActions row={r} /> };
  const tagCol = { key: 'statusTag', label: 'Official status in NetSuite', render: (r: WiRow) => <StatusBadge tag={r.statusTag} note={r.statusNote} /> };
  const cols = cfg.kind === 'validation'
    ? [num, { key: 'title', label: 'Title', sort: true }, { key: 'partNumber', label: 'Part', render: (r: WiRow) => r.partNumber ?? '—' },
      { key: 'validationType', label: folderKey === 'fai' ? 'FAI type' : 'Validation type', render: (r: WiRow) => (r.type === 'Fai' ? r.faiType : r.validationType) ?? r.faiType ?? '—' },
      { key: 'revision', label: 'Rev' }, { key: 'status', label: 'Status', sort: true, render: (r: WiRow) => <SqfStatus status={r.status} /> }, ...people, ...tail,
      { key: 'resultStatus', label: 'Result status', render: (r: WiRow) => <SqfStatus status={r.resultStatus ?? null} /> }, tagCol, actions]
    : cfg.kind === 'repair'
      ? [num, { key: 'title', label: 'Title', sort: true }, { key: 'partNumber', label: 'Part being repaired', render: (r: WiRow) => r.partNumber ?? '—' },
        { key: 'repairDescription', label: 'Repair description', render: (r: WiRow) => r.repairDescription ?? (r.type === 'RepairTest' ? 'Repair test' : '—') },
        { key: 'repairCategory', label: 'Repair category', render: (r: WiRow) => r.repairCategory ?? '—' }, { key: 'revision', label: 'Rev' },
        { key: 'status', label: 'Status', sort: true, render: (r: WiRow) => <SqfStatus status={r.status} /> }, ...people, ...tail, tagCol, actions]
      : [num, { key: 'title', label: 'Title', sort: true }, { key: 'type', label: 'Type', render: (r: WiRow) => typeLabels[r.type] ?? r.type },
        { key: 'partNumber', label: 'Part', render: (r: WiRow) => r.partNumber ?? '—' }, { key: 'revision', label: 'Rev' },
        { key: 'status', label: 'Status', sort: true, render: (r: WiRow) => <SqfStatus status={r.status} /> }, people[0], ...tail, tagCol, actions];
  return (
    <SqfPage title={title} crumbs={crumbsOverride ?? wiCrumbs(title)} description={descriptionOverride ?? cfg.description} permission={permission} tabs={tabs} tab={tab} onTab={onTab}
      actions={can('sequfloh.create_work_instruction') && <Button variant="contained" component={RouterLink} to={`/sequfloh/instructions/new${cfg.newType ? `?type=${cfg.newType}` : ''}`}>New instruction</Button>}>
      <Filters search={v('q')} onSearch={(s) => set('q', s)} values={Object.fromEntries(filters.map((f) => [f.key, v(f.key)]))} onChange={set} onClear={() => { setSp({}); setPage(1); }} filters={filters}
        extra={cfg.kind === 'repair' && <TextField size="small" label="Part number" defaultValue={v('part')} key={v('part')} onBlur={(e) => set('part', e.target.value.trim())} sx={{ width: 160 }} />} />
      <PagedTable q={q} page={page} size={size} onPage={setPage} onSize={setSize} sort={sort} onSort={setSort} onRow={(r) => nav(`/sequfloh/instruction/${r.id}`)}
        empty={v('q') || v('status') ? 'No work instructions match these filters.' : 'No work instructions in this folder yet.'} cols={cols} />
    </SqfPage>
  );
}

const editable = ['Draft'];
/** Row menu: open, edit, duplicate, create revision, print, history, mark obsolete (each re-checked by the server). */
function RowActions({ row }: { row: WiRow }) {
  const { can } = useAuth();
  const nav = useNavigate();
  const qc = useQueryClient();
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const [dlg, setDlg] = useState<'revise' | 'obsolete' | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const go = (to: string) => { setAnchor(null); nav(to); };
  const items: [string, () => void, boolean][] = [
    ['Open', () => go(`/sequfloh/instruction/${row.id}`), true],
    ['Edit', () => go(`/sequfloh/instruction/${row.id}`), editable.includes(row.status) && can('sequfloh.edit_draft_work_instruction')],
    ['Duplicate', async () => { setAnchor(null); try { const r = await post<{ id: string }>(`${S}/instructions/${row.id}/duplicate`); nav(`/sequfloh/instruction/${r.id}`); } catch (e) { setErr((e as Error).message); } },
      can('sequfloh.create_work_instruction')],
    ['Create revision', () => { setAnchor(null); setDlg('revise'); }, row.status === 'Released' && can('sequfloh.revise_work_instruction')],
    ['Print', () => { setAnchor(null); window.open(`${S}/instructions/${row.id}/print`, '_blank', 'noopener'); }, true],
    ['History', () => go(`/sequfloh/instruction/${row.id}?tab=history`), true],
    ['Mark obsolete', () => { setAnchor(null); setDlg('obsolete'); }, ['Draft', 'Released', 'Superseded'].includes(row.status) && can('sequfloh.obsolete_work_instruction')],
  ];
  return (
    <Box onClick={(e) => e.stopPropagation()}>
      <IconButton size="small" aria-label={`Actions for ${row.number}`} onClick={(e) => setAnchor(e.currentTarget)}><MoreVert fontSize="small" /></IconButton>
      <Menu anchorEl={anchor} open={!!anchor} onClose={() => setAnchor(null)}>{items.filter(([, , ok]) => ok).map(([l, f]) => <MenuItem key={l} onClick={f}>{l}</MenuItem>)}</Menu>
      {err && <Alert severity="error" onClose={() => setErr(null)} sx={{ mt: 1 }}>{err}</Alert>}
      <Confirm open={dlg === 'revise'} title={`Create a revision of ${row.number}`} confirm="Create revision" reason="required" reasonLabel="Revision reason" onClose={() => setDlg(null)}
        text="The released revision stays released and unchanged. The new revision starts as a draft copy until it is released."
        onConfirm={async (reason) => { const r = await post<{ id: string }>(`${S}/instructions/${row.id}/revise`, { reason }); nav(`/sequfloh/instruction/${r.id}`); }} />
      <Confirm open={dlg === 'obsolete'} title={`Mark ${row.number} obsolete`} confirm="Mark obsolete" reason="required" onClose={() => setDlg(null)} text="The instruction stays on file with its revisions and history, and cannot be used for new work."
        onConfirm={async (comment) => { await post(`${S}/instructions/${row.id}/actions/obsolete`, { comment }); qc.invalidateQueries({ queryKey: ['sqf-wi'] }); }} />
    </Box>
  );
}

// ------------------------------------------------------------------ new instruction (starts with the type)
interface PartOpt { id: string; partNumber: string; name: string; revision: string; productFamily?: string; assemblyType?: string; partType: string; status: string; isRepairable?: boolean }
export function PartPicker({ value, label, onChange, disabled, repairable }: { value: { id: string; partNumber: string; name?: string } | null; label: string; onChange: (p: PartOpt | null) => void; disabled?: boolean; repairable?: boolean }) {
  const [text, setText] = useState('');
  const q = useQuery({ queryKey: ['sqf-part-pick', text, repairable], queryFn: () => get<Paged<PartOpt>>(`${S}/parts?${qs({ q: text, status: 'Active', view: repairable ? 'repairable' : '', pageSize: 20, sort: 'partNumber:asc' })}`) });
  const opts: PartOpt[] = q.data?.items ?? [];
  const listbox = useSetupListbox('sqfPart');
  const current = value ? (opts.find((o) => o.id === value.id) ?? ({ id: value.id, partNumber: value.partNumber, name: value.name ?? '', revision: '', partType: '', status: 'Active' } as PartOpt)) : null;
  return <Autocomplete size="small" options={opts} value={current} slots={{ listbox }} noOptionsText={text ? 'No matching part' : <SetupHint kind="sqfPart" count={0} />} disabled={disabled} loading={q.isLoading} filterOptions={(x) => x}
    isOptionEqualToValue={(a, b) => a.id === b.id} getOptionLabel={(o) => `${o.partNumber}${o.name ? ' — ' + o.name : ''}`}
    onInputChange={(_, t, reason) => { if (reason === 'input') setText(t); }} onChange={(_, o) => onChange(o)}
    renderInput={(p) => <TextField {...p} label={label} />} />;
}

export function NewInstructionPage() {
  const nav = useNavigate();
  const [sp] = useSearchParams();
  const sites = useSites();
  const schemas = useSchemas();
  const [type, setType] = useState(sp.get('type') ?? '');
  const [title, setTitle] = useState('');
  const [siteId, setSiteId] = useState('');
  const [departmentId, setDepartmentId] = useState('');
  const [part, setPart] = useState<PartOpt | null>(null);
  const [templateId, setTemplateId] = useState('');
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { if (!siteId && sites.data?.length) setSiteId(sites.data[0].id); }, [sites.data, siteId]);
  const site = sites.data?.find((s) => s.id === siteId);
  const schema = schemas.data?.find((s) => s.type === type);
  const create = async () => {
    setBusy(true); setErr(null);
    try {
      const r = await post<{ id: string }>(`${S}/instructions`, { type, title, siteId, departmentId: nul(departmentId), partId: part?.id ?? null, templateId: nul(templateId) });
      nav(`/sequfloh/instruction/${r.id}`, { replace: true });
    } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return (
    <SqfPage title="New work instruction" crumbs={wiCrumbs('New work instruction')} permission="sequfloh.create_work_instruction" recent={null}
      description="Choose the work instruction type first. The type decides which designer opens: Validation, Validation / FAI, FAI, Repair Test or Repair Work Instruction.">
      <Paper variant="outlined" sx={{ p: 3, maxWidth: 880 }}>
        {err && <Alert severity="error" sx={{ mb: 2 }}>{err}</Alert>}
        <Typography variant="overline" component="h2" sx={{ fontWeight: 700 }}>WORK INSTRUCTION TYPE</Typography>
        <TextField select fullWidth label="Work instruction type" value={type} onChange={(e) => { setType(e.target.value); setTemplateId(''); }} sx={{ mb: 1, mt: 1 }}>
          {types.map((t) => <MenuItem key={t} value={t}>{typeLabels[t]}</MenuItem>)}</TextField>
        {schema && <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Opens the {schema.title} designer: {schema.sections.map((s) => s.title).join(', ')}; {schema.stepsTitle.toLowerCase()}{schema.hasCharacteristics ? '; FAI characteristics' : ''}{schema.hasPartsRows ? '; repair parts list' : ''}.
        </Typography>}
        <Grid cols={2}>
          <TextField label="Title" value={title} onChange={(e) => setTitle(e.target.value)} required sx={{ gridColumn: '1 / -1' }} disabled={!type} />
          <TextField select label="Site" value={siteId} onChange={(e) => { setSiteId(e.target.value); setDepartmentId(''); }} disabled={!type}>
            {(sites.data ?? []).map((s) => <MenuItem key={s.id} value={s.id}>{s.code} · {s.name}</MenuItem>)}{setupOptions('site', sites.data)}</TextField>
          <TextField select label="Department" value={departmentId} onChange={(e) => setDepartmentId(e.target.value)} disabled={!type}>
            <MenuItem value=""><em>None</em></MenuItem>{(site?.departments ?? []).map((d) => <MenuItem key={d.id} value={d.id}>{d.name}</MenuItem>)}{site && setupOptions('department', site.departments)}</TextField>
          <Box sx={{ gridColumn: '1 / -1' }}><TemplatePicker type={type || undefined} value={templateId} onChange={setTemplateId} disabled={!type} /></Box>
          <Box sx={{ gridColumn: '1 / -1' }}><PartPicker label={type === 'RepairWork' ? 'Part being repaired' : 'Part (optional)'} value={part} onChange={setPart} disabled={!type} repairable={type === 'RepairWork'} /></Box>
        </Grid>
        <Stack direction="row" gap={1} sx={{ mt: 3 }}>
          <Button variant="contained" disabled={!type || !title.trim() || !siteId || busy} onClick={create}>Create and open designer</Button>
          <Button component={RouterLink} to="/sequfloh/instructions/all">Cancel</Button>
        </Stack>
      </Paper>
    </SqfPage>
  );
}

// ------------------------------------------------------------------ templates
/** Designer templates (one per instruction type) and released instructions that can be duplicated as a starting point. */
export function TemplatesPage() {
  const { can } = useAuth();
  const nav = useNavigate();
  const schemas = useSchemas();
  const released = useQuery({ queryKey: ['sqf-wi-templates'], queryFn: () => get<Paged<WiRow>>(`${S}/instructions?${qs({ folder: 'released', sort: 'number:asc', pageSize: 100 })}`) });
  const [err, setErr] = useState<string | null>(null);
  return (
    <SqfPage title="Templates" crumbs={wiCrumbs('Templates')} description="Start a work instruction from a designer template, or duplicate a released instruction as a new draft with its own number. Copies never change the source.">
      {err && <Alert severity="error" sx={{ mb: 2 }}>{err}</Alert>}
      {schemas.isLoading ? <Loading /> : schemas.error ? <ErrorState error={schemas.error} /> :
        <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: 'minmax(0, 1fr)', md: 'repeat(2, 1fr)', lg: 'repeat(3, 1fr)' }, mb: 3 }}>
          {(schemas.data ?? []).map((s) => <Card key={s.type} variant="outlined">
            <CardContent>
              <Typography variant="h3" sx={{ mb: 1 }}>{s.title}</Typography>
              <Typography variant="body2" color="text.secondary">{s.sections.length} sections · {s.stepsTitle} ({s.stepColumns.length} fields per step){s.hasCharacteristics ? ' · FAI characteristics' : ''}{s.hasPartsRows ? ' · repair parts rows' : ''}</Typography>
              <Typography variant="caption" color="text.secondary" component="p" sx={{ mt: 1 }}>{s.sections.map((x) => x.title).join(' · ')}</Typography>
            </CardContent>
            {can('sequfloh.create_work_instruction') && <CardActions><Button size="small" component={RouterLink} to={`/sequfloh/instructions/new?type=${s.type}`}>Use template</Button></CardActions>}
          </Card>)}
        </Box>}
      <LayoutTemplates typeLabels={typeLabels} />
      <Typography variant="h3" sx={{ mb: 1 }}>Released instructions</Typography>
      {released.isLoading ? <Loading /> : released.error ? <ErrorState error={released.error} /> : !released.data?.items.length ? <Empty text="No released instructions yet." /> :
        <SimpleTable head={['Instruction', 'Title', 'Type', 'Rev', '']} rows={released.data.items.map((r) => [
          <Link component={RouterLink} to={`/sequfloh/instruction/${r.id}`}><Mono>{r.number}</Mono></Link>, r.title, typeLabels[r.type], r.revision,
          can('sequfloh.create_work_instruction') ? <Button size="small" onClick={async () => { try { const x = await post<{ id: string }>(`${S}/instructions/${r.id}/duplicate`); nav(`/sequfloh/instruction/${x.id}`); } catch (e) { setErr((e as Error).message); } }}>Duplicate as new</Button> : null])} />}
    </SqfPage>
  );
}

// ------------------------------------------------------------------ designer
interface WiDoc {
  statusTag?: string | null; statusNote?: string | null;
  id: string; familyId: string; number: string; type: string; title: string; revision: string; status: string; siteId: string; departmentId?: string | null; ownerId?: string | null; authorId?: string | null;
  partId?: string | null; partNumber?: string | null; partDescription?: string | null; productFamily?: string | null; assembly?: string | null;
  model?: string | null; variant?: string | null; process?: string | null; operation?: string | null; workstation?: string | null; location?: string | null; validationType?: string | null; faiType?: string | null;
  repairCategory?: string | null; repairType?: string | null; repairDescription?: string | null; sourcePartsListId?: string | null; resultStatus?: string | null; effectiveDate?: string | null;
  supersededAt?: string | null; obsoleteAt?: string | null; releasedAt?: string | null; revisionReason?: string | null; createdAt: string; updatedAt: string;
  rowVersion: number; content: Record<string, Record<string, Val>>; steps: Row[]; characteristics: Row[]; partsRows: Row[]; blocks: Block[]; templateId?: string | null; template?: string | null; owner?: string; author?: string;
  releasedBy?: string; createdBy?: string; updatedBy?: string; site?: string; department?: string;
  developmentStatus?: string | null; engineeringNotes?: string | null;
}
interface WiResponse { instruction: WiDoc; transitions: { id: string; action: string; fromStatus?: string; toStatus: string; comment?: string; revision: string; createdAt: string; user?: string }[];
  revisions: { id: string; revision: string; status: string; effectiveDate?: string; releasedAt?: string; supersededAt?: string; revisionReason?: string; createdAt: string }[];
  canEdit: boolean; actions: { action: string; label: string; commentRequired: boolean }[]; releaseProblems: string[]; canDuplicate: boolean; canDevelop: boolean }

const applicability: [keyof WiDoc, string][] = [['productFamily', 'Product family'], ['assembly', 'Assembly'], ['model', 'Model'], ['variant', 'Variant'], ['process', 'Process'], ['operation', 'Operation'],
  ['workstation', 'Workstation'], ['location', 'Location']];
const pick = (c: WiDoc['content'], ...paths: [string, string][]) => { for (const [s, f] of paths) { const v = c?.[s]?.[f]; if (v !== undefined && v !== null && v !== '') return String(v); } return null; };

export function InstructionPage() {
  const { can } = useAuth();
  const { id = '' } = useParams();
  const [sp, setSp] = useSearchParams();
  const tab = sp.get('tab') ?? 'control';
  const setTab = (t: string) => { const n = new URLSearchParams(sp); n.set('tab', t); setSp(n, { replace: true }); };
  const qc = useQueryClient();
  const nav = useNavigate();
  const schemas = useSchemas();
  const q = useQuery({ queryKey: ['sqf-wi-one', id], queryFn: () => get<WiResponse>(`${S}/instructions/${id}`) });
  const history = useQuery({ queryKey: ['sqf-wi-history', id], queryFn: () => get<AuditRow[]>(`${S}/instructions/${id}/history`), enabled: tab === 'history' });
  const [doc, setDoc] = useState<WiDoc | null>(null);
  const [saved, setSaved] = useState('');
  const [msg, setMsg] = useState<{ sev: 'success' | 'error'; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [act, setAct] = useState<WiResponse['actions'][number] | null>(null);
  useEffect(() => { if (q.data) { setDoc(q.data.instruction); setSaved(JSON.stringify(q.data.instruction)); } }, [q.data]);
  const dirty = !!doc && JSON.stringify(doc) !== saved;
  const guard = useUnsaved(dirty);
  if (q.isLoading || schemas.isLoading) return <SqfPage title="Work instruction" crumbs={wiCrumbs('Loading')}><Loading /></SqfPage>;
  if (q.error) return <SqfPage title="Work instruction" crumbs={wiCrumbs('Not available')}><ErrorState error={q.error} retry={q.refetch} /></SqfPage>;
  if (!doc || !q.data) return null;
  const schema = schemas.data!.find((s) => s.type === doc.type)!;
  const ro = !q.data.canEdit || busy;
  const upd = (patch: Partial<WiDoc>) => setDoc({ ...doc, ...patch });
  const setContent = (section: string, key: string, v: Val) => setDoc({ ...doc, content: { ...doc.content, [section]: { ...(doc.content[section] ?? {}), [key]: v } } });
  const refresh = async () => { await qc.invalidateQueries({ queryKey: ['sqf-wi-one', id] }); qc.invalidateQueries({ queryKey: ['sqf-wi-history', id] }); qc.invalidateQueries({ queryKey: ['sqf-wi'] }); };
  const body = (d: WiDoc) => ({
    title: d.title, siteId: d.siteId, departmentId: d.departmentId ?? null, ownerId: d.ownerId ?? null, partId: d.partId ?? null,
    productFamily: nul(d.productFamily), assembly: nul(d.assembly), model: nul(d.model), variant: nul(d.variant), process: nul(d.process), operation: nul(d.operation), workstation: nul(d.workstation),
    location: nul(d.location), validationType: pick(d.content, ['validationIdentity', 'validationType']), faiType: pick(d.content, ['faiIdentity', 'faiType']),
    repairCategory: pick(d.content, ['repairIdentity', 'repairCategory'], ['repairTest', 'repairCategory']), repairType: pick(d.content, ['repairIdentity', 'repairType']),
    repairDescription: nul(d.repairDescription), resultStatus: pick(d.content, ['results', 'resultStatus'], ['faiDisposition', 'finalDisposition']), effectiveDate: nul(d.effectiveDate),
    content: d.content, steps: d.steps, characteristics: d.characteristics, partsRows: d.partsRows, blocks: d.blocks ?? [], rowVersion: d.rowVersion,
  });
  const save = async (quiet = false) => {
    setBusy(true); setMsg(null);
    try { await put(`${S}/instructions/${id}`, body(doc)); await refresh(); if (!quiet) setMsg({ sev: 'success', text: 'Draft saved.' }); return true; }
    catch (e) { setMsg({ sev: 'error', text: (e as Error).message }); return false; } finally { setBusy(false); }
  };
  /** Saves pending edits first (library copies and transitions work on the stored record), then runs the call. */
  const withSave = async (fn: () => Promise<unknown>, done: string) => {
    if (dirty && !(await save(true))) return;
    setBusy(true); setMsg(null);
    try { await fn(); await refresh(); setMsg({ sev: 'success', text: done }); } catch (e) { setMsg({ sev: 'error', text: (e as Error).message }); } finally { setBusy(false); }
  };
  const runAction = async (a: WiResponse['actions'][number], comment: string, effectiveDate?: string) => {
    if (dirty && !(await save(true))) throw new Error('Save the draft first.');
    if (a.action === 'revise') { const r = await post<{ id: string }>(`${S}/instructions/${id}/revise`, { reason: comment }); nav(`/sequfloh/instruction/${r.id}`); return; }
    await post(`${S}/instructions/${id}/actions/${a.action}`, { comment: nul(comment), effectiveDate: nul(effectiveDate), rowVersion: q.data!.instruction.rowVersion });
    await refresh(); setMsg({ sev: 'success', text: `${a.label}: done.` });
  };
  const seqIssues = [...sequenceProblems(doc.steps, 'step'), ...(schema.hasCharacteristics ? sequenceProblems(doc.characteristics, 'characteristic') : [])];
  const tabs = [{ label: 'Document control', value: 'control' }, { label: 'Content', value: 'content' }, { label: 'Layout', value: 'layout' }, { label: schema.stepsTitle, value: 'steps' },
    ...(schema.hasCharacteristics ? [{ label: 'Characteristics', value: 'characteristics' }] : []), ...(schema.hasPartsRows ? [{ label: 'Repair parts', value: 'parts' }] : []),
    ...(q.data.canDevelop || doc.developmentStatus || doc.engineeringNotes ? [{ label: 'Development', value: 'development' }] : []),
    { label: 'History', value: 'history' }];
  const folder = schema.type === 'RepairWork' || schema.type === 'RepairTest' ? { label: 'Repair Work Instructions', to: '/sequfloh/instructions/repair' } : { label: 'Validation Work Instructions', to: '/sequfloh/instructions/validation' };
  return (
    <SqfPage title={<>{doc.number} rev {doc.revision} — {doc.title}</>} crumbs={wiCrumbs(`${doc.number} rev ${doc.revision}`, folder)} recent={{ label: `${doc.number} rev ${doc.revision} ${doc.title}`, itemType: 'Work instruction', recordId: doc.id }}
      tabs={tabs} tab={tab} onTab={setTab}
      actions={<Stack direction="row" gap={1} flexWrap="wrap" alignItems="center">
        <SqfStatus status={doc.status} />
        {q.data.canEdit && <Button variant="contained" disabled={!dirty || busy} onClick={() => save()}>Save draft</Button>}
        {q.data.actions.map((a) => <Button key={a.action} variant={a.action === 'release' ? 'contained' : 'outlined'} disabled={busy} color={a.action === 'release' ? 'success' : a.action === 'obsolete' ? 'error' : 'primary'} onClick={() => setAct(a)}>{a.label}</Button>)}
        <Button variant="outlined" href={`${S}/instructions/${id}/print`} target="_blank" rel="noopener" disabled={dirty}>Print</Button>
        {q.data.canDuplicate && <Button disabled={busy} onClick={() => guard(() => withSave(async () => { const r = await post<{ id: string }>(`${S}/instructions/${id}/duplicate`); nav(`/sequfloh/instruction/${r.id}`); }, 'Duplicated.'))}>Duplicate</Button>}
        <DeleteButton what={`${doc.number} ${doc.title}`} url={`${S}/instructions/${id}`} to={folder.to}
          extra={`Every revision of ${doc.number} is removed, with its files and history. Work instructions that already have work orders can't be deleted; mark them obsolete instead.`} />
      </Stack>}>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>{typeLabels[doc.type]} designer · {doc.site ?? ''}{doc.department ? ` · ${doc.department}` : ''}
        {!q.data.canEdit && doc.status === 'Released' && ' · Released revisions are read-only. Use Create revision to change this instruction.'}
        {!q.data.canEdit && doc.status !== 'Released' && ` · Read-only while ${words(doc.status).toLowerCase()}.`}</Typography>
      <StatusEditor kind="instruction" id={doc.id} tag={doc.statusTag} note={doc.statusNote} canEdit={can('sequfloh.edit_draft_work_instruction') && !dirty} onSaved={refresh} />
      {msg && <Alert severity={msg.sev} sx={{ mb: 2 }} onClose={() => setMsg(null)}>{msg.text}</Alert>}
      {dirty && <Alert severity="info" sx={{ mb: 2 }}>You have unsaved changes.</Alert>}
      {doc.status === 'Draft' &&
        <ProblemList title="Before this instruction can be released:" problems={[...new Set([...seqIssues, ...q.data.releaseProblems])]} />}

      {tab === 'control' && <Stack gap={2}>
        <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Document control">
          <Typography variant="h3" sx={{ mb: 1.5 }}>Document control</Typography>
          <Facts items={[['Instruction number', <Mono>{doc.number}</Mono>], ['Instruction type', typeLabels[doc.type]], ['Revision', doc.revision], ['Status', <SqfStatus status={doc.status} />],
            ['Internal record ID', <Mono>{doc.id}</Mono>], ['Author', doc.author ?? '—'], ['Created', fmtDateTime(doc.createdAt)], ['Updated', `${fmtDateTime(doc.updatedAt)}${doc.updatedBy ? ' · ' + doc.updatedBy : ''}`],
            ['Released', doc.releasedAt ? `${fmtDateTime(doc.releasedAt)} · ${doc.releasedBy ?? ''}` : '—'], ['Superseded', fmtDateTime(doc.supersededAt)], ['Obsolete', fmtDateTime(doc.obsoleteAt)],
            ...(doc.revisionReason ? [['Revision reason', doc.revisionReason] as [string, string]] : [])]} />
          <Box sx={{ mt: 2 }}><ControlFields doc={doc} upd={upd} ro={ro} /></Box>
        </Paper>
        {doc.type === 'RepairWork' ? <RepairControls doc={doc} upd={upd} ro={ro} withSave={withSave} id={id} /> :
          <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Applicability">
            <Typography variant="h3" sx={{ mb: 1.5 }}>Applicability</Typography>
            <Grid cols={3}>
              <Box sx={{ gridColumn: { md: 'span 2' } }}><PartPicker label="Part number" value={doc.partId ? { id: doc.partId, partNumber: doc.partNumber ?? '', name: doc.partDescription ?? '' } : null} disabled={ro}
                onChange={(p) => upd({ partId: p?.id ?? null, partNumber: p?.partNumber ?? null, partDescription: p?.name ?? null, productFamily: doc.productFamily || p?.productFamily || null, assembly: doc.assembly || p?.assemblyType || null })} /></Box>
              <TextField size="small" label="Part description" value={doc.partDescription ?? ''} disabled InputLabelProps={{ shrink: true }} />
              {applicability.map(([k, l]) => <TextField key={k} size="small" label={l} value={(doc[k] as string) ?? ''} disabled={ro} onChange={(e) => upd({ [k]: e.target.value } as Partial<WiDoc>)} />)}
            </Grid>
          </Paper>}
      </Stack>}

      {tab === 'content' && <Stack gap={2}>
        {schema.sections.map((s) => <SectionCard key={s.key} section={s} values={doc.content[s.key] ?? {}} disabled={ro} onChange={(k, v) => setContent(s.key, k, v)}
          extra={s.key === 'repairDescription' ? <Box sx={{ mb: 2, maxWidth: 420 }}><RepairDescriptionSelect value={doc.repairDescription ?? null} disabled={ro} onChange={(v) => upd({ repairDescription: v })} /></Box> : undefined} />)}
      </Stack>}

      {tab === 'layout' && <Stack gap={2}>
        <Stack direction="row" justifyContent="space-between" alignItems="center" flexWrap="wrap" gap={1}>
          <Typography variant="body2" color="text.secondary">{doc.template ? `Started from template ${doc.template}. ` : ''}Build the page from blocks in the order the operator reads them.</Typography>
          <TemplateActions id={id} type={doc.type} canApply={!ro} canSave={can('sequfloh.create_work_instruction') && (doc.blocks ?? []).length > 0}
            beforeRun={async () => (dirty ? save(true) : true)} onApplied={refresh} />
        </Stack>
        <BlocksEditor blocks={doc.blocks ?? []} disabled={ro} onChange={(blocks) => upd({ blocks })} ownerType="SequflohWorkInstruction" ownerId={doc.id} />
      </Stack>}

      {tab === 'steps' && <Stack gap={2}>
        <ProblemList title="Step sequence" problems={sequenceProblems(doc.steps, 'step')} />
        <RowsEditor title={schema.stepsTitle} noun="step" cols={schema.stepColumns} rows={doc.steps} disabled={ro} onChange={(steps) => upd({ steps })}
          summary={doc.type === 'RepairWork' ? ['operationStep', 'title', 'specification', 'expectedResult'] : ['title', 'specification', 'expectedResult']}
          actions={!ro && <CopyStepsButton onCopy={(ids) => withSave(() => post(`${S}/instructions/${id}/copy-operation-steps`, { operationStepIds: ids }), 'Operation steps copied into this instruction. Editing them does not change the library templates.')} />} />
      </Stack>}

      {tab === 'characteristics' && schema.hasCharacteristics && <Stack gap={2}>
        <ProblemList title="Characteristic sequence" problems={sequenceProblems(doc.characteristics, 'characteristic')} />
        <RowsEditor title="FAI characteristics" noun="characteristic" seqLabel="Order" cols={schema.characteristicColumns} rows={doc.characteristics} disabled={ro} onChange={(characteristics) => upd({ characteristics })}
          summary={['characteristicNumber', 'drawingZone', 'requirement', 'specification', 'inspectionMethod']}
          actions={!ro && <CharacteristicSources id={id} familyId={doc.familyId} withSave={withSave} />} />
      </Stack>}

      {tab === 'parts' && schema.hasPartsRows && <RowsEditor title="Repair parts list" noun="parts row" seqLabel="Item" cols={schema.partsRowColumns} rows={doc.partsRows} disabled={ro} onChange={(partsRows) => upd({ partsRows })}
        summary={['replacementPartNumber', 'description', 'quantity', 'unit', 'required', 'approvedAlternate']}
        actions={!ro && <CopyPartsButton withSave={withSave} id={id} />} />}

      {tab === 'development' && <DevelopmentPanel id={id} doc={doc as unknown as Record<string, unknown>} canEdit={q.data.canDevelop} onSaved={() => qc.invalidateQueries({ queryKey: ['sqf-wi-one', id] })} />}

      {tab === 'history' && <Stack gap={2}>
        <Typography variant="h3">Lifecycle transitions</Typography>
        {q.data.transitions.length === 0 ? <Empty text="No transitions yet." /> :
          <SimpleTable head={['When', 'Action', 'Previous status', 'New status', 'User', 'Revision', 'Comment']} rows={q.data.transitions.map((t) => [fmtDateTime(t.createdAt), words(t.action.replace(/-/g, ' ')),
            t.fromStatus ? words(t.fromStatus) : '—', words(t.toStatus), t.user ?? '—', t.revision, t.comment ?? '—'])} />}
        <Typography variant="h3">Revisions</Typography>
        <SimpleTable head={['Revision', 'Status', 'Effective', 'Released', 'Superseded', 'Reason', '']} rows={q.data.revisions.map((r) => [r.revision, <SqfStatus status={r.status} />, fmtDate(r.effectiveDate),
          fmtDateTime(r.releasedAt), fmtDateTime(r.supersededAt), r.revisionReason ?? '—', r.id === doc.id ? 'This revision' : <Link component={RouterLink} to={`/sequfloh/instruction/${r.id}`}>Open</Link>])} />
        <Typography variant="h3">Audit history</Typography>
        <HistoryTable q={history} />
      </Stack>}

      <ActionDialog action={act} number={`${doc.number} rev ${doc.revision}`} effectiveDate={doc.effectiveDate ?? ''} onClose={() => setAct(null)} onRun={runAction} />
    </SqfPage>
  );
}

function ControlFields({ doc, upd, ro }: { doc: WiDoc; upd: (p: Partial<WiDoc>) => void; ro: boolean }) {
  const sites = useSites();
  const users = useUsers();
  const site = sites.data?.find((s) => s.id === doc.siteId);
  const userSel = (k: 'ownerId', label: string) => <TextField select size="small" label={label} value={users.data ? (doc[k] ?? '') : ''} disabled={ro} onChange={(e) => upd({ [k]: e.target.value || null })}>
    <MenuItem value=""><em>None</em></MenuItem>{(users.data ?? []).map((u) => <MenuItem key={u.id} value={u.id}>{u.displayName}</MenuItem>)}{setupOptions('user', users.data)}</TextField>;
  return (
    <Grid cols={3}>
      <TextField size="small" label="Title" value={doc.title} disabled={ro} onChange={(e) => upd({ title: e.target.value })} sx={{ gridColumn: { md: 'span 2' } }} />
      <TextField size="small" type="date" label="Effective date" value={doc.effectiveDate ?? ''} disabled={ro} InputLabelProps={{ shrink: true }} onChange={(e) => upd({ effectiveDate: e.target.value || null })} />
      <TextField select size="small" label="Site" value={sites.data ? doc.siteId : ''} disabled={ro} onChange={(e) => upd({ siteId: e.target.value, departmentId: null })}>
        {(sites.data ?? []).map((s) => <MenuItem key={s.id} value={s.id}>{s.code} · {s.name}</MenuItem>)}{setupOptions('site', sites.data)}</TextField>
      <TextField select size="small" label="Department" value={site ? (doc.departmentId ?? '') : ''} disabled={ro} onChange={(e) => upd({ departmentId: e.target.value || null })}>
        <MenuItem value=""><em>None</em></MenuItem>{(site?.departments ?? []).map((d) => <MenuItem key={d.id} value={d.id}>{d.name}</MenuItem>)}{site && setupOptions('department', site.departments)}</TextField>
      {userSel('ownerId', 'Owner')}
    </Grid>
  );
}

function RepairDescriptionSelect({ value, onChange, disabled }: { value: string | null; onChange: (v: string | null) => void; disabled?: boolean }) {
  return <FieldInput fld={{ key: 'repairDescription', label: 'Repair description (controlled)', kind: 'select', list: 'repair-descriptions' }} value={value} disabled={disabled} onChange={(v) => onChange((v as string) || null)} />;
}

/** Repair Work Instruction: the four controlled dropdowns (part being repaired, repair description, operation steps, parts list) and part identification. */
function RepairControls({ doc, upd, ro, withSave, id }: { doc: WiDoc; upd: (p: Partial<WiDoc>) => void; ro: boolean; withSave: (fn: () => Promise<unknown>, done: string) => Promise<void>; id: string }) {
  const part = useQuery({ queryKey: ['sqf-part', doc.partId], queryFn: () => get<{ part: PartOpt & { description?: string; isRepairable: boolean } }>(`${S}/parts/${doc.partId}`), enabled: !!doc.partId });
  const p = part.data?.part;
  return (
    <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Repair controls">
      <Typography variant="h3" sx={{ mb: 1.5 }}>Repair controls</Typography>
      <Grid cols={2}>
        <PartPicker label="1. Part being repaired" repairable value={doc.partId ? { id: doc.partId, partNumber: doc.partNumber ?? '', name: doc.partDescription ?? '' } : null} disabled={ro}
          onChange={(x) => upd({ partId: x?.id ?? null, partNumber: x?.partNumber ?? null, partDescription: x?.name ?? null, productFamily: x?.productFamily ?? doc.productFamily ?? null, assembly: x?.assemblyType ?? doc.assembly ?? null })} />
        <RepairDescriptionSelect value={doc.repairDescription ?? null} disabled={ro} onChange={(v) => upd({ repairDescription: v })} />
        <Box><Typography variant="body2" sx={{ mb: 0.5 }}>3. Operation steps</Typography>{ro ? <Typography variant="body2" color="text.secondary">{doc.steps.length} repair step(s)</Typography> :
          <CopyStepsButton onCopy={(ids) => withSave(() => post(`${S}/instructions/${id}/copy-operation-steps`, { operationStepIds: ids }), 'Operation steps copied into this instruction. Editing them does not change the library templates.')} />}</Box>
        <Box><Typography variant="body2" sx={{ mb: 0.5 }}>4. Parts list</Typography>{ro ? <Typography variant="body2" color="text.secondary">{doc.partsRows.length} parts row(s)</Typography> :
          <CopyPartsButton withSave={withSave} id={id} />}</Box>
      </Grid>
      {doc.partId && <Box sx={{ mt: 2 }}>{part.isLoading ? <Loading /> : p ? <Facts items={[['Part number', <Mono>{p.partNumber}</Mono>], ['Description', p.name], ['Current revision', p.revision],
        ['Product family', p.productFamily ?? '—'], ['Assembly classification', p.assemblyType ?? words(p.partType)], ['Active status', words(p.status)]]} /> : null}</Box>}
      <Typography variant="h3" sx={{ mt: 2, mb: 1.5 }}>Part identification</Typography>
      <Grid cols={3}>
        <TextField size="small" label="Description" value={doc.partDescription ?? ''} disabled InputLabelProps={{ shrink: true }} />
        <TextField size="small" label="Assembly" value={doc.assembly ?? ''} disabled={ro} onChange={(e) => upd({ assembly: e.target.value })} />
        <TextField size="small" label="Product family" value={doc.productFamily ?? ''} disabled={ro} onChange={(e) => upd({ productFamily: e.target.value })} />
        {applicability.slice(2).map(([k, l]) => <TextField key={k} size="small" label={l} value={(doc[k] as string) ?? ''} disabled={ro} onChange={(e) => upd({ [k]: e.target.value } as Partial<WiDoc>)} />)}
      </Grid>
    </Paper>
  );
}

interface OpStep { id: string; number: string; title: string; category?: string; isActive: boolean }
function CopyStepsButton({ onCopy }: { onCopy: (ids: string[]) => Promise<void> }) {
  const [open, setOpen] = useState(false);
  const [sel, setSel] = useState<string[]>([]);
  const [text, setText] = useState('');
  const lib = useQuery({ queryKey: ['sqf-opsteps-pick', text], queryFn: () => get<Paged<OpStep>>(`${S}/operation-steps?${qs({ q: text, active: true, pageSize: 100 })}`), enabled: open });
  return <>
    <Button size="small" variant="outlined" onClick={() => { setSel([]); setOpen(true); }}>Copy operation steps</Button>
    <Dialog open={open} onClose={() => setOpen(false)} fullWidth maxWidth="sm">
      <DialogTitle>Copy reusable operation steps</DialogTitle>
      <DialogContent>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>Selected steps are copied into this instruction in the order chosen. The copies belong to this instruction; the library templates do not change.</Typography>
        <TextField size="small" fullWidth label="Search operation steps" value={text} onChange={(e) => setText(e.target.value)} sx={{ my: 1 }} />
        {lib.isLoading ? <Loading /> : !lib.data?.items.length ? <Empty text="No active operation steps match." /> :
          <List dense>{lib.data.items.map((s) => <ListItem key={s.id} disablePadding>
            <FormControlLabel sx={{ width: '100%', ml: 0 }} control={<Checkbox checked={sel.includes(s.id)} onChange={(_, c) => setSel(c ? [...sel, s.id] : sel.filter((x) => x !== s.id))} />}
              label={<ListItemText primary={`${s.number} ${s.title}`} secondary={s.category} />} /></ListItem>)}</List>}
      </DialogContent>
      <DialogActions><Button onClick={() => setOpen(false)}>Cancel</Button>
        <Button variant="contained" disabled={sel.length === 0} onClick={async () => { setOpen(false); await onCopy(sel); }}>Copy {sel.length || ''} step{sel.length === 1 ? '' : 's'}</Button></DialogActions>
    </Dialog>
  </>;
}

const sources: [string, string][] = [['Standard', 'Existing parts list'], ['RepairKit', 'Repair kit'], ['AssemblyBom', 'Assembly bill of materials'], ['RepairParts', 'Repair parts list'], ['parts', 'Individually selected parts'], ['blank', 'Blank custom list']];
function CopyPartsButton({ withSave, id }: { withSave: (fn: () => Promise<unknown>, done: string) => Promise<void>; id: string }) {
  const [open, setOpen] = useState(false);
  const [src, setSrc] = useState('Standard');
  const [listId, setListId] = useState('');
  const [parts, setParts] = useState<PartOpt[]>([]);
  const [replace, setReplace] = useState(false);
  const lists = useQuery({ queryKey: ['sqf-lists-pick', src], queryFn: () => get<Paged<{ id: string; number: string; title: string; revision: string; status: string; items: number }>>(`${S}/parts-lists?${qs({ type: src === 'Standard' ? '' : src, pageSize: 100, sort: 'number:asc' })}`),
    enabled: open && !['parts', 'blank'].includes(src) });
  const usable = (lists.data?.items ?? []).filter((l) => l.status !== 'Inactive');
  const run = async () => {
    setOpen(false);
    if (src === 'blank') { await withSave(() => post(`${S}/instructions/${id}/copy-parts`, { partsListId: null, partIds: [], replace: true }), 'Parts rows cleared: build a custom list with Add parts row.'); return; }
    await withSave(() => post(`${S}/instructions/${id}/copy-parts`, { partsListId: src === 'parts' ? null : listId, partIds: src === 'parts' ? parts.map((p) => p.id) : null, replace }),
      'Parts copied into this instruction. Editing them does not change the source list.');
  };
  return <>
    <Button size="small" variant="outlined" onClick={() => { setListId(''); setParts([]); setOpen(true); }}>Copy parts</Button>
    <Dialog open={open} onClose={() => setOpen(false)} fullWidth maxWidth="sm">
      <DialogTitle>Copy parts into this instruction</DialogTitle>
      <DialogContent>
        <TextField select fullWidth size="small" label="Source" value={src} onChange={(e) => { setSrc(e.target.value); setListId(''); }} sx={{ mt: 1, mb: 2 }}>
          {sources.map(([v, l]) => <MenuItem key={v} value={v}>{l}</MenuItem>)}</TextField>
        {!['parts', 'blank'].includes(src) && (lists.isLoading ? <Loading /> :
          <TextField select fullWidth size="small" label="Parts list" value={listId} onChange={(e) => setListId(e.target.value)} helperText={usable.length === 0 ? 'No parts lists of this type.' : undefined}>
            {usable.map((l) => <MenuItem key={l.id} value={l.id}>{l.number} rev {l.revision} — {l.title} ({l.items} items, {words(l.status).toLowerCase()})</MenuItem>)}</TextField>)}
        {src === 'parts' && <PartsMulti value={parts} onChange={setParts} />}
        {src === 'blank' ? <Alert severity="warning" sx={{ mt: 1 }}>All current parts rows are removed so a custom list can be entered row by row.</Alert>
          : <FormControlLabel sx={{ mt: 1 }} control={<Checkbox checked={replace} onChange={(_, c) => setReplace(c)} />} label="Replace the current parts rows" />}
      </DialogContent>
      <DialogActions><Button onClick={() => setOpen(false)}>Cancel</Button>
        <Button variant="contained" color={src === 'blank' ? 'error' : 'primary'} disabled={(src === 'parts' && parts.length === 0) || (!['parts', 'blank'].includes(src) && !listId)} onClick={run}>{src === 'blank' ? 'Start blank list' : 'Copy'}</Button></DialogActions>
    </Dialog>
  </>;
}
function PartsMulti({ value, onChange }: { value: PartOpt[]; onChange: (v: PartOpt[]) => void }) {
  const listbox = useSetupListbox('sqfPart');
  const [text, setText] = useState('');
  const q = useQuery({ queryKey: ['sqf-part-pick', text, false], queryFn: () => get<Paged<PartOpt>>(`${S}/parts?${qs({ q: text, status: 'Active', pageSize: 20, sort: 'partNumber:asc' })}`) });
  return <Autocomplete multiple size="small" options={q.data?.items ?? []} value={value} slots={{ listbox }} noOptionsText={text ? 'No matching part' : <SetupHint kind="sqfPart" count={0} />} filterOptions={(x) => x} isOptionEqualToValue={(a, b) => a.id === b.id}
    getOptionLabel={(o) => `${o.partNumber} — ${o.name}`} onInputChange={(_, t, r) => { if (r === 'input') setText(t); }} onChange={(_, v) => onChange(v)} renderInput={(p) => <TextField {...p} label="Parts" />} />;
}

/** Copy characteristics from another instruction or revision, or import them from an inspection plan (existing application data). */
function CharacteristicSources({ id, familyId, withSave }: { id: string; familyId: string; withSave: (fn: () => Promise<unknown>, done: string) => Promise<void> }) {
  const [open, setOpen] = useState<'copy' | 'import' | null>(null);
  const [sel, setSel] = useState('');
  const others = useQuery({ queryKey: ['sqf-wi-charsrc'], queryFn: () => get<Paged<WiRow>>(`${S}/instructions?${qs({ folder: 'validation', pageSize: 100, sort: 'number:asc' })}`), enabled: open === 'copy' });
  const plans = useQuery({ queryKey: ['sqf-wi-plans', id], queryFn: () => get<{ id: string; number: string; title: string; revision: string; count: number }[]>(`${S}/instructions/${id}/characteristic-sources`), enabled: open === 'import' });
  const copyRows = useMemo(() => (others.data?.items ?? []).filter((r) => r.id !== id && (r.type === 'Fai' || r.type === 'ValidationFai')), [others.data, id]);
  void familyId;
  return <>
    <Button size="small" onClick={() => { setSel(''); setOpen('copy'); }}>Copy from another revision</Button>
    <Button size="small" onClick={() => { setSel(''); setOpen('import'); }}>Import from inspection plan</Button>
    <Dialog open={!!open} onClose={() => setOpen(null)} fullWidth maxWidth="sm">
      <DialogTitle>{open === 'copy' ? 'Copy characteristics from another instruction or revision' : 'Import characteristics from an inspection plan'}</DialogTitle>
      <DialogContent>
        {open === 'copy' && (others.isLoading ? <Loading /> : copyRows.length === 0 ? <Empty text="No other FAI instructions to copy from." /> :
          <TextField select fullWidth size="small" sx={{ mt: 1 }} label="Instruction and revision" value={sel} onChange={(e) => setSel(e.target.value)}>
            {copyRows.map((r) => <MenuItem key={r.id} value={r.id}>{r.number} rev {r.revision} — {r.title} ({words(r.status).toLowerCase()})</MenuItem>)}</TextField>)}
        {open === 'import' && (plans.isLoading ? <Loading /> : plans.error ? <ErrorState error={plans.error} /> : !plans.data?.length ? <Empty text="No effective inspection plans for this part that you can read." /> :
          <TextField select fullWidth size="small" sx={{ mt: 1 }} label="Inspection plan" value={sel} onChange={(e) => setSel(e.target.value)}>
            {plans.data.map((p) => <MenuItem key={p.id} value={p.id}>{p.number} rev {p.revision} — {p.title} ({p.count} characteristics)</MenuItem>)}</TextField>)}
        <Typography variant="caption" color="text.secondary" component="p" sx={{ mt: 1 }}>Copied characteristics become part of this instruction. The source does not change.</Typography>
      </DialogContent>
      <DialogActions><Button onClick={() => setOpen(null)}>Cancel</Button>
        <Button variant="contained" disabled={!sel} onClick={async () => {
          const mode = open; setOpen(null);
          if (mode === 'copy') await withSave(() => post(`${S}/instructions/${id}/copy-characteristics`, { fromInstructionId: sel }), 'Characteristics copied.');
          else await withSave(() => post(`${S}/instructions/${id}/import-characteristics`, { inspectionPlanId: sel }), 'Characteristics imported.');
        }}>{open === 'copy' ? 'Copy' : 'Import'}</Button></DialogActions>
    </Dialog>
  </>;
}

/** Lifecycle action dialog: release (with effective date) or mark obsolete (comment required). */
function ActionDialog({ action, number, effectiveDate, onClose, onRun }: { action: WiResponse['actions'][number] | null; number: string; effectiveDate: string;
  onClose: () => void; onRun: (a: WiResponse['actions'][number], comment: string, effectiveDate?: string) => Promise<void> }) {
  const [comment, setComment] = useState('');
  const [eff, setEff] = useState(effectiveDate);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  if (!action) return null;
  const danger = action.action === 'obsolete';
  const required = action.commentRequired;
  const texts: Record<string, string> = {
    release: 'Releases the instruction for use. The released revision becomes read-only; later changes need a new revision. Any earlier released revision is superseded.',
    obsolete: 'Marks the instruction obsolete. It stays on file and cannot be used for new work.',
    revise: 'Creates a new draft revision. The released revision stays released and unchanged until the new revision is released.',
  };
  return (
    <Dialog open onClose={busy ? undefined : onClose} fullWidth maxWidth="sm" TransitionProps={{ onEnter: () => { setComment(''); setErr(null); setEff(effectiveDate); } }}>
      <DialogTitle>{action.label} — {number}</DialogTitle>
      <DialogContent>
        <Typography variant="body2" sx={{ mb: 2 }}>{texts[action.action] ?? ''}</Typography>
        {err && <Alert severity="error" sx={{ mb: 2 }}>{err}</Alert>}
        {action.action === 'release' && <TextField size="small" type="date" label="Effective date" value={eff} onChange={(e) => setEff(e.target.value)} InputLabelProps={{ shrink: true }} sx={{ mb: 2 }} />}
        <TextField fullWidth multiline minRows={2} label={action.action === 'revise' ? 'Revision reason (required)' : required ? 'Comment (required)' : 'Comment'} value={comment} onChange={(e) => setComment(e.target.value)} />
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>Cancel</Button>
        <Button variant="contained" color={danger ? 'error' : action.action === 'release' ? 'success' : 'primary'} disabled={busy || (required && !comment.replace(/:\s*$/, '').trim())}
          onClick={async () => { setBusy(true); setErr(null); try { await onRun(action, comment.trim(), eff); onClose(); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); } }}>{action.label}</Button>
      </DialogActions>
    </Dialog>
  );
}
export type { DSchema };
