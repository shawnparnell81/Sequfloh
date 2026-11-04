import { useEffect } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get } from '../api';
import { useAuth } from '../auth';

/** Unread conversations for the signed-in person. One small count, checked while the app is open, so the menu badge moves without a reload. */
export function useMessageUnreadCount() {
  const { can } = useAuth();
  const qc = useQueryClient();
  const allowed = can('messages.use');
  const q = useQuery({
    queryKey: ['messages-unread'],
    queryFn: () => get<{ count: number }>('/api/messages/unread-count'),
    enabled: allowed,
    refetchInterval: 5_000,
    refetchIntervalInBackground: false,
  });
  useEffect(() => {
    if (!allowed) return;
    const check = () => { if (document.visibilityState === 'visible') qc.invalidateQueries({ queryKey: ['messages-unread'] }); };
    window.addEventListener('focus', check);
    document.addEventListener('visibilitychange', check);
    return () => { window.removeEventListener('focus', check); document.removeEventListener('visibilitychange', check); };
  }, [qc, allowed]);
  return allowed ? q.data?.count ?? 0 : 0;
}
