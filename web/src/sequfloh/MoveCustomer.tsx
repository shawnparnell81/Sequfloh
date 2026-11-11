import { useEffect, useState } from 'react';
import { Alert, Button, Dialog, DialogActions, DialogContent, MenuItem, Stack, TextField, Typography } from '@mui/material';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { FormDialogTitle } from '../components/FullScreen';
import { get, post } from '../api';
import { S } from './common';

interface Customer { id: string; label: string; isActive: boolean }
interface Conflict { id: string; partNumber: string; line?: string | null; reason: string }
interface From { customer: string; count: number }
interface Plan { move: number; alreadyThere: number; conflicts: number; leftOut: number; target: string; from: From[]; conflictRows: Conflict[] }

/** Move ticked parts, or every part matching the current list, to one customer. Conflicts are skipped. */
export function MoveCustomerDialog({ open, onClose, onDone, allMatching, ticked, filters }: {
  open: boolean; onClose: () => void; onDone: () => void; allMatching: boolean; ticked: string[]; filters: Record<string, string | undefined>;
}) {
  const qc = useQueryClient();
  const customers = useQuery({ queryKey: ['sqf-customers-active'], queryFn: () => get<Customer[]>(`${S}/reference/customers?active=true`), enabled: open });
  const [customerId, setCustomerId] = useState('');
  const [plan, setPlan] = useState<Plan | null>(null);
  const [done, setDone] = useState<Plan | null>(null);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);
  useEffect(() => { if (open) { setCustomerId(''); setPlan(null); setDone(null); setErr(''); } }, [open]);
  const body = () => ({
    mode: allMatching ? 'filter' : 'ids',
    customerId: customerId || null,
    toUnassigned: customerId === '',
    ...(allMatching ? {} : { partIds: ticked }),
    q: filters.q || null, view: filters.view || null, family: filters.family || null, status: filters.status || null,
    partType: filters.partType || null, customer: filters.customer || null, type: filters.type || null, brand: filters.brand || null,
  });
  const run = async (apply: boolean) => {
    setBusy(true); setErr('');
    try {
      const next = await post<Plan>(`${S}/parts/move-customer${apply ? '' : '/preview'}`, body());
      if (apply) { setDone(next); setPlan(null); qc.invalidateQueries({ queryKey: ['sqf-parts'] }); qc.invalidateQueries({ queryKey: ['sqf-customer-tabs'] }); }
      else setPlan(next);
    } catch (e) { setErr((e as Error).message); }
    finally { setBusy(false); }
  };
  const count = allMatching ? 'all parts matching this list' : `${ticked.length} ticked part${ticked.length === 1 ? '' : 's'}`;
  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm">
      <FormDialogTitle>Move to customer</FormDialogTitle>
      <DialogContent>
        <Stack gap={2} sx={{ pt: 1 }}>
          {err && <Alert severity="error">{err}</Alert>}
          {done ? <Alert severity="success" data-testid="move-result">Moved {done.move} part{done.move === 1 ? '' : 's'} to {done.target}.{done.conflicts ? ` ${done.conflicts} skipped because the same part number is already used in that product line and site.` : ''}</Alert> : <>
            <Typography variant="body2">Moving {count}. Only the customer changes. Part numbers, drawings, revisions, work orders and links stay on the same part.</Typography>
            <TextField select size="small" label="Customer" value={customerId} onChange={(e) => { setCustomerId(e.target.value); setPlan(null); }} inputProps={{ 'data-testid': 'move-customer-select' }}>
              <MenuItem value="">Unassigned</MenuItem>
              {(customers.data ?? []).filter((c) => c.isActive).map((c) => <MenuItem key={c.id} value={c.id}>{c.label}</MenuItem>)}
            </TextField>
            <Button variant="outlined" disabled={busy} onClick={() => run(false)} data-testid="move-preview-button">Review</Button>
            {plan && <Stack gap={1} data-testid="move-preview">
              <Alert severity="info">{plan.move} part{plan.move === 1 ? '' : 's'} will move to {plan.target}.{plan.from.length ? ` From ${plan.from.map((f) => `${f.customer} (${f.count})`).join(', ')}.` : ''}{plan.alreadyThere ? ` ${plan.alreadyThere} already with ${plan.target}.` : ''}{plan.leftOut ? ` ${plan.leftOut} ticked parts are not included because you cannot edit them.` : ''}</Alert>
              {plan.conflicts > 0 && <Alert severity="warning" data-testid="move-conflicts">{plan.conflicts} will be skipped, not overwritten. {plan.conflictRows.map((c) => c.reason).join(' ')}</Alert>}
            </Stack>}
          </>}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={done ? onDone : onClose}>{done ? 'Close' : 'Cancel'}</Button>
        {!done && <Button variant="contained" disabled={busy || !plan || plan.move === 0} onClick={() => run(true)} data-testid="move-confirm">Move {plan?.move ?? ''} parts</Button>}
      </DialogActions>
    </Dialog>
  );
}
