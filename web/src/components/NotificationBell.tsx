import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, Badge, Box, Button, Divider, IconButton, Menu, MenuItem, Snackbar, Tooltip, Typography } from '@mui/material';
import NotificationsOutlined from '@mui/icons-material/NotificationsOutlined';
import { get, post } from '../api';

export type AppNotification = { id: string; title: string; body: string | null; link: string | null; recordType: string | null; recordId: string | null; createdAt: string; readAt: string | null };

const when = (iso: string) => new Date(iso).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });

/**
 * The bell in the top bar: the signed-in person's own notifications with an unread count. It checks every minute and when the window
 * regains focus, shows a short message when something new and unread arrives, and refreshes the person's work lists when a work order
 * notification comes in so a new assignment shows without a reload.
 */
export function NotificationBell() {
  const qc = useQueryClient();
  const nav = useNavigate();
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const [toast, setToast] = useState<AppNotification | null>(null);
  const seen = useRef<Set<string> | null>(null);
  const q = useQuery({ queryKey: ['notifications'], queryFn: () => get<AppNotification[]>('/api/notifications'), refetchInterval: 60_000 });
  const items = q.data ?? [];
  const unread = items.filter((n) => !n.readAt).length;

  // check again whenever the window comes back into view
  useEffect(() => {
    const check = () => { if (document.visibilityState === 'visible') qc.invalidateQueries({ queryKey: ['notifications'] }); };
    window.addEventListener('focus', check);
    document.addEventListener('visibilitychange', check);
    return () => { window.removeEventListener('focus', check); document.removeEventListener('visibilitychange', check); };
  }, [qc]);

  useEffect(() => {
    if (!q.data) return;
    const ids = new Set(q.data.map((n) => n.id));
    // the first load only sets what has been seen; later loads announce anything new that is still unread
    if (seen.current) {
      const fresh = q.data.filter((n) => !n.readAt && !seen.current!.has(n.id));
      if (fresh.length > 0) {
        setToast(fresh[0]);
        if (fresh.some((n) => n.recordType === 'SequflohTrackingRecord')) {
          qc.invalidateQueries({ queryKey: ['sqf-my-work'] });
          qc.invalidateQueries({ queryKey: ['sqf-dash'] });
        }
      }
    }
    seen.current = ids;
  }, [q.data, qc]);

  const refresh = () => qc.invalidateQueries({ queryKey: ['notifications'] });
  const open = async (n: AppNotification) => {
    setAnchor(null);
    setToast(null);
    if (!n.readAt) { await post(`/api/notifications/${n.id}/read`); refresh(); }
    if (n.link) nav(n.link);
  };
  const readAll = async () => { await post('/api/notifications/read-all'); refresh(); };

  return (
    <>
      <Tooltip title="Notifications">
        <IconButton data-testid="notification-bell" data-unread-count={unread} aria-label={unread ? `Notifications, ${unread} unread` : 'Notifications'} color="inherit" size="small" onClick={(e) => { setToast(null); setAnchor(e.currentTarget); }}>
          <Badge badgeContent={unread} color="error" max={99} data-testid="notification-count"><NotificationsOutlined /></Badge>
        </IconButton>
      </Tooltip>
      <Menu anchorEl={anchor} open={!!anchor} onClose={() => setAnchor(null)} slotProps={{ paper: { sx: { width: 380, maxHeight: 480 } } }}>
        <Box sx={{ px: 2, py: 1, display: 'flex', alignItems: 'center' }} data-testid="notification-menu">
          <Typography variant="subtitle2" sx={{ flex: 1 }}>Notifications</Typography>
          <Button size="small" data-testid="notification-read-all" disabled={unread === 0} onClick={readAll}>Mark all read</Button>
        </Box>
        <Divider />
        {items.length === 0 && <MenuItem disabled><Typography variant="body2">No notifications yet.</Typography></MenuItem>}
        {items.slice(0, 20).map((n) => (
          <MenuItem key={n.id} data-testid="notification-item" data-unread={!n.readAt} onClick={() => open(n)} sx={{ whiteSpace: 'normal', alignItems: 'flex-start', gap: 1, py: 1 }}>
            <Box sx={{ width: 8, height: 8, mt: 0.9, borderRadius: '50%', flexShrink: 0, bgcolor: n.readAt ? 'transparent' : 'primary.main' }} />
            <Box sx={{ minWidth: 0 }}>
              <Typography variant="body2" sx={{ fontWeight: n.readAt ? 400 : 600 }}>{n.title}</Typography>
              <Typography variant="caption" color="text.secondary">{when(n.createdAt)}</Typography>
            </Box>
          </MenuItem>
        ))}
      </Menu>
      <Snackbar open={!!toast} autoHideDuration={8000} onClose={(_, reason) => { if (reason !== 'clickaway') setToast(null); }} anchorOrigin={{ vertical: 'top', horizontal: 'right' }} sx={{ mt: 7 }}>
        <Alert data-testid="notification-toast" severity="info" variant="filled" onClose={() => setToast(null)}
          action={toast?.link ? <Button color="inherit" size="small" onClick={() => toast && open(toast)}>Open</Button> : undefined}>
          {toast?.title}
        </Alert>
      </Snackbar>
    </>
  );
}
