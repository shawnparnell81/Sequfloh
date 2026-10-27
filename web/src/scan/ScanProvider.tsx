import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { useNavigate } from 'react-router-dom';
import { Alert, Autocomplete, Button, Dialog, DialogActions, DialogContent, InputAdornment, List, ListItemButton, ListItemText, TextField, Typography } from '@mui/material';
import QrCodeScanner from '@mui/icons-material/QrCodeScanner';
import { get, put } from '../api';
import { usePartSearch } from '../sequfloh/partSearch';
import { useAuth } from '../auth';
import { FormDialogTitle } from '../components/FullScreen';
import { useQuery } from '@tanstack/react-query';
import { createScanDetector, isTypingTarget, normalizeScan } from './detector';

export interface ScanPart { id: string; partNumber: string; name: string; description?: string | null; productFamily?: string | null; status: string; drawingNumber?: string | null }
export interface ScanRecord { kind: string; id: string; number: string; title?: string | null; link: string }
export interface ScanResult { code: string; matchedOn: string | null; matches: ScanPart[]; records: ScanRecord[]; canAdd: boolean; isBarcode?: boolean }

/** Looks a scanned code up: exact part numbers (then exact DWG #, DMA PN or JH PN), and with records, exact work order, inspection request and project numbers. */
export const lookupScan = (code: string, partsOnly = false) =>
  get<ScanResult>(`/api/sequfloh/scan?code=${encodeURIComponent(code)}${partsOnly ? '&only=parts' : ''}`);

/** Marks a part field: a scan while focus is inside it is handed to onScan instead of opening a page. */
export const SCAN_PART_ATTR = 'data-scan-part';
export const SCAN_EVENT = 'sqf-part-scan';

type Ctx = { scan: (code: string) => Promise<void>; notFound: (r: ScanResult, onLinked?: (p: ScanPart) => void) => void };
const ScanCtx = createContext<Ctx>({ scan: async () => {}, notFound: () => {} });
export const useScan = () => useContext(ScanCtx);

/** One scan service for the signed-in app: the global listener, the result dialogs and the Scan box. */
export function ScanProvider({ children }: { children: ReactNode }) {
  const nav = useNavigate();
  const { can } = useAuth();
  const enabled = can('sequfloh.access');
  const [result, setResult] = useState<ScanResult | null>(null);
  const [err, setErr] = useState('');
  const [menu, setMenu] = useState<ScanPart | null>(null);
  const open = (link: string) => { setResult(null); nav(link); };
  const scan = useCallback(async (raw: string) => {
    const code = normalizeScan(raw);
    if (!code) return;
    try {
      const r = await lookupScan(code);
      const total = r.matches.length + r.records.length;
      if (total === 1 && r.records.length) { setResult(null); nav(r.records[0].link); }
      else if (total === 1) { setResult(null); setMenu(r.matches[0]); }
      else setResult(r);
    } catch (e) { setErr((e as Error).message); }
  }, [nav]);
  const detector = useRef(createScanDetector());
  useEffect(() => {
    if (!enabled) return;
    const onKey = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement | null;
      const partField = t instanceof HTMLElement ? t.closest(`[${SCAN_PART_ATTR}]`) : null;
      // ordinary fields type as normal; only part fields and pages without focus in a field take scans
      if (isTypingTarget(t) && !partField) { detector.current.reset(); return; }
      const code = detector.current.feed(e, e.timeStamp || performance.now());
      if (code === null) return;
      e.preventDefault(); e.stopPropagation();
      if (partField) partField.dispatchEvent(new CustomEvent(SCAN_EVENT, { detail: code }));
      else void scan(code);
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [enabled, scan]);
  const linked = useRef<((p: ScanPart) => void) | null>(null);
  const notFound = useCallback((r: ScanResult, onLinked?: (p: ScanPart) => void) => { linked.current = onLinked ?? null; setResult(r); }, []);
  const none = result && result.matches.length + result.records.length === 0 && !result.isBarcode;
  const unlinked = result && result.isBarcode && result.matches.length === 0 ? result : null;
  return <ScanCtx.Provider value={{ scan, notFound }}>
    {children}
    <Dialog open={(!!result && !unlinked) || !!err} onClose={() => { setResult(null); setErr(''); }} maxWidth="xs" fullWidth data-testid="scan-dialog">
      <FormDialogTitle>{none ? 'Not found' : 'Choose what you scanned'}</FormDialogTitle>
      <DialogContent>
        {err && <Alert severity="error">{err}</Alert>}
        {none && <Typography data-testid="scan-not-found">No part found for <b>{result!.code}</b>.{result!.canAdd ? ' Add it?' : ''}</Typography>}
        {none && result!.canAdd && <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>The part is added with this number. Link or add its drawing on the part afterward.</Typography>}
        {result && !none && <List dense data-testid="scan-pick-list">
          {result.records.map((r) => <ListItemButton key={r.id} onClick={() => open(r.link)}><ListItemText primary={`${r.kind} ${r.number}`} secondary={r.title || undefined} /></ListItemButton>)}
          {result.matches.map((p) => <ListItemButton key={p.id} onClick={() => { setResult(null); setMenu(p); }}>
            <ListItemText primary={`Part ${p.partNumber} · ${p.name}`} secondary={[p.productFamily, p.status !== 'Active' ? p.status : null, result.matchedOn !== 'Part number' ? `matched on ${result.matchedOn}` : null].filter(Boolean).join(' · ') || undefined} /></ListItemButton>)}
        </List>}
      </DialogContent>
      <DialogActions>
        <Button onClick={() => { setResult(null); setErr(''); }}>{none && result!.canAdd ? 'No' : 'Close'}</Button>
        {none && result!.canAdd && <Button variant="contained" data-testid="scan-add-part" onClick={() => open(`/sequfloh/parts?add=${encodeURIComponent(result!.code)}`)}>Add part</Button>}
      </DialogActions>
    </Dialog>
    {unlinked && <UnlinkedBarcode r={unlinked} onClose={() => setResult(null)} onLinked={(p) => { setResult(null); const cb = linked.current; linked.current = null; if (cb) cb(p); else setMenu(p); }}
      onAdd={() => open(`/sequfloh/parts?addUpc=${encodeURIComponent(unlinked.code)}`)} />}
    {menu && <PartMenu part={menu} onClose={() => setMenu(null)} go={(l) => { setMenu(null); nav(l); }} />}
  </ScanCtx.Provider>;
}

/** The Scan box: a scanner (or typing) into it, then Enter, looks the code up. */
export function ScanBox() {
  const { scan } = useScan();
  const [v, setV] = useState('');
  return <TextField size="small" placeholder="Scan or type a code" value={v} onChange={(e) => setV(e.target.value)} sx={{ width: 220 }}
    inputProps={{ 'data-testid': 'scan-box', 'aria-label': 'Scan' }}
    InputProps={{ startAdornment: <InputAdornment position="start"><QrCodeScanner fontSize="small" /></InputAdornment> }}
    onKeyDown={(e) => { if (e.key === 'Enter' && v.trim()) { e.preventDefault(); const c = v; setV(''); void scan(c); } }} />;
}

/** For a part field: a scan into it picks the single exact match, or offers to add the part when none matches. */
export function usePartFieldScan(onPart: (p: ScanPart) => void) {
  const { notFound } = useScan();
  const ref = useRef<HTMLDivElement | null>(null);
  const cb = useRef(onPart); cb.current = onPart;
  useEffect(() => {
    const el = ref.current; if (!el) return;
    const on = async (e: Event) => {
      const code = normalizeScan((e as CustomEvent<string>).detail ?? ''); if (!code) return;
      const r = await lookupScan(code, true);
      if (r.matches.length === 1) cb.current(r.matches[0]);
      // a part number with no part offers Add; a barcode on no part offers to link it to a part, which is then selected here
      else if (r.matches.length === 0) notFound(r, r.isBarcode ? cb.current : undefined);
    };
    el.addEventListener(SCAN_EVENT, on);
    return () => el.removeEventListener(SCAN_EVENT, on);
  }, [notFound]);
  return { ref, [SCAN_PART_ATTR]: '' } as const;
}

interface Activity { kind: string; id: string; number: string; type: string; status: string; createdAt: string; link: string; detail?: string | null }
interface Drawing { id: string; title: string; drawingNumber?: string; currentRevision: string; viewable: boolean }

/** What a scanned part opens: history, drawings or the full part page, plus the most recent record and a new repair work order. */
function PartMenu({ part, onClose, go }: { part: ScanPart; onClose: () => void; go: (link: string) => void }) {
  const [show, setShow] = useState<'menu' | 'history' | 'drawings'>('menu');
  const act = useQuery({ queryKey: ['sqf-part-activity', part.id], queryFn: () => get<{ items: Activity[]; canCreateRepair: boolean }>(`/api/sequfloh/parts/${part.id}/activity`) });
  const dwg = useQuery({ queryKey: ['scan-part-drawings', part.id], queryFn: () => get<Drawing[]>(`/api/drawings/for-part?partId=${part.id}`) });
  const items = act.data?.items ?? [];
  const latest = items[0];
  const fmt = (d: string) => new Date(d).toLocaleString();
  const row = (a: Activity, top: boolean) => <ListItemButton key={a.kind + a.id} selected={top} onClick={() => go(a.link)} data-testid="scan-history-row">
    <ListItemText primary={`${a.number} · ${a.type}${a.detail ? ` · ${a.detail}` : ''}`} secondary={`${a.status} · ${fmt(a.createdAt)}${top ? ' · most recent' : ''}`} /></ListItemButton>;
  return <Dialog open onClose={onClose} maxWidth="xs" fullWidth data-testid="scan-part-menu">
    <FormDialogTitle>{part.partNumber}</FormDialogTitle>
    <DialogContent>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>{part.description || part.name}</Typography>
      {show === 'menu' && <List dense>
        {latest && <ListItemButton onClick={() => go(latest.link)} data-testid="scan-most-recent"><ListItemText primary={`Most recent: ${latest.number} · ${latest.type}`} secondary={`${latest.status} · ${fmt(latest.createdAt)}`} /></ListItemButton>}
        <ListItemButton onClick={() => setShow('history')} data-testid="scan-history"><ListItemText primary="History" secondary={act.isLoading ? 'Loading…' : `${items.length} work order${items.length === 1 ? '' : 's'} and inspection request${items.length === 1 ? '' : 's'}`} /></ListItemButton>
        <ListItemButton onClick={() => setShow('drawings')} data-testid="scan-drawings"><ListItemText primary="Drawings" secondary={dwg.isLoading ? 'Loading…' : `${dwg.data?.length ?? 0} linked`} /></ListItemButton>
        <ListItemButton onClick={() => go(`/sequfloh/parts/${part.id}`)} data-testid="scan-part-info"><ListItemText primary="All part info" /></ListItemButton>
        {act.data?.canCreateRepair && <ListItemButton onClick={() => go(`/sequfloh/tracking/new?type=Repair&partId=${part.id}`)} data-testid="scan-new-repair"><ListItemText primary="New Repair / Rework work order" /></ListItemButton>}
      </List>}
      {show === 'history' && (items.length ? <List dense>{items.map((a, i) => row(a, i === 0))}</List> : <Typography variant="body2">No work orders or inspection requests for this part yet.</Typography>)}
      {show === 'drawings' && ((dwg.data?.length ?? 0) ? <List dense>{dwg.data!.map((d) =>
        <ListItemButton key={d.id} component="a" href={d.viewable ? `/drawings/view/${d.id}` : `/api/drawings/${d.id}/file`} target="_blank" rel="noopener" data-testid="scan-drawing-row">
          <ListItemText primary={d.drawingNumber ? `${d.drawingNumber} · ${d.title}` : d.title} secondary={`Rev ${d.currentRevision}`} /></ListItemButton>)}</List>
        : <Typography variant="body2">No drawings linked to this part.</Typography>)}
    </DialogContent>
    <DialogActions>{show !== 'menu' && <Button onClick={() => setShow('menu')}>Back</Button>}<Button onClick={onClose}>Close</Button></DialogActions>
  </Dialog>;
}

/** A box-label barcode that is on no part: link it to an existing part, or add a new part with it. Never a part named after the digits. */
function UnlinkedBarcode({ r, onClose, onLinked, onAdd }: { r: ScanResult; onClose: () => void; onLinked: (p: ScanPart) => void; onAdd: () => void }) {
  const [text, setText] = useState('');
  const [pick, setPick] = useState<ScanPart | null>(null);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);
  const q = usePartSearch<ScanPart>(text, {}, r.canAdd);
  const link = async () => {
    if (!pick) return;
    setBusy(true); setErr('');
    try { await put(`/api/sequfloh/parts/${pick.id}/upc`, { upc: r.code }); onLinked(pick); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return <Dialog open onClose={onClose} maxWidth="xs" fullWidth data-testid="scan-unlinked">
    <FormDialogTitle>Barcode not linked</FormDialogTitle>
    <DialogContent>
      <Typography data-testid="scan-unlinked-text">Barcode <b>{r.code}</b> isn't linked to a part.{r.canAdd ? ' Link to a part?' : ''}</Typography>
      {!r.canAdd && <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>Ask someone who can edit parts to link it.</Typography>}
      {r.canAdd && <>
        <Autocomplete size="small" options={q.items} value={pick} filterOptions={(x) => x} loading={q.loading} isOptionEqualToValue={(a, b) => a.id === b.id}
          getOptionLabel={(o) => `${o.partNumber} · ${o.name}`} noOptionsText={text ? 'No matching part' : 'Type a part number'}
          onInputChange={(_, t, reason) => { if (reason === 'input') setText(t); }} onChange={(_, o) => setPick(o)}
          renderInput={(p) => <TextField {...p} sx={{ mt: 2 }} label="Part number" inputProps={{ ...p.inputProps, 'data-testid': 'scan-link-search' }} />} />
        {err && <Alert severity="error" sx={{ mt: 1 }}>{err}</Alert>}
      </>}
    </DialogContent>
    <DialogActions>
      <Button onClick={onClose}>Close</Button>
      {r.canAdd && <Button data-testid="scan-add-new-part" onClick={onAdd}>Add new part</Button>}
      {r.canAdd && <Button variant="contained" disabled={!pick || busy} data-testid="scan-link-part" onClick={link}>Link</Button>}
    </DialogActions>
  </Dialog>;
}
