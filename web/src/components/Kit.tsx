import { useState, type ReactNode } from 'react';
import { Box, Paper, Stack, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { get } from '../api';
import { Link as RouterLink } from 'react-router-dom';
import { mono } from '../theme';

export interface Site { id: string; code: string; name: string; isArchived: boolean; departments: { id: string; code: string; name: string }[] }
export const useSites = () => useQuery({ queryKey: ['sites'], queryFn: () => get<Site[]>('/api/organization/sites') });
export const useUsers = () => useQuery({ queryKey: ['userlookup'], queryFn: () => get<{ id: string; displayName: string }[]>('/api/users/lookup') });

/** Label/value list used in record side panels: a ruled two-column sheet with labels on the left. */
export function Facts({ items, children }: { items: [string, ReactNode][]; children?: ReactNode }) {
  return (
    <Paper sx={{ alignSelf: 'start', overflow: 'hidden' }}>
      <PanelHead title="Details" />
      <Box component="dl" sx={{ m: 0, display: 'grid', gridTemplateColumns: { xs: 'minmax(0, 1fr)', sm: 'minmax(96px, 38%) minmax(0, 1fr)' } }}>
        {items.map(([k, v]) => (
          <Box key={k} sx={{ display: 'contents', '& > *': { px: 2, py: 0.9, borderTop: 1, borderColor: 'divider' }, '& > dd': { borderTop: { xs: 0, sm: 1 }, borderColor: 'divider' } }}>
            <Typography component="dt" variant="caption" color="text.secondary" sx={{ fontWeight: 600, bgcolor: 'background.band' }}>{k}</Typography>
            <Typography component="dd" variant="body2" sx={{ m: 0, whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>{v ?? '—'}</Typography>
          </Box>
        ))}
      </Box>
      {children && <Box sx={{ px: 2, pb: 2 }}>{children}</Box>}
    </Paper>
  );
}

/** Panel header: a quiet title on the frosted panel with a hairline underneath, optional action on the right. */
export function PanelHead({ title, action }: { title: ReactNode; action?: ReactNode }) {
  return (
    <Stack direction="row" alignItems="center" gap={1} sx={{ px: 2, minHeight: 44, borderBottom: 1, borderColor: 'divider' }}>
      <Typography component="h3" sx={{ flex: 1, color: 'text.primary', fontWeight: 700, fontSize: 14.5, letterSpacing: '-0.005em' }}>{title}</Typography>
      {action}
    </Stack>
  );
}

/**
 * Key figure readout: an uppercase label over a large monospaced number on a flat band with a coloured top rule.
 * Used for dashboard counts in place of floating cards. Opens the list behind it when `to` or `onClick` is given.
 */
export function Readout({ label, value, hint, color, to, onClick, title, children }: { label: string; value: ReactNode; hint?: ReactNode; color?: string; to?: string; onClick?: () => void; title?: string; children?: ReactNode }) {
  const link = to ? { component: RouterLink, to } : {};
  return (
    <Box {...link} onClick={onClick} title={title} aria-label={label}
      sx={{ display: 'block', color: 'inherit', textDecoration: 'none', bgcolor: 'background.paper', borderTop: '3px solid', borderTopColor: color ?? 'text.secondary', borderBottom: 1, borderBottomColor: 'divider', px: 1.75, pt: 1.25, pb: 1.25, minWidth: 0,
        cursor: to || onClick ? 'pointer' : 'default', '&:hover': to || onClick ? { bgcolor: 'action.hover' } : undefined }}>
      <Typography component="div" sx={{ fontSize: 10.5, fontWeight: 700, letterSpacing: '0.1em', textTransform: 'uppercase', color: 'text.secondary', lineHeight: 1.4 }}>{label}</Typography>
      <Typography component="div" sx={{ fontFamily: mono, fontSize: 26, lineHeight: 1.25, mt: 0.25 }}>{value}</Typography>
      {hint && <Typography variant="caption" color="text.secondary" component="div">{hint}</Typography>}
      {children}
    </Box>
  );
}

export function Section({ title, children, action }: { title: string; children: ReactNode; action?: ReactNode }) {
  return (
    <Paper sx={{ overflow: 'hidden' }}>
      <PanelHead title={title} action={action} />
      <Box sx={{ p: 2 }}>{children}</Box>
    </Paper>
  );
}

export function Prose({ label, text }: { label: string; text?: string | null }) {
  if (!text) return null;
  return <Box sx={{ mb: 1.5 }}><Typography variant="caption" color="text.secondary">{label}</Typography><Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>{text}</Typography></Box>;
}

/** Runs a mutation and keeps its error message for display. */
export function useRunner(after: () => void) {
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const run = async (fn: () => Promise<unknown>) => {
    setError(null); setBusy(true);
    try { await fn(); after(); } catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  };
  return { error, busy, run, setError };
}

