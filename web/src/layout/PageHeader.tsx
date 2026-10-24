import type { ReactNode } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Box, Breadcrumbs, Link, Paper, Stack, Tab, Tabs, Typography } from '@mui/material';
import { useTheme } from '@mui/material/styles';
import { PageNav } from '../tabs/Workspace';
import { SPACE } from './spacing';

export interface Crumb { label: string; to?: string }
export interface WorkspaceTab { label: string; value: string }

/**
 * The one page header used by every page: breadcrumb, then the title with its subtitle and meta chips on the left and the action bar on the
 * right (primary, secondary, then More for rare or destructive actions), then the page's own tabs. It wraps to one column on narrow screens.
 */
export function PageHeader({ crumbs, homeLabel, title, icon, subtitle, meta, actions, tabs, tab, onTab }: {
  crumbs: Crumb[]; homeLabel?: string; title: ReactNode; icon?: ReactNode; subtitle?: ReactNode; meta?: ReactNode; actions?: ReactNode;
  tabs?: WorkspaceTab[]; tab?: string; onTab?: (v: string) => void;
}) {
  const tk = useTheme().tk;
  return (
    <Box data-page-header sx={{ px: SPACE.pageX, pt: 3, pb: tabs ? 0 : SPACE.header - 1, color: tk.header.fg }}>
      <Box sx={{ display: 'flex', alignItems: 'center', mb: SPACE.crumbs }}>
        <PageNav />
        <Breadcrumbs separator="/" className="sqf-no-print" sx={{ fontSize: 11.5, letterSpacing: '0.04em', color: tk.header.muted, '& .MuiBreadcrumbs-separator': { mx: 0.75, color: 'divider' } }}>
          <Link component={RouterLink} to="/sequfloh" underline="hover" sx={{ color: tk.header.muted }}>Home</Link>
          {crumbs.filter((c, i) => !(i === 0 && c.label === homeLabel)).map((c, i) => c.to
            ? <Link key={i} component={RouterLink} to={c.to} underline="hover" sx={{ color: tk.header.muted }}>{c.label}</Link>
            : <Typography key={i} fontSize={11.5} fontWeight={600} sx={{ color: tk.header.fg }}>{c.label}</Typography>)}
        </Breadcrumbs>
      </Box>
      <Box sx={{ display: 'flex', alignItems: 'flex-start', gap: 2, flexWrap: 'wrap' }}>
        {icon && <Box aria-hidden sx={{ width: tk.icon.size, height: tk.icon.size, flexShrink: 0, display: { xs: 'none', sm: 'grid' }, placeItems: 'center', background: tk.gradient, color: '#FFFFFF', borderRadius: `${tk.icon.radius}px`, '& svg': { fontSize: tk.icon.size * 0.56 }, boxShadow: `0 8px 20px ${tk.glow}` }}>{icon}</Box>}
        <Box sx={{ flex: '1 1 320px', minWidth: 0, alignSelf: 'center' }}>
          <Typography variant="h1" sx={{ color: tk.header.fg }}>{title}</Typography>
        </Box>
        {actions && <Box data-page-actions className="sqf-no-print" sx={{ display: 'flex', alignItems: 'center', gap: SPACE.gap, flexWrap: 'wrap', justifyContent: 'flex-end', ml: 'auto', alignSelf: 'center' }}>{actions}</Box>}
      </Box>
      {(subtitle || meta) && <Stack direction={{ xs: 'column', md: 'row' }} gap={1} justifyContent="space-between" alignItems={{ md: 'center' }} sx={{ mt: 1.5, displayPrint: 'none' }}>
        {subtitle ? <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 820 }}>{subtitle}</Typography> : <span />}
        {meta && <Stack direction="row" gap={0.75} flexWrap="wrap" alignItems="center" aria-label="Context">{meta}</Stack>}
      </Stack>}
      {tabs && (
        <Tabs value={tab} onChange={(_, v) => onTab?.(v)} variant="scrollable" textColor="inherit" sx={{ mt: 1, minHeight: 40, '& .MuiTab-root': { minHeight: 40, color: tk.header.muted, opacity: 1 }, '& .MuiTab-root.Mui-selected': { color: 'primary.main' } }}>
          {tabs.map((t) => <Tab key={t.value} value={t.value} label={t.label} />)}
        </Tabs>
      )}
    </Box>
  );
}

/**
 * The header of a record shown inside a page (for example a drawing folder inside Parts Drawings): its name as the title, linked items under it,
 * and its action bar on the right. Same spacing as the page header.
 */
export function RecordHeader({ title, meta, actions, testid }: { title: ReactNode; meta?: ReactNode; actions?: ReactNode; testid?: string }) {
  return (
    <Paper variant="outlined" data-record-header data-testid={testid} sx={{ p: SPACE.card, mb: SPACE.block, display: 'flex', gap: 2, alignItems: 'center', flexWrap: 'wrap' }}>
      <Box sx={{ flex: '1 1 280px', minWidth: 0 }}>
        <Typography variant="h6" component="h2" sx={{ fontWeight: 600, overflowWrap: 'anywhere' }}>{title}</Typography>
        {meta && <Stack direction="row" gap={0.75} flexWrap="wrap" alignItems="center" sx={{ mt: 0.75 }}>{meta}</Stack>}
      </Box>
      {actions && <Box data-page-actions sx={{ display: 'flex', gap: SPACE.gap, flexWrap: 'wrap', justifyContent: 'flex-end', ml: 'auto' }}>{actions}</Box>}
    </Paper>
  );
}

/** The row above a list: search box, filters, the Search button, then the checkboxes right after it (no gap). Wraps on narrow screens. */
export function ListToolbar({ onSubmit, children, end }: { onSubmit: () => void; children: ReactNode; end?: ReactNode }) {
  return (
    <Box component="form" data-list-toolbar onSubmit={(e) => { e.preventDefault(); onSubmit(); }}
      sx={{ display: 'flex', gap: SPACE.gap, rowGap: 1.5, flexWrap: 'wrap', alignItems: 'center', mb: SPACE.block }}>
      {children}
      {end && <Box data-toolbar-end sx={{ display: 'flex', gap: SPACE.gap, alignItems: 'center', flexWrap: 'wrap' }}>{end}</Box>}
    </Box>
  );
}
