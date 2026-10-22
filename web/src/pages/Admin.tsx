import { useMemo, useState } from 'react';
import { Alert, Box, Button, Checkbox, Chip, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, IconButton, MenuItem, Paper, Stack, Switch, Table, TableBody, TableCell,
  TableContainer, TableHead, TablePagination, TableRow, TextField, Tooltip, Typography } from '@mui/material';
import CloseIcon from '@mui/icons-material/Close';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post, put } from '../api';
import { useAuth } from '../auth';
import { ModuleWorkspace } from '../layout/ModuleWorkspace';
import { useSites } from '../components/Kit';
import { fmtDateTime } from '../components/Status';

interface RoleRow { id: string; name: string; description: string | null; isSystem: boolean; permissions: string[]; users: number }
interface UserRow { id: string; email: string; displayName: string; jobTitle: string | null; isActive: boolean; defaultSiteId: string | null; lastSignInAt: string | null; mustChangePassword: boolean;
  roles: { id: string; roleId: string; role: string; siteId: string | null; departmentId: string | null }[] }
interface PermInfo { code: string; group: string; label: string }

const crumbsFor = (label: string) => [{ label: 'Administration' }, { label }];

function Denied({ what }: { what: string }) {
  return <Alert severity="warning">You do not have permission to manage {what}. Ask an administrator.</Alert>;
}

function ErrorLine({ error }: { error: string | null }) {
  return error ? <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert> : null;
}

// ------------------------------------------------------------------ people
export function UsersPage() {
  const { can, me } = useAuth();
  const qc = useQueryClient();
  const users = useQuery({ queryKey: ['admin-users'], queryFn: () => get<UserRow[]>('/api/admin/users'), enabled: can('admin.users') });
  const roles = useQuery({ queryKey: ['admin-roles-lite'], queryFn: () => get<RoleRow[]>('/api/admin/roles').catch(() => [] as RoleRow[]), enabled: can('admin.users') });
  const sites = useSites();
  const [adding, setAdding] = useState(false);
  const [editing, setEditing] = useState<UserRow | null>(null);
  const [showInactive, setShowInactive] = useState(false);
  const refresh = () => { qc.invalidateQueries({ queryKey: ['admin-users'] }); qc.invalidateQueries({ queryKey: ['userlookup'] }); };
  const siteCode = (id: string | null) => (id ? sites.data?.find((s) => s.id === id)?.code ?? '—' : 'All sites');
  const rows = (users.data ?? []).filter((u) => showInactive || u.isActive);
  return (
    <ModuleWorkspace crumbs={crumbsFor('People')} title="People"
      actions={can('admin.users') && <Button variant="contained" onClick={() => setAdding(true)}>Add person</Button>}>
      {!can('admin.users') ? <Denied what="people" /> : <>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2, maxWidth: 820 }}>Everyone who signs in. Roles decide what each person can do; a role can be given for all sites or for one site only.</Typography>
        <FormControlLabel control={<Switch checked={showInactive} onChange={(e) => setShowInactive(e.target.checked)} />} label="Show inactive people" sx={{ mb: 1 }} />
        <TableContainer component={Paper}>
          <Table>
            <TableHead><TableRow><TableCell>Name</TableCell><TableCell>Email</TableCell><TableCell>Roles</TableCell><TableCell>Last sign-in</TableCell><TableCell>Status</TableCell><TableCell /></TableRow></TableHead>
            <TableBody>
              {rows.map((u) => (
                <TableRow key={u.id} hover>
                  <TableCell><Typography fontWeight={600} fontSize={14}>{u.displayName}</Typography>{u.jobTitle && <Typography variant="caption" color="text.secondary">{u.jobTitle}</Typography>}</TableCell>
                  <TableCell>{u.email}</TableCell>
                  <TableCell><Stack direction="row" gap={0.5} flexWrap="wrap">{u.roles.map((r) => <Chip key={r.id} size="small" variant="outlined" label={`${r.role} · ${siteCode(r.siteId)}`} />)}</Stack></TableCell>
                  <TableCell>{u.lastSignInAt ? fmtDateTime(u.lastSignInAt) : 'Never'}</TableCell>
                  <TableCell>{u.isActive ? <Chip size="small" color="success" variant="outlined" label="Active" /> : <Chip size="small" label="Inactive" />}{u.mustChangePassword && <Chip size="small" color="warning" variant="outlined" label="Temporary password" sx={{ ml: 0.5 }} />}</TableCell>
                  <TableCell align="right"><Button size="small" onClick={() => setEditing(u)}>{u.id === me?.id ? 'View' : 'Edit'}</Button></TableCell>
                </TableRow>))}
            </TableBody>
          </Table>
        </TableContainer>
      </>}
      {adding && <AddUserDialog roles={roles.data ?? []} onClose={() => setAdding(false)} onSaved={() => { setAdding(false); refresh(); }} />}
      {editing && <EditUserDialog user={(users.data ?? []).find((u) => u.id === editing.id) ?? editing} roles={roles.data ?? []} self={editing.id === me?.id} onClose={() => setEditing(null)} onSaved={refresh} />}
    </ModuleWorkspace>
  );
}

function AddUserDialog({ roles, onClose, onSaved }: { roles: RoleRow[]; onClose: () => void; onSaved: () => void }) {
  const sites = useSites();
  const [f, setF] = useState({ displayName: '', email: '', jobTitle: '', password: '', defaultSiteId: '', roleId: '' });
  const [error, setError] = useState<string | null>(null);
  const save = async () => {
    setError(null);
    try {
      await post('/api/admin/users', { displayName: f.displayName, email: f.email, jobTitle: f.jobTitle || null, password: f.password, mustChangePassword: true,
        defaultSiteId: f.defaultSiteId || null, roleIds: f.roleId ? [f.roleId] : [] });
      onSaved();
    } catch (e) { setError((e as Error).message); }
  };
  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle>Add person</DialogTitle>
      <DialogContent>
        <ErrorLine error={error} />
        <Stack gap={2} sx={{ mt: 1 }}>
          <TextField label="Name" value={f.displayName} onChange={(e) => setF({ ...f, displayName: e.target.value })} required autoFocus />
          <TextField label="Email" type="email" value={f.email} onChange={(e) => setF({ ...f, email: e.target.value })} required />
          <TextField label="Job title" value={f.jobTitle} onChange={(e) => setF({ ...f, jobTitle: e.target.value })} />
          <TextField label="Temporary password (at least 10 characters)" value={f.password} onChange={(e) => setF({ ...f, password: e.target.value })} required helperText="They choose their own password the first time they sign in. Give it to them in person, not by email." />
          <TextField select label="Site" value={f.defaultSiteId} onChange={(e) => setF({ ...f, defaultSiteId: e.target.value })} helperText="The first role is given for this site.">
            {(sites.data ?? []).map((s) => <MenuItem key={s.id} value={s.id}>{s.code} · {s.name}</MenuItem>)}
          </TextField>
          <TextField select label="First role" value={f.roleId} onChange={(e) => setF({ ...f, roleId: e.target.value })}>
            <MenuItem value="">No role yet</MenuItem>
            {roles.map((r) => <MenuItem key={r.id} value={r.id}>{r.name}</MenuItem>)}
          </TextField>
        </Stack>
      </DialogContent>
      <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" onClick={save}>Add person</Button></DialogActions>
    </Dialog>
  );
}

function EditUserDialog({ user, roles, self, onClose, onSaved }: { user: UserRow; roles: RoleRow[]; self: boolean; onClose: () => void; onSaved: () => void }) {
  const sites = useSites();
  const [name, setName] = useState(user.displayName);
  const [title, setTitle] = useState(user.jobTitle ?? '');
  const [active, setActive] = useState(user.isActive);
  const [temp, setTemp] = useState('');
  const [grant, setGrant] = useState({ roleId: '', siteId: '' });
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const run = async (fn: () => Promise<unknown>, ok?: string) => { setError(null); setNote(null); try { await fn(); onSaved(); if (ok) setNote(ok); } catch (e) { setError((e as Error).message); } };
  const siteName = (id: string | null) => (id ? (sites.data ?? []).find((s) => s.id === id)?.code ?? '' : 'All sites');
  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="md">
      <DialogTitle>{user.displayName}</DialogTitle>
      <DialogContent>
        <ErrorLine error={error} />
        {note && <Alert severity="success" sx={{ mb: 2 }}>{note}</Alert>}
        <Stack gap={2} sx={{ mt: 1 }}>
          <Stack direction={{ xs: 'column', sm: 'row' }} gap={2}>
            <TextField label="Name" value={name} onChange={(e) => setName(e.target.value)} fullWidth />
            <TextField label="Job title" value={title} onChange={(e) => setTitle(e.target.value)} fullWidth />
          </Stack>
          <Stack direction="row" gap={2} alignItems="center">
            <FormControlLabel control={<Switch checked={active} disabled={self} onChange={(e) => setActive(e.target.checked)} />} label={active ? 'Active' : 'Inactive (cannot sign in)'} />
            <Box sx={{ flex: 1 }} />
            <Button variant="outlined" onClick={() => run(() => put(`/api/admin/users/${user.id}`, { displayName: name, jobTitle: title || null, defaultSiteId: user.defaultSiteId, isActive: active }), 'Saved.')}>Save details</Button>
          </Stack>
          <Typography variant="h3">Roles</Typography>
          <Stack direction="row" gap={1} flexWrap="wrap">
            {user.roles.length === 0 && <Typography variant="body2" color="text.secondary">No roles yet. This person can sign in but cannot open anything.</Typography>}
            {user.roles.map((r) => <Chip key={r.id} label={`${r.role} · ${siteName(r.siteId)}`} onDelete={() => run(() => del(`/api/admin/users/${user.id}/roles/${r.id}`))} deleteIcon={<Tooltip title="Remove role"><CloseIcon /></Tooltip>} />)}
          </Stack>
          <Stack direction={{ xs: 'column', sm: 'row' }} gap={2}>
            <TextField select label="Role" value={grant.roleId} onChange={(e) => setGrant({ ...grant, roleId: e.target.value })} sx={{ minWidth: 220 }}>{roles.map((r) => <MenuItem key={r.id} value={r.id}>{r.name}</MenuItem>)}</TextField>
            <TextField select label="Where" value={grant.siteId} onChange={(e) => setGrant({ ...grant, siteId: e.target.value })} sx={{ minWidth: 220 }}>
              <MenuItem value="">All sites</MenuItem>{(sites.data ?? []).map((s) => <MenuItem key={s.id} value={s.id}>{s.code} · {s.name}</MenuItem>)}
            </TextField>
            <Button variant="outlined" disabled={!grant.roleId} onClick={() => run(() => post(`/api/admin/users/${user.id}/roles`, { roleId: grant.roleId, siteId: grant.siteId || null, departmentId: null }))}>Give role</Button>
          </Stack>
          {!self && <>
            <Typography variant="h3">Temporary password</Typography>
            <Stack direction={{ xs: 'column', sm: 'row' }} gap={2}>
              <TextField label="New temporary password" value={temp} onChange={(e) => setTemp(e.target.value)} sx={{ minWidth: 300 }} helperText="Signs them out everywhere; they choose their own at the next sign-in." />
              <Box><Button variant="outlined" disabled={temp.length < 10} onClick={() => run(() => post(`/api/admin/users/${user.id}/password`, { password: temp, mustChangePassword: true }).then(() => setTemp('')), 'Temporary password set.')}>Set password</Button></Box>
            </Stack>
          </>}
        </Stack>
      </DialogContent>
      <DialogActions><Button onClick={onClose}>Close</Button></DialogActions>
    </Dialog>
  );
}

// ------------------------------------------------------------------ roles
export function RolesPage() {
  const { can } = useAuth();
  const qc = useQueryClient();
  const roles = useQuery({ queryKey: ['admin-roles'], queryFn: () => get<RoleRow[]>('/api/admin/roles'), enabled: can('admin.roles') });
  const catalog = useQuery({ queryKey: ['perm-catalog'], queryFn: () => get<PermInfo[]>('/api/admin/roles/catalog'), enabled: can('admin.roles') });
  const [editing, setEditing] = useState<RoleRow | 'new' | null>(null);
  return (
    <ModuleWorkspace crumbs={crumbsFor('Roles and Permissions')} title="Roles and Permissions"
      actions={can('admin.roles') && <Button variant="contained" onClick={() => setEditing('new')}>New role</Button>}>
      {!can('admin.roles') ? <Denied what="roles" /> : <>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2, maxWidth: 860 }}>A role is a named set of permissions. The starting roles come with suggested permissions ticked; change any of them to match how your shop works. Nothing in the app checks a role name, only the permissions it holds.</Typography>
        <TableContainer component={Paper}>
          <Table>
            <TableHead><TableRow><TableCell>Role</TableCell><TableCell>Description</TableCell><TableCell align="right">Permissions</TableCell><TableCell align="right">People</TableCell><TableCell /></TableRow></TableHead>
            <TableBody>
              {(roles.data ?? []).map((r) => (
                <TableRow key={r.id} hover>
                  <TableCell><Typography fontWeight={600} fontSize={14}>{r.name}</Typography></TableCell>
                  <TableCell>{r.description}</TableCell>
                  <TableCell align="right">{r.permissions.length}</TableCell>
                  <TableCell align="right">{r.users}</TableCell>
                  <TableCell align="right"><Button size="small" onClick={() => setEditing(r)}>Edit</Button></TableCell>
                </TableRow>))}
            </TableBody>
          </Table>
        </TableContainer>
      </>}
      {editing && <RoleDialog role={editing === 'new' ? null : editing} catalog={catalog.data ?? []} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); qc.invalidateQueries({ queryKey: ['admin-roles'] }); qc.invalidateQueries({ queryKey: ['me'] }); }} />}
    </ModuleWorkspace>
  );
}

function RoleDialog({ role, catalog, onClose, onSaved }: { role: RoleRow | null; catalog: PermInfo[]; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(role?.name ?? '');
  const [description, setDescription] = useState(role?.description ?? '');
  const [perms, setPerms] = useState<Set<string>>(new Set(role?.permissions ?? []));
  const [error, setError] = useState<string | null>(null);
  const groups = useMemo(() => { const m = new Map<string, PermInfo[]>(); catalog.forEach((p) => m.set(p.group, [...(m.get(p.group) ?? []), p])); return [...m.entries()]; }, [catalog]);
  const toggle = (c: string) => { const n = new Set(perms); if (n.has(c)) n.delete(c); else n.add(c); setPerms(n); };
  const save = async () => {
    setError(null);
    const body = { name, description: description || null, permissions: [...perms] };
    try { if (role) await put(`/api/admin/roles/${role.id}`, body); else await post('/api/admin/roles', body); onSaved(); } catch (e) { setError((e as Error).message); }
  };
  const remove = async () => { setError(null); try { await del(`/api/admin/roles/${role!.id}`); onSaved(); } catch (e) { setError((e as Error).message); } };
  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="md">
      <DialogTitle>{role ? `Edit role: ${role.name}` : 'New role'}</DialogTitle>
      <DialogContent>
        <ErrorLine error={error} />
        <Stack direction={{ xs: 'column', sm: 'row' }} gap={2} sx={{ mt: 1, mb: 2 }}>
          <TextField label="Role name" value={name} onChange={(e) => setName(e.target.value)} disabled={role?.isSystem} sx={{ minWidth: 260 }} />
          <TextField label="Description" value={description} onChange={(e) => setDescription(e.target.value)} fullWidth />
        </Stack>
        {groups.map(([g, list]) => (
          <Box key={g} sx={{ mb: 2 }}>
            <Stack direction="row" alignItems="center" gap={1}>
              <Typography variant="overline" color="text.secondary">{g}</Typography>
              <Button size="small" onClick={() => { const n = new Set(perms); const all = list.every((p) => n.has(p.code)); list.forEach((p) => (all ? n.delete(p.code) : n.add(p.code))); setPerms(n); }}>
                {list.every((p) => perms.has(p.code)) ? 'Clear all' : 'Tick all'}</Button>
            </Stack>
            <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr', md: '1fr 1fr 1fr' } }}>
              {list.map((p) => <FormControlLabel key={p.code} control={<Checkbox size="small" checked={perms.has(p.code)} onChange={() => toggle(p.code)} />} label={<Typography variant="body2">{p.label}</Typography>} />)}
            </Box>
          </Box>))}
      </DialogContent>
      <DialogActions>
        {role && !role.isSystem && <Button color="error" onClick={remove} sx={{ mr: 'auto' }}>Delete role</Button>}
        <Button onClick={onClose}>Cancel</Button><Button variant="contained" onClick={save}>Save role</Button>
      </DialogActions>
    </Dialog>
  );
}

// ------------------------------------------------------------------ sites
export function SitesPage() {
  const { can } = useAuth();
  const qc = useQueryClient();
  const sites = useSites();
  const [site, setSite] = useState({ code: '', name: '' });
  const [dept, setDept] = useState<{ siteId: string; code: string; name: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const refresh = () => qc.invalidateQueries({ queryKey: ['sites'] });
  const run = async (fn: () => Promise<unknown>) => { setError(null); try { await fn(); refresh(); } catch (e) { setError((e as Error).message); } };
  const [storage, setStorage] = useState<{ ok: boolean; message: string } | null>(null);
  return (
    <ModuleWorkspace crumbs={crumbsFor('Sites and Departments')} title="Sites and Departments">
      {!can('admin.organization') ? <Denied what="sites" /> : <>
        <ErrorLine error={error} />
        <Paper sx={{ p: 2, mb: 2 }}>
          <Stack direction="row" alignItems="center" gap={2}>
            <Typography variant="h3">File storage</Typography>
            <Button size="small" variant="outlined" data-testid="storage-check" onClick={async () => { setStorage(null); try { setStorage(await post<{ ok: boolean; message: string }>('/api/admin/storage-check')); } catch (e) { setStorage({ ok: false, message: (e as Error).message }); } }}>Test file storage</Button>
          </Stack>
          {storage && <Alert severity={storage.ok ? 'success' : 'error'} sx={{ mt: 1.5 }}>{storage.message}</Alert>}
        </Paper>
        <Paper sx={{ p: 2, mb: 2 }}>
          <Typography variant="h3" sx={{ mb: 1.5 }}>Add a site</Typography>
          <Stack direction={{ xs: 'column', sm: 'row' }} gap={2}>
            <TextField size="small" label="Code" value={site.code} onChange={(e) => setSite({ ...site, code: e.target.value })} />
            <TextField size="small" label="Name" value={site.name} onChange={(e) => setSite({ ...site, name: e.target.value })} sx={{ minWidth: 280 }} />
            <Button variant="contained" disabled={!site.code || !site.name} onClick={() => run(() => post('/api/organization/sites', { code: site.code, name: site.name }).then(() => setSite({ code: '', name: '' })))}>Add site</Button>
          </Stack>
        </Paper>
        <Stack gap={2}>
          {(sites.data ?? []).map((s) => (
            <Paper key={s.id} sx={{ p: 2 }}>
              <Stack direction="row" alignItems="center" gap={1.5} sx={{ mb: 1 }}>
                <Chip color="primary" label={s.code} /><Typography variant="h3">{s.name}</Typography>
                <Box sx={{ flex: 1 }} /><Button size="small" onClick={() => setDept({ siteId: s.id, code: '', name: '' })}>Add department</Button>
              </Stack>
              <Stack direction="row" gap={1} flexWrap="wrap">
                {s.departments.length === 0 ? <Typography variant="body2" color="text.secondary">No departments.</Typography> : s.departments.map((d) => <Chip key={d.id} variant="outlined" label={`${d.code} · ${d.name}`} />)}
              </Stack>
              {dept?.siteId === s.id && <Stack direction={{ xs: 'column', sm: 'row' }} gap={2} sx={{ mt: 2 }}>
                <TextField size="small" label="Code" value={dept.code} onChange={(e) => setDept({ ...dept, code: e.target.value })} />
                <TextField size="small" label="Name" value={dept.name} onChange={(e) => setDept({ ...dept, name: e.target.value })} />
                <Button variant="contained" disabled={!dept.code || !dept.name} onClick={() => run(() => post(`/api/organization/sites/${s.id}/departments`, { code: dept.code, name: dept.name }).then(() => setDept(null)))}>Save</Button>
                <IconButton onClick={() => setDept(null)} aria-label="Cancel"><CloseIcon /></IconButton>
              </Stack>}
            </Paper>))}
        </Stack>
      </>}
    </ModuleWorkspace>
  );
}

// ------------------------------------------------------------------ audit trail
interface AuditRow { id: number; who: string; what: string; when: string; description: string }
export function AuditTrailPage() {
  const { can } = useAuth();
  const [q, setQ] = useState('');
  const [page, setPage] = useState(0);
  const [size, setSize] = useState(50);
  const data = useQuery({ queryKey: ['audit-trail', q, page, size], queryFn: () => get<{ total: number; items: AuditRow[] }>(`/api/audit-trail?q=${encodeURIComponent(q)}&page=${page + 1}&pageSize=${size}`), enabled: can('audittrail.view') });
  return (
    <ModuleWorkspace crumbs={crumbsFor('Audit Trail')} title="Audit Trail">
      {!can('audittrail.view') ? <Denied what="the audit trail" /> : <>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2, maxWidth: 820 }}>Every change and sign-in: who did it, what they did, when, and a plain description. Entries can't be edited or removed.</Typography>
        <TextField size="small" label="Search descriptions and names" value={q} onChange={(e) => { setQ(e.target.value); setPage(0); }} sx={{ mb: 2, minWidth: 360 }} />
        <TableContainer component={Paper}>
          <Table size="small">
            <TableHead><TableRow><TableCell>Who</TableCell><TableCell>What</TableCell><TableCell>When</TableCell><TableCell>Description</TableCell></TableRow></TableHead>
            <TableBody>
              {(data.data?.items ?? []).map((a) => (
                <TableRow key={a.id} hover>
                  <TableCell sx={{ whiteSpace: 'nowrap' }}>{a.who}</TableCell>
                  <TableCell sx={{ whiteSpace: 'nowrap' }}>{a.what.replace(/([a-z])([A-Z])/g, '$1 $2')}</TableCell>
                  <TableCell sx={{ whiteSpace: 'nowrap' }}>{fmtDateTime(a.when)}</TableCell>
                  <TableCell>{a.description}</TableCell>
                </TableRow>))}
            </TableBody>
          </Table>
          <TablePagination component="div" count={data.data?.total ?? 0} page={page} rowsPerPage={size} rowsPerPageOptions={[25, 50, 100]}
            onPageChange={(_, p) => setPage(p)} onRowsPerPageChange={(e) => { setSize(Number(e.target.value)); setPage(0); }} />
        </TableContainer>
      </>}
    </ModuleWorkspace>
  );
}
