import { setupOptions } from '../components/SetupLink';
import { ListToolbar } from '../layout/PageHeader';
import { FormDialogTitle } from '../components/FullScreen';
import { useEffect, useState } from 'react';
import { Link as RouterLink, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { Alert, Autocomplete, Box, Button, Checkbox, Chip, Dialog, DialogTitle, Menu, DialogActions, DialogContent, FormControlLabel, IconButton, Link, MenuItem, Paper, Stack, Table, TableBody, TableCell,
  TableContainer, TableHead, TableRow, TextField, Tooltip, Typography } from '@mui/material';
import ArrowUpward from '@mui/icons-material/ArrowUpward';
import ArrowDownward from '@mui/icons-material/ArrowDownward';
import ContentCopy from '@mui/icons-material/ContentCopy';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, getBlob, post, put } from '../api';
import { otherNumbers, usePartSearch } from './partSearch';
import { usePartFieldScan } from '../scan/ScanProvider';
import { useAuth } from '../auth';
import { useSites } from '../components/Kit';
import { fmtDateTime } from '../components/Status';
import { ImportPartsDialog } from './PartsImport';
import { ChangeFamilyDialog } from './ChangeFamily';
import { CustomerAssignmentDialog } from './CustomerAssignment';
import { CustomFieldDetails, CustomFieldInputs, missingRequired, saveCustomFields, useCustomFields, type PartCustom } from './CustomFields';
import { MoveCustomerDialog } from './MoveCustomer';
import { PartLinksPanel } from './PartLinks';
import { DrawingFileFields, DrawingPicker, PartDrawingLinks, ViewDrawingButton, blankDrawing, drawingForm, type PartDrawing } from './Drawings';
import { InventoryNote } from './Job';
import { partOption } from './partLabel';
import { RecordActions } from '../components/RecordActions';
import { EditNumberDialog, NumberField, useNumberPrompt } from '../components/RecordNumber';
import { LOT_CHOICES, UNITS, Confirm, DeleteButton, Empty, ErrorState, Filters, Grid, Loading, Mono, Paged, PagedTable, type Col, S, SqfPage, SqfStatus, nul, numOrNull, qs, useOptions, useUnsaved, words } from './common';

const crumbs = (label: string, extra?: { label: string; to?: string }) => [{ label: 'SEQUFLOH', to: '/sequfloh' }, { label: 'Parts and Items', to: '/sequfloh/parts' }, { label, to: extra ? undefined : undefined }, ...(extra ? [extra] : [])];
const yes = (b?: boolean) => (b ? 'Yes' : 'No');
/** Shared Part Master enums (numeric on the wire, as in the Master Data module). */
const partTypeValue: Record<string, number> = { Component: 0, Assembly: 1, Material: 2, Subassembly: 3, Consumable: 4, Packaging: 5, Other: 9 };
const masterStatuses = ['Active', 'Inactive', 'Obsolete'];
/** Labels of the Part Master number fields. */
export const PN_LABEL = 'Part number';
export const DWG_LABEL = 'DWG #';
interface PartTypeValue { id: string; code: string; label: string; isActive: boolean; shownFields?: string | null }
interface AttrDef { key: string; label: string; maxLength: number; types?: string[] | null; notTypes?: string[] | null }
const FUEL_PUMP = 'FUEL_PUMP';
/** Whether an attribute field belongs to a product family code (no code: fields for every family), or the family's settings turn it on (shown: its extra fields). */
const attrFor = (d: AttrDef, code?: string, shown?: string | null) => !!shown?.split(',').includes(d.key) || (!d.types?.length || (!!code && d.types.includes(code))) && !(code && d.notTypes?.includes(code));
/** The part number label, the same for every family. */
const pnLabel = (_code?: string) => PN_LABEL;
/** Values of the admin-managed product families list, including deactivated ones (shown on parts that still have them). */
const usePartTypes = () => useQuery({ queryKey: ['sqf-part-types'], queryFn: () => get<PartTypeValue[]>(`${S}/reference/product-families`), staleTime: 60_000 });
/** Attribute fields defined on the server (cross-reference numbers and others). */
const usePartFields = () => useQuery({ queryKey: ['sqf-part-fields'], queryFn: () => get<{ key: string; label: string }[]>(`${S}/parts/fields`), staleTime: 300_000 });
const useAttrDefs = () => useQuery({ queryKey: ['sqf-part-attrs'], queryFn: () => get<AttrDef[]>(`${S}/parts/attributes`), staleTime: 300_000 });
const attrsOf = (json?: string | null): Record<string, string> => { try { return json ? JSON.parse(json) : {}; } catch { return {}; } };
const COLS_KEY = 'sqf-parts-hidden-cols';
/** The Fuel Pump list shows these seven columns by default, in this order. */
const FP_COLS = ['partNumber', 'attr:orNumber', 'attr:napaPn', 'attr:dmaPn', 'attr:cross', 'attr:jhPn', 'attr:boxSize'];
const defaultHidden = ['attr:keyApplication'];
/** The choices plus the current value when it is not one of them, so an older value is shown rather than lost. */
const withCurrent = (list: string[], current?: string) => (current && !list.some((x) => x.toLowerCase() === current.toLowerCase()) ? [...list, current] : list);

// ------------------------------------------------------------------ Part Master
interface SoldAs { id: string; partNumber: string; customerId?: string | null; customerName?: string | null; upc?: string | null; drawingNumber?: string | null }
interface PartRow { id: string; partNumber: string; name: string; description?: string; revision: string; productFamily?: string; partType: string; assemblyType?: string; partClass?: string | null; isAssembly?: boolean; lotControl?: string | null; unitOfMeasure: string;
  status: string; isRepairable: boolean; isRepairPart?: boolean; isSerialized: boolean; isLotControlled: boolean; drawingNumber?: string; drawingRevision?: string | null; upc?: string; specification?: string; updatedAt: string;
  typeId?: string | null; typeName?: string | null; attributes?: string | null; notes?: string | null; customerId?: string | null; customerName?: string | null; brandId?: string | null; brandName?: string | null; alsoSoldAs?: SoldAs[] | null; custom?: PartCustom[] | null }
interface CustomerLine { typeId: string | null; label: string; count: number }
interface CustomerTab { id: string; label: string; isActive: boolean; count: number; lines: CustomerLine[] }
interface CustomerTabs { customers: CustomerTab[]; unassigned: { count: number; lines: CustomerLine[] } }
interface LocateHit { id: string; partNumber: string; customerId?: string | null; customerName?: string | null; typeId?: string | null; line?: string | null; exactPartNumber: boolean }
const useCustomerTabs = () => useQuery({ queryKey: ['sqf-customer-tabs'], queryFn: () => get<CustomerTabs>(`${S}/parts/customer-tabs`) });
const viewHelp: Record<string, string> = {
  repairable: 'Parts marked Repairable: the part itself can be repaired and can be the subject of a repair work order.',
  'repair-parts': 'Component and replacement parts ordered to carry out repairs, such as screws, nuts and top mounts. A part is listed here only when Repair part is ticked; Repairable does not add it.',
};
const views: Record<string, string> = { '': 'Part Master', repairable: 'Repairable Parts', 'repair-parts': 'Repair Parts', assemblies: 'Assemblies' };

export function PartMasterPage() {
  const { can } = useAuth();
  const nav = useNavigate();
  const [sp, setSp] = useSearchParams();
  const view = sp.get('view') ?? '';
  // a scanned number with no part opens Add part with that number (the drawing is linked by hand afterward)
  const addFromScan = sp.get('add') ?? '';
  // a scanned box-label barcode that is on no part: Add part with the UPC filled in and the part number left for the user
  const addUpc = sp.get('addUpc') ?? '';
  const home = !view && [...sp.keys()].length === 0;
  const [page, setPage] = useState(1); const [size, setSize] = useState(25); const [sort, setSort] = useState('partNumber:asc');
  const v = (k: string) => sp.get(k) ?? '';
  const set = (k: string, val: string) => { const n = new URLSearchParams(sp); if (val) n.set(k, val); else n.delete(k); setSp(n); setPage(1); };
  const customFields = useCustomFields('parts');
  const customQuery = () => (customFields.data ?? []).filter((f) => f.filterable).map((f) => { const val = v(`custom:${f.id}`); return val ? `${f.id}:${val}` : ''; }).filter(Boolean).join(',');
  const filterQs = () => qs({ q: v('q'), view, family: v('family'), status: v('status'), type: v('type'), partType: v('partType'), tag: v('tag'), customer: v('customer'), brand: v('brand'), custom: customQuery(), sort });
  const customerKeyEarly = v('customer');
  const waitingLine = !view && !!customerKeyEarly && !v('partType');
  const q = useQuery({ queryKey: ['sqf-parts', sp.toString(), page, size, sort], queryFn: () => get<Paged<PartRow>>(`${S}/parts?${filterQs()}&${qs({ page, pageSize: size })}`), enabled: !home && !waitingLine });
  const types = usePartTypes();
  const brands = useQuery({ queryKey: ['sqf-brands'], queryFn: () => get<{ id: string; label: string; isActive: boolean }[]>(`${S}/reference/brands`), staleTime: 60_000 });
  const attrDefs = useAttrDefs();
  const untyped = useQuery({ queryKey: ['sqf-parts-untyped'], queryFn: () => get<Paged<PartRow>>(`${S}/parts?partType=none&pageSize=1`) });
  const [hidden, setHidden] = useState<string[]>(() => { try { return JSON.parse(localStorage.getItem(COLS_KEY) ?? 'null') ?? defaultHidden; } catch { return defaultHidden; } });
  const [colsAnchor, setColsAnchor] = useState<HTMLElement | null>(null);
  const FP_KEY = `${COLS_KEY}:fuel-pump-shown`;
  const [fpShown, setFpShown] = useState<string[]>(() => {
    try {
      const saved: string[] | null = JSON.parse(localStorage.getItem(FP_KEY) ?? 'null');
      // a column choice saved before the OR # column existed still shows it
      if (saved && !localStorage.getItem(`${FP_KEY}:or`)) { localStorage.setItem(`${FP_KEY}:or`, '1'); if (!saved.includes('attr:orNumber')) saved.push('attr:orNumber'); }
      return saved ?? FP_COLS;
    } catch { return FP_COLS; }
  });
  const toggleCol = (k: string) => {
    if (fp) { const n = fpShown.includes(k) ? fpShown.filter((x) => x !== k) : [...fpShown, k]; setFpShown(n); localStorage.setItem(FP_KEY, JSON.stringify(n)); return; }
    const n = hidden.includes(k) ? hidden.filter((x) => x !== k) : [...hidden, k]; setHidden(n); localStorage.setItem(COLS_KEY, JSON.stringify(n));
  };
  const exportCsv = async () => {
    const a = document.createElement('a'); a.href = URL.createObjectURL(await getBlob(`${S}/parts/export?${filterQs()}`)); a.download = 'parts.csv'; a.click(); URL.revokeObjectURL(a.href);
  };
  const [open, setOpen] = useState(false);
  const [importing, setImporting] = useState(false);
  const [changing, setChanging] = useState(false);
  const [assigning, setAssigning] = useState(false);
  const [moving, setMoving] = useState(false);
  const [allMatching, setAllMatching] = useState(false);
  const [wipe, setWipe] = useState(false);
  const [ticked, setTicked] = useState<string[]>([]);
  const canChange = can('sequfloh.edit_parts') && can('parts.edit');
  const family = !view && v('partType') ? (v('partType') === 'none' ? 'No product family' : types.data?.find((t) => t.id === v('partType'))?.label) : undefined;
  const preset = family && v('partType') !== 'none' ? family : undefined;
  const famRow = types.data?.find((t) => t.id === v('partType')); const familyCode = famRow?.code;
  const fp = familyCode === FUEL_PUMP;
  const customerKey = v('customer');
  const customerTabs = useCustomerTabs();
  const customerTab = customerKey && customerKey !== 'unassigned' ? customerTabs.data?.customers.find((c) => c.id === customerKey) : undefined;
  const customerLines = customerKey === 'unassigned' ? customerTabs.data?.unassigned.lines : customerTab?.lines;
  const customerName = customerKey === 'unassigned' ? 'Unassigned' : customerTab ? (customerTab.isActive ? customerTab.label : `${customerTab.label} (inactive)`) : '';
  const lineName = v('partType') === 'none' ? 'No product line' : types.data?.find((t) => t.id === v('partType'))?.label;
  const customerList = !view && !!customerKey;
  useEffect(() => {
    if (!customerList || !customerTabs.data) return;
    const first = customerLines?.[0];
    if (!first) return;
    setSp((prev) => {
      if (prev.get('view') || prev.get('partType')) return prev;
      const n = new URLSearchParams(prev);
      n.set('partType', first.typeId ?? 'none');
      return n;
    });
  }, [customerList, customerTabs.data, customerLines, setSp]);
  const title = customerList ? [customerName, lineName].filter(Boolean).join(' · ') || 'Part Master' : family ?? views[view] ?? 'Part Master';
  const baseCols: Col<PartRow>[] = [{ key: 'partNumber', label: pnLabel(familyCode), sort: true, render: (r) => <Mono>{r.partNumber}</Mono> }, { key: 'name', label: 'Name', sort: true }, { key: 'typeName', label: 'Product family', sort: true, render: (r) => r.typeName ?? r.productFamily ?? '—' },
          { key: 'brandName', label: 'Brand', render: (r) => r.brandName || '—' },
          ...(familyCode === FUEL_PUMP ? [] : [{ key: 'drawingNumber', label: DWG_LABEL, render: (r: PartRow) => r.drawingNumber ?? '—' }]),
          ...(attrDefs.data ?? []).filter((d) => attrFor(d, familyCode, famRow?.shownFields)).map((d): Col<PartRow> => ({ key: `attr:${d.key}`, label: d.label, render: (r) => attrsOf(r.attributes)[d.key] ?? '—' })),
          { key: 'partClass', label: 'Part class', render: (r) => r.partClass ?? '—' },
          { key: 'isAssembly', label: 'Assembly', render: (r) => yes(r.isAssembly) }, { key: 'unitOfMeasure', label: 'UoM' },
          { key: 'flags', label: 'Repairable / Serialized / Lot', render: (r) => `${yes(r.isRepairable)} / ${yes(r.isSerialized)} / ${r.lotControl ?? yes(r.isLotControlled)}` },
          { key: 'status', label: 'Status', render: (r) => <SqfStatus status={r.status} /> },
          { key: 'updatedAt', label: 'Updated', sort: true, render: (r) => fmtDateTime(r.updatedAt) }];
  // the Fuel Pump list leads with the numbers of the fuel pump sheet
  const lead = familyCode === FUEL_PUMP ? FP_COLS : ['partNumber', 'attr:orNumber'];
  const isHidden = (k: string) => (fp ? !fpShown.includes(k) && k !== 'partNumber' : hidden.includes(k));
  const customListCols: Col<PartRow>[] = (customFields.data ?? []).filter((f) => f.showInList).map((f) => ({ key: `custom:${f.id}`, label: f.name, render: (r: PartRow) => <span data-testid={`custom-col-${f.name}`}>{r.custom?.find((c) => c.dropdownId === f.id)?.label || '—'}</span> }));
  const allCols = [...lead.map((k) => baseCols.find((c) => c.key === k)!).filter(Boolean), ...customListCols, ...baseCols.filter((c) => !lead.includes(c.key))];
  const buttons = (csv: boolean) => can('sequfloh.edit_parts') && can('parts.edit') && <Stack direction="row" gap={1} flexWrap="wrap" useFlexGap>{csv && <Button variant="outlined" onClick={exportCsv}>Export CSV</Button>}<Button variant="outlined" onClick={() => setChanging(true)}>Change product family{ticked.length ? ` (${ticked.length})` : ''}</Button><Button variant="outlined" data-testid="move-to-customer" disabled={!allMatching && ticked.length === 0} onClick={() => setMoving(true)}>Move to customer</Button><Button variant="outlined" data-testid="assign-customers" onClick={() => setAssigning(true)}>Assign customers</Button><Button variant="outlined" onClick={() => setImporting(true)}>Import parts</Button><Button variant="contained" onClick={() => setOpen(true)}>New part</Button></Stack>;
  const dialogs = <>
    <PartDialog open={open || !!addFromScan || !!addUpc} prefill={addFromScan || undefined} prefillUpc={addUpc || undefined} onClose={() => { setOpen(false); if (addFromScan) set('add', ''); if (addUpc) set('addUpc', ''); }} onSaved={(id) => nav(`/sequfloh/parts/${id}`)} />
    <ImportPartsDialog open={importing} onClose={() => setImporting(false)} preset={preset} />
    {canChange && <ChangeFamilyDialog open={changing} onClose={() => setChanging(false)} families={types.data ?? []} defs={attrDefs.data ?? []} ticked={ticked}
      filters={{ q: v('q'), partType: v('partType'), status: v('status'), tag: v('tag') }} currentFamily={v('partType') && v('partType') !== 'none' ? v('partType') : undefined} onDone={() => setTicked([])} />}
    {canChange && <CustomerAssignmentDialog open={assigning} onClose={() => setAssigning(false)} />}
    {canChange && <MoveCustomerDialog open={moving} onClose={() => setMoving(false)} onDone={() => { setMoving(false); setTicked([]); setAllMatching(false); }} allMatching={allMatching} ticked={ticked} filters={{ q: v('q'), view, family: v('family'), status: v('status'), partType: v('partType'), customer: v('customer'), type: v('type'), tag: v('tag'), brand: v('brand'), custom: customQuery() }} />}
    {wipe && family && <FamilyDeleteDialog familyId={v('partType')} label={family} onClose={() => setWipe(false)} />}
  </>;
  const soldAs = (r: PartRow) => (r.alsoSoldAs ?? []).length === 0 ? '—' : <Stack gap={0.25} onClick={(e) => e.stopPropagation()} onContextMenu={(e) => e.stopPropagation()} data-testid="also-sold-as">
    {(r.alsoSoldAs ?? []).map((x) => <Link key={x.id} component={RouterLink} to={`/sequfloh/parts/${x.id}`} underline="hover" data-testid="also-sold-as-link">{(x.customerName || 'Unassigned')}: {x.partNumber}</Link>)}</Stack>;
  const customerCols: Col<PartRow>[] = [
    { key: 'partNumber', label: 'Customer part #', sort: true, render: (r) => <Box data-testid={r.id === v('found') ? 'found-part' : undefined} sx={r.id === v('found') ? { bgcolor: 'warning.light', px: 0.5, borderRadius: 0.5 } : undefined}><Mono>{r.partNumber}</Mono></Box> },
    { key: 'description', label: 'Description', render: (r) => r.description?.trim() || r.name || '—' },
    { key: 'brandName', label: 'Brand', render: (r) => <span data-testid={`part-brand-${r.partNumber}`}>{r.brandName || '—'}</span> },
    { key: 'upc', label: 'UPC / Barcode', render: (r) => r.upc || '—' },
    { key: 'drawingNumber', label: 'Drawing #', render: (r) => r.drawingNumber || '—' },
    { key: 'alsoSoldAs', label: 'Also sold as', render: soldAs },
    ...(customFields.data ?? []).filter((f) => f.showInList).map((f): Col<PartRow> => ({ key: `custom:${f.id}`, label: f.name, render: (r) => <span data-testid={`custom-col-${f.name}`}>{r.custom?.find((c) => c.dropdownId === f.id)?.label || '—'}</span> })),
  ];
  if (home) return (
    <SqfPage title="Part Master" crumbs={crumbs('Part Master')} description="Customers first. Each customer keeps its own parts and the links already attached to those parts." actions={buttons(false)}>
      {(untyped.data?.total ?? 0) > 0 && v('partType') !== 'none' && <Alert severity="info" sx={{ mb: 2 }} data-testid="untyped-parts"
        action={<Button color="inherit" size="small" onClick={() => set('partType', 'none')}>Show them</Button>}>
        {untyped.data!.total} {untyped.data!.total === 1 ? 'part has' : 'parts have'} no product family yet. Open a part and choose its product family with Edit.</Alert>}
      <PartsHome />
      {dialogs}
    </SqfPage>
  );
  return (
    <SqfPage title={title} crumbs={customerList ? crumbs('Part Master', { label: title }) : crumbs(title)} description={viewHelp[view] ?? (customerList ? 'Only this customer’s product lines are shown. Each row is that customer’s own part.' : family ? `Parts in the ${family} product family.` : 'The part master with execution attributes for each part and revision.')}
      actions={<Stack direction="row" gap={1} flexWrap="wrap" useFlexGap alignItems="center">{buttons(true)}{family && !customerList && <FamilyDeleteButton label={family} onClick={() => setWipe(true)} />}</Stack>}>
      {!view && <Link component={RouterLink} to="/sequfloh/parts" sx={{ display: 'inline-block', mb: 1 }} data-testid="parts-home-link">Back to Part Master home</Link>}
      {customerList && customerTabs.data && <CustomerBar tabs={customerTabs.data} customer={customerKey} line={v('partType')} onCustomer={(id) => { const n = new URLSearchParams(); n.set('customer', id); setSp(n); setPage(1); }} onLine={(id) => set('partType', id)} />}
      {customerList && v('exact') === '1' && v('found') && <Alert severity="info" sx={{ mb: 2 }} data-testid="search-landed" action={<Button color="inherit" size="small" component={RouterLink} to={`/sequfloh/parts/${v('found')}`}>Open this part</Button>}>
        Exact part-number match: {customerName}{lineName ? ` › ${lineName}` : ''}. The original part is selected.</Alert>}
      {(untyped.data?.total ?? 0) > 0 && v('partType') !== 'none' && <Alert severity="info" sx={{ mb: 2 }} data-testid="untyped-parts"
        action={<Button color="inherit" size="small" onClick={() => set('partType', 'none')}>Show them</Button>}>
        {untyped.data!.total} {untyped.data!.total === 1 ? 'part has' : 'parts have'} no product family yet. Open a part and choose its product family with Edit.</Alert>}
      <Filters search={v('q')} onSearch={(s) => set('q', s)} values={{ partType: v('partType'), status: v('status'), type: v('type'), tag: v('tag'), brand: v('brand'), ...Object.fromEntries((customFields.data ?? []).filter((f) => f.filterable).map((f) => [`custom:${f.id}`, v(`custom:${f.id}`)])) }} onChange={set}
        extra={customerList ? undefined : <><Button size="small" onClick={(e) => setColsAnchor(e.currentTarget)}>Columns</Button>
          <Menu anchorEl={colsAnchor} open={!!colsAnchor} onClose={() => setColsAnchor(null)}>
            {allCols.filter((c) => c.key !== 'partNumber').map((c) => <MenuItem key={c.key} dense onClick={() => toggleCol(c.key)}><Checkbox size="small" checked={!isHidden(c.key)} sx={{ p: 0.5, mr: 1 }} />{c.label}</MenuItem>)}
          </Menu></>}
        onClear={() => {
          if (customerList) { const n = new URLSearchParams(); n.set('customer', customerKey); if (v('partType')) n.set('partType', v('partType')); setSp(n); }
          else setSp(view ? { view } : family ? { partType: v('partType') } : {});
          setPage(1);
        }}
        filters={[{ key: 'partType', label: 'Product family', options: [['none', 'No product family'], ...(types.data ?? []).map((t): [string, string] => [t.id, t.isActive ? t.label : `${t.label} (inactive)`])] },
          { key: 'brand', label: 'Brand', options: [['none', 'No brand'], ...(brands.data ?? []).map((b): [string, string] => [b.id, b.isActive ? b.label : `${b.label} (inactive)`])] },
          ...(customFields.data ?? []).filter((f) => f.filterable).map((f) => ({ key: `custom:${f.id}`, label: f.name, options: [['none', `No ${f.name}`], ...f.values.filter((x) => x.isActive).map((x): [string, string] => [x.id, x.label])] as [string, string][] })),
          { key: 'status', label: 'Status', options: masterStatuses.map((x) => [x, x]) }]} />
      <InventoryNote />
      {canChange && (allMatching || ticked.length > 0) && <Alert severity="info" sx={{ mb: 2 }} data-testid="select-all-matching" action={<Stack direction="row" gap={1}>
        {!allMatching && (q.data?.total ?? 0) > ticked.length && <Button color="inherit" size="small" onClick={() => setAllMatching(true)}>Select all {q.data!.total} matching</Button>}
        {allMatching && <Button color="inherit" size="small" onClick={() => setAllMatching(false)}>Only the ticked rows</Button>}
        <Button color="inherit" size="small" onClick={() => setMoving(true)}>Move to customer</Button>
      </Stack>}>{allMatching ? `All ${q.data?.total ?? 0} parts matching this list are selected, including other pages.` : `${ticked.length} selected on this page.`}</Alert>}
      <PagedTable q={q} page={page} size={size} onPage={setPage} onSize={setSize} sort={sort} onSort={setSort} onRow={(r) => nav(`/sequfloh/parts/${r.id}`)} empty="No parts match these filters."
        selected={canChange ? ticked : undefined} onSelect={canChange ? (ids) => { setAllMatching(false); setTicked(ids); } : undefined}
        cols={(customerList ? customerCols : allCols).filter((c) => customerList || !isHidden(c.key))} />
      {dialogs}
    </SqfPage>
  );
}

/** Part Master home: one card per active customer, with the product lines that customer already has. */
function PartsHome() {
  const nav = useNavigate();
  const tabs = useCustomerTabs();
  const [text, setText] = useState('');
  const [hits, setHits] = useState<LocateHit[] | null>(null);
  const [err, setErr] = useState('');
  const search = async () => {
    const term = text.trim();
    setErr(''); setHits(null);
    if (term.length < 2) { setErr('Enter at least two characters.'); return; }
    const r = await get<{ items: LocateHit[] }>(`${S}/parts/locate?${qs({ q: term })}`);
    const exact = r.items.filter((i) => i.exactPartNumber);
    if (exact.length === 1) {
      const i = exact[0];
      nav(`/sequfloh/parts?${qs({ customer: i.customerId ?? 'unassigned', partType: i.typeId ?? 'none', found: i.id, exact: '1', q: i.partNumber })}`);
      return;
    }
    setHits(r.items);
  };
  const place = (i: LocateHit) => [i.customerName || 'Unassigned', i.line || 'No product line', i.partNumber].join(' › ');
  const cards = [...(tabs.data?.customers ?? []).map((c) => ({ ...c, key: c.id, name: c.isActive ? c.label : `${c.label} (inactive)`, to: c.id })),
    { key: 'unassigned', id: 'unassigned', name: 'Unassigned', isActive: true, count: tabs.data?.unassigned.count ?? 0, lines: tabs.data?.unassigned.lines ?? [], to: 'unassigned' }];
  return (
    <Stack gap={3}>
      <ListToolbar onSubmit={() => { void search(); }}>
        <TextField size="small" label="Search parts" placeholder="Search any customer part number, drawing or application" value={text} onChange={(e) => setText(e.target.value)} inputProps={{ 'data-testid': 'parts-search' }} sx={{ width: { xs: '100%', sm: 520 } }} />
        <Button type="submit" variant="contained">Search</Button>
      </ListToolbar>
      {err && <Alert severity="info">{err}</Alert>}
      {hits && <Paper variant="outlined" data-testid="parts-search-results" sx={{ p: 2 }}>
        {hits.length === 0 ? <Typography color="text.secondary">No parts match "{text.trim()}".</Typography> : <>
          <Typography variant="body2" sx={{ mb: 1 }}>{hits.length === 1 ? '1 match' : `${hits.length} matches`}. Choose the customer and product line. Nothing is opened on its own when more than one part matches.</Typography>
          <Stack gap={0.5}>{hits.map((i) => <Button key={i.id} component={RouterLink} to={`/sequfloh/parts?${qs({ customer: i.customerId ?? 'unassigned', partType: i.typeId ?? 'none', found: i.id, q: i.partNumber })}`} sx={{ justifyContent: 'flex-start' }} data-testid="search-hit">{place(i)}{i.exactPartNumber ? '' : ' · alias'}</Button>)}</Stack>
        </>}
      </Paper>}
      <Alert severity="info" icon={false}>Open a customer, then a product line. Each customer shows only the lines and parts it has.</Alert>
      <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))' }}>
        {cards.map((c) => (
          <Paper key={c.key} variant="outlined" data-testid={`customer-card-${c.name.replace(' (inactive)', '')}`}
            sx={{ p: 2, minHeight: 148, display: 'flex', flexDirection: 'column', justifyContent: 'space-between', gap: 1, '&:hover': { borderColor: 'primary.main' } }}>
            <Box component={RouterLink} to={`/sequfloh/parts?${qs({ customer: c.to, partType: c.lines[0] ? (c.lines[0].typeId ?? 'none') : undefined })}`} sx={{ textDecoration: 'none', color: 'inherit' }}>
              <Stack direction="row" justifyContent="space-between" alignItems="baseline" gap={1}>
                <Typography fontWeight={700}>{c.name}</Typography>
                <Typography variant="h5" color="primary" data-testid="customer-count">{tabs.isLoading ? '…' : c.count.toLocaleString('en-US')}</Typography>
              </Stack>
              <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>{c.lines.length === 0 ? 'No product lines yet' : c.lines.map((l) => `${l.label} (${l.count.toLocaleString('en-US')})`).join(' · ')}</Typography>
              <Typography variant="caption" color="text.secondary">{c.lines.length} product {c.lines.length === 1 ? 'line' : 'lines'}</Typography>
            </Box>
            <Typography variant="body2" color="primary" fontWeight={600}>Open customer</Typography>
          </Paper>))}
      </Box>
    </Stack>
  );
}

/** A chosen customer or product line: white text on the indigo fill, so a long name stays readable. */
const selectedChip = { backgroundColor: '#4F46E5', color: '#FFFFFF', '& .MuiChip-label': { color: '#FFFFFF' }, '&:hover': { backgroundColor: '#3730A3', color: '#FFFFFF', '& .MuiChip-label': { color: '#FFFFFF' } } };

/** Customer chips and, under the chosen customer, only the product lines that customer has. */
function CustomerBar({ tabs, customer, line, onCustomer, onLine }: { tabs: CustomerTabs; customer: string; line: string; onCustomer: (id: string) => void; onLine: (id: string) => void }) {
  const mine = customer === 'unassigned' ? tabs.unassigned.lines : tabs.customers.find((c) => c.id === customer)?.lines ?? [];
  const chips = [...tabs.customers.map((c) => ({ id: c.id, label: `${c.isActive ? c.label : `${c.label} (inactive)`} ${c.count.toLocaleString('en-US')}` })), { id: 'unassigned', label: `Unassigned ${tabs.unassigned.count.toLocaleString('en-US')}` }];
  return <Stack gap={1} sx={{ mb: 2 }} data-testid="customer-bar">
    <Stack direction="row" gap={1} flexWrap="wrap" useFlexGap>{chips.map((c) => { const on = c.id === customer; return <Chip key={c.id} label={c.label} clickable variant={on ? 'filled' : 'outlined'} sx={on ? selectedChip : undefined} onClick={() => onCustomer(c.id)} data-testid={`customer-chip-${c.id}`} />; })}</Stack>
    {mine.length > 0 && <Stack direction="row" gap={1} flexWrap="wrap" useFlexGap data-testid="line-tabs">{mine.map((l) => {
      const id = l.typeId ?? 'none';
      const on = id === line;
      return <Chip key={id} label={`${l.label} ${l.count.toLocaleString('en-US')}`} clickable variant={on ? 'filled' : 'outlined'} sx={on ? selectedChip : undefined} onClick={() => onLine(id)} data-testid={`line-tab-${l.label}`} />;
    })}</Stack>}
    {mine.length === 0 && <Typography variant="body2" color="text.secondary" data-testid="no-lines">This customer has no parts yet. Import a sheet to add the first product line.</Typography>}
  </Stack>;
}

const nParts = (n: number) => `${n.toLocaleString('en-US')} ${n === 1 ? 'part' : 'parts'}`;
interface FamilyPartSkip { partNumber: string; workOrders: number; instructions: number; partsLists: number; reason: string }
interface FamilyPlan { family: string; total: number; deletable: number; skipped: number; skips: FamilyPartSkip[] }
interface FamilyDeleteResult { family: string; deleted: number; skipped: number; more: boolean; skips: FamilyPartSkip[] }

function SkipList({ skips }: { skips: FamilyPartSkip[] }) {
  if (!skips.length) return null;
  const shown = skips.slice(0, 12);
  return (
    <Box component="ul" sx={{ m: 0, mt: 1, pl: 2.5, maxHeight: 220, overflow: 'auto' }} data-testid="delete-family-skips">
      {shown.map((s, i) => <Typography key={`${s.partNumber}-${i}`} component="li" variant="body2" sx={{ mb: 0.25 }}><Mono>{s.partNumber}</Mono> — {s.reason}</Typography>)}
      {skips.length > shown.length && <Typography component="li" variant="body2" color="text.secondary">and {(skips.length - shown.length).toLocaleString('en-US')} more</Typography>}
    </Box>
  );
}

/** Opens the confirm dialog that wipes one product family's unused parts. Hidden without Delete records. */
function FamilyDeleteButton({ label, onClick, size = 'medium' }: { label: string; onClick: () => void; size?: 'small' | 'medium' }) {
  const { can } = useAuth();
  if (!can('sequfloh.delete_records')) return null;
  return <Button size={size} color="error" variant="outlined" data-testid="delete-family" aria-label={`Delete all parts in ${label}`} onClick={(e) => { e.preventDefault(); e.stopPropagation(); onClick(); }}>Delete all</Button>;
}

/** Confirms, then deletes the unused parts of one family and reports what was kept. */
function FamilyDeleteDialog({ familyId, label, onClose }: { familyId: string; label: string; onClose: () => void }) {
  const qc = useQueryClient();
  const [plan, setPlan] = useState<FamilyPlan | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [soFar, setSoFar] = useState(0);
  const [done, setDone] = useState<{ deleted: number; skipped: number; skips: FamilyPartSkip[]; family: string } | null>(null);
  const url = `${S}/parts/families/${encodeURIComponent(familyId)}`;
  useEffect(() => {
    let live = true;
    setLoading(true); setError(null); setPlan(null);
    get<FamilyPlan>(`${url}/delete-preview`).then((p) => { if (live) setPlan(p); }, (e: unknown) => { if (live) setError((e as Error).message); }).finally(() => { if (live) setLoading(false); });
    return () => { live = false; };
  }, [url]);
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['sqf-parts'] }); void qc.invalidateQueries({ queryKey: ['sqf-parts-untyped'] }); };
  const run = async () => {
    setBusy(true); setError(null);
    let deleted = 0;
    try {
      let last: FamilyDeleteResult;
      do {
        last = await post<FamilyDeleteResult>(`${url}/delete`);
        deleted += last.deleted;
        setSoFar(deleted);
      } while (last.more);
      setDone({ deleted, skipped: last.skipped, skips: last.skips, family: last.family });
      refresh();
    } catch (e) {
      setError(`${(e as Error).message}${deleted ? ` ${nParts(deleted)} were already deleted.` : ''}`);
      refresh();
    } finally { setBusy(false); }
  };
  const name = done?.family ?? plan?.family ?? label;
  const close = () => { if (!busy) onClose(); };
  return (
    <Dialog open onClose={close} fullWidth maxWidth="sm" data-testid={done ? 'delete-family-result' : 'delete-family-confirm'}>
      <DialogTitle>{done ? 'Parts deleted' : `Delete all parts in ${name}?`}</DialogTitle>
      <DialogContent>
        {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
        {done ? <>
          <Typography variant="body2" data-testid="delete-family-deleted">Deleted {nParts(done.deleted)} in {done.family}.</Typography>
          <Typography variant="body2" sx={{ mt: 1 }} data-testid="delete-family-skipped">Skipped {nParts(done.skipped)}.</Typography>
          {done.skipped > 0 && <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>These stay because a work order, a work instruction, or a parts list uses them.</Typography>}
          <SkipList skips={done.skips} />
        </> : loading ? <Loading /> : plan && plan.deletable === 0 && plan.skipped === 0 ? <Typography variant="body2">{name} has no parts to delete.</Typography> : plan && <>
          <Typography variant="body2">This deletes {nParts(plan.deletable)} in {name}. Other product families are not changed.</Typography>
          {plan.skipped > 0 && <Typography variant="body2" sx={{ mt: 1 }}>{nParts(plan.skipped)} will be skipped because a work order, a work instruction, or a parts list uses them.</Typography>}
          <SkipList skips={plan.skips} />
          <Typography variant="body2" color="text.secondary" sx={{ mt: 1.5 }}>Drawing links on the deleted parts are removed. The drawings stay. This can't be undone.</Typography>
          {busy && soFar > 0 && <Alert severity="info" sx={{ mt: 2 }}>Deleted {nParts(soFar)} so far. Still working through this family.</Alert>}
        </>}
      </DialogContent>
      <DialogActions>
        <Button onClick={close} disabled={busy}>{done || (plan && plan.deletable === 0) ? 'Close' : 'Cancel'}</Button>
        {!done && plan && plan.deletable > 0 && <Button variant="contained" color="error" disabled={busy || loading} data-testid="delete-family-go" onClick={run}>{busy ? 'Deleting…' : `Delete ${nParts(plan.deletable)}`}</Button>}
      </DialogActions>
    </Dialog>
  );
}

interface PartDetail { part: Omit<PartRow, 'attributes'> & { attributes?: Record<string, string>; productId?: string; material?: string; hasSpecialCharacteristics: boolean; hiddenFields?: string[]; siteId: string; departmentId?: string; rowVersion: number; createdAt: string; createdBy: string; updatedBy: string; customerId?: string | null; customerName?: string | null; brandId?: string | null; brandName?: string | null };
  revisions: { id: string; revision: string; reason: string; createdAt: string }[]; partsLists: { id: string; number: string; title: string; revision: string; status: string; listType: string }[];
  related: { partsLists: number }; crossReferences?: SoldAs[]; canEdit: boolean; canEditMaster: boolean }

function PartDialog({ open, onClose, onSaved, edit, prefill, prefillUpc }: { open: boolean; onClose: () => void; onSaved: (id: string) => void; edit?: PartDetail['part']; prefill?: string; prefillUpc?: string }) {
  const sites = useSites();
  const qc = useQueryClient();
  const customers = useQuery({ queryKey: ['sqf-customers-active'], queryFn: () => get<{ id: string; label: string; isActive: boolean }[]>(`${S}/reference/customers?active=true`), enabled: open });
  const brands = useQuery({ queryKey: ['sqf-brands'], queryFn: () => get<{ id: string; label: string; isActive: boolean }[]>(`${S}/reference/brands`), enabled: open });
  const [customerId, setCustomerId] = useState('');
  const [brandId, setBrandId] = useState('');
  const [custom, setCustom] = useState<Record<string, string>>({});
  const customDefs = useCustomFields('parts', edit?.id);
  const { can } = useAuth();
  const drawingsOk = can('sequfloh.manage_drawings');
  const [links, setLinks] = useState<{ id: string; label: string }[]>([]);
  const [inline, setInline] = useState<ReturnType<typeof blankDrawing> | null>(null);
  const types = usePartTypes();
  const attrDefs = useAttrDefs();
  const [typeId, setTypeId] = useState('');
  const [attrs, setAttrs] = useState<Record<string, string>>({});
  const [hidden, setHidden] = useState<string[]>([]);
  const [naOpen, setNaOpen] = useState(false);
  const fieldDefs = usePartFields();
  const typeFam = types.data?.find((t) => t.id === typeId); const typeCode = typeFam?.code;
  const shownAttrs = (attrDefs.data ?? []).filter((d) => attrFor(d, typeCode, typeFam?.shownFields));
  const blank = { partNumber: '', name: '', description: '', partType: 'Component', revision: 'A', unitOfMeasure: 'Each', material: '', siteId: '', status: 'Active',
    productFamily: '', assemblyType: '', partClass: '', isAssembly: false, lotControl: '', isRepairable: false, isRepairPart: false, isSerialized: false, isLotControlled: false, notes: '', drawingNumber: '', drawingRevision: '', upc: '', specification: '' };
  const [f, setF] = useState(blank);
  const [err, setErr] = useState<string | null>(null);
  const init = () => {
    setLinks([]); setInline(null); setTypeId(edit?.typeId ?? ''); setAttrs({ ...(edit?.attributes ?? {}) }); setHidden(edit?.hiddenFields ?? []);
    if (edit && drawingsOk) get<PartDrawing[]>(`/api/drawings/for-part?partId=${edit.id}`).then((l) => setLinks(l.map((d) => ({ id: d.id, label: `${d.drawingNumber ? d.drawingNumber + ' · ' : ''}${d.title} (rev ${d.currentRevision})` })))).catch(() => undefined);
    const lc = (e?: PartDetail['part']) => (e ? (e.lotControl || (e.isLotControlled ? 'Yes' : 'No')) : '');
    setCustomerId(edit?.customerId ?? '');
    setBrandId(edit?.brandId ?? '');
    setCustom({});
    setErr(null); setF(edit ? { ...blank, ...Object.fromEntries(Object.entries(edit).map(([k, x]) => [k, x ?? ''])), lotControl: lc(edit) } as typeof blank : { ...blank, partNumber: prefill ?? '', upc: prefillUpc ?? '', siteId: sites.data?.[0]?.id ?? '' }); };
  const familyLabels = (types.data ?? []).filter((t) => t.isActive).map((t) => t.label);
  const yn = (k: 'isAssembly' | 'isRepairable') => ({ value: f[k] ? 'Yes' : 'No', onChange: (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: e.target.value === 'Yes' }) });
  const s = (k: keyof typeof blank) => (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: e.target.value });
  const b = (k: keyof typeof blank) => (_: unknown, c: boolean) => setF({ ...f, [k]: c });
  const save = async () => {
    setErr(null);
    const body = { part: { partNumber: f.partNumber, name: f.name, description: nul(f.description), partType: partTypeValue[f.partType] ?? 0, revision: f.revision, unitOfMeasure: f.unitOfMeasure, productId: edit?.productId ?? null,
      material: nul(f.material), hasSpecialCharacteristics: edit?.hasSpecialCharacteristics ?? false, siteId: f.siteId, departmentId: edit?.departmentId ?? null, status: Math.max(0, masterStatuses.indexOf(f.status)), rowVersion: edit?.rowVersion ?? 0 },
      execution: { productFamily: nul(f.productFamily), assemblyType: nul(f.assemblyType), isRepairable: f.isRepairable, isRepairPart: f.isRepairPart, isSerialized: f.isSerialized, isLotControlled: f.lotControl === 'Yes',
        partClass: nul(f.partClass), isAssembly: f.isAssembly, lotControl: nul(f.lotControl),
        drawingNumber: nul(f.drawingNumber), drawingRevision: nul(f.drawingRevision), specification: nul(f.specification), rowVersion: edit?.rowVersion ?? 0 },
      typeId: typeId || null, brandId: brandId || null, attributes: Object.fromEntries(shownAttrs.map((d) => [d.key, nul(attrs[d.key] ?? '')])), notes: f.notes, hiddenFields: edit || hidden.length ? hidden : null, upc: f.upc.trim() };
    const missing = missingRequired(customDefs.data, custom);
    if (missing) { setErr(`${missing.name} is required.`); return; }
    try {
      let id = edit?.id;
      if (edit) await put(`${S}/parts/${edit.id}`, body); else id = (await post<{ id: string }>(`${S}/parts`, body)).id;
      if (id && (edit?.customerId ?? '') !== customerId) {
        await post(`${S}/parts/move-customer`, { mode: 'ids', partIds: [id], customerId: customerId || null, toUnassigned: customerId === '' });
        qc.invalidateQueries({ queryKey: ['sqf-customer-tabs'] });
      }
      if (id) await saveCustomFields('parts', id, custom, `part ${f.partNumber}`);
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
      <FormDialogTitle>{edit ? `Edit part ${edit.partNumber}` : 'New part'}</FormDialogTitle>
      <DialogContent>
        {err && <Alert severity="error" sx={{ mb: 2 }}>{err}</Alert>}
        <Box sx={{ pt: 1 }}><Grid>
          <TextField label={pnLabel(typeCode)} required value={f.partNumber} onChange={s('partNumber')} />
          <TextField select label="Customer" value={customerId} onChange={(e) => setCustomerId(e.target.value)} inputProps={{ 'data-testid': 'part-customer-select' }} helperText="Unassigned until a customer is chosen. A part number the customer already has in this product line is left unchanged.">
            <MenuItem value="">Unassigned</MenuItem>
            {(customers.data ?? []).filter((c) => c.isActive || c.id === customerId).map((c) => <MenuItem key={c.id} value={c.id}>{c.isActive ? c.label : `${c.label} (inactive)`}</MenuItem>)}
          </TextField>
          <TextField select label="Brand" value={brandId} onChange={(e) => setBrandId(e.target.value)} inputProps={{ 'data-testid': 'part-brand-select' }}>
            <MenuItem value="">None</MenuItem>
            {(brands.data ?? []).filter((b) => b.isActive || b.id === brandId).map((b) => <MenuItem key={b.id} value={b.id}>{b.isActive ? b.label : `${b.label} (inactive)`}</MenuItem>)}
          </TextField>
          <CustomFieldInputs screen="parts" recordId={edit?.id} value={custom} onChange={setCustom} />
          <TextField select label="Product family" required value={typeId} onChange={(e) => setTypeId(e.target.value)} inputProps={{ 'data-testid': 'part-type-select' }}
            error={!!edit && !typeId} helperText={edit && !typeId ? 'Choose the product family for this part.' : undefined}>
            {(types.data ?? []).filter((t) => t.isActive || t.id === typeId).map((t) => <MenuItem key={t.id} value={t.id}>{t.isActive ? t.label : `${t.label} (inactive)`}</MenuItem>)}</TextField>
          <TextField select label="Name" required value={f.name} onChange={s('name')} inputProps={{ 'data-testid': 'part-name' }}>
            {withCurrent(familyLabels, f.name).map((x) => <MenuItem key={x} value={x}>{x}</MenuItem>)}</TextField>
          <TextField select label="Part class" value={f.partClass} onChange={s('partClass')} inputProps={{ 'data-testid': 'part-class' }}>
            <MenuItem value=""><em>None</em></MenuItem>{withCurrent(familyLabels, f.partClass).map((x) => <MenuItem key={x} value={x}>{x}</MenuItem>)}</TextField>
          <TextField select label="Unit of measure" value={f.unitOfMeasure} onChange={s('unitOfMeasure')} inputProps={{ 'data-testid': 'part-uom' }}>
            {withCurrent(UNITS, f.unitOfMeasure).map((x) => <MenuItem key={x} value={x}>{x}</MenuItem>)}</TextField>
          <TextField select label="Site" required value={f.siteId} onChange={s('siteId')}>{(sites.data ?? []).map((x) => <MenuItem key={x.id} value={x.id}>{x.code} · {x.name}</MenuItem>)}{setupOptions('site', sites.data)}</TextField>
          <TextField select label="Assembly" {...yn('isAssembly')} inputProps={{ 'data-testid': 'part-assembly' }}>{['Yes', 'No'].map((x) => <MenuItem key={x} value={x}>{x}</MenuItem>)}</TextField>
          <TextField select label="Lot controlled" value={f.lotControl} onChange={s('lotControl')} inputProps={{ 'data-testid': 'part-lot' }}>
            <MenuItem value=""><em>Not set</em></MenuItem>{LOT_CHOICES.map((x) => <MenuItem key={x} value={x}>{x}</MenuItem>)}</TextField>
          <TextField select label="Repairable" {...yn('isRepairable')} inputProps={{ 'data-testid': 'part-repairable' }} helperText="Yes: the part can be repaired if required.">{['Yes', 'No'].map((x) => <MenuItem key={x} value={x}>{x}</MenuItem>)}</TextField>
          <TextField select label="Status" value={f.status} onChange={s('status')}>{masterStatuses.map((t) => <MenuItem key={t} value={t}>{t}</MenuItem>)}</TextField>
          {typeCode !== FUEL_PUMP && <TextField label={DWG_LABEL} value={f.drawingNumber} onChange={s('drawingNumber')} helperText="Kept whole. A trailing letter stays part of the number." />}
          <TextField label="Drawing revision" value={f.drawingRevision} onChange={(e) => setF({ ...f, drawingRevision: e.target.value.toUpperCase() })} inputProps={{ 'data-testid': 'part-drawing-revision' }}
            helperText="The revision recorded in the Part Master. Not read from a file name. The part's own revision is separate." />
          <TextField label="UPC / Barcode" value={f.upc} onChange={(e) => setF({ ...f, upc: e.target.value.replace(/\s/g, '') })} inputProps={{ inputMode: 'numeric', 'data-testid': 'part-upc' }}
            helperText="The digits under the box-label barcode (8, 12, 13 or 14 digits). Optional." />
          <TextField label="Specification" value={f.specification} onChange={s('specification')} />
          <TextField label="Material" value={f.material} onChange={s('material')} />
        </Grid>
        {shownAttrs.length > 0 && <><Typography variant="subtitle2" sx={{ mt: 2, mb: 1 }}>Cross-references and application</Typography>
          <Grid>{shownAttrs.map((d) => <TextField key={d.key} label={d.label} value={attrs[d.key] ?? ''} onChange={(e) => setAttrs({ ...attrs, [d.key]: e.target.value })}
            multiline={d.maxLength > 200} inputProps={{ maxLength: d.maxLength }} />)}</Grid></>}
        <TextField label="Description" fullWidth multiline minRows={2} value={f.description} onChange={s('description')} sx={{ mt: 2 }} />
        <TextField label="Notes" fullWidth multiline minRows={3} value={f.notes} onChange={s('notes')} sx={{ mt: 2 }} inputProps={{ maxLength: 4000, 'data-testid': 'part-notes-input' }}
          helperText="Shown at the top of the part. Changes are kept in the part's History." />
        <Stack direction="row" gap={2} sx={{ mt: 1 }}>
          <FormControlLabel control={<Checkbox checked={!!f.isRepairPart} onChange={b('isRepairPart')} />} label="Repair part" />
          <FormControlLabel control={<Checkbox checked={f.isSerialized} onChange={b('isSerialized')} />} label="Serialized" />
        </Stack>
        <Typography variant="caption" color="text.secondary" component="p" data-testid="repair-flags-help">
          Repairable: the part itself can be repaired. Repair part: a component or replacement part ordered to carry out repairs (screws, nuts, top mounts); only these appear in Repair Parts.
        </Typography>
        <Paper variant="outlined" sx={{ p: 2, mt: 1 }} data-testid="part-na-fields">
          <Stack direction="row" alignItems="center" gap={1}><Typography variant="subtitle2">Not applicable on this part's screen{hidden.length ? ` (${hidden.length} hidden)` : ''}</Typography>
            <Button size="small" data-testid="part-na-toggle" onClick={() => setNaOpen(!naOpen)}>{naOpen ? 'Done' : 'Choose fields'}</Button></Stack>
          <Typography variant="caption" color="text.secondary" component="p">Ticked fields are hidden on this part's page. Their values are kept, and the page can show them again.{!edit && ' Left empty, the product family defaults apply.'}</Typography>
          {naOpen && <Stack direction="row" flexWrap="wrap" columnGap={2}>{(fieldDefs.data ?? []).filter((d) => !(attrDefs.data ?? []).some((a) => a.key === d.key) || shownAttrs.some((a) => a.key === d.key) || hidden.includes(d.key)).map((d) =>
            <FormControlLabel key={d.key} control={<Checkbox size="small" checked={hidden.includes(d.key)} 
              onChange={(_, c) => setHidden(c ? [...hidden, d.key] : hidden.filter((x) => x !== d.key))} />} label={d.label} />)}</Stack>}
        </Paper>
        {drawingsOk && <Paper variant="outlined" sx={{ p: 2, mt: 1 }}>
          <Typography variant="subtitle2" sx={{ mb: 1.5 }}>Drawings</Typography>
          <DrawingPicker value={links} onChange={setLinks} />
          {inline ? <Box sx={{ mt: 2 }}><DrawingFileFields f={inline} set={setInline} /><Button size="small" sx={{ mt: 1 }} onClick={() => setInline(null)}>Don't upload</Button></Box>
            : <Button size="small" sx={{ mt: 1 }} onClick={() => setInline(blankDrawing())}>Upload drawing</Button>}
        </Paper>}</Box>
      </DialogContent>
      <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" onClick={save} disabled={!f.partNumber.trim() || !f.name.trim() || !f.siteId || !typeId || (!!inline?.file && (!inline.title.trim() || !inline.revision.trim()))}>Save</Button></DialogActions>
    </Dialog>
  );
}

const Field = ({ label, children, hide }: { label: string; children: React.ReactNode; hide?: boolean }) => hide ? null :
  <Box><Typography variant="caption" color="text.secondary">{label}</Typography><Typography variant="body2">{children ?? '—'}</Typography></Box>;

export function PartDetailPage() {
  const { id } = useParams();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['sqf-part', id], queryFn: () => get<PartDetail>(`${S}/parts/${id}`) });
  const [edit, setEdit] = useState(false);
  const [confirm, setConfirm] = useState(false);
  const [tab, setTab] = useState('overview');
  const [showHidden, setShowHidden] = useState(false);
  const attrDefs = useAttrDefs();
  const ptypes = usePartTypes();
  const p = q.data?.part;
  const pFam = ptypes.data?.find((t) => t.id === p?.typeId); const code = pFam?.code;
  const hid = (k: string) => !showHidden && !!p?.hiddenFields?.includes(k);
  const refresh = () => { qc.invalidateQueries({ queryKey: ['drawings-for-part'] }); qc.invalidateQueries({ queryKey: ['sqf-part', id] }); };
  return (
    <SqfPage title={p ? `${p.partNumber} · ${p.name}` : 'Part'} crumbs={crumbs('Part Master', { label: p?.partNumber ?? '…' })} recent={p ? { label: `Part ${p.partNumber}`, itemType: 'Part', recordId: p.id } : null}
      tabs={[{ label: 'Overview', value: 'overview' }, { label: 'Parts list', value: 'lists' }, { label: 'Repair parts', value: 'repair' }]} tab={tab} onTab={setTab}
      actions={p && <RecordActions surface="part" edit={!!q.data?.canEdit && { onClick: () => setEdit(true) }}
        del={{ what: `part ${p.partNumber}`, url: `${S}/parts/${p.id}`, to: '/sequfloh/parts',
          extra: "Links to drawings are removed; the drawings stay. Parts used by work instructions, work orders or parts lists can't be deleted; deactivate them instead." }}>
        {q.data?.canEdit && <Button color={p.status === 'Inactive' ? 'primary' : 'error'} onClick={() => setConfirm(true)}>{p.status === 'Inactive' ? 'Reactivate' : 'Deactivate'}</Button>}
      </RecordActions>}>
      {q.isLoading ? <Loading /> : q.error ? <ErrorState error={q.error} retry={q.refetch} /> : p && <>
        <LatestActivity partId={p.id} />
        {p.notes && <Alert severity="warning" icon={false} sx={{ mb: 2, whiteSpace: 'pre-wrap' }} data-testid="part-notes"><Typography variant="subtitle2">Notes</Typography>{p.notes}</Alert>}
        <Paper variant="outlined" sx={{ p: 2, mb: 2 }} data-testid="part-drawings-box"><Typography variant="subtitle2" sx={{ mb: 1 }}>Drawings</Typography><PartDrawingLinks partId={p.id} /></Paper>
        <InventoryNote />
        {tab === 'overview' && !p.typeId && <Alert severity="warning" sx={{ mb: 2 }} data-testid="set-part-type"
          action={q.data?.canEdit && <Button color="inherit" size="small" onClick={() => setEdit(true)}>Set product family</Button>}>This part has no product family yet.</Alert>}
        {tab === 'overview' && <Paper variant="outlined" sx={{ p: 2 }}>
          {!q.data!.canEditMaster && q.data!.canEdit && <Alert severity="info" sx={{ mb: 2 }}>You can change SEQUFLOH execution attributes. Core Part Master fields need Master Data edit permission.</Alert>}
          {(p.hiddenFields?.length ?? 0) > 0 && <Button size="small" sx={{ mb: 1 }} data-testid="show-hidden-fields" onClick={() => setShowHidden(!showHidden)}>
            {showHidden ? 'Hide not-applicable fields' : `Show ${p.hiddenFields!.length} not-applicable field${p.hiddenFields!.length === 1 ? '' : 's'}`}</Button>}
          <Grid cols={4}>
            <Field label={pnLabel(code)}><Mono>{p.partNumber}</Mono><ViewDrawingButton partId={p.id} /></Field><Field label="Name">{p.name}</Field><Field label="Status"><SqfStatus status={p.status} /></Field>
            <Field label="Customer"><span data-testid="part-customer">{p.customerName || 'Unassigned'}</span></Field>
            <Field label="Brand"><span data-testid="part-brand">{p.brandName || '—'}</span></Field>
            <CustomFieldDetails screen="parts" recordId={p.id} />
            <Field label="Product family">{p.typeName ?? p.productFamily}</Field><Field label="Part class" hide={hid('partClass')}>{p.partClass}</Field><Field label="Assembly" hide={hid('isAssembly')}>{yes(p.isAssembly)}</Field><Field label="Unit of measure" hide={hid('unitOfMeasure')}>{p.unitOfMeasure}</Field>
            <Field label="Repairable" hide={hid('isRepairable')}>{yes(p.isRepairable)}</Field><Field label="Repair part" hide={hid('isRepairPart')}>{yes(!!p.isRepairPart)}</Field><Field label="Serialized" hide={hid('isSerialized')}>{yes(p.isSerialized)}</Field><Field label="Lot controlled" hide={hid('isLotControlled')}>{p.lotControl || yes(p.isLotControlled)}</Field><Field label="Material" hide={hid('material')}>{p.material}</Field>
            {(attrDefs.data ?? []).filter((d) => attrFor(d, code, pFam?.shownFields)).map((d) => <Field key={d.key} label={d.label} hide={hid(d.key)}>{p.attributes?.[d.key]}</Field>)}
            {(attrDefs.data ?? []).filter((d) => !attrFor(d, code, pFam?.shownFields) && p.attributes?.[d.key]).map((d) => <Field key={d.key} label={`${d.label} (kept from another family)`}>{p.attributes?.[d.key]}</Field>)}
            {code !== FUEL_PUMP && <Field label={DWG_LABEL} hide={hid('drawingNumber')}>{p.drawingNumber}</Field>}<Field label="Drawing revision" hide={hid('drawingRevision')}><span data-testid="part-drawing-revision-value">{p.drawingRevision || '—'}</span></Field><Field label="UPC / Barcode"><span data-testid="part-upc-value">{p.upc}</span>{q.data?.canEdit && <BarcodeButton partId={p.id} upc={p.upc} onSaved={refresh} />}</Field><Field label="Specification" hide={hid('specification')}>{p.specification}</Field><Field label="Created">{`${fmtDateTime(p.createdAt)} · ${p.createdBy}`}</Field><Field label="Last updated">{`${fmtDateTime(p.updatedAt)} · ${p.updatedBy}`}</Field>
          </Grid>
          {p.description && !hid('description') && <Typography variant="body2" sx={{ mt: 2 }}>{p.description}</Typography>}
          <Typography variant="subtitle2" sx={{ mt: 2 }}>Also sold as — other customer parts</Typography>
          <Typography variant="caption" color="text.secondary">Matched by drawing number. Each link opens that customer's own part.</Typography>
          {(q.data?.crossReferences ?? []).length === 0 ? <Typography variant="body2" data-testid="cross-references">No other part uses this drawing.</Typography>
            : <Table size="small" data-testid="cross-references"><TableHead><TableRow><TableCell>Customer</TableCell><TableCell>Their part number</TableCell><TableCell>UPC / Barcode</TableCell><TableCell>Same drawing</TableCell></TableRow></TableHead>
              <TableBody>{q.data!.crossReferences!.map((x) => <TableRow key={x.id}><TableCell>{x.customerName || 'Unassigned'}</TableCell>
                <TableCell><Link component={RouterLink} to={`/sequfloh/parts/${x.id}`} data-testid="cross-reference-link"><Mono>{x.partNumber}</Mono></Link></TableCell>
                <TableCell>{x.upc || '—'}</TableCell><TableCell>{x.drawingNumber || '—'}</TableCell></TableRow>)}</TableBody></Table>}
          <Typography variant="subtitle2" sx={{ mt: 2 }}>Related SEQUFLOH records</Typography>
          <Typography variant="body2">Parts lists: {q.data!.related.partsLists}</Typography>
        </Paper>}
        {tab === 'lists' && <PartLinksPanel partId={p.id} list="parts-list" canEdit={!!q.data!.canEditMaster && !!q.data!.canEdit} />}
        {tab === 'repair' && <PartLinksPanel partId={p.id} list="repair-parts" canEdit={!!q.data!.canEditMaster && !!q.data!.canEdit} />}
        {tab === 'lists' && q.data!.partsLists.length > 0 && <Typography variant="subtitle2" sx={{ mb: 1 }}>Controlled parts lists that use this part</Typography>}
        {tab === 'lists' && (q.data!.partsLists.length === 0 ? null : <SimpleTable head={['Number', 'Title', 'Rev', 'Type', 'Status']}
          rows={q.data!.partsLists.map((l) => [<Link key="n" component={RouterLink} to={`/sequfloh/parts-lists/${l.id}`}><Mono>{l.number}</Mono></Link>, l.title, l.revision, words(l.listType), <SqfStatus key="s" status={l.status} />])} />)}
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
  const [text, setText] = useState('');
  const parts = usePartSearch<PartRow>(text, { view: 'active' });
  // the chosen part may not be on the current page of matches, so read it on its own
  const chosen = useQuery({ queryKey: ['sqf-part-one', value], enabled: !!value, queryFn: () => get<{ part: PartRow }>(`${S}/parts/${value}`) });
  const current = value ? (parts.items.find((p) => p.id === value) ?? chosen.data?.part ?? null) : null;
  const scan = usePartFieldScan((p) => { setText(''); onChange(p.id, p.partNumber); });
  return <Box {...scan}><Autocomplete size="small" options={parts.items} value={current} filterOptions={(x) => x} isOptionEqualToValue={(a, b) => a.id === b.id} loading={parts.loading}
    getOptionLabel={(p) => partOption(p)} noOptionsText={text ? 'No part matches' : 'Type a part number'}
    renderOption={(props, p) => <li {...props} key={p.id}><Box><Typography variant="body2">{partOption(p)}</Typography><Typography variant="caption" color="text.secondary">{[p.customerName || 'Unassigned', p.typeName].filter(Boolean).join(' › ')}{otherNumbers(p) ? ` · ${otherNumbers(p)}` : ''}</Typography></Box></li>}
    onInputChange={(_, t, r) => { if (r === 'input') setText(t); }} onChange={(_, p) => onChange(p?.id ?? '', p?.partNumber)}
    renderInput={(p) => <TextField {...p} label={label} helperText={parts.hint} placeholder="Search by part number, DWG #, NAPA, DMA or JH PN" />} /></Box>;
}

interface ListDetail { list: { id: string; number: string; title: string; parentPartId?: string; revision: string; status: string; description?: string; listType: string; releasedAt?: string; releasedBy: string;
  rowVersion: number; createdAt: string; updatedAt: string; createdBy: string; updatedBy: string; parentPart?: { id: string; partNumber: string; name: string } };
  items: ItemRow[]; revisions: { id: string; revision: string; statusAtRevision: string; reason?: string; snapshot: string; createdAt: string; createdBy?: string }[]; canEdit: boolean }
interface ItemRow { id: string; itemNumber: number; partId?: string; partNumber: string; description?: string; quantity: number; unit?: string; required: boolean; alternatePartNumber?: string; traceability: string; notes?: string }

function ListDialog({ open, onClose, onSaved, edit }: { open: boolean; onClose: () => void; onSaved: (id: string) => void; edit?: ListDetail['list'] }) {
  const [f, setF] = useState({ number: '', title: '', parentPartId: '', description: '', listType: 'Standard' });
  const [err, setErr] = useState<string | null>(null);
  useUnsaved(open && !!f.title && !edit);
  const save = async () => {
    const body = { number: edit ? null : f.number.trim(), title: f.title, parentPartId: f.parentPartId || null, description: nul(f.description), listType: f.listType, rowVersion: edit?.rowVersion ?? 0 };
    try { if (edit) { await put(`${S}/parts-lists/${edit.id}`, body); onSaved(edit.id); } else onSaved((await post<{ id: string }>(`${S}/parts-lists`, body)).id); onClose(); } catch (e) { setErr((e as Error).message); }
  };
  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" TransitionProps={{ onEnter: () => { setErr(null); setF(edit ? { number: edit.number, title: edit.title, parentPartId: edit.parentPartId ?? '', description: edit.description ?? '', listType: edit.listType } : { number: '', title: '', parentPartId: '', description: '', listType: 'Standard' }); } }}>
      <FormDialogTitle>{edit ? `Edit ${edit.number}` : 'New parts list'}</FormDialogTitle>
      <DialogContent><Stack gap={2} sx={{ pt: 1 }}>
        {err && <Alert severity="error">{err}</Alert>}
        {!edit && <NumberField value={f.number} onChange={(e) => setF({ ...f, number: e.target.value })} inputProps={{ 'data-testid': 'list-number', maxLength: 40 }} />}
        <TextField label="Title" required value={f.title} onChange={(e) => setF({ ...f, title: e.target.value })} />
        <TextField select label="List type" value={f.listType} onChange={(e) => setF({ ...f, listType: e.target.value })}>{listTypes.map((t) => <MenuItem key={t} value={t}>{words(t)}</MenuItem>)}</TextField>
        <PartPicker label="Parent part" value={f.parentPartId} onChange={(id) => setF({ ...f, parentPartId: id })} />
        <TextField label="Description" multiline minRows={2} value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} />
      </Stack></DialogContent>
      <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" disabled={!f.title.trim() || (!edit && !f.number.trim())} onClick={save}>Save</Button></DialogActions>
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
      <FormDialogTitle>{edit ? `Edit item ${edit.itemNumber}` : 'Add item'}</FormDialogTitle>
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
  const [tab, setTab] = useState('items');
  const [dlg, setDlg] = useState<null | 'edit' | 'item' | 'release' | 'revise' | 'active' | 'copy'>(null);
  const [item, setItem] = useState<ItemRow | null>(null);
  const [copyFrom, setCopyFrom] = useState('');
  const [err, setErr] = useState<string | null>(null);
  const np = useNumberPrompt();
  const lists = useQuery({ queryKey: ['sqf-list-pick'], queryFn: () => get<Paged<ListRow>>(`${S}/parts-lists?pageSize=200`), enabled: dlg === 'copy' });
  const refresh = () => { qc.invalidateQueries({ queryKey: ['sqf-list', id] }); };
  const act = async (fn: () => Promise<unknown>) => { setErr(null); try { await fn(); refresh(); } catch (e) { setErr((e as Error).message); } };
  const l = q.data?.list;
  const editable = !!q.data?.canEdit && l?.status === 'Draft';
  const items = q.data?.items ?? [];
  const move = (i: number, d: number) => { const ids = items.map((x) => x.id); [ids[i], ids[i + d]] = [ids[i + d], ids[i]]; act(() => post(`${S}/parts-lists/${id}/items/reorder`, { ids })); };
  return (
    <SqfPage title={l ? `${l.number} · ${l.title}` : 'Parts list'} crumbs={crumbs('Parts Lists', { label: l?.number ?? '…' })} recent={l ? { label: `Parts list ${l.number}`, itemType: 'PartsList', recordId: l.id } : null}
      tabs={[{ label: 'Items', value: 'items' }, { label: 'Revisions', value: 'revisions' }]} tab={tab} onTab={setTab}
      actions={q.data?.canEdit && l && <RecordActions surface="parts-list" edit={l.status === 'Draft' && { onClick: () => setDlg('edit') }}
        rename={{ kind: 'parts-list', id: l.id, number: l.number, title: l.title, onDone: refresh }}
        del={{ what: `parts list ${l.number ?? ''}`.trim(), url: `${S}/parts-lists/${l.id}`, to: '/sequfloh/parts-lists', extra: 'All revisions and items of this list are removed.' }}>
        {l.status === 'Draft' && <Button variant="contained" onClick={() => setDlg('release')}>Release</Button>}
        {l.status === 'Released' && <Button variant="contained" onClick={() => setDlg('revise')}>New revision</Button>}
        <Button onClick={() => act(async () => { const n = await np.ask(`Duplicate ${l.number}`); if (!n) return; const r = await post<{ id: string }>(`${S}/parts-lists/${l.id}/duplicate`, { number: n }); nav(`/sequfloh/parts-lists/${r.id}`); })}>Duplicate</Button>
        {np.dialog}
        <Button color={l.status === 'Inactive' ? 'primary' : 'error'} onClick={() => setDlg('active')}>{l.status === 'Inactive' ? 'Reactivate' : 'Deactivate'}</Button>
      </RecordActions>}>
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
          <FormDialogTitle>Copy items from another list</FormDialogTitle>
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
  const [renaming, setRenaming] = useState<StepRow | null>(null);
  const np = useNumberPrompt();
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
            <Button size="small" data-testid="edit-number" onClick={() => setRenaming(s)}>Edit number</Button>
            <Button size="small" onClick={() => act(async () => { const n = await np.ask(`Duplicate ${s.number}`); if (n) await post(`${S}/operation-steps/${s.id}/duplicate`, { number: n }); })}>Duplicate</Button>
            <Button size="small" color={s.isActive ? 'error' : 'primary'} onClick={() => act(() => post(`${S}/operation-steps/${s.id}/${s.isActive ? 'deactivate' : 'activate'}`, {}))}>{s.isActive ? 'Deactivate' : 'Activate'}</Button>
            <DeleteButton size="small" what={`step ${s.number}`} url={`${S}/operation-steps/${s.id}`} extra="Copies already placed in work instructions stay." />
          </Stack> : null])} />}
      <StepDialog edit={edit} onClose={() => setEdit(undefined)} onSaved={refresh} />
      {np.dialog}
      {renaming && <EditNumberDialog open kind="operation-step" id={renaming.id} number={renaming.number} title={renaming.title} onClose={() => setRenaming(null)} onDone={() => { setRenaming(null); refresh(); }} />}
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
    if (!edit) body.number = f.number?.trim() ?? '';
    try { if (edit) await put(`${S}/operation-steps/${edit.id}`, body); else await post(`${S}/operation-steps`, body); onSaved(); onClose(); } catch (e) { setErr((e as Error).message); }
  };
  return (
    <Dialog open={edit !== undefined} onClose={onClose} fullWidth maxWidth="md"
      TransitionProps={{ onEnter: () => { setErr(null); setF(Object.fromEntries(stepFields.map(([k]) => [k, edit?.[k] === null || edit?.[k] === undefined ? '' : String(edit[k])]))); } }}>
      <FormDialogTitle>{edit ? `Edit ${edit.number}` : 'New operation step'}</FormDialogTitle>
      <DialogContent>{err && <Alert severity="error" sx={{ mb: 2 }}>{err}</Alert>}<Box sx={{ pt: 1 }}><Grid>
        {!edit && <NumberField value={f.number ?? ''} onChange={(e) => setF({ ...f, number: e.target.value })} inputProps={{ 'data-testid': 'step-number', maxLength: 40 }} />}
        {stepFields.map(([k, label, req]) => k === 'category' && (categories.data?.length ?? 0) > 0
          ? <TextField key={k} select label={label} value={f[k] ?? ''} onChange={(e) => setF({ ...f, [k]: e.target.value })}><MenuItem value="">—</MenuItem>{categories.data!.map((o) => <MenuItem key={o} value={o}>{o}</MenuItem>)}</TextField>
          : <TextField key={k} label={label} required={req} type={['target', 'minimum', 'maximum'].includes(k) ? 'number' : 'text'} multiline={k === 'detailedAction' || k === 'safetyWarning'}
            value={f[k] ?? ''} onChange={(e) => setF({ ...f, [k]: e.target.value })} />)}
      </Grid></Box></DialogContent>
      <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" disabled={(!edit && !f.number?.trim()) || !f.title?.trim() || !f.detailedAction?.trim()} onClick={save}>Save</Button></DialogActions>
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
    <SqfPage title="Revision History" crumbs={crumbs('Revision History')} description="Revisions of parts lists (work instruction revisions are added with the instruction engine).">
      <Filters search={search} onSearch={(s) => { setSearch(s); setPage(1); }} values={f} onChange={(k, v) => { setF({ ...f, [k]: v }); setPage(1); }} onClear={() => { setSearch(''); setF({}); }}
        filters={[{ key: 'type', label: 'Record type', options: [['parts-list', 'Parts list']] }]} />
      <PagedTable q={q} page={page} size={size} onPage={setPage} onSize={setSize} onRow={(r) => nav(r.link)} empty="No revisions recorded yet."
        cols={[{ key: 'type', label: 'Type' }, { key: 'number', label: 'Number', render: (r) => <Mono>{r.number}</Mono> }, { key: 'title', label: 'Title' }, { key: 'revision', label: 'Revision' },
          { key: 'reason', label: 'Reason', render: (r) => r.reason ?? '—' }, { key: 'at', label: 'Date', render: (r) => fmtDateTime(r.at) }]} />
    </SqfPage>
  );
}

interface Activity { kind: string; id: string; number: string; type: string; status: string; createdAt: string; link: string; detail?: string | null }

/** Work orders of every type and inspection requests for this part, newest first. */
function LatestActivity({ partId }: { partId: string }) {
  const q = useQuery({ queryKey: ['sqf-part-activity', partId], queryFn: () => get<{ items: Activity[]; canCreateRepair: boolean }>(`${S}/parts/${partId}/activity`) });
  const items = q.data?.items ?? [];
  return <Paper variant="outlined" sx={{ p: 2, mb: 2 }} data-testid="part-activity">
    <Stack direction="row" alignItems="center" sx={{ mb: 1 }}><Typography variant="subtitle2" sx={{ flex: 1 }}>Latest activity</Typography>
      {q.data?.canCreateRepair && <Button size="small" variant="outlined" component={RouterLink} to={`/sequfloh/tracking/new?type=Repair&partId=${partId}`} data-testid="part-new-repair">New Repair / Rework work order</Button>}</Stack>
    {q.isLoading ? <Loading /> : items.length === 0 ? <Typography variant="body2" color="text.secondary">No work orders or inspection requests for this part yet.</Typography> :
      <Stack gap={0.5}>{items.map((a) => <Box key={a.kind + a.id} component={RouterLink} to={a.link} data-testid="activity-row"
        sx={{ display: 'flex', gap: 2, color: 'inherit', textDecoration: 'none', py: 0.5, '&:hover': { bgcolor: 'action.hover' } }}>
        <Mono>{a.number}</Mono><Typography variant="body2" sx={{ flex: 1 }}>{a.type}{a.detail ? ` · ${a.detail}` : ''}</Typography>
        <SqfStatus status={a.status} /><Typography variant="caption" color="text.secondary">{fmtDateTime(a.createdAt)}</Typography></Box>)}</Stack>}
  </Paper>;
}

/** Add barcode / Change barcode: scan the box label (or type its digits) into the box and save it on this part. The scan goes into the box, not the global scan. */
function BarcodeButton({ partId, upc, onSaved }: { partId: string; upc?: string | null; onSaved: () => void }) {
  const [open, setOpen] = useState(false);
  const [v, setV] = useState('');
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const save = async () => {
    setBusy(true); setErr(null);
    try { await put(`${S}/parts/${partId}/upc`, { upc: v.trim() }); setOpen(false); onSaved(); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return <>
    <Button size="small" sx={{ ml: 1 }} data-testid="part-barcode-button" onClick={() => { setV(upc ?? ''); setErr(null); setOpen(true); }}>{upc ? 'Change barcode' : 'Add barcode'}</Button>
    <Dialog open={open} onClose={() => setOpen(false)} maxWidth="xs" fullWidth>
      <FormDialogTitle>{upc ? 'Change barcode' : 'Add barcode'}</FormDialogTitle>
      <DialogContent>
        <TextField autoFocus fullWidth sx={{ mt: 1 }} label="UPC / Barcode" value={v} onChange={(e) => setV(e.target.value.replace(/\s/g, ''))}
          inputProps={{ inputMode: 'numeric', 'data-testid': 'part-barcode-input' }} onKeyDown={(e) => { if (e.key === 'Enter' && v.trim()) { e.preventDefault(); void save(); } }}
          helperText="Scan the box label now, or type the digits under the barcode." />
        {err && <Alert severity="error" sx={{ mt: 1 }}>{err}</Alert>}
      </DialogContent>
      <DialogActions><Button onClick={() => setOpen(false)}>Cancel</Button><Button variant="contained" disabled={!v.trim() || busy} onClick={save} data-testid="part-barcode-save">Save</Button></DialogActions>
    </Dialog>
  </>;
}
