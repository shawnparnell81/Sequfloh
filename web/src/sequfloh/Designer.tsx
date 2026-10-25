import { useState } from 'react';
import { FormDialogTitle } from '../components/FullScreen';
import { Alert, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, FormControlLabel, IconButton, MenuItem, Paper, Stack, Table, TableBody, TableCell,
  TableContainer, TableHead, TableRow, TextField, Tooltip, Typography } from '@mui/material';
import ArrowUpward from '@mui/icons-material/ArrowUpward';
import ArrowDownward from '@mui/icons-material/ArrowDownward';
import ContentCopy from '@mui/icons-material/ContentCopy';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import EditOutlined from '@mui/icons-material/EditOutlined';
import { useQuery } from '@tanstack/react-query';
import { get } from '../api';
import { Confirm, Grid, S, useOptions } from './common';

/** Designer schema served by the API (the same definitions drive the server-side release rules). */
export interface DField { key: string; label: string; kind: 'text' | 'multiline' | 'date' | 'number' | 'bool' | 'select'; list?: string | null }
export interface DSection { key: string; title: string; fields: DField[] }
export interface DSchema { type: string; title: string; numberingType: string; sections: DSection[]; stepsTitle: string; stepColumns: DField[]; hasCharacteristics: boolean;
  characteristicColumns: DField[]; hasPartsRows: boolean; partsRowColumns: DField[] }
export const useSchemas = () => useQuery({ queryKey: ['sqf-wi-schema'], queryFn: () => get<DSchema[]>(`${S}/instructions/schema`), staleTime: Infinity });
export const typeLabels: Record<string, string> = { Validation: 'Validation / FAI', ValidationFai: 'Validation / FAI', Fai: 'Validation / FAI', RepairTest: 'Repair / Rework Work Instruction', RepairWork: 'Repair / Rework Work Instruction', Inspection: 'Inspection', Procedure: 'Procedure Instructions' };
/** Types that can be chosen when creating, importing or changing an instruction. Older repair test instructions show as repair work instructions. */
export const types = Object.keys(typeLabels).filter((t) => t !== 'RepairTest' && t !== 'Validation' && t !== 'Fai');
/** One entry per type for filters: Validation, Validation / FAI and FAI instructions are listed once, as Validation / FAI. */
export const typeOptions: [string, string][] = types.map((t) => [t, typeLabels[t]]);

export type Row = Record<string, unknown> & { id?: string; sequence?: number };
export type Val = string | number | boolean | null | undefined;

function SelectField({ fld, value, onChange, disabled }: { fld: DField; value: Val; onChange: (v: Val) => void; disabled?: boolean }) {
  const opts = useOptions(fld.list ?? '');
  const list = [...(opts.data ?? [])];
  if (typeof value === 'string' && value && !list.includes(value)) list.push(value);
  return <TextField select fullWidth size="small" label={fld.label} value={(value as string) ?? ''} disabled={disabled} onChange={(e) => onChange(e.target.value || null)}>
    <MenuItem value=""><em>None</em></MenuItem>{list.map((o) => <MenuItem key={o} value={o}>{o}</MenuItem>)}</TextField>;
}

/** One designer field rendered with the shared form controls. */
export function FieldInput({ fld, value, onChange, disabled }: { fld: DField; value: Val; onChange: (v: Val) => void; disabled?: boolean }) {
  switch (fld.kind) {
    case 'bool': return <FormControlLabel control={<Checkbox checked={value === true} disabled={disabled} onChange={(_, c) => onChange(c)} />} label={fld.label} />;
    case 'select': return <SelectField fld={fld} value={value} onChange={onChange} disabled={disabled} />;
    case 'date': return <TextField fullWidth size="small" type="date" label={fld.label} value={(value as string) ?? ''} disabled={disabled} InputLabelProps={{ shrink: true }} onChange={(e) => onChange(e.target.value || null)} />;
    case 'number': return <TextField fullWidth size="small" type="number" label={fld.label} value={value ?? ''} disabled={disabled} onChange={(e) => onChange(e.target.value === '' ? null : Number(e.target.value))} />;
    case 'multiline': return <TextField fullWidth size="small" multiline minRows={2} label={fld.label} value={(value as string) ?? ''} disabled={disabled} onChange={(e) => onChange(e.target.value)} />;
    default: return <TextField fullWidth size="small" label={fld.label} value={(value as string) ?? ''} disabled={disabled} onChange={(e) => onChange(e.target.value)} />;
  }
}

/** A designer section: heading plus a responsive grid of fields; multiline fields take the full row. */
export function SectionCard({ section, values, onChange, disabled, extra }: { section: DSection; values: Record<string, Val>; onChange: (k: string, v: Val) => void; disabled?: boolean; extra?: React.ReactNode }) {
  return (
    <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label={section.title}>
      <Typography variant="h3" sx={{ mb: 1.5 }}>{section.title}</Typography>
      {extra}
      <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: 'minmax(0, 1fr)', sm: 'repeat(2, minmax(0, 1fr))', md: 'repeat(3, minmax(0, 1fr))' } }}>
        {section.fields.map((f) => <Box key={f.key} sx={{ gridColumn: f.kind === 'multiline' ? '1 / -1' : undefined }}>
          <FieldInput fld={f} value={values[f.key]} onChange={(v) => onChange(f.key, v)} disabled={disabled} /></Box>)}
      </Box>
    </Paper>
  );
}

const show = (v: unknown) => (v === true ? 'Yes' : v === false ? 'No' : v === null || v === undefined || v === '' ? '—' : String(v));
export const nextSequence = (rows: Row[]) => (Math.floor(Math.max(0, ...rows.map((r) => Number(r.sequence) || 0)) / 10) + 1) * 10;

/**
 * Ordered rows (steps, characteristics, parts rows) with explicit sequence numbers: add, edit, duplicate, move up / down, renumber and delete with confirmation.
 * Moving a row swaps it with its neighbour and keeps the sequence numbers ascending.
 */
export function RowsEditor({ title, noun, cols, rows, onChange, disabled, summary, seqLabel = 'Seq', actions }: {
  title: string; noun: string; cols: DField[]; rows: Row[]; onChange: (rows: Row[]) => void; disabled?: boolean; summary?: string[]; seqLabel?: string; actions?: React.ReactNode;
}) {
  const [edit, setEdit] = useState<{ index: number; row: Row } | null>(null);
  const [remove, setRemove] = useState<number | null>(null);
  const shown = (summary ?? cols.slice(0, 4).map((c) => c.key)).map((k) => cols.find((c) => c.key === k)!).filter(Boolean);
  const move = (i: number, d: -1 | 1) => {
    const j = i + d; if (j < 0 || j >= rows.length) return;
    const n = rows.map((r) => ({ ...r }));
    const si = n[i].sequence, sj = n[j].sequence;
    [n[i], n[j]] = [n[j], n[i]]; n[i].sequence = si; n[j].sequence = sj;
    onChange(n);
  };
  const duplicate = (i: number) => {
    const copy: Row = { ...rows[i], id: undefined, sequence: nextSequence(rows) };
    if ('characteristicNumber' in copy) copy.characteristicNumber = `${copy.characteristicNumber ?? ''}-copy`;
    onChange([...rows, copy]);
  };
  const renumber = () => onChange(rows.map((r, i) => ({ ...r, sequence: (i + 1) * 10 })));
  const n = noun.charAt(0).toUpperCase() + noun.slice(1);
  return (
    <Paper variant="outlined" sx={{ p: 2 }} component="section" aria-label={title}>
      <Stack direction="row" justifyContent="space-between" alignItems="center" flexWrap="wrap" gap={1} sx={{ mb: 1.5 }}>
        <Typography variant="h3">{title} ({rows.length})</Typography>
        <Stack direction="row" gap={1} flexWrap="wrap">
          {actions}
          {!disabled && rows.length > 1 && <Button size="small" onClick={renumber}>Renumber</Button>}
          {!disabled && <Button size="small" variant="outlined" onClick={() => setEdit({ index: -1, row: { sequence: nextSequence(rows) } })}>Add {noun}</Button>}
        </Stack>
      </Stack>
      {rows.length === 0 ? <Typography variant="body2" color="text.secondary">No {noun}s yet.</Typography> :
        <TableContainer sx={{ overflowX: 'auto' }}><Table size="small">
          <TableHead><TableRow><TableCell sx={{ width: 64 }}>{seqLabel}</TableCell>{shown.map((c) => <TableCell key={c.key}>{c.label}</TableCell>)}<TableCell align="right" sx={{ width: 190 }}>Actions</TableCell></TableRow></TableHead>
          <TableBody>{rows.map((r, i) => <TableRow key={(r.id as string) ?? `new-${i}`}>
            <TableCell>{show(r.sequence)}</TableCell>
            {shown.map((c) => <TableCell key={c.key} sx={{ maxWidth: 360, whiteSpace: 'pre-wrap' }}>{show(r[c.key])}</TableCell>)}
            <TableCell align="right" sx={{ whiteSpace: 'nowrap' }}>
              <Tooltip title={disabled ? 'View' : 'Edit'}><IconButton size="small" aria-label={`${disabled ? 'View' : 'Edit'} ${noun} ${i + 1}`} onClick={() => setEdit({ index: i, row: { ...r } })}><EditOutlined fontSize="small" /></IconButton></Tooltip>
              {!disabled && <>
                <Tooltip title="Duplicate"><IconButton size="small" aria-label={`Duplicate ${noun} ${i + 1}`} onClick={() => duplicate(i)}><ContentCopy fontSize="small" /></IconButton></Tooltip>
                <Tooltip title="Move up"><span><IconButton size="small" aria-label={`Move ${noun} ${i + 1} up`} disabled={i === 0} onClick={() => move(i, -1)}><ArrowUpward fontSize="small" /></IconButton></span></Tooltip>
                <Tooltip title="Move down"><span><IconButton size="small" aria-label={`Move ${noun} ${i + 1} down`} disabled={i === rows.length - 1} onClick={() => move(i, 1)}><ArrowDownward fontSize="small" /></IconButton></span></Tooltip>
                <Tooltip title="Delete"><IconButton size="small" aria-label={`Delete ${noun} ${i + 1}`} onClick={() => setRemove(i)}><DeleteOutline fontSize="small" /></IconButton></Tooltip>
              </>}
            </TableCell></TableRow>)}</TableBody>
        </Table></TableContainer>}
      <Dialog open={!!edit} onClose={() => setEdit(null)} fullWidth maxWidth="md">
        <FormDialogTitle>{edit?.index === -1 ? `Add ${noun}` : `${disabled ? '' : 'Edit '}${n} ${edit ? edit.index + 1 : ''}`}</FormDialogTitle>
        <DialogContent>
          {edit && <Box sx={{ pt: 1 }}><Grid cols={3}>
            <TextField size="small" type="number" label="Sequence number" value={edit.row.sequence ?? ''} disabled={disabled} onChange={(e) => setEdit({ ...edit, row: { ...edit.row, sequence: e.target.value === '' ? undefined : Number(e.target.value) } })} />
            {cols.map((c) => <Box key={c.key} sx={{ gridColumn: c.kind === 'multiline' ? '1 / -1' : undefined }}>
              <FieldInput fld={c} value={edit.row[c.key] as Val} disabled={disabled} onChange={(v) => setEdit({ ...edit, row: { ...edit.row, [c.key]: v } })} /></Box>)}
          </Grid></Box>}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setEdit(null)}>{disabled ? 'Close' : 'Cancel'}</Button>
          {!disabled && <Button variant="contained" onClick={() => {
            if (!edit) return;
            const n2 = edit.index === -1 ? [...rows, edit.row] : rows.map((r, i) => (i === edit.index ? edit.row : r));
            onChange(n2); setEdit(null);
          }}>Save {noun}</Button>}
        </DialogActions>
      </Dialog>
      <Confirm open={remove !== null} title={`Delete ${noun}`} danger confirm="Delete" onClose={() => setRemove(null)}
        text={`Remove ${noun} ${remove !== null ? remove + 1 : ''}${remove !== null && rows[remove]?.title ? ` (${rows[remove].title})` : ''} from this work instruction? The change is kept only when you save.`}
        onConfirm={async () => { if (remove !== null) onChange(rows.filter((_, i) => i !== remove)); }} />
    </Paper>
  );
}

/** Client-side copy of the server's sequence rules, shown before release. */
export function sequenceProblems(rows: Row[], noun: string) {
  const seqs = rows.map((r) => Number(r.sequence) || 0);
  const p: string[] = [];
  if (seqs.some((s) => s <= 0)) p.push(`Every ${noun} needs a positive sequence number.`);
  if (new Set(seqs).size !== seqs.length) p.push(`${noun.charAt(0).toUpperCase() + noun.slice(1)} sequence numbers must be unique.`);
  if (seqs.some((s, i) => i > 0 && s < seqs[i - 1])) p.push(`${noun.charAt(0).toUpperCase() + noun.slice(1)}s must be in ascending sequence order.`);
  return p;
}
export const ProblemList = ({ problems, title }: { problems: string[]; title: string }) =>
  problems.length === 0 ? null : <Alert severity="warning" sx={{ mb: 2 }}><strong>{title}</strong><ul style={{ margin: '4px 0 0', paddingLeft: 20 }}>{problems.map((p) => <li key={p}>{p}</li>)}</ul></Alert>;
