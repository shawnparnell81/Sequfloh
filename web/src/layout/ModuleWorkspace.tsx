import { useEffect, useState, type ReactNode } from 'react';
import { Link as RouterLink, useLocation } from 'react-router-dom';
import { Box, Breadcrumbs, Collapse, IconButton, Link, List, ListItemButton, ListItemText, Tab, Tabs, Tooltip, Typography, useMediaQuery } from '@mui/material';
import ChevronLeft from '@mui/icons-material/ChevronLeft';
import ChevronRight from '@mui/icons-material/ChevronRight';
import ExpandLess from '@mui/icons-material/ExpandLess';
import ExpandMore from '@mui/icons-material/ExpandMore';
import { useTheme } from '@mui/material/styles';
import { useAuth } from '../auth';
import { modules, type ModuleDef, type NavItem } from '../modules/registry';

export interface Crumb { label: string; to?: string }
export interface WorkspaceTab { label: string; value: string }

/** A page: collapsible left menu, breadcrumbs, a large title with the page's buttons, and an optional tab bar. */
export function ModuleWorkspace({ module, crumbs, title, actions, tabs, tab, onTab, children }: {
  module?: ModuleDef; crumbs: Crumb[]; title: ReactNode; actions?: ReactNode;
  tabs?: WorkspaceTab[]; tab?: string; onTab?: (v: string) => void; children: ReactNode;
}) {
  const mod = module ?? modules[0];
  const titleText = typeof title === 'string' ? title : crumbs.length ? crumbs[crumbs.length - 1].label : mod.label;
  const [stored, setStored] = useState(() => localStorage.getItem('sqf.menu.collapsed') === '1');
  const narrow = useMediaQuery('(max-width:899.95px)');
  const [peek, setPeek] = useState(false);
  const loc = useLocation();
  const tk = useTheme().tk;
  useEffect(() => { document.title = `${titleText} · SEQUFLOH`; }, [titleText]);
  useEffect(() => setPeek(false), [loc.pathname, loc.search]);
  const collapsed = narrow ? !peek : stored;
  const toggle = () => { if (narrow) { setPeek(!peek); return; } localStorage.setItem('sqf.menu.collapsed', stored ? '0' : '1'); setStored(!stored); };
  return (
    <Box sx={{ display: 'flex', minHeight: 'calc(100vh - 56px)', position: 'relative' }}>
      {narrow && <Box sx={{ width: 48, flexShrink: 0 }} />}
      <Box component="aside" className="sqf-no-print" sx={{ width: collapsed ? 48 : 256, flexShrink: 0, borderRight: 1, background: tk.glass.side, backdropFilter: 'blur(14px)', borderColor: tk.glass.edge, transition: 'width 150ms',
        ...(narrow ? { position: 'absolute', top: 0, bottom: 0, left: 0, zIndex: 20, boxShadow: peek ? 6 : 0 } : {}) }}>
        <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: collapsed ? 'center' : 'space-between', px: collapsed ? 0 : 2, py: 1.5 }}>
          {!collapsed && <Typography variant="overline" color="text.secondary" sx={{ display: 'flex', gap: 1, alignItems: 'center' }}>{mod.icon}Menu</Typography>}
          <Tooltip title={collapsed ? 'Expand menu' : 'Collapse menu'}><IconButton size="small" onClick={toggle}>{collapsed ? <ChevronRight /> : <ChevronLeft />}</IconButton></Tooltip>
        </Box>
        {!collapsed && <SideNav items={mod.sidebar} />}
      </Box>
      <Box component="main" sx={{ flex: 1, minWidth: 0 }}>
        <Box sx={{ px: { xs: 2, md: 4 }, pt: 3, color: tk.header.fg }}>
          <Breadcrumbs separator="/" className="sqf-no-print" sx={{ mb: 1.25, fontSize: 11.5, letterSpacing: '0.04em', color: tk.header.muted, '& .MuiBreadcrumbs-separator': { mx: 0.75, color: 'divider' } }}>
            <Link component={RouterLink} to="/sequfloh" underline="hover" sx={{ color: tk.header.muted }}>Home</Link>
            {crumbs.filter((c, i) => !(i === 0 && c.label === mod.label)).map((c, i) => c.to ? <Link key={i} component={RouterLink} to={c.to} underline="hover" sx={{ color: tk.header.muted }}>{c.label}</Link> : <Typography key={i} fontSize={11.5} fontWeight={600} sx={{ color: tk.header.fg }}>{c.label}</Typography>)}
          </Breadcrumbs>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, pb: tabs ? 0.5 : 2.5, flexWrap: 'wrap' }}>
            <Box aria-hidden sx={{ width: tk.icon.size, height: tk.icon.size, flexShrink: 0, display: { xs: 'none', sm: 'grid' }, placeItems: 'center', background: tk.gradient, color: '#FFFFFF', borderRadius: `${tk.icon.radius}px`, '& svg': { fontSize: tk.icon.size * 0.56 }, boxShadow: `0 8px 20px ${tk.glow}` }}>{mod.icon}</Box>
            <Typography variant="h1" sx={{ flex: '1 1 320px', minWidth: 0, color: tk.header.fg }}>{title}</Typography>
            {actions && <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, flexWrap: 'wrap', justifyContent: 'flex-end', ml: 'auto' }}>{actions}</Box>}
          </Box>
          {tabs && (
            <Tabs value={tab} onChange={(_, v) => onTab?.(v)} variant="scrollable" textColor="inherit" sx={{ mt: 0.5, minHeight: 40, '& .MuiTab-root': { minHeight: 40, color: tk.header.muted, opacity: 1 }, '& .MuiTab-root.Mui-selected': { color: 'primary.main' } }}>
              {tabs.map((t) => <Tab key={t.value} value={t.value} label={t.label} />)}
            </Tabs>
          )}
        </Box>
        <Box sx={{ p: { xs: 1.5, sm: 2.5, md: 3.5 }, maxWidth: 1600 }}>{children}</Box>
      </Box>
    </Box>
  );
}

function SideNav({ items, depth = 0 }: { items: NavItem[]; depth?: number }) {
  const { can } = useAuth();
  const loc = useLocation();
  const current = loc.pathname + loc.search;
  const visible = items.filter((i) => (!i.permission || can(i.permission)) && (!i.children || i.children.some((c) => !c.permission || can(c.permission))));
  return (
    <List dense disablePadding>
      {visible.map((i) => <SideItem key={i.label} item={i} depth={depth} current={current} />)}
    </List>
  );
}

function SideItem({ item, depth, current }: { item: NavItem; depth: number; current: string }) {
  const loc = useLocation();
  const inside = !!item.children?.some((c) => current === c.path || loc.pathname === c.path.split('?')[0]);
  const [open, setOpen] = useState(inside);
  const tk = useTheme().tk;
  const selected = current === item.path;
  if (item.children) {
    return (
      <>
        <ListItemButton onClick={() => setOpen(!open)} sx={{ pl: 2 + depth * 2 }}>
          <ListItemText primary={item.label} slotProps={{ primary: { fontWeight: 700, fontSize: 10.5, letterSpacing: '0.12em', textTransform: 'uppercase', color: 'text.secondary' } }} />
          {open ? <ExpandLess fontSize="small" /> : <ExpandMore fontSize="small" />}
        </ListItemButton>
        <Collapse in={open}><SideNav items={item.children} depth={depth + 1} /></Collapse>
      </>
    );
  }
  return (
    <ListItemButton component={RouterLink} to={item.path} selected={selected}
      sx={{ ml: 1, mr: 1, pl: 1 + depth * 2, borderRadius: '10px', '&.Mui-selected': { background: tk.gradient, color: '#FFFFFF', boxShadow: `0 6px 14px ${tk.glow}`, '&:hover': { background: tk.gradient, filter: 'brightness(1.08)' } } }}>
      <ListItemText primary={item.label} slotProps={{ primary: { fontSize: 13, fontWeight: selected ? 600 : 500 } }} />
    </ListItemButton>
  );
}
