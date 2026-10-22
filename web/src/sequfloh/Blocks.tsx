import { useState } from 'react';
import { Alert, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, IconButton, Menu, MenuItem, Paper, Stack, TextField, Tooltip, Typography } from '@mui/material';
import ArrowUpward from '@mui/icons-material/ArrowUpward';
import ArrowDownward from '@mui/icons-material/ArrowDownward';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put } from '../api';
import { useAuth } from '../auth';
import { FilesPanel } from '../components/Files';
import { Confirm, S, nul } from './common';

/** One block of a work instruction page. The order of the list is the order on the page. */
export type BlockKind = 'text' | 'image' | 'table' | 'step' | 'caution' | 'check';
export interface Block {
  id?: string; kind: BlockKind; title?: string; body?: string; caption?: string; alt?: string; attachmentId?: string;
  columns?: string[]; rows?: string[][]; operation?: string; tools?: string; expected?: string; level?: 'note' | 'caution' | 'warning';
  label?: string; check?: 'check' | 'signoff'; signer?: string; required?: boolean;
}
export interface InstructionTemplate { id: string; name: string; description?: string | null; type?: string | null; isActive: boolean; ownerId: string; rowVersion: number; blocks: Block[] }

export const blockKinds: [BlockKind, string][] = [['text', 'Text'], ['image', 'Image or visual aid'], ['table', 'Table'], ['step', 'Step'], ['caution', 'Caution or warning'], ['check', 'Check or sign-off']];
const kindLabel = (k: BlockKind) => blockKinds.find(([x]) => x === k)?.[1] ?? k;

const nextOperation = (blocks: Block[]) => {
  const n = blocks.filter((b) => b.kind === 'step').map((b) => Number(b.operation)).filter((x) => Number.isFinite(x));
  return String((Math.floor(Math.max(0, ...n) / 10) + 1) * 10);
};
const fresh = (kind: BlockKind, blocks: Block[]): Block => {
  switch (kind) {
    case 'text': return { kind, title: '', body: '' };
    case 'image': return { kind, caption: '', alt: '' };
    case 'table': return { kind, title: '', columns: ['Item', 'Value'], rows: [['', '']] };
    case 'step': return { kind, operation: nextOperation(blocks), title: '', body: '' };
    case 'caution': return { kind, level: 'caution', body: '' };
    default: return { kind, label: '', check: 'check', signer: '', required: true };
  }
};

/** Problems the server would refuse, shown before saving. */
export function blockProblems(blocks: Block[]) {
  const p: string[] = [];
  const ops = new Map<string, number>();
  blocks.forEach((b, i) => {
    const n = `Block ${i + 1} (${kindLabel(b.kind).toLowerCase()})`;
    if ((b.kind === 'text' || b.kind === 'caution') && !b.body?.trim()) p.push(`${n}: add the text.`);
    if (b.kind === 'step') { if (!b.operation?.trim()) p.push(`${n}: add the operation number.`); if (!b.title?.trim()) p.push(`${n}: add the step title.`); }
    if (b.kind === 'check' && !b.label?.trim()) p.push(`${n}: say what is checked.`);
    if (b.kind === 'table' && (b.columns ?? []).some((c) => !c.trim())) p.push(`${n}: every column needs a heading.`);
    if (b.kind === 'step' && b.operation?.trim()) ops.set(b.operation.trim(), (ops.get(b.operation.trim()) ?? 0) + 1);
  });
  ops.forEach((c, op) => { if (c > 1) p.push(`Operation ${op} is used by more than one step.`); });
  return p;
}

interface FileRow { id: string; fileName: string; contentType: string; isArchived: boolean }

function BlockFields({ block, set, disabled, images }: { block: Block; set: (b: Block) => void; disabled?: boolean; images: FileRow[] }) {
  const f = (key: keyof Block, label: string, opts: { multiline?: boolean; required?: boolean; width?: number } = {}) =>
    <TextField size="small" label={label} value={(block[key] as string) ?? ''} disabled={disabled} required={opts.required} multiline={opts.multiline} minRows={opts.multiline ? 2 : undefined}
      sx={{ minWidth: opts.width ?? 200, flex: opts.multiline ? '1 1 100%' : '1 1 200px' }} onChange={(e) => set({ ...block, [key]: e.target.value })} />;
  switch (block.kind) {
    case 'text': return <>{f('title', 'Heading')}{f('body', 'Text', { multiline: true, required: true })}</>;
    case 'caution': return <>
      <TextField select size="small" label="Level" value={block.level ?? 'caution'} disabled={disabled} sx={{ width: 180 }} onChange={(e) => set({ ...block, level: e.target.value as Block['level'] })}>
        <MenuItem value="note">Note</MenuItem><MenuItem value="caution">Caution</MenuItem><MenuItem value="warning">Warning</MenuItem></TextField>
      {f('body', 'Text', { multiline: true, required: true })}</>;
    case 'step': return <>{f('operation', 'Operation number', { required: true, width: 140 })}{f('title', 'Step title', { required: true })}{f('body', 'Instructions', { multiline: true })}{f('tools', 'Tools and materials')}{f('expected', 'Expected result')}</>;
    case 'check': return <>
      {f('label', 'What is checked', { required: true })}
      <TextField select size="small" label="Type" value={block.check ?? 'check'} disabled={disabled} sx={{ width: 160 }} onChange={(e) => set({ ...block, check: e.target.value as Block['check'] })}>
        <MenuItem value="check">Check</MenuItem><MenuItem value="signoff">Sign-off</MenuItem></TextField>
      {f('signer', 'Signed by (for example Inspector)')}
      <FormControlLabel control={<Checkbox checked={!!block.required} disabled={disabled} onChange={(e) => set({ ...block, required: e.target.checked })} />} label="Required" /></>;
    case 'image': {
      const pics = images.filter((x) => !x.isArchived && x.contentType.startsWith('image/'));
      return <>
        <TextField select size="small" label="Picture" value={block.attachmentId ?? ''} disabled={disabled} sx={{ minWidth: 240 }} onChange={(e) => set({ ...block, attachmentId: e.target.value || undefined })}
          helperText={pics.length === 0 ? 'Attach pictures under Files on the Layout tab.' : undefined}>
          <MenuItem value=""><em>None yet</em></MenuItem>{pics.map((p) => <MenuItem key={p.id} value={p.id}>{p.fileName}</MenuItem>)}</TextField>
        {f('caption', 'Caption')}{f('alt', 'Description for screen readers')}
        {block.attachmentId && <Box component="img" src={`/api/files/${block.attachmentId}`} alt={block.alt || block.caption || 'Visual aid'} sx={{ maxWidth: 320, maxHeight: 220, border: 1, borderColor: 'divider', flex: '1 1 100%', objectFit: 'contain' }} />}</>;
    }
    case 'table': {
      const cols = block.columns ?? []; const rows = block.rows ?? [];
      const setCols = (c: string[]) => set({ ...block, columns: c, rows: rows.map((r) => c.map((_, i) => r[i] ?? '')) });
      return <Box sx={{ flex: '1 1 100%' }}>
        {f('title', 'Table title')}
        <Box sx={{ overflowX: 'auto', mt: 1 }}>
          <Box component="table" sx={{ borderCollapse: 'collapse', '& td': { p: 0.5, verticalAlign: 'top' } }}>
            <tbody>
              <tr>{cols.map((c, i) => <td key={i}><TextField size="small" label={`Column ${i + 1} heading`} value={c} disabled={disabled} onChange={(e) => setCols(cols.map((x, j) => (j === i ? e.target.value : x)))} /></td>)}
                {!disabled && <td><Stack direction="row"><Button size="small" disabled={cols.length >= 12} onClick={() => setCols([...cols, ''])}>Add column</Button>
                  <Button size="small" disabled={cols.length <= 1} onClick={() => setCols(cols.slice(0, -1))}>Remove last column</Button></Stack></td>}</tr>
              {rows.map((r, ri) => <tr key={ri}>{cols.map((c, ci) => <td key={ci}><TextField size="small" label={`Row ${ri + 1} ${c || `column ${ci + 1}`}`} value={r[ci] ?? ''} disabled={disabled}
                onChange={(e) => set({ ...block, rows: rows.map((x, j) => (j === ri ? cols.map((_, k) => (k === ci ? e.target.value : x[k] ?? '')) : x)) })} /></td>)}
                {!disabled && <td><Button size="small" color="error" onClick={() => set({ ...block, rows: rows.filter((_, j) => j !== ri) })}>Remove row {ri + 1}</Button></td>}</tr>)}
            </tbody>
          </Box>
        </Box>
        {!disabled && <Button size="small" sx={{ mt: 1 }} disabled={rows.length >= 200} onClick={() => set({ ...block, rows: [...rows, cols.map(() => '')] })}>Add row</Button>}
      </Box>;
    }
  }
}

/** Add, edit, reorder and remove the blocks of a work instruction or template. */
export function BlocksEditor({ blocks, onChange, disabled, ownerType, ownerId }: { blocks: Block[]; onChange: (b: Block[]) => void; disabled?: boolean; ownerType?: string; ownerId?: string }) {
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const [remove, setRemove] = useState<number | null>(null);
  const files = useQuery({ queryKey: ['files', ownerType, ownerId, false], queryFn: () => get<FileRow[]>(`/api/records/${ownerType}/${ownerId}/files?history=false`), enabled: !!ownerType && !!ownerId });
  const move = (i: number, d: -1 | 1) => { const j = i + d; if (j < 0 || j >= blocks.length) return; const n = [...blocks]; [n[i], n[j]] = [n[j], n[i]]; onChange(n); };
  const problems = blockProblems(blocks);
  return (
    <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label="Layout blocks">
      <Stack direction="row" justifyContent="space-between" alignItems="center" flexWrap="wrap" gap={1} sx={{ mb: 1.5 }}>
        <Typography variant="h3">Layout blocks ({blocks.length})</Typography>
        {!disabled && <>
          <Button size="small" variant="outlined" onClick={(e) => setAnchor(e.currentTarget)} aria-haspopup="menu">Add block</Button>
          <Menu anchorEl={anchor} open={!!anchor} onClose={() => setAnchor(null)}>
            {blockKinds.map(([k, l]) => <MenuItem key={k} onClick={() => { onChange([...blocks, fresh(k, blocks)]); setAnchor(null); }}>{l}</MenuItem>)}
          </Menu></>}
      </Stack>
      {problems.length > 0 && <Alert severity="warning" sx={{ mb: 1.5 }}><Box component="ul" sx={{ m: 0, pl: 2 }}>{problems.map((p) => <li key={p}>{p}</li>)}</Box></Alert>}
      {blocks.length === 0 && <Typography variant="body2" color="text.secondary">No blocks yet. Add text, pictures, tables, numbered steps, cautions and sign-offs in the order they appear on the page, or start from a template.</Typography>}
      <Stack gap={1.5}>
        {blocks.map((b, i) => (
          <Paper key={b.id ?? `new-${i}`} variant="outlined" component="section" aria-label={`Block ${i + 1}: ${kindLabel(b.kind)}`}
            sx={{ p: 1.5, borderLeft: 4, borderLeftColor: b.kind === 'caution' ? (b.level === 'warning' ? 'error.main' : b.level === 'note' ? 'info.main' : 'warning.main') : b.kind === 'check' ? 'success.main' : 'divider' }}>
            <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ mb: 1 }}>
              <Typography variant="subtitle2">{i + 1}. {kindLabel(b.kind)}{b.kind === 'step' && b.operation ? ` · Operation ${b.operation}` : ''}</Typography>
              {!disabled && <Stack direction="row">
                <Tooltip title="Move up"><span><IconButton size="small" aria-label={`Move block ${i + 1} up`} disabled={i === 0} onClick={() => move(i, -1)}><ArrowUpward fontSize="small" /></IconButton></span></Tooltip>
                <Tooltip title="Move down"><span><IconButton size="small" aria-label={`Move block ${i + 1} down`} disabled={i === blocks.length - 1} onClick={() => move(i, 1)}><ArrowDownward fontSize="small" /></IconButton></span></Tooltip>
                <Tooltip title="Remove"><IconButton size="small" aria-label={`Remove block ${i + 1}`} onClick={() => setRemove(i)}><DeleteOutline fontSize="small" /></IconButton></Tooltip>
              </Stack>}
            </Stack>
            <Stack direction="row" gap={1.5} flexWrap="wrap" alignItems="flex-start">
              <BlockFields block={b} disabled={disabled} images={files.data ?? []} set={(nb) => onChange(blocks.map((x, j) => (j === i ? nb : x)))} />
            </Stack>
          </Paper>))}
      </Stack>
      <Confirm open={remove !== null} title="Remove block" danger confirm="Remove" onClose={() => setRemove(null)}
        text={`Remove block ${remove !== null ? remove + 1 : ''} (${remove !== null && blocks[remove] ? kindLabel(blocks[remove].kind).toLowerCase() : ''})? The change is kept only when you save.`}
        onConfirm={async () => { if (remove !== null) onChange(blocks.filter((_, j) => j !== remove)); }} />
      {ownerType && ownerId && <Box sx={{ mt: 2 }}><Typography variant="subtitle2" sx={{ mb: 1 }}>Files (pictures for image blocks)</Typography><FilesPanel type={ownerType} id={ownerId} canEdit={!disabled} /></Box>}
    </Paper>
  );
}

export const useInstructionTemplates = (type?: string) =>
  useQuery({ queryKey: ['sqf-instruction-templates', type ?? ''], queryFn: () => get<InstructionTemplate[]>(`${S}/instruction-templates${type ? `?type=${type}` : ''}`) });

/** Pick an active template for the given instruction type (templates for any type are listed too). */
export function TemplatePicker({ type, value, onChange, disabled, label = 'Start from template' }: { type?: string; value: string; onChange: (id: string) => void; disabled?: boolean; label?: string }) {
  const q = useInstructionTemplates(type);
  return (
    <TextField select size="small" fullWidth label={label} value={value} disabled={disabled || !type} onChange={(e) => onChange(e.target.value)}>
      <MenuItem value=""><em>Blank (no template)</em></MenuItem>
      {(q.data ?? []).map((t) => <MenuItem key={t.id} value={t.id}>{t.name}{t.description ? ` · ${t.description}` : ''}</MenuItem>)}
    </TextField>
  );
}

/** "Use template" and "Save as template" for an instruction. */
export function TemplateActions({ id, type, canApply, canSave, beforeRun, onApplied }: { id: string; type: string; canApply: boolean; canSave: boolean; beforeRun: () => Promise<boolean>; onApplied: () => Promise<void> }) {
  const [open, setOpen] = useState<'apply' | 'save' | null>(null);
  const [tpl, setTpl] = useState('');
  const [replace, setReplace] = useState(false);
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [onlyType, setOnlyType] = useState(true);
  const [err, setErr] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const close = () => { setOpen(null); setErr(null); };
  const run = async () => {
    setErr(null);
    try {
      if (!(await beforeRun())) return;
      if (open === 'apply') { await post(`${S}/instructions/${id}/apply-template`, { templateId: tpl, replace }); await onApplied(); setDone('Template blocks added.'); }
      else { const r = await post<{ name: string }>(`${S}/instructions/${id}/save-as-template`, { name, description: nul(description), forThisTypeOnly: onlyType }); setDone(`Saved as template "${r.name}".`); }
      close();
    } catch (e) { setErr((e as Error).message); }
  };
  return <>
    {done && <Alert severity="success" onClose={() => setDone(null)} sx={{ mb: 1 }}>{done}</Alert>}
    <Stack direction="row" gap={1}>
      {canApply && <Button size="small" variant="outlined" onClick={() => { setTpl(''); setReplace(false); setOpen('apply'); }}>Use template</Button>}
      {canSave && <Button size="small" variant="outlined" onClick={() => { setName(''); setDescription(''); setOpen('save'); }}>Save as template</Button>}
    </Stack>
    <Dialog open={!!open} onClose={close} fullWidth maxWidth="sm">
      <DialogTitle>{open === 'apply' ? 'Use a template' : 'Save the layout as a template'}</DialogTitle>
      <DialogContent>
        {err && <Alert severity="error" sx={{ mb: 2 }}>{err}</Alert>}
        {open === 'apply' ? <Stack gap={2} sx={{ pt: 1 }}>
          <TemplatePicker type={type} value={tpl} onChange={setTpl} label="Template" />
          <FormControlLabel control={<Checkbox checked={replace} onChange={(e) => setReplace(e.target.checked)} />} label="Replace the blocks already here (otherwise the template's blocks are added after them)" />
        </Stack> : <Stack gap={2} sx={{ pt: 1 }}>
          <TextField label="Template name" value={name} onChange={(e) => setName(e.target.value)} required />
          <TextField label="Description" value={description} onChange={(e) => setDescription(e.target.value)} multiline minRows={2} />
          <FormControlLabel control={<Checkbox checked={onlyType} onChange={(e) => setOnlyType(e.target.checked)} />} label="Only for this instruction type" />
          <Typography variant="body2" color="text.secondary">Pictures stay with this instruction; the template keeps their captions as placeholders.</Typography>
        </Stack>}
      </DialogContent>
      <DialogActions><Button onClick={close}>Cancel</Button>
        <Button variant="contained" disabled={open === 'apply' ? !tpl : !name.trim()} onClick={run}>{open === 'apply' ? 'Use template' : 'Save template'}</Button></DialogActions>
    </Dialog>
  </>;
}

/** Saved layout templates: create, change, retire and restore. Their owner or a person who manages Sequfloh settings may change them. */
export function LayoutTemplates({ typeLabels }: { typeLabels: Record<string, string> }) {
  const { can, me } = useAuth();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['sqf-instruction-templates', 'all'], queryFn: () => get<InstructionTemplate[]>(`${S}/instruction-templates?includeRetired=true`) });
  const [edit, setEdit] = useState<(Omit<InstructionTemplate, 'id' | 'ownerId' | 'isActive' | 'rowVersion'> & { id?: string; rowVersion?: number }) | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const manage = (t: InstructionTemplate) => can('sequfloh.manage_settings') || (t.ownerId === me?.id && can('sequfloh.create_work_instruction'));
  const reload = () => qc.invalidateQueries({ queryKey: ['sqf-instruction-templates'] });
  const save = async () => {
    if (!edit) return;
    setErr(null);
    try {
      const body = { name: edit.name, description: nul(edit.description ?? ''), type: edit.type || null, blocks: edit.blocks, rowVersion: edit.rowVersion ?? 0 };
      if (edit.id) await put(`${S}/instruction-templates/${edit.id}`, body); else await post(`${S}/instruction-templates`, body);
      setEdit(null); await reload();
    } catch (e) { setErr((e as Error).message); }
  };
  const toggle = async (t: InstructionTemplate) => {
    setErr(null);
    try { await post(`${S}/instruction-templates/${t.id}/${t.isActive ? 'retire' : 'restore'}`); await reload(); } catch (e) { setErr((e as Error).message); }
  };
  return (
    <Paper variant="outlined" sx={{ p: 2, mb: 3 }} component="section" aria-label="Layout templates">
      <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ mb: 1 }}>
        <Typography variant="h3">Layout templates</Typography>
        {can('sequfloh.create_work_instruction') && <Button size="small" variant="outlined" onClick={() => setEdit({ name: '', description: '', type: null, blocks: [] })}>New layout template</Button>}
      </Stack>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>Reusable page layouts made of text, pictures, tables, numbered steps, cautions and sign-offs. Pick one when you create an instruction, or use Save as template on an instruction's Layout tab.</Typography>
      {err && !edit && <Alert severity="error" sx={{ mb: 1 }}>{err}</Alert>}
      {q.isLoading ? <Typography variant="body2">Loading…</Typography> : !(q.data ?? []).length ? <Typography variant="body2" color="text.secondary">No layout templates yet.</Typography> :
        <Stack gap={1}>{(q.data ?? []).map((t) => (
          <Stack key={t.id} direction="row" alignItems="center" justifyContent="space-between" gap={1} flexWrap="wrap" sx={{ borderBottom: 1, borderColor: 'divider', pb: 1, opacity: t.isActive ? 1 : 0.6 }}>
            <Box><Typography variant="subtitle2">{t.name}{t.isActive ? '' : ' (retired)'}</Typography>
              <Typography variant="caption" color="text.secondary">{t.type ? typeLabels[t.type] ?? t.type : 'Any instruction type'} · {t.blocks.length} blocks{t.description ? ` · ${t.description}` : ''}</Typography></Box>
            {manage(t) && <Stack direction="row" gap={1}>
              <Button size="small" onClick={() => setEdit({ id: t.id, name: t.name, description: t.description ?? '', type: t.type ?? null, blocks: t.blocks, rowVersion: t.rowVersion })}>Edit {t.name}</Button>
              <Button size="small" color={t.isActive ? 'error' : 'primary'} onClick={() => toggle(t)}>{t.isActive ? 'Retire' : 'Restore'} {t.name}</Button>
            </Stack>}
          </Stack>))}</Stack>}
      <Dialog open={!!edit} onClose={() => setEdit(null)} fullWidth maxWidth="lg">
        <DialogTitle>{edit?.id ? `Edit ${edit.name}` : 'New layout template'}</DialogTitle>
        <DialogContent>
          {err && <Alert severity="error" sx={{ mb: 2 }}>{err}</Alert>}
          {edit && <Stack gap={2} sx={{ pt: 1 }}>
            <Stack direction="row" gap={2} flexWrap="wrap">
              <TextField label="Template name" value={edit.name} required onChange={(e) => setEdit({ ...edit, name: e.target.value })} sx={{ flex: '1 1 260px' }} />
              <TextField select label="Instruction type" value={edit.type ?? ''} onChange={(e) => setEdit({ ...edit, type: e.target.value || null })} sx={{ width: 260 }}>
                <MenuItem value=""><em>Any instruction type</em></MenuItem>{Object.entries(typeLabels).map(([k, l]) => <MenuItem key={k} value={k}>{l}</MenuItem>)}</TextField>
            </Stack>
            <TextField label="Description" value={edit.description ?? ''} multiline minRows={2} onChange={(e) => setEdit({ ...edit, description: e.target.value })} />
            <BlocksEditor blocks={edit.blocks} onChange={(blocks) => setEdit({ ...edit, blocks })} />
          </Stack>}
        </DialogContent>
        <DialogActions><Button onClick={() => setEdit(null)}>Cancel</Button><Button variant="contained" disabled={!edit?.name.trim()} onClick={save}>Save template</Button></DialogActions>
      </Dialog>
    </Paper>
  );
}
