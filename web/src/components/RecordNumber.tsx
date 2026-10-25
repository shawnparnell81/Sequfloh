import { useRef, useState } from 'react';
import { Alert, Button, Dialog, DialogActions, DialogContent, Stack, TextField, type TextFieldProps } from '@mui/material';
import { put } from '../api';
import { FormDialogTitle } from './FullScreen';

/** Record kinds whose number and title are edited through the shared number dialog. */
export type NumberedKind = 'file' | 'work-order' | 'inspection-request' | 'work-instruction' | 'parts-list' | 'operation-step';

/** The record number field: always typed by the user in the company's own format; the app never fills in a number of its own. */
export function NumberField({ label = 'Number', ...p }: TextFieldProps) {
  return <TextField required label={label} helperText={p.helperText ?? 'Your own number, exactly as your company writes it'} inputProps={{ 'data-testid': 'record-number', maxLength: 300 }} {...p} />;
}

/** Asks for a number before a copy or a new record is made. Resolves to the typed number, or null when cancelled. */
export function useNumberPrompt() {
  const [open, setOpen] = useState<{ title: string; label: string; initial: string } | null>(null);
  const [v, setV] = useState('');
  const done = useRef<(n: string | null) => void>(() => {});
  const ask = (title: string, initial = '', label = 'Number') => new Promise<string | null>((resolve) => { done.current = resolve; setV(initial); setOpen({ title, label, initial }); });
  const close = (n: string | null) => { setOpen(null); done.current(n); };
  const dialog = (
    <Dialog open={!!open} onClose={() => close(null)} fullWidth maxWidth="sm" data-testid="number-prompt">
      <FormDialogTitle>{open?.title}</FormDialogTitle>
      <DialogContent><NumberField autoFocus fullWidth sx={{ mt: 1 }} label={open?.label} value={v} onChange={(e) => setV(e.target.value)} /></DialogContent>
      <DialogActions>
        <Button onClick={() => close(null)}>Cancel</Button>
        <Button variant="contained" disabled={!v.trim()} data-testid="number-prompt-ok" onClick={() => close(v.trim())}>Continue</Button>
      </DialogActions>
    </Dialog>
  );
  return { ask, dialog };
}

/** Edits the number (and title, when the record has one) of a record. The change is checked for a duplicate within the same type and written to the audit trail. */
export function EditNumberDialog({ open, kind, id, number, title, onClose, onDone }: {
  open: boolean; kind: NumberedKind; id: string; number: string; title?: string | null; onClose: () => void; onDone: () => void;
}) {
  const [n, setN] = useState(number);
  const [t, setT] = useState(title ?? '');
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const hasTitle = title !== undefined && title !== null;
  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} fullWidth maxWidth="sm" data-testid="edit-number-dialog" TransitionProps={{ onEnter: () => { setN(number); setT(title ?? ''); setErr(null); } }}>
      <FormDialogTitle>{hasTitle ? 'Edit number and title' : 'Edit number'}</FormDialogTitle>
      <DialogContent>
        <Stack gap={2} sx={{ mt: 1 }}>
          {err && <Alert severity="error">{err}</Alert>}
          <NumberField fullWidth value={n} onChange={(e) => setN(e.target.value)} />
          {hasTitle && <TextField fullWidth required label="Title" value={t} onChange={(e) => setT(e.target.value)} inputProps={{ 'data-testid': 'record-title' }} />}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>Cancel</Button>
        <Button variant="contained" data-testid="edit-number-save" disabled={busy || !n.trim() || (hasTitle && !t.trim())}
          onClick={async () => { setBusy(true); setErr(null); try { await put(`/api/sequfloh/records/${kind}/${id}/number`, { number: n.trim(), title: hasTitle ? t.trim() : null }); onDone(); onClose(); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); } }}>Save</Button>
      </DialogActions>
    </Dialog>
  );
}
