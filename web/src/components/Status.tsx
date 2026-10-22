import { Box, Chip } from '@mui/material';
import { alpha, useTheme } from '@mui/material/styles';

const colors: Record<string, 'default' | 'success' | 'warning' | 'error' | 'info' | 'primary' | 'secondary'> = {
  Draft: 'default', InReview: 'warning', Approved: 'info', Effective: 'success', Active: 'success', Superseded: 'default', Obsolete: 'error',
  Open: 'warning', Contained: 'info', DispositionPending: 'warning', DispositionApproved: 'info', Closed: 'success', Investigation: 'info', Implementation: 'info', EffectivenessCheck: 'warning',
  Planned: 'default', Reporting: 'warning', ActionPlanned: 'info', Submitted: 'warning', Assigned: 'warning', Quarantined: 'error', OutOfService: 'error', Retired: 'default',
  Pass: 'success', PassAfterAdjustment: 'success', OutOfTolerance: 'error', Fail: 'error', Minor: 'default', Major: 'warning', Critical: 'error', InProgress: 'info', Completed: 'primary', Verified: 'success', Cancelled: 'default', Pending: 'warning', Rejected: 'error', Succeeded: 'success', Failed: 'error',
  Prospective: 'default', PendingApproval: 'warning', Conditional: 'warning', Suspended: 'error', Disqualified: 'error', Inactive: 'default',
};
const labels: Record<string, string> = { InReview: 'In review', InProgress: 'In progress', DispositionPending: 'Disposition pending', DispositionApproved: 'Disposition approved',
  EffectivenessCheck: 'Effectiveness check', ActionPlanned: 'Action planned', OutOfService: 'Out of service', PassAfterAdjustment: 'Pass after adjustment', OutOfTolerance: 'Out of tolerance', PendingApproval: 'Pending approval' };

/** Soft tinted status pill with a dot in the status colour. */
export const StatusChip = ({ status }: { status: string }) => {
  const t = useTheme();
  const c = colors[status] ?? 'default';
  const dark = t.palette.mode === 'dark';
  const tone = c === 'default' ? (dark ? '#A8B3C2' : '#6B7790') : t.palette[c].main;
  // text one step darker (lighter in dark mode) than the dot so it stays readable on the tint
  const ink = c === 'default' ? (dark ? '#C2CBD8' : '#556179') : dark ? t.palette[c].light : t.palette[c].dark;
  return <Chip size="small" label={labels[status] ?? status} icon={<Box component="span" sx={{ width: 7, height: 7, borderRadius: '50%', bgcolor: tone, ml: '8px !important' }} />}
    sx={{ bgcolor: alpha(tone, 0.12), color: ink, border: `1px solid ${alpha(tone, 0.2)}`, fontWeight: 700 }} />;
};

/** The date format chosen in settings ("auto" follows the browser). Set once preferences load. */
let dateFormat = (() => { try { return localStorage.getItem('sqf.dateFormat') ?? 'auto'; } catch { return 'auto'; } })();
export const setDateFormat = (f: string) => { dateFormat = f || 'auto'; try { localStorage.setItem('sqf.dateFormat', dateFormat); } catch { /* private mode */ } };
const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const two = (n: number) => String(n).padStart(2, '0');
const dayText = (x: Date) => {
  const y = x.getFullYear(), m = x.getMonth(), d = x.getDate();
  switch (dateFormat) {
    case 'yyyy-MM-dd': return `${y}-${two(m + 1)}-${two(d)}`;
    case 'MM/dd/yyyy': return `${two(m + 1)}/${two(d)}/${y}`;
    case 'dd/MM/yyyy': return `${two(d)}/${two(m + 1)}/${y}`;
    case 'dd-MMM-yyyy': return `${two(d)}-${months[m]}-${y}`;
    case 'dd.MM.yyyy': return `${two(d)}.${two(m + 1)}.${y}`;
    default: return x.toLocaleDateString();
  }
};
export const fmtDate = (d?: string | null) => (d ? dayText(new Date(d.length === 10 ? d + 'T00:00:00' : d)) : '—');
export const fmtDateTime = (d?: string | null) => {
  if (!d) return '—';
  const x = new Date(d);
  return dateFormat === 'auto' ? x.toLocaleString() : `${dayText(x)} ${x.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`;
};
export const masterStatus = ['Active', 'Inactive', 'Obsolete'];
export const partTypes: [number, string][] = [[0, 'Component'], [1, 'Assembly'], [2, 'Material'], [3, 'Subassembly'], [4, 'Consumable'], [5, 'Packaging'], [9, 'Other']];
/** Local calendar date (YYYY-MM-DD) offset by whole days; the server re-checks against the site's time zone. */
// Business dates follow the organization's time zone (as the server does), not the browser's, so a default
// "today" never lands on tomorrow for someone working in a different zone than the plant.
let businessZone: string | undefined;
export const setBusinessZone = (zone: string | null | undefined) => {
  if (!zone) return;
  try { new Intl.DateTimeFormat('en-CA', { timeZone: zone }); businessZone = zone; } catch { /* unknown zone: keep the browser's */ }
};
export const localDate = (addDays = 0) => {
  const d = new Date(Date.now() + addDays * 86_400_000);
  return new Intl.DateTimeFormat('en-CA', { timeZone: businessZone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(d);
};
