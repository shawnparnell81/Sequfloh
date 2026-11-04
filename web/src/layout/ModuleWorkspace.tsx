import { useEffect, useState, type ReactNode } from 'react';
import { Link as RouterLink, useLocation } from 'react-router-dom';
import { Badge, Box, Chip, IconButton, List, ListItemButton, ListItemIcon, ListItemText, Tooltip, Typography, useMediaQuery } from '@mui/material';
import ChevronLeft from '@mui/icons-material/ChevronLeft';
import ChevronRight from '@mui/icons-material/ChevronRight';
import { useTheme } from '@mui/material/styles';
import { useAuth } from '../auth';
import { landingOf, modules, sectionOf, type ModuleDef, type NavItem } from '../modules/registry';
import { useMessageUnreadCount } from '../components/MessageBadge';
import { useTabTitle } from '../tabs/Workspace';

import { PageHeader, type Crumb, type WorkspaceTab } from './PageHeader';
import { SPACE } from './spacing';
export type { Crumb, WorkspaceTab };

/**
 * A page: collapsible left menu of sections, the section's pages as a menu across the top, Back and Home for this tab,
 * breadcrumbs, a large title with the page's buttons, and an optional tab bar for the page's own parts.
 */
export function ModuleWorkspace({ module, crumbs, title, subtitle, meta, actions, summary, tabs, tab, onTab, children }: {
  module?: ModuleDef; crumbs: Crumb[]; title: ReactNode; subtitle?: ReactNode; meta?: ReactNode; actions?: ReactNode; summary?: ReactNode;
  tabs?: WorkspaceTab[]; tab?: string; onTab?: (v: string) => void; children: ReactNode;
}) {
  const mod = module ?? modules[0];
  const titleText = typeof title === 'string' ? title : crumbs.length ? crumbs[crumbs.length - 1].label : mod.label;
  const [stored, setStored] = useState(() => localStorage.getItem('sqf.menu.collapsed') === '1');
  const narrow = useMediaQuery('(max-width:899.95px)');
  const [peek, setPeek] = useState(false);
  const loc = useLocation();
  const tk = useTheme().tk;
  useTabTitle(titleText);
  const section = sectionOf(loc.pathname);
  useEffect(() => { document.title = `${titleText} · SEQUFLOH`; }, [titleText]);
  useEffect(() => setPeek(false), [loc.pathname, loc.search]);
  const collapsed = narrow ? !peek : stored;
  const toggle = () => { if (narrow) { setPeek(!peek); return; } localStorage.setItem('sqf.menu.collapsed', stored ? '0' : '1'); setStored(!stored); };
  return (
    <Box sx={{ display: 'flex', minHeight: 'calc(100vh - 64px)', position: 'relative', gap: { md: 1.5 }, p: { md: 1.5 }, alignItems: 'stretch' }}>
      {narrow && <Box sx={{ width: 56, flexShrink: 0 }} />}
      <Box component="aside" className="sqf-no-print" sx={{ width: collapsed ? 64 : 248, flexShrink: 0, background: tk.glass.side, border: '1px solid', borderColor: tk.glass.edge, borderRadius: '16px',
        boxShadow: '0 1px 2px rgba(30,27,75,0.04), 0 8px 24px rgba(30,27,75,0.06)', overflow: 'auto', transition: 'width 150ms',
        ...(narrow ? { position: 'absolute', top: 8, bottom: 8, left: 8, zIndex: 20, boxShadow: peek ? 6 : 0 } : {}) }}>
        <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: collapsed ? 'center' : 'space-between', px: collapsed ? 0 : 2, py: 1.5 }}>
          {!collapsed && <Typography variant="overline" color="text.secondary" sx={{ display: 'flex', gap: 1, alignItems: 'center' }}>{mod.icon}Menu</Typography>}
          <Tooltip title={collapsed ? 'Expand menu' : 'Collapse menu'}><IconButton size="small" onClick={toggle}>{collapsed ? <ChevronRight /> : <ChevronLeft />}</IconButton></Tooltip>
        </Box>
        <SideNav items={mod.sidebar} compact={collapsed} />
      </Box>
      <Box component="main" data-fullscreen-root sx={{ flex: 1, minWidth: 0 }}>
        {section && <SectionMenu section={section} />}
        <PageHeader crumbs={crumbs} homeLabel={mod.label} title={title} icon={mod.icon} subtitle={subtitle} meta={meta} actions={actions} summary={summary} tabs={tabs} tab={tab} onTab={onTab} />
        <Box data-page-body sx={{ px: SPACE.pageX, pt: SPACE.block, pb: 4, maxWidth: 1600 }}>{children}</Box>
      </Box>
    </Box>
  );
}

/** The sections down the left. Choosing one opens its first page; the section of the page being viewed is highlighted. */
function SideNav({ items, compact }: { items: NavItem[]; compact: boolean }) {
  const { can } = useAuth();
  const unread = useMessageUnreadCount();
  const loc = useLocation();
  const tk = useTheme().tk;
  const current = sectionOf(loc.pathname);
  const visible = items.filter((i) => (!i.permission || can(i.permission)) && (!i.children || i.children.some((c) => !c.permission || can(c.permission))));
  return (
    <List dense disablePadding aria-label="Sections" component="nav">
      {visible.map((i) => {
        const selected = current?.label === i.label;
        const messages = i.label === 'Messages';
        const label = messages && unread > 0 ? `${i.label}, ${unread} unread` : i.label;
        return (
          <Tooltip key={i.label} title={compact ? label : ''} placement="right"><ListItemButton component={RouterLink} to={landingOf(i, can)} selected={selected} aria-current={selected ? 'page' : undefined} aria-label={compact ? label : undefined} data-testid={`section-${i.label}`} {...(messages ? { 'data-unread-count': unread } : {})}
            sx={{ mx: compact ? 0.75 : 1, my: 0.35, px: compact ? 1 : 1.25, justifyContent: compact ? 'center' : undefined, borderRadius: 999, color: 'text.secondary',
              '& .MuiListItemIcon-root': { color: 'text.secondary' },
              '&:hover': { bgcolor: 'action.hover', color: 'text.primary' },
              '&.Mui-selected': { bgcolor: 'primary.main', color: '#FFFFFF', boxShadow: `0 6px 14px ${tk.glow}`, '& .MuiListItemIcon-root': { color: '#FFFFFF' }, '&:hover': { bgcolor: 'primary.main', filter: 'brightness(1.06)' } } }}>
            {i.icon && <ListItemIcon sx={{ minWidth: compact ? 0 : 32 }}>{messages && compact ? <Badge color="error" badgeContent={unread} invisible={unread === 0} max={99}>{i.icon}</Badge> : i.icon}</ListItemIcon>}
            {!compact && <ListItemText primary={i.label} slotProps={{ primary: { fontSize: 13.5, fontWeight: selected ? 700 : 500 } }} />}
            {!compact && messages && unread > 0 && <Chip size="small" color="error" label={unread > 99 ? '99+' : unread} data-testid="messages-unread" sx={{ height: 20, ml: 0.5, '& .MuiChip-label': { px: 0.75, fontSize: 11, fontWeight: 700 } }} />}
          </ListItemButton></Tooltip>
        );
      })}
    </List>
  );
}

/** The pages of the current section as a menu across the top of the page. */
function SectionMenu({ section }: { section: NavItem }) {
  const { can } = useAuth();
  const loc = useLocation();
  const tk = useTheme().tk;
  const pages = (section.children ?? []).filter((c) => !c.permission || can(c.permission));
  if (pages.length < 2) return null;
  const here = loc.pathname + loc.search;
  // a page with a query (Parts › Repair Parts) matches only with that query; others match on the path
  const exact = pages.find((c) => c.path.includes('?') && c.path === here);
  const active = exact ?? pages.find((c) => !c.path.includes('?') && c.path === loc.pathname);
  return (
    <Box component="nav" aria-label={`${section.label} pages`} data-testid="section-menu" className="sqf-no-print"
      sx={{ display: 'flex', alignItems: 'center', gap: 0.5, px: { xs: 1.5, md: 3 }, py: 1, overflowX: 'auto', scrollbarWidth: 'thin' }}>
      <Typography sx={{ fontSize: 11, fontWeight: 800, letterSpacing: '0.1em', textTransform: 'uppercase', color: 'text.secondary', mr: 1, whiteSpace: 'nowrap', display: { xs: 'none', md: 'block' } }}>{section.label}</Typography>
      {pages.map((c) => {
        const on = c === active;
        return (
          <Box key={c.path} component={RouterLink} to={c.path} aria-current={on ? 'page' : undefined} data-testid="section-page"
            sx={{ flexShrink: 0, px: 1.5, py: 0.75, borderRadius: 999, fontSize: 13, fontWeight: on ? 700 : 500, textDecoration: 'none', whiteSpace: 'nowrap',
              color: on ? '#FFFFFF' : 'text.secondary', bgcolor: on ? 'primary.main' : 'transparent', boxShadow: on ? `0 4px 12px ${tk.glow}` : 'none',
              '&:hover': { bgcolor: on ? 'primary.main' : tk.glass.field, color: on ? '#FFFFFF' : 'text.primary' }, '&:focus-visible': { outline: '2px solid', outlineColor: 'primary.main', outlineOffset: 1 } }}>
            {c.label}
          </Box>
        );
      })}
    </Box>
  );
}
