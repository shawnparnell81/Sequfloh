import { useEffect, useState } from 'react';
import { FormDialogTitle } from '../components/FullScreen';
import { Alert, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, MenuItem, Paper, Stack, Tab, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Tabs, TextField, Typography } from '@mui/material';
import { useQueryClient } from '@tanstack/react-query';
import { get, getBlob, post } from '../api';
import { ErrorState, Mono, S } from './common';

interface Customer { id: string; label: string }
interface Row {
  id: string; partNumber: string; name: string; family?: string | null; drawingNumber?: string | null;
  customerName?: string | null; outcome: string; proposedCustomer?: string | null; reason: string;
}
interface Preview { total: number; clear: number; ambiguous: number; unassigned: number; page: number; pageSize: number; customers: Customer[]; rows: Row[] }
interface Result { assigned: number; unchanged: number; cleared: number }

const outcomes = ['', 'Clear', 'Ambiguous', 'Unassigned'] as const;

/**
 * Preview which existing parts have one clear customer, then confirm. Only the customer is saved.
 * Ambiguous and unassigned parts stay unassigned unless a customer is chosen on that row.
 */
export function CustomerAssignmentDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const qc = useQueryClient();
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [outcome, setOutcome] = useState('');
  const [page, setPage] = useState(1);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [err, setErr] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<string | null>(null);
  const [skip, setSkip] = useState<string[]>([]);
  const [choices, setChoices] = useState<Record<string, string>>({});

  const refresh = async (next: { q: string; outcome: string; page: number }) => {
    setBusy(true); setErr(null);
    try { setPreview(await get<Preview>(`${S}/parts/customers/assignment?${params(next)}`)); }
    catch (e) { setErr(e); }
    finally { setBusy(false); }
  };

  useEffect(() => {
    if (!open) return;
    setQ(''); setSearch(''); setOutcome(''); setPage(1); setPreview(null); setErr(null); setDone(null); setSkip([]); setChoices({});
    let cancel = false;
    (async () => {
      setBusy(true);
      try {
        const data = await get<Preview>(`${S}/parts/customers/assignment?page=1&pageSize=25`);
        if (!cancel) setPreview(data);
      } catch (e) { if (!cancel) setErr(e); }
      finally { if (!cancel) setBusy(false); }
    })();
    return () => { cancel = true; };
  }, [open]);

  const choiceOf = (id: string) => choices[id] ?? '';
  const setChoice = (row: Row, value: string) => {
    setChoices((prev) => {
      const next = { ...prev };
      if (value) next[row.id] = value; else delete next[row.id];
      return next;
    });
    if (row.outcome !== 'Clear') return;
    setSkip((prev) => value ? (prev.includes(row.id) ? prev : [...prev, row.id]) : prev.filter((id) => id !== row.id));
  };
  const included = (row: Row) => choiceOf(row.id) ? true : row.outcome === 'Clear' && !skip.includes(row.id);
  const toggle = (row: Row, on: boolean) => {
    if (choiceOf(row.id)) { if (!on) setChoice(row, ''); return; }
    if (row.outcome === 'Clear') setSkip((prev) => on ? prev.filter((id) => id !== row.id) : (prev.includes(row.id) ? prev : [...prev, row.id]));
  };

  const chosen = Object.entries(choices).filter(([, value]) => value);
  const clearCount = Math.max(0, (preview?.clear ?? 0) - skip.length);
  const canConfirm = clearCount > 0 || chosen.length > 0;

  const confirm = async () => {
    setBusy(true); setErr(null);
    try {
      const r = await post<Result>(`${S}/parts/customers/assignment`, {
        assignClear: clearCount > 0, q: search || null, skipPartIds: skip,
        exceptions: chosen.map(([partId, customerId]) => ({ partId, customerId: customerId === 'clear' ? null : customerId })),
      });
      const bits = [`${r.assigned} assigned`, `${r.unchanged} already set`];
      if (r.cleared) bits.push(`${r.cleared} cleared`);
      setDone(`${bits.join(', ')}. Part numbers and links were not changed.`);
      setSkip([]); setChoices({});
      await qc.invalidateQueries({ queryKey: ['sqf-parts'] });
      await qc.invalidateQueries({ queryKey: ['sqf-part'] });
      await refresh({ q: search, outcome, page });
    } catch (e) { setErr(e); }
    finally { setBusy(false); }
  };

  const exportCsv = async () => {
    setBusy(true); setErr(null);
    try {
      const a = document.createElement('a');
      a.href = URL.createObjectURL(await getBlob(`${S}/parts/customers/assignment/export?${params({ q: search, outcome, page: 1 })}`));
      a.download = 'part-customer-assignment.csv'; a.click(); URL.revokeObjectURL(a.href);
    } catch (e) { setErr(e); }
    finally { setBusy(false); }
  };

  return <Dialog open={open} onClose={busy ? undefined : onClose} maxWidth="lg" fullWidth>
    <FormDialogTitle>Assign customers</FormDialogTitle>
    <DialogContent data-testid="assign-customers-dialog">
      <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
        Only the customer is saved. Part numbers, drawings, work orders and every other link stay on the same part.
        A shared drawing, including a front right and a front left part, is not a conflict.
        A part whose primary number matches more than one customer, or only an alias, stays unassigned unless you choose a customer on that row.
        Customers are added and edited under Reference Data.
      </Typography>
      {done && <Alert severity="success" sx={{ mb: 1 }} data-testid="assign-done">{done}</Alert>}
      {err ? <ErrorState error={err} /> : null}
      {preview && <Stack direction="row" gap={2} sx={{ mb: 1 }} data-testid="assign-counts">
        <Typography variant="body2"><b>{preview.clear}</b> clear</Typography>
        <Typography variant="body2"><b>{preview.ambiguous}</b> ambiguous</Typography>
        <Typography variant="body2"><b>{preview.unassigned}</b> unassigned</Typography>
      </Stack>}
      <Tabs value={outcome} onChange={(_, v) => { setOutcome(v); setPage(1); void refresh({ q: search, outcome: v, page: 1 }); }} sx={{ mb: 1 }}>
        {outcomes.map((o) => <Tab key={o || 'all'} value={o} label={o || 'All'} />)}
      </Tabs>
      <Stack direction="row" gap={1} sx={{ mb: 1 }}>
        <TextField size="small" label="Search parts" value={q} onChange={(e) => setQ(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') { const next = q.trim(); setSearch(next); setPage(1); void refresh({ q: next, outcome, page: 1 }); } }}
          inputProps={{ 'data-testid': 'assign-search' }} sx={{ width: 360 }} />
        <Button onClick={() => { const next = q.trim(); setSearch(next); setPage(1); void refresh({ q: next, outcome, page: 1 }); }}>Search</Button>
        <Box sx={{ flex: 1 }} />
        <Button onClick={exportCsv} disabled={busy}>Export CSV</Button>
      </Stack>
      <TableContainer component={Paper} variant="outlined" sx={{ maxHeight: 360 }}>
        <Table size="small" stickyHeader data-testid="assign-table">
          <TableHead><TableRow>
            <TableCell padding="checkbox" />
            <TableCell>Part number</TableCell><TableCell>Product family</TableCell><TableCell>Drawing #</TableCell>
            <TableCell>Customer now</TableCell><TableCell>Suggestion</TableCell><TableCell>Why</TableCell><TableCell>Choose</TableCell>
          </TableRow></TableHead>
          <TableBody>
            {(preview?.rows ?? []).map((r) => <TableRow key={r.id} data-testid="assign-row">
              <TableCell padding="checkbox">
                <Checkbox size="small" checked={included(r)} disabled={r.outcome !== 'Clear' && !choiceOf(r.id)} inputProps={{ 'aria-label': `Include ${r.partNumber}` }}
                  onChange={(_, on) => toggle(r, on)} />
              </TableCell>
              <TableCell><Mono>{r.partNumber}</Mono></TableCell>
              <TableCell>{r.family ?? '—'}</TableCell>
              <TableCell>{r.drawingNumber ?? '—'}</TableCell>
              <TableCell>{r.customerName ?? 'Unassigned'}</TableCell>
              <TableCell>{r.outcome === 'Clear' ? r.proposedCustomer : r.outcome}</TableCell>
              <TableCell sx={{ fontSize: 12, whiteSpace: 'normal', minWidth: 220 }}>{r.reason}</TableCell>
              <TableCell>
                <TextField select size="small" value={choiceOf(r.id)} onChange={(e) => setChoice(r, e.target.value)} sx={{ minWidth: 170 }} inputProps={{ 'aria-label': `Customer for ${r.partNumber}` }}>
                  <MenuItem value="">{r.outcome === 'Clear' ? 'Use suggestion' : 'No change'}</MenuItem>
                  {(preview?.customers ?? []).map((c) => <MenuItem key={c.id} value={c.id}>{c.label}</MenuItem>)}
                  {r.customerName && <MenuItem value="clear">Unassigned</MenuItem>}
                </TextField>
              </TableCell>
            </TableRow>)}
            {preview && preview.rows.length === 0 && <TableRow><TableCell colSpan={8}><Typography color="text.secondary">No parts in this view.</Typography></TableCell></TableRow>}
          </TableBody>
        </Table>
      </TableContainer>
      {preview && preview.total > preview.pageSize && <Stack direction="row" gap={1} alignItems="center" sx={{ mt: 1 }}>
        <Button size="small" disabled={page <= 1 || busy} onClick={() => { const n = page - 1; setPage(n); void refresh({ q: search, outcome, page: n }); }}>Previous</Button>
        <Typography variant="caption">Page {page} of {Math.ceil(preview.total / preview.pageSize)}</Typography>
        <Button size="small" disabled={page * preview.pageSize >= preview.total || busy} onClick={() => { const n = page + 1; setPage(n); void refresh({ q: search, outcome, page: n }); }}>Next</Button>
      </Stack>}
    </DialogContent>
    <DialogActions>
      <Button onClick={onClose} disabled={busy}>Close</Button>
      <Button variant="contained" data-testid="assign-confirm" disabled={!canConfirm || busy} onClick={confirm}>
        Assign clear suggestions{chosen.length ? ` and ${chosen.length} chosen` : ''}
      </Button>
    </DialogActions>
  </Dialog>;
}

function params(next: { q: string; outcome: string; page: number }) {
  const p = new URLSearchParams({ page: String(next.page), pageSize: '25' });
  if (next.q) p.set('q', next.q);
  if (next.outcome) p.set('outcome', next.outcome);
  return p;
}
