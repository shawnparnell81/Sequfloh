import { useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Alert, Box, Button, Dialog, DialogActions, DialogContent, IconButton, Link, Stack, TextField, Tooltip, Typography } from '@mui/material';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post } from '../api';
import { FormDialogTitle } from '../components/FullScreen';
import { PartPicker } from './Instructions';
import { Empty, Mono, SqfStatus } from './common';

const S = '/api/sequfloh';
export interface PartLinkInput { partId?: string | null; partNumber?: string | null; description?: string | null; quantity?: number | null; notes?: string | null }
interface LinkRow { id: string; partNumber: string; name: string; description?: string | null; status: string; createdAt: string; createdBy?: string | null }

/** Pick an existing part or type a new part number (created in the Part Master as a repair part). */
export function AddPartDialog({ open, title, withQty, onClose, onAdd }: { open: boolean; title: string; withQty?: boolean; onClose: () => void; onAdd: (r: PartLinkInput) => Promise<void> }) {
  const [pick, setPick] = useState<{ id: string; partNumber: string; name?: string } | null>(null);
  const [num, setNum] = useState('');
  const [desc, setDesc] = useState('');
  const [qty, setQty] = useState('1');
  const [notes, setNotes] = useState('');
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);
  const close = () => { setPick(null); setNum(''); setDesc(''); setQty('1'); setNotes(''); setErr(''); onClose(); };
  const save = async () => {
    setBusy(true); setErr('');
    try {
      await onAdd({ partId: pick?.id ?? null, partNumber: pick ? null : num.trim(), description: desc.trim() || null,
        quantity: withQty ? Number(qty) || 1 : null, notes: withQty ? notes.trim() || null : null });
      close();
    } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return <Dialog open={open} onClose={close} maxWidth="sm" fullWidth>
    <FormDialogTitle>{title}</FormDialogTitle>
    <DialogContent><Stack spacing={2} sx={{ mt: 1 }}>
      {err && <Alert severity="error">{err}</Alert>}
      <PartPicker label="Find a part in the Part Master" value={pick} onChange={(p) => { setPick(p); if (p) setNum(''); }} />
      <Typography variant="body2" color="text.secondary">Or type a part number that isn't in the app yet. It is added to the Part Master as a repair part with exactly that number.</Typography>
      <TextField size="small" label="New part number" value={num} disabled={!!pick} onChange={(e) => setNum(e.target.value)} inputProps={{ 'data-testid': 'new-part-number' }} />
      <TextField size="small" label="Description (optional)" value={desc} onChange={(e) => setDesc(e.target.value)} />
      {withQty && <Box sx={{ display: 'flex', gap: 2 }}>
        <TextField size="small" type="number" label="Qty" value={qty} onChange={(e) => setQty(e.target.value)} sx={{ width: 120 }} />
        <TextField size="small" label="Notes" value={notes} onChange={(e) => setNotes(e.target.value)} fullWidth /></Box>}
    </Stack></DialogContent>
    <DialogActions><Button onClick={close}>Cancel</Button><Button variant="contained" disabled={busy || (!pick && !num.trim())} onClick={save}>Add</Button></DialogActions>
  </Dialog>;
}

/** Parts linked to a part: its Parts list or its Repair parts. */
export function PartLinksPanel({ partId, list, canEdit }: { partId: string; list: 'parts-list' | 'repair-parts'; canEdit: boolean }) {
  const qc = useQueryClient();
  const key = ['sqf-part-links', partId, list];
  const q = useQuery({ queryKey: key, queryFn: () => get<LinkRow[]>(`${S}/parts/${partId}/links/${list}`) });
  const [open, setOpen] = useState(false);
  const label = list === 'parts-list' ? 'Add parts' : 'Add repair parts';
  const refresh = () => qc.invalidateQueries({ queryKey: key });
  const rows = q.data ?? [];
  return <Box sx={{ mb: 2 }}>
    {canEdit && <Button variant="contained" size="small" sx={{ mb: 1 }} data-testid={`add-${list}`} onClick={() => setOpen(true)}>{label}</Button>}
    {q.error && <Alert severity="error">{(q.error as Error).message}</Alert>}
    {!q.isLoading && rows.length === 0 ? <Empty text={list === 'parts-list' ? 'No parts added to this list yet.' : 'No repair parts added yet.'} /> :
      <Box component="table" sx={{ width: '100%', borderCollapse: 'collapse', '& td, & th': { borderBottom: 1, borderColor: 'divider', p: 1, textAlign: 'left', fontSize: 14 } }}>
        <thead><tr><th>Part number</th><th>Name</th><th>Description</th><th>Status</th><th>Added</th><th /></tr></thead>
        <tbody>{rows.map((r) => <tr key={r.id}>
          <td><Link component={RouterLink} to={`/sequfloh/parts/${r.id}`}><Mono>{r.partNumber}</Mono></Link></td><td>{r.name}</td><td>{r.description}</td>
          <td><SqfStatus status={r.status} /></td><td>{new Date(r.createdAt).toLocaleDateString()}{r.createdBy ? ` · ${r.createdBy}` : ''}</td>
          <td>{canEdit && <Tooltip title="Remove from this list (the part stays in the Part Master)"><IconButton size="small" onClick={async () => { await del(`${S}/parts/${partId}/links/${list}/${r.id}`); refresh(); }}><DeleteOutline fontSize="small" /></IconButton></Tooltip>}</td>
        </tr>)}</tbody></Box>}
    <AddPartDialog open={open} title={label} onClose={() => setOpen(false)} onAdd={async (r) => { await post(`${S}/parts/${partId}/links/${list}`, r); refresh(); }} />
  </Box>;
}
