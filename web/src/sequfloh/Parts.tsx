import { setupOptions } from '../components/SetupLink';
import { useState } from 'react';
import { Link as RouterLink, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { Alert, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, IconButton, Link, MenuItem, Paper, Stack, Table, TableBody, TableCell,
  TableContainer, TableHead, TableRow, TextField, Tooltip, Typography } from '@mui/material';
import ArrowUpward from '@mui/icons-material/ArrowUpward';
import ArrowDownward from '@mui/icons-material/ArrowDownward';
import ContentCopy from '@mui/icons-material/ContentCopy';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post, put } from '../api';
import { useAuth } from '../auth';
import { useSites } from '../components/Kit';
import { fmtDateTime } from '../components/Status';
import { DrawingFileFields, DrawingPicker, PartDrawingLinks, blankDrawing, drawingForm, type PartDrawing } from './Drawings';
import { Confirm, Empty, ErrorState, Filters, Grid, Loading, Mono, Paged, PagedTable, S, SqfPage, SqfStatus, nul, numOrNull, qs, useOptions, useUnsaved, words } from './common';

const crumbs = (label: string, extra?: { label: string; to?: string }) => [{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Parts and Items', to: '/sequfloh/parts' }, { label, to: extra ? undefined : undefined }, ...(extra ? [extra] : [])];
const yes = (b?: boolean) => (b ? 'Yes' : 'No');
/** Shared Part Master enums (numeric on the wire, as in the Master Data module). */
const partTypes = ['Component', 'Assembly', 'Material', 'Subassembly', 'Consumable', 'Packaging', 'Other'];
const partTypeValue: Record<string, number> = { Component: 0, Assembly: 1, Material: 2, Subassembly: 3, Consumable: 4, Packaging: 5, Other: 9 };
const masterStatuses = ['Active', 'Inactive', 'Obsolete'];

// ------------------------------------------------------------------ Part Master
interface PartRow { id: string; partNumber: string; name: string; description?: string; revision: string; productFamily?: string; partType: string; assemblyType?: string; unitOfMeasure: string;
  status: string; isRepairable: boolean; isSerialized: boolean; isLotControlled: boolean; drawingNumber?: string; specification?: string; updatedAt: string }
const views: Record<string, string> = { '': 'Part Master', repairable: 'Repair Parts', assemblies: 'Assemblies' };

export function PartMasterPage() {
  const { can } = useAuth();
  const nav = useNavigate();
  const [sp, setSp] = useSearchParams();
  const view = sp.get('view') ?? '';
  const [page, setPage] = useState(1); const [size, setSize] = useState(25); const [sort, setSort] = useState('partNumber:asc');
  const v = (k: string) => sp.get(k) ?? '';
  const set = (k: string, val: string) => { const n = new URLSearchParams(sp); if (val) n.set(k, val); else n.delete(k); setSp(n); setPage(1); };
  const families = useQuery({ queryKey: ['sqf-families'], queryFn: () => get<string[]>(`${S}/parts/families`) });
  const q = useQuery({ queryKey: ['sqf-parts', sp.toString(), page, size, sort], queryFn: () => get<Paged<PartRow>>(`${S}/parts?${qs({ q: v('q'), view, family: v('family'), status: v('status'), type: v('type'), sort, page, pageSize: size })}`) });
  const [open, setOpen] = useState(false);
  const title = views[view] ?? 'Part Master';
  return (
    <SqfPage title={title} crumbs={crumbs(title)} description="The part master with execution attributes for each part and revision."
      actions={can('sequfloh.edit_parts') && can('parts.edit') && <Button variant="contained" onClick={() => setOpen(true)}>New part</Button>}>
      <Filters search={v('q')} onSearch={(s) => set('q', s)} values={{ family: v('family'), status: v('status'), type: v('type') }} onChange={set}
        onClear={() => { setSp(view ? { view } : {}); setPage(1); }}
        filters={[{ key: 'family', label: 'Product family', options: (families.data ?? []).map((f) => [f, f]) },
          { key: 'status', label: 'Status', options: masterStatuses.map((x) => [x, x]) },
          { key: 'type', label: 'Part type', options: partTypes.map((t) => [t, words(t)]) }]} />
      <PagedTable q={q} page={page} size={size} onPage={setPage} onSize={setSize} sort={sort} onSort={setSort} onRow={(r) => nav(`/sequfloh/parts/${r.id}`)} empty="No parts match these filters."
        cols={[{ key: 'partNumber', label: 'Part number', sort: true, render: (r) => <Mono>{r.partNumber}</Mono> }, { key: 'name', label: 'Name', sort: true }, { key: 'revision', label: 'Rev' },
          { key: 'productFamily', label: 'Product family', sort: true, render: (r) => r.productFamily ?? '—' }, { key: 'partType', label: 'Type', render: (r) => words(r.partType) },
          { key: 'assemblyType', label: 'Assembly type', render: (r) => r.assemblyType ?? '—' }, { key: 'unitOfMeasure', label: 'UoM' },
          { key: 'flags', label: 'Repairable / Serialized / Lot', render: (r) => `${yes(r.isRepairable)} / ${yes(r.isSerialized)} / ${yes(r.isLotControlled)}` },
          { key: 'status', label: 'Status', render: (r) => <SqfStatus status={r.status} /> }, { key: 'updatedAt', label: 'Updated', sort: true, render: (r) => fmtDateTime(r.updatedAt) }]} />
      <PartDialog open={open} onClose={() => setOpen(false)} onSaved={(id) => nav(`/sequfloh/parts/${id}`)} />
    </SqfPage>
  );
}

interface PartDetail { part: PartRow & { productId?: string; material?: string; hasSpecialCharacteristics: boolean; siteId: string; departmentId?: string; rowVersion: number; createdAt: string; createdBy: string; updatedBy: string };
  revisions: { id: string; revision: string; reason: string; createdAt: string }[]; partsLists: { id: string; number: string; title: string; revision: string; status: string; listType: string }[];
  related: { partsLists: number }; canEdit: boolean; canEditMaster: boolean }

function PartDialog({ open, onClose, onSaved, edit }: { open: boolean; onClose: () => void; onSaved: (id: string) => void; edit?: PartDetail['part'] }) {
  const sites = useSites();
  const { can } = useAuth();
  const drawingsOk = can('sequfloh.manage_drawings');
  const [links, setLinks] = useState<{ id: string; label: string }[]>([]);
  const [inline, setInline] = useState<ReturnType<typeof blankDrawing> | null>(null);
  const blank = { partNumber: '', name: '', description: '', partType: 'Component', revision: 'A', unitOfMeasure: 'EA', material: '', siteId: '', status: 'Active',
    productFamily: '', assemblyType: '', isRepairable: false, isSerialized: false, isLotControlled: false, drawingNumber: '', specification: '' };
  const [f, setF] = useState(blank);
  const [err, setErr] = useState<string | null>(null);
  const init = () => {
    setLinks([]); setInline(null);
    if (edit && drawingsOk) get<PartDrawing[]>(`/api/drawings/for-part?partId=${edit.id}`).then((l) => setLinks(l.map((d) => ({ id: d.id, label: `${d.drawingNumber ? d.drawingNumber + ' · ' : ''}${d.title} (rev ${d.currentRevision})` })))).catch(() => undefined);
    setErr(null); setF(edit ? { ...blank, ...Object.fromEntries(Object.entries(edit).map(([k, x]) => [k, x ?? ''])) } as typeof blank : { ...blank, siteId: sites.data?.[0]?.id ?? '' }); };
  const s = (k: keyof typeof blank) => (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: e.target.value });
  const b = (k: keyof typeof blank) => (_: unknown, c: boolean) => setF({ ...f, [k]: c });
  const save = async () => {
    setErr(null);
    const body = { part: { partNumber: f.partNumber, name: f.name, description: nul(f.description), partType: partTypeValue[f.partType] ?? 0, revision: f.revision, unitOfMeasure: f.unitOfMeasure, productId: edit?.productId ?? null,
      material: nul(f.material), hasSpecialCharacteristics: edit?.hasSpecialCharacteristics ?? false, siteId: f.siteId, departmentId: edit?.departmentId ?? null, status: Math.max(0, masterStatuses.indexOf(f.status)), rowVersion: edit?.rowVersion ?? 0 },
      execution: { productFamily: nul(f.productFamily), assemblyType: nul(f.assemblyType), isRepairable: f.isRepairable, isSerialized: f.isSerialized, isLotControlled: f.isLotControlled,
        drawingNumber: nul(f.drawingNumber), specification: nul(f.specification), rowVersion: edit?.rowVersion ?? 0 } };
    try {
      let id = edit?.id;
      if (edit) await put(`${S}/parts/${edit.id}`, body); else id = (await post<{ id: string }>(`${S}/parts`, body)).id;
      if (drawingsOk && id) {
        try {
          await put(`/api/drawings/for-part/${id}`, { ids: links.map((l) => l.id) });
          if (inline?.file) await post(`/api/drawings/for-part/${id}/upload`, drawingForm(inline));
        } catch (e) { onSaved(id); setErr(`The part was saved, but the drawings were not: ${(e as Error).message}`); return; }
      }
      onSaved(id!);
      onClose();
    } catch (e) { setErr((e as Error).message); }
  };
  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="md" TransitionProps={{ onEnter: init }}>
      <DialogTitle>{edit ? `Edit part ${edit.partNumber}` : 'New part'}</DialogTitle>
      <DialogContent>
        {err && <Alert severity="error" sx={{ mb: 2 }}>{err}</Alert>}
        <Box sx={{ pt: 1 }}><Grid>
          <TextField label="Part number" required value={f.partNumber} onChange={s('partNumber')} />
          <TextField label="Name" required value={f.name} onChange={s('name')} />
          <TextField label="Revision" required value={f.revision} onChange={s('revision')} />
          <TextField select label="Part type" value={f.partType} onChange={s('partType')}>{partTypes.map((t) => <MenuItem key={t} value={t}>{words(t)}</MenuItem>)}</TextField>
          <TextField label="Unit of measure" value={f.unitOfMeasure} onChange={s('unitOfMeasure')} />
          <TextField select label="Site" required value={f.siteId} onChange={s('siteId')}>{(sites.data ?? []).map((x) => <MenuItem key={x.id} value={x.id}>{x.code} · {x.name}</MenuItem>)}{setupOptions('site', sites.data)}</TextField>
          <TextField label="Product family" value={f.productFamily} onChange={s('productFamily')} />
          <TextField label="Assembly type" value={f.assemblyType} onChange={s('assemblyType')} />
          <TextField select label="Status" value={f.status} onChange={s('status')}>{masterStatuses.map((t) => <MenuItem key={t} value={t}>{t}</MenuItem>)}</TextField>
          <TextField label="Drawing number" value={f.drawingNumber} onChange={s('drawingNumber')} />
          <TextField label="Specification" value={f.specification} onChange={s('specification')} />
          <TextField label="Material" value={f.material} onChange={s('material')} />
        </Grid>
        <TextField label="Description" fullWidth multiline minRows={2} value={f.description} onChange={s('description')} sx={{ mt: 2 }} />
        <Stack direction="row" gap={2} sx={{ mt: 1 }}>
          <FormControlLabel control={<Checkbox checked={f.isRepairable} onChange={b('isRepairable')} />} label="Repairable" />
          <FormControlLabel control={<Checkbox checked={f.isSerialized} onChange={b('isSerialized')} />} label="Serialized" />
          <FormControlLabel control={<Checkbox checked={f.isLotControlled} onChange={b('isLotControlled')} />} label="Lot controlled" />
        </Stack>
        {drawingsOk && <Paper variant="outlined" sx={{ p: 2, mt: 1 }}>
          <Typography variant="subtitle2" sx={{ mb: 1.5 }}>Drawings</Typography>
          <DrawingPicker value={links} onChange={setLinks} />
          {inline ? <Box sx={{ mt: 2 }}><DrawingFileFields f={inline} set={setInline} /><Button size="small" sx={{ mt: 1 }} onClick={() => setInline(null)}>Don't upload</Button></Box>
            : <Button size="small" sx={{ mt: 1 }} onClick={() => setInline(blankDrawing())}>Upload drawing</Button>}
        </Paper>}</Box>
      </DialogContent>
      <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" onClick={save} disabled={!f.partNumber.trim() || !f.name.trim() || !f.siteId || (!!inline?.file && (!inline.title.trim() || !inline.revision.trim()))}>Save</Button></DialogActions>
    </Dialog>
  );
}

const Field = ({ label, children }: { label: string; children: React.ReactNode }) =>
  <Box><Typography variant="caption" color="text.secondary">{label}</Typography><Typography variant="body2">{children ?? '—'}</Typography></Box>;

export function PartDetailPage() {
  const { id } = useParams();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['sqf-part', id], queryFn: () => get<PartDetail>(`${S}/parts/${id}`) });
  const history = useQuery({ queryKey: ['sqf-part-history', id], queryFn: () => get<AuditRow[]>(`${S}/parts/${id}/history`) });
  const [edit, setEdit] = useState(false);
  const [confirm, setConfirm] = useState(false);
  const [tab, setTab] = useState('overview');
  const p = q.data?.part;
  const refresh = () => { qc.invalidateQueries({ queryKey: ['drawings-for-part'] }); qc.invalidateQueries({ queryKey: ['sqf-part', id] }); qc.invalidateQueries({ queryKey: ['sqf-part-history', id] }); };
  return (
    <SqfPage title={p ? `${p.partNumber} · ${p.name}` : 'Part'} crumbs={crumbs('Part Master', { label: p?.partNumber ?? '…' })} recent={p ? { label: `Part ${p.partNumber}`, itemType: 'Part', recordId: p.id } : null}
      tabs={[{ label: 'Overview', value: 'overview' }, { label: 'Parts lists', value: 'lists' }, { label: 'Revisions', value: 'revisions' }, { label: 'History', value: 'history' }]} tab={tab} onTab={setTab}
      actions={q.data?.canEdit && p && <>
        <Button variant="outlined" onClick={() => setEdit(true)}>Edit</Button>
        <Button color={p.status === 'Inactive' ? 'primary' : 'error'} onClick={() => setConfirm(true)}>{p.status === 'Inactive' ? 'Reactivate' : 'Deactivate'}</Button></>}>
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : p && <>
        {tab === 'overview' && <Paper variant="outlined" sx={{ p: 2 }}>
          {!q.data!.canEditMaster && q.data!.canEdit && <Alert severity="info" sx={{ mb: 2 }}>You can change SEQUFLOH execution attributes. Core Part Master fields need Master Data edit permission.</Alert>}
          <Grid cols={4}>
            <Field label="Part number"><Mono>{p.partNumber}</Mono></Field><Field label="Name">{p.name}</Field><Field label="Revision">{p.revision}</Field><Field label="Status"><SqfStatus status={p.status} /></Field>
            <Field label="Product family">{p.productFamily}</Field><Field label="Part type">{words(p.partType)}</Field><Field label="Assembly type">{p.assemblyType}</Field><Field label="Unit of measure">{p.unitOfMeasure}</Field>
            <Field label="Repairable">{yes(p.isRepairable)}</Field><Field label="Serialized">{yes(p.isSerialized)}</Field><Field label="Lot controlled">{yes(p.isLotControlled)}</Field><Field label="Material">{p.material}</Field>
            <Field label="Drawing number">{p.drawingNumber}</Field><Field label="Specification">{p.specification}</Field><Field label="Created">{`${fmtDateTime(p.createdAt)} · ${p.createdBy}`}</Field><Field label="Last updated">{`${fmtDateTime(p.updatedAt)} · ${p.updatedBy}`}</Field>
          </Grid>
          {p.description && <Typography variant="body2" sx={{ mt: 2 }}>{p.description}</Typography>}
          <Typography variant="subtitle2" sx={{ mt: 2, mb: 1 }}>Drawings</Typography>
          <PartDrawingLinks partId={p.id} />
          <Typography variant="subtitle2" sx={{ mt: 2 }}>Related SEQUFLOH records</Typography>
          <Typography variant="body2">Parts lists: {q.data!.related.partsLists}</Typography>
        </Paper>}
        {tab === 'lists' && (q.data!.partsLists.length === 0 ? <Empty text="No parts lists use this part." /> : <SimpleTable head={['Number', 'Title', 'Rev', 'Type', 'Status']}
          rows={q.data!.partsLists.map((l) => [<Link key="n" component={RouterLink} to={`/sequfloh/parts-lists/${l.id}`}><Mono>{l.number}</Mono></Link>, l.title, l.revision, words(l.listType), <SqfStatus key="s" status={l.status} />])} />)}
        {tab === 'revisions' && (q.data!.revisions.length === 0 ? <Empty text="No Part Master revisions recorded." /> : <SimpleTable head={['Revision', 'Reason', 'Date']} rows={q.data!.revisions.map((r) => [r.revision, r.reason, fmtDateTime(r.createdAt)])} />)}
        {tab === 'history' && <HistoryTable q={history} />}
        <PartDialog open={edit} edit={p} onClose={() => setEdit(false)} onSaved={refresh} />
        <Confirm open={confirm} title={p.status === 'Inactive' ? 'Reactivate part' : 'Deactivate part'} confirm={p.status === 'Inactive' ? 'Reactivate' : 'Deactivate'} danger={p.status !== 'Inactive'}
          text={p.status === 'Inactive' ? 'The part becomes available for new SEQUFLOH records again.' : 'The part stays on existing records but cannot be selected for new ones.'}
          onClose={() => setConfirm(false)} onConfirm={async () => { await post(`${S}/parts/${p.id}/${p.status === 'Inactive' ? 'reactivate' : 'deactivate'}`, {}); refresh(); }} />
      </>}
    </SqfPage>
  );
}

export function SimpleTable({ head, rows }: { head: string[]; rows: React.ReactNode[][] }) {
  return <Paper variant="outlined"><TableContainer sx={{ overflowX: 'auto' }}><Table size="small">
    <TableHead><TableRow>{head.map((h) => <TableCell key={h}>{h}</TableCell>)}</TableRow></TableHead>
    <TableBody>{rows.map((r, i) => <TableRow key={i}>{r.map((c, j) => <TableCell key={j}>{c}</TableCell>)}</TableRow>)}</TableBody></Table></TableContainer></Paper>;
}

export interface AuditRow { occurredAt: string; userName?: string; action: string; description?: string; reason?: string }
export function HistoryTable({ q }: { q: { data?: AuditRow[]; isLoading: boolean; error: unknown } }) {
  if (q.isLoading) return <Loading />;
  if (q.error) return <ErrorState error={q.error} />;
  if (!q.data?.length) return <Empty text="No audit history yet." />;
  return <SimpleTable head={['When', 'Action', 'User', 'Details']} rows={q.data.map((h) => [fmtDateTime(h.occurredAt), words(h.action), h.userName ?? '—', [h.description, h.reason].filter(Boolean).join(' · ')])} />;
}

// ------------------------------------------------------------------ parts lists
interface ListRow { id: string; number: string; title: string; revision: string; status: string; listType: string; updatedAt: string; items: number; parentPart?: string }
const listTypes = ['Standard', 'RepairKit', 'AssemblyBom', 'RepairParts', 'Custom'];

export function PartsListsPage() {
  const { can } = useAuth();
  const nav = useNavigate();
  const [f, setF] = useState<Record<string, string>>({}); const [search, setSearch] = useState('');
  const [page, setPage] = useState(1); const [size, setSize] = useState(25); const [sort, setSort] = useState('updatedAt:desc');
  const q = useQuery({ queryKey: ['sqf-lists', f, search, page, size, sort], queryFn: () => get<Paged<ListRow>>(`${S}/parts-lists?${qs({ q: search, status: f.status, type: f.type, sort, page, pageSize: size })}`) });
  const [open, setOpen] = useState(false);
  return (
    <SqfPage title="Parts Lists" crumbs={crumbs('Parts Lists')} description="Controlled parts lists, repair kits and assembly BOMs. Released lists are changed only through a new revision."
      actions={can('sequfloh.manage_parts_lists') && <Button variant="contained" onClick={() => setOpen(true)}>New parts list</Button>}>
      <Filters search={search} onSearch={(s) => { setSearch(s); setPage(1); }} values={f} onChange={(k, v) => { setF({ ...f, [k]: v }); setPage(1); }} onClear={() => { setF({}); setSearch(''); }}
        filters={[{ key: 'status', label: 'Status', options: [['Draft', 'Draft'], ['Released', 'Released'], ['Inactive', 'Inactive']] }, { key: 'type', label: 'List type', options: listTypes.map((t) => [t, words(t)]) }]} />
      <PagedTable q={q} page={page} size={size} onPage={setPage} onSize={setSize} sort={sort} onSort={setSort} onRow={(r) => nav(`/sequfloh/parts-lists/${r.id}`)} empty="No parts lists yet."
        cols={[{ key: 'number', label: 'Number', sort: true, render: (r) => <Mono>{r.number}</Mono> }, { key: 'title', label: 'Title', sort: true }, { key: 'revision', label: 'Rev' },
          { key: 'listType', label: 'Type', render: (r) => words(r.listType) }, { key: 'parentPart', label: 'Parent part', render: (r) => r.parentPart ?? '—' }, { key: 'items', label: 'Items' },
          { key: 'status', label: 'Status', render: (r) => <SqfStatus status={r.status} /> }, { key: 'updatedAt', label: 'Updated', sort: true, render: (r) => fmtDateTime(r.updatedAt) }]} />
      <ListDialog open={open} onClose={() => setOpen(false)} onSaved={(id) => nav(`/sequfloh/parts-lists/${id}`)} />
    </SqfPage>
  );
}

function PartPicker({ value, onChange, label = 'Part' }: { value: string; onChange: (id: string, pn?: string) => void; label?: string }) {
  const parts = useQuery({ queryKey: ['sqf-part-pick'], queryFn: () => get<Paged<PartRow>>(`${S}/parts?view=active&pageSize=200`) });
  return <TextField select label={label} value={value} onChange={(e) => onChange(e.target.value, parts.data?.items.find((p) => p.id === e.target.value)?.partNumber)}>
    <MenuItem value="">—</MenuItem>{(parts.data?.items ?? []).map((p) => <MenuItem key={p.id} value={p.id}>{p.partNumber} · {p.name}</MenuItem>)}{setupOptions('sqfPart', parts.data?.items)}</TextField>;
}

interface ListDetail { list: { id: string; number: string; title: string; parentPartId?: string; revision: string; status: string; description?: string; listType: string; releasedAt?: string; releasedBy: string;
  rowVersion: number; createdAt: string; updatedAt: string; createdBy: string; updatedBy: string; parentPart?: { id: string; partNumber: string; name: string } };
  items: ItemRow[]; revisions: { id: string; revision: string; statusAtRevision: string; reason?: string; snapshot: string; createdAt: string; createdBy?: string }[]; canEdit: boolean }
interface ItemRow { id: string; itemNumber: number; partId?: string; partNumber: string; description?: string; quantity: number; unit?: string; required: boolean; alternatePartNumber?: string; traceability: string; notes?: string }

function ListDialog({ open, onClose, onSaved, edit }: { open: boolean; onClose: () => void; onSaved: (id: string) => void; edit?: ListDetail['list'] }) {
  const [f, setF] = useState({ title: '', parentPartId: '', description: '', listType: 'Standard' });
  const [err, setErr] = useState<string | null>(null);
  useUnsaved(open && !!f.title && !edit);
  const save = async () => {
    const body = { title: f.title, parentPartId: f.parentPartId || null, description: nul(f.description), listType: f.listType, rowVersion: edit?.rowVersion ?? 0 };
    try { if (edit) { await put(`${S}/parts-lists/${edit.id}`, body); onSaved(edit.id); } else onSaved((await post<{ id: string }>(`${S}/parts-lists`, body)).id); onClose(); } catch (e) { setErr((e as Error).message); }
  };
  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" TransitionProps={{ onEnter: () => { setErr(null); setF(edit ? { title: edit.title, parentPartId: edit.parentPartId ?? '', description: edit.description ?? '', listType: edit.listType } : { title: '', parentPartId: '', description: '', listType: 'Standard' }); } }}>
      <DialogTitle>{edit ? `Edit ${edit.number}` : 'New parts list'}</DialogTitle>
      <DialogContent><Stack gap={2} sx={{ pt: 1 }}>
        {err && <Alert severity="error">{err}</Alert>}
        <TextField label="Title" required value={f.title} onChange={(e) => setF({ ...f, title: e.target.value })} />
        <TextField select label="List type" value={f.listType} onChange={(e) => setF({ ...f, listType: e.target.value })}>{listTypes.map((t) => <MenuItem key={t} value={t}>{words(t)}</MenuItem>)}</TextField>
        <PartPicker label="Parent part" value={f.parentPartId} onChange={(id) => setF({ ...f, parentPartId: id })} />
        <TextField label="Description" multiline minRows={2} value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} />
      </Stack></DialogContent>
      <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" disabled={!f.title.trim()} onClick={save}>Save</Button></DialogActions>
    </Dialog>
  );
}

function ItemDialog({ open, listId, edit, onClose, onSaved }: { open: boolean; listId: string; edit?: ItemRow | null; onClose: () => void; onSaved: () => void }) {
  const blank = { partId: '', partNumber: '', description: '', quantity: '1', unit: 'EA', required: true, alternatePartNumber: '', traceability: 'None', notes: '' };
  const [f, setF] = useState(blank);
  const [err, setErr] = useState<string | null>(null);
  const save = async () => {
    const body = { partId: f.partId || null, partNumber: nul(f.partNumber), description: nul(f.description), quantity: numOrNull(f.quantity) ?? 0, unit: nul(f.unit), required: f.required,
      alternatePartNumber: nul(f.alternatePartNumber), traceability: f.traceability, notes: nul(f.notes) };
    try { if (edit) await put(`${S}/parts-lists/${listId}/items/${edit.id}`, body); else await post(`${S}/parts-lists/${listId}/items`, body); onSaved(); onClose(); } catch (e) { setErr((e as Error).message); }
  };
  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" TransitionProps={{ onEnter: () => { setErr(null); setF(edit ? { ...blank, ...Object.fromEntries(Object.entries(edit).map(([k, v]) => [k, v === null || v === undefined ? '' : typeof v === 'boolean' ? v : String(v)])) } as typeof blank : blank); } }}>
      <DialogTitle>{edit ? `Edit item ${edit.itemNumber}` : 'Add item'}</DialogTitle>
      <DialogContent><Stack gap={2} sx={{ pt: 1 }}>
        {err && <Alert severity="error">{err}</Alert>}
        <PartPicker label="Part from Part Master" value={f.partId} onChange={(id, pn) => setF({ ...f, partId: id, partNumber: pn ?? f.partNumber })} />
        <TextField label="Part number" helperText="Pick a Part Master part or type a part number" value={f.partNumber} onChange={(e) => setF({ ...f, partNumber: e.target.value })} />
        <TextField label="Description" value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} />
        <Stack direction="row" gap={2}><TextField label="Quantity" type="number" value={f.quantity} onChange={(e) => setF({ ...f, quantity: e.target.value })} />
          <TextField label="Unit" value={f.unit} onChange={(e) => setF({ ...f, unit: e.target.value })} /></Stack>
        <TextField select label="Traceability" value={f.traceability} onChange={(e) => setF({ ...f, traceability: e.target.value })}>{['None', 'Lot', 'Serial', 'LotAndSerial'].map((t) => <MenuItem key={t} value={t}>{words(t)}</MenuItem>)}</TextField>
        <TextField label="Alternate part number" value={f.alternatePartNumber} onChange={(e) => setF({ ...f, alternatePartNumber: e.target.value })} />
        <FormControlLabel control={<Checkbox checked={f.required} onChange={(_, c) => setF({ ...f, required: c })} />} label="Required" />
        <TextField label="Notes" multiline value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} />
      </Stack></DialogContent>
      <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" onClick={save}>Save item</Button></DialogActions>
    </Dialog>
  );
}

export function PartsListDetailPage() {
  const { id } = useParams();
  const nav = useNavigate();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['sqf-list', id], queryFn: () => get<ListDetail>(`${S}/parts-lists/${id}`) });
  const history = useQuery({ queryKey: ['sqf-list-history', id], queryFn: () => get<AuditRow[]>(`${S}/parts-lists/${id}/history`) });
  const [tab, setTab] = useState('items');
  const [dlg, setDlg] = useState<null | 'edit' | 'item' | 'release' | 'revise' | 'active' | 'copy'>(null);
  const [item, setItem] = useState<ItemRow | null>(null);
  const [copyFrom, setCopyFrom] = useState('');
  const [err, setErr] = useState<string | null>(null);
  const lists = useQuery({ queryKey: ['sqf-list-pick'], queryFn: () => get<Paged<ListRow>>(`${S}/parts-lists?pageSize=200`), enabled: dlg === 'copy' });
  const refresh = () => { qc.invalidateQueries({ queryKey: ['sqf-list', id] }); qc.invalidateQueries({ queryKey: ['sqf-list-history', id] }); };
  const act = async (fn: () => Promise<unknown>) => { setErr(null); try { await fn(); refresh(); } catch (e) { setErr((e as Error).message); } };
  const l = q.data?.list;
  const editable = !!q.data?.canEdit && l?.status === 'Draft';
  const items = q.data?.items ?? [];
  const move = (i: number, d: number) => { const ids = items.map((x) => x.id); [ids[i], ids[i + d]] = [ids[i + d], ids[i]]; act(() => post(`${S}/parts-lists/${id}/items/reorder`, { ids })); };
  return (
    <SqfPage title={l ? `${l.number} · ${l.title}` : 'Parts list'} crumbs={crumbs('Parts Lists', { label: l?.number ?? '…' })} recent={l ? { label: `Parts list ${l.number}`, itemType: 'PartsList', recordId: l.id } : null}
      tabs={[{ label: 'Items', value: 'items' }, { label: 'Revisions', value: 'revisions' }, { label: 'History', value: 'history' }]} tab={tab} onTab={setTab}
      actions={q.data?.canEdit && l && <>
        {l.status === 'Draft' && <Button variant="outlined" onClick={() => setDlg('edit')}>Edit</Button>}
        {l.status === 'Draft' && <Button variant="contained" onClick={() => setDlg('release')}>Release</Button>}
        {l.status === 'Released' && <Button variant="contained" onClick={() => setDlg('revise')}>New revision</Button>}
        <Button onClick={() => act(async () => { const r = await post<{ id: string }>(`${S}/parts-lists/${l.id}/duplicate`, {}); nav(`/sequfloh/parts-lists/${r.id}`); })}>Duplicate</Button>
        <Button color={l.status === 'Inactive' ? 'primary' : 'error'} onClick={() => setDlg('active')}>{l.status === 'Inactive' ? 'Reactivate' : 'Deactivate'}</Button></>}>
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : l && <>
        {err && <Alert severity="error" sx={{ mb: 2 }} onClose={() => setErr(null)}>{err}</Alert>}
        <Paper variant="outlined" sx={{ p: 2, mb: 2 }}><Grid cols={4}>
          <Field label="Number"><Mono>{l.number}</Mono></Field><Field label="Revision">{l.revision}</Field><Field label="Status"><SqfStatus status={l.status} /></Field><Field label="List type">{words(l.listType)}</Field>
          <Field label="Parent part">{l.parentPart ? <Link component={RouterLink} to={`/sequfloh/parts/${l.parentPart.id}`}>{l.parentPart.partNumber} · {l.parentPart.name}</Link> : '—'}</Field>
          <Field label="Released">{l.releasedAt ? `${fmtDateTime(l.releasedAt)} · ${l.releasedBy}` : '—'}</Field><Field label="Created">{`${fmtDateTime(l.createdAt)} · ${l.createdBy}`}</Field><Field label="Last updated">{`${fmtDateTime(l.updatedAt)} · ${l.updatedBy}`}</Field>
        </Grid>{l.description && <Typography variant="body2" sx={{ mt: 1 }}>{l.description}</Typography>}
          {l.status === 'Released' && <Alert severity="info" sx={{ mt: 2 }}>This list is released and locked. Create a new revision to change it.</Alert>}</Paper>
        {tab === 'items' && <>
          {editable && <Stack direction="row" gap={1} sx={{ mb: 1 }}><Button variant="outlined" onClick={() => { setItem(null); setDlg('item'); }}>Add item</Button><Button onClick={() => setDlg('copy')}>Copy items from another list</Button></Stack>}
          {items.length === 0 ? <Empty text="No items on this list yet." /> : <SimpleTable head={['#', 'Part number', 'Description', 'Qty', 'Unit', 'Required', 'Traceability', 'Alternate', 'Notes', ...(editable ? [''] : [])]}
            rows={items.map((x, i) => [x.itemNumber, x.partId ? <Link key="p" component={RouterLink} to={`/sequfloh/parts/${x.partId}`}><Mono>{x.partNumber}</Mono></Link> : <Mono key="p">{x.partNumber}</Mono>,
              x.description ?? '—', x.quantity, x.unit ?? '—', yes(x.required), words(x.traceability), x.alternatePartNumber ?? '—', x.notes ?? '',
              ...(editable ? [<Stack key="a" direction="row">
                <Tooltip title="Move up"><span><IconButton size="small" aria-label={`Move item ${x.itemNumber} up`} disabled={i === 0} onClick={() => move(i, -1)}><ArrowUpward fontSize="small" /></IconButton></span></Tooltip>
                <Tooltip title="Move down"><span><IconButton size="small" aria-label={`Move item ${x.itemNumber} down`} disabled={i === items.length - 1} onClick={() => move(i, 1)}><ArrowDownward fontSize="small" /></IconButton></span></Tooltip>
                <Button size="small" onClick={() => { setItem(x); setDlg('item'); }}>Edit</Button>
                <Tooltip title="Duplicate"><IconButton size="small" aria-label={`Duplicate item ${x.itemNumber}`} onClick={() => act(() => post(`${S}/parts-lists/${id}/items/${x.id}/duplicate`, {}))}><ContentCopy fontSize="small" /></IconButton></Tooltip>
                <Tooltip title="Remove"><IconButton size="small" aria-label={`Remove item ${x.itemNumber}`} onClick={() => window.confirm('Remove this item?') && act(() => del(`${S}/parts-lists/${id}/items/${x.id}`))}><DeleteOutline fontSize="small" /></IconButton></Tooltip>
              </Stack>] : [])])} />}
        </>}
        {tab === 'revisions' && (q.data!.revisions.length === 0 ? <Empty text="No earlier revisions." /> : <SimpleTable head={['Revision', 'Status', 'Reason', 'Superseded', 'By', 'Items']}
          rows={q.data!.revisions.map((r) => [r.revision, words(r.statusAtRevision), r.reason ?? '—', fmtDateTime(r.createdAt), r.createdBy ?? '—', (() => { try { return (JSON.parse(r.snapshot).items ?? []).length; } catch { return '—'; } })()])} />)}
        {tab === 'history' && <HistoryTable q={history} />}
        <ListDialog open={dlg === 'edit'} edit={l} onClose={() => setDlg(null)} onSaved={refresh} />
        <ItemDialog open={dlg === 'item'} listId={l.id} edit={item} onClose={() => setDlg(null)} onSaved={refresh} />
        <Confirm open={dlg === 'release'} title="Release parts list" confirm="Release" text="Releasing locks the list. Further changes need a new revision." onClose={() => setDlg(null)}
          onConfirm={async () => { await post(`${S}/parts-lists/${l.id}/release`, {}); refresh(); }} />
        <Confirm open={dlg === 'revise'} title="New revision" confirm="Create revision" reason="required" reasonLabel="Revision reason" text={`Revision ${l.revision} is kept as a snapshot and a new draft revision opens.`}
          onClose={() => setDlg(null)} onConfirm={async (reason) => { await post(`${S}/parts-lists/${l.id}/revise`, { reason }); refresh(); }} />
        <Confirm open={dlg === 'active'} title={l.status === 'Inactive' ? 'Reactivate parts list' : 'Deactivate parts list'} confirm={l.status === 'Inactive' ? 'Reactivate' : 'Deactivate'} danger={l.status !== 'Inactive'}
          text={l.status === 'Inactive' ? 'The list returns to its previous status.' : 'The list can no longer be selected for new work instructions.'} onClose={() => setDlg(null)}
          onConfirm={async () => { await post(`${S}/parts-lists/${l.id}/${l.status === 'Inactive' ? 'reactivate' : 'deactivate'}`, {}); refresh(); }} />
        <Dialog open={dlg === 'copy'} onClose={() => setDlg(null)} fullWidth maxWidth="sm">
          <DialogTitle>Copy items from another list</DialogTitle>
          <DialogContent><TextField select fullWidth label="Source list" value={copyFrom} onChange={(e) => setCopyFrom(e.target.value)} sx={{ mt: 1 }}>
            {(lists.data?.items ?? []).filter((x) => x.id !== l.id).map((x) => <MenuItem key={x.id} value={x.id}>{x.number} · {x.title} ({x.items} items)</MenuItem>)}</TextField></DialogContent>
          <DialogActions><Button onClick={() => setDlg(null)}>Cancel</Button><Button variant="contained" disabled={!copyFrom} onClick={() => { setDlg(null); act(() => post(`${S}/parts-lists/${l.id}/copy-items`, { sourceListId: copyFrom })); }}>Copy items</Button></DialogActions>
        </Dialog>
      </>}
    </SqfPage>
  );
}

// ------------------------------------------------------------------ reusable operation steps
interface StepRow { id: string; number: string; title: string; category?: string; detailedAction: string; defaultTools?: string; defaultParts?: string; specification?: string; target?: number; minimum?: number;
  maximum?: number; unit?: string; safetyWarning?: string; qualityCheckpoint?: string; expectedResult?: string; evidenceRequirement?: string; requiredRole?: string; requiredQualification?: string;
  isActive: boolean; displayOrder: number; rowVersion: number }
const stepFields: [keyof StepRow, string, boolean?][] = [['title', 'Title', true], ['category', 'Category'], ['detailedAction', 'Detailed action', true], ['defaultTools', 'Default tools'], ['defaultParts', 'Default parts'],
  ['specification', 'Specification'], ['target', 'Target'], ['minimum', 'Minimum'], ['maximum', 'Maximum'], ['unit', 'Unit'], ['safetyWarning', 'Safety warning'], ['qualityCheckpoint', 'Quality checkpoint'],
  ['expectedResult', 'Expected result'], ['evidenceRequirement', 'Evidence requirement'], ['requiredRole', 'Required role'], ['requiredQualification', 'Required qualification']];

export function OperationStepsPage() {
  const { can } = useAuth();
  const qc = useQueryClient();
  const [search, setSearch] = useState(''); const [f, setF] = useState<Record<string, string>>({ active: 'true' });
  const q = useQuery({ queryKey: ['sqf-steps', search, f], queryFn: () => get<Paged<StepRow>>(`${S}/operation-steps?${qs({ q: search, active: f.active, pageSize: 200 })}`) });
  const [edit, setEdit] = useState<StepRow | null | undefined>(undefined);
  const [err, setErr] = useState<string | null>(null);
  const manage = can('sequfloh.manage_operation_steps');
  const refresh = () => qc.invalidateQueries({ queryKey: ['sqf-steps'] });
  const act = async (fn: () => Promise<unknown>) => { setErr(null); try { await fn(); refresh(); } catch (e) { setErr((e as Error).message); } };
  const rows = q.data?.items ?? [];
  const move = async (i: number, d: number) => {
    const all = (await get<Paged<StepRow>>(`${S}/operation-steps?pageSize=1000`)).items.map((x) => x.id);
    const a = all.indexOf(rows[i].id), b = all.indexOf(rows[i + d].id); [all[a], all[b]] = [all[b], all[a]];
    await act(() => post(`${S}/operation-steps/reorder`, { ids: all }));
  };
  return (
    <SqfPage title="Reusable Operation Steps" crumbs={crumbs('Reusable Operation Steps')} description="A library of standard steps that designers insert into work instructions. Changing a library step never changes released instructions."
      actions={manage && <Button variant="contained" onClick={() => setEdit(null)}>New operation step</Button>}>
      {err && <Alert severity="error" sx={{ mb: 2 }} onClose={() => setErr(null)}>{err}</Alert>}
      <Filters search={search} onSearch={setSearch} values={f} onChange={(k, v) => setF({ ...f, [k]: v })} onClear={() => { setSearch(''); setF({}); }}
        filters={[{ key: 'active', label: 'Active', options: [['true', 'Active'], ['false', 'Inactive']] }]} />
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : rows.length === 0 ? <Empty text="No operation steps yet." /> :
        <SimpleTable head={['Number', 'Title', 'Category', 'Action', 'Spec / range', 'Status', '']} rows={rows.map((s, i) => [<Mono key="n">{s.number}</Mono>, s.title, s.category ?? '—', s.detailedAction,
          [s.specification, s.target !== null && s.target !== undefined ? `${s.minimum ?? '—'} ≤ ${s.target} ≤ ${s.maximum ?? '—'} ${s.unit ?? ''}` : null].filter(Boolean).join(' · ') || '—',
          <SqfStatus key="s" status={s.isActive ? 'Active' : 'Inactive'} />,
          manage ? <Stack key="a" direction="row">
            <Tooltip title="Move up"><span><IconButton size="small" aria-label={`Move ${s.number} up`} disabled={i === 0} onClick={() => move(i, -1)}><ArrowUpward fontSize="small" /></IconButton></span></Tooltip>
            <Tooltip title="Move down"><span><IconButton size="small" aria-label={`Move ${s.number} down`} disabled={i === rows.length - 1} onClick={() => move(i, 1)}><ArrowDownward fontSize="small" /></IconButton></span></Tooltip>
            <Button size="small" onClick={() => setEdit(s)}>Edit</Button>
            <Button size="small" onClick={() => act(() => post(`${S}/operation-steps/${s.id}/duplicate`, {}))}>Duplicate</Button>
            <Button size="small" color={s.isActive ? 'error' : 'primary'} onClick={() => act(() => post(`${S}/operation-steps/${s.id}/${s.isActive ? 'deactivate' : 'activate'}`, {}))}>{s.isActive ? 'Deactivate' : 'Activate'}</Button>
          </Stack> : null])} />}
      <StepDialog edit={edit} onClose={() => setEdit(undefined)} onSaved={refresh} />
    </SqfPage>
  );
}

function StepDialog({ edit, onClose, onSaved }: { edit: StepRow | null | undefined; onClose: () => void; onSaved: () => void }) {
  const [f, setF] = useState<Record<string, string>>({});
  const [err, setErr] = useState<string | null>(null);
  const categories = useOptions('operation-categories');
  const save = async () => {
    const body: Record<string, unknown> = Object.fromEntries(stepFields.map(([k]) => [k, ['target', 'minimum', 'maximum'].includes(k) ? numOrNull(f[k]) : nul(f[k])]));
    body.isActive = edit?.isActive ?? true; body.rowVersion = edit?.rowVersion ?? 0;
    try { if (edit) await put(`${S}/operation-steps/${edit.id}`, body); else await post(`${S}/operation-steps`, body); onSaved(); onClose(); } catch (e) { setErr((e as Error).message); }
  };
  return (
    <Dialog open={edit !== undefined} onClose={onClose} fullWidth maxWidth="md"
      TransitionProps={{ onEnter: () => { setErr(null); setF(Object.fromEntries(stepFields.map(([k]) => [k, edit?.[k] === null || edit?.[k] === undefined ? '' : String(edit[k])]))); } }}>
      <DialogTitle>{edit ? `Edit ${edit.number}` : 'New operation step'}</DialogTitle>
      <DialogContent>{err && <Alert severity="error" sx={{ mb: 2 }}>{err}</Alert>}<Box sx={{ pt: 1 }}><Grid>
        {stepFields.map(([k, label, req]) => k === 'category' && (categories.data?.length ?? 0) > 0
          ? <TextField key={k} select label={label} value={f[k] ?? ''} onChange={(e) => setF({ ...f, [k]: e.target.value })}><MenuItem value="">—</MenuItem>{categories.data!.map((o) => <MenuItem key={o} value={o}>{o}</MenuItem>)}</TextField>
          : <TextField key={k} label={label} required={req} type={['target', 'minimum', 'maximum'].includes(k) ? 'number' : 'text'} multiline={k === 'detailedAction' || k === 'safetyWarning'}
            value={f[k] ?? ''} onChange={(e) => setF({ ...f, [k]: e.target.value })} />)}
      </Grid></Box></DialogContent>
      <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" disabled={!f.title?.trim() || !f.detailedAction?.trim()} onClick={save}>Save</Button></DialogActions>
    </Dialog>
  );
}

// ------------------------------------------------------------------ revision history
interface RevRow { type: string; recordId: string; number: string; title: string; revision: string; reason?: string; at: string; link: string }
export function RevisionHistoryPage() {
  const [search, setSearch] = useState(''); const [f, setF] = useState<Record<string, string>>({});
  const [page, setPage] = useState(1); const [size, setSize] = useState(25);
  const nav = useNavigate();
  const q = useQuery({ queryKey: ['sqf-revs', search, f, page, size], queryFn: () => get<Paged<RevRow & { id?: string }>>(`${S}/revision-history?${qs({ q: search, type: f.type, page, pageSize: size })}`) });
  return (
    <SqfPage title="Revision History" crumbs={crumbs('Revision History')} description="Revisions of parts and parts lists (work instruction revisions are added with the instruction engine).">
      <Filters search={search} onSearch={(s) => { setSearch(s); setPage(1); }} values={f} onChange={(k, v) => { setF({ ...f, [k]: v }); setPage(1); }} onClear={() => { setSearch(''); setF({}); }}
        filters={[{ key: 'type', label: 'Record type', options: [['part', 'Part'], ['parts-list', 'Parts list']] }]} />
      <PagedTable q={q} page={page} size={size} onPage={setPage} onSize={setSize} onRow={(r) => nav(r.link)} empty="No revisions recorded yet."
        cols={[{ key: 'type', label: 'Type' }, { key: 'number', label: 'Number', render: (r) => <Mono>{r.number}</Mono> }, { key: 'title', label: 'Title' }, { key: 'revision', label: 'Revision' },
          { key: 'reason', label: 'Reason', render: (r) => r.reason ?? '—' }, { key: 'at', label: 'Date', render: (r) => fmtDateTime(r.at) }]} />
    </SqfPage>
  );
}
