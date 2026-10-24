import { PageNav } from '../tabs/Workspace';
import { useState, type FormEvent, type ReactNode } from 'react';
import { Alert, Box, Button, Checkbox, FormControlLabel, MenuItem, Paper, Stack, TextField, Typography } from '@mui/material';
import { useTheme } from '@mui/material/styles';
import { useNavigate } from 'react-router-dom';
import { post } from '../api';
import { useAuth, useBranding } from '../auth';

function Frame({ title, subtitle, children }: { title: string; subtitle: string; children: ReactNode }) {
  const tk = useTheme().tk;
  return (
    <Box sx={{ minHeight: '100vh', display: 'grid', gridTemplateColumns: { xs: '1fr', md: '1.05fr 1fr' } }}>
      <Box sx={{ display: { xs: 'none', md: 'flex' }, flexDirection: 'column', justifyContent: 'space-between', p: 6, background: tk.gradient, color: '#FFFFFF' }}>
        <Stack direction="row" alignItems="center" gap={1.5}>
          <Box component="img" src="/favicon.svg" alt="" sx={{ width: 44, height: 44, borderRadius: '12px', boxShadow: '0 0 0 2px rgba(255,255,255,0.5)' }} />
          <Typography sx={{ fontWeight: 800, letterSpacing: '0.12em', fontSize: 20 }}>SEQUFLOH</Typography>
        </Stack>
        <Box sx={{ maxWidth: 480 }}>
          <Typography sx={{ fontSize: 34, fontWeight: 800, lineHeight: 1.15, letterSpacing: '-0.02em', mb: 2 }}>Every job, every step, tracked from release to sign-off.</Typography>
          <Typography sx={{ fontSize: 15.5, opacity: 0.92, lineHeight: 1.6 }}>Controlled work instructions, live task execution with timing, and validation, FAI, repair and production tracking records in one place.</Typography>
        </Box>
        <Stack direction="row" gap={4} sx={{ opacity: 0.9, fontSize: 13 }}>
          <span>Work instructions</span><span>Live execution</span><span>Tracking records</span><span>Reports</span>
        </Stack>
      </Box>
      <Stack alignItems="center" justifyContent="center" sx={{ p: 3 }}>
        <Paper sx={{ p: { xs: 3, sm: 5 }, width: '100%', maxWidth: 460 }}>
          <Typography variant="h1" sx={{ fontSize: '1.7rem', mb: 0.75 }}>{title}</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>{subtitle}</Typography>
          {children}
        </Paper>
      </Stack>
    </Box>
  );
}

export function LoginPage() {
  const { login } = useAuth();
  const branding = useBranding();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [remember, setRemember] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const submit = async (e: FormEvent) => {
    e.preventDefault(); setError(null); setBusy(true);
    try { await login(email, password, remember); } catch (x) { setError((x as Error).message); } finally { setBusy(false); }
  };
  return (
    <Frame title="Sign in" subtitle={branding.data?.organizationName ? `${branding.data.organizationName} job tracking` : 'Sign in with the email address your administrator set up for you.'}>
      <Stack component="form" gap={2} onSubmit={submit}>
        {error && <Alert severity="error">{error}</Alert>}
        <TextField label="Email" type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} required autoFocus />
        <TextField label="Password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
        <FormControlLabel sx={{ mt: -1 }} control={<Checkbox checked={remember} onChange={(e) => setRemember(e.target.checked)} />}
          label={<Typography variant="body2">Remember me for 30 days on this device</Typography>} />
        <Button type="submit" variant="contained" size="large" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</Button>
        <Typography variant="caption" color="text.secondary">Forgot your password? Ask an administrator to set a temporary one for you.</Typography>
      </Stack>
    </Frame>
  );
}

const zones = ['America/New_York', 'America/Chicago', 'America/Denver', 'America/Phoenix', 'America/Los_Angeles', 'America/Anchorage', 'Pacific/Honolulu', 'Europe/London', 'Europe/Berlin', 'Asia/Kolkata', 'Asia/Shanghai', 'Asia/Tokyo', 'Australia/Sydney', 'UTC'];

/** First run: shown until the first administrator exists. */
export function SetupPage() {
  const { refresh } = useAuth();
  const [f, setF] = useState({ organizationName: '', siteCode: 'MAIN', siteName: '', timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'America/New_York', displayName: '', email: '', password: '', confirm: '' });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value });
  const submit = async (e: FormEvent) => {
    e.preventDefault(); setError(null);
    if (f.password !== f.confirm) { setError('The two passwords do not match.'); return; }
    setBusy(true);
    try { await post('/api/auth/setup', f); await refresh(); } catch (x) { setError((x as Error).message); } finally { setBusy(false); }
  };
  return (
    <Frame title="Set up SEQUFLOH" subtitle="This page appears once. It creates your organization, your first site and your administrator account.">
      <Stack component="form" gap={2} onSubmit={submit}>
        {error && <Alert severity="error">{error}</Alert>}
        <TextField label="Organization name" value={f.organizationName} onChange={set('organizationName')} required autoFocus />
        <Stack direction="row" gap={2}>
          <TextField label="Site code" value={f.siteCode} onChange={set('siteCode')} required sx={{ width: 140 }} />
          <TextField label="Site name" value={f.siteName} onChange={set('siteName')} required fullWidth />
        </Stack>
        <TextField select label="Time zone" value={zones.includes(f.timeZone) ? f.timeZone : 'America/New_York'} onChange={set('timeZone')}>{zones.map((z) => <MenuItem key={z} value={z}>{z}</MenuItem>)}</TextField>
        <TextField label="Your name" value={f.displayName} onChange={set('displayName')} required />
        <TextField label="Your email" type="email" autoComplete="username" value={f.email} onChange={set('email')} required />
        <TextField label="Password (at least 10 characters)" type="password" autoComplete="new-password" value={f.password} onChange={set('password')} required />
        <TextField label="Confirm password" type="password" autoComplete="new-password" value={f.confirm} onChange={set('confirm')} required />
        <Button type="submit" variant="contained" size="large" disabled={busy}>{busy ? 'Setting up…' : 'Create organization and sign in'}</Button>
      </Stack>
    </Frame>
  );
}

export function ChangePasswordPage({ forced }: { forced?: boolean }) {
  const { refresh, logout } = useAuth();
  const nav = useNavigate();
  const [cur, setCur] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const submit = async (e: FormEvent) => {
    e.preventDefault(); setError(null);
    if (next !== confirm) { setError('The two new passwords do not match.'); return; }
    try { await post('/api/auth/change-password', { currentPassword: cur, newPassword: next }); setDone(true); await refresh(); if (!forced) nav('/sequfloh'); } catch (x) { setError((x as Error).message); }
  };
  const form = (
    <Stack component="form" gap={2} onSubmit={submit} sx={{ maxWidth: 420 }}>
      {error && <Alert severity="error">{error}</Alert>}
      {done && <Alert severity="success">Password changed.</Alert>}
      <TextField label="Current password" type="password" autoComplete="current-password" value={cur} onChange={(e) => setCur(e.target.value)} required />
      <TextField label="New password (at least 10 characters)" type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} required />
      <TextField label="Confirm new password" type="password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} required />
      <Stack direction="row" gap={1}><Button type="submit" variant="contained">Change password</Button>{forced && <Button onClick={() => logout()}>Sign out</Button>}</Stack>
    </Stack>
  );
  if (forced) return <Frame title="Choose a new password" subtitle="Your administrator gave you a temporary password. Choose your own before you continue.">{form}</Frame>;
  return <Box sx={{ p: { xs: 2, md: 4 } }}><Box sx={{ mb: 1 }}><PageNav /></Box><Typography variant="h1" sx={{ mb: 3 }}>Change password</Typography><Paper sx={{ p: 3, maxWidth: 520 }}>{form}</Paper></Box>;
}
