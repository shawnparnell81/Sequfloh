import { useEffect } from 'react';
import { Box, Tooltip } from '@mui/material';
import { useTheme } from '@mui/material/styles';
import { usePresence, type PresenceInfo } from './Presence';

/** Vivid fills that keep white initials at WCAG AA. Teal, violet, pink, blue, orange, green and the rest stay distinct. */
const palette = ['#0F766E', '#7C3AED', '#DB2777', '#2563EB', '#C2410C', '#15803D', '#C026D3', '#B45309', '#0E7490', '#E11D48', '#9333EA', '#1D4ED8'];

export function initials(name?: string | null) {
  const parts = (name ?? '').trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}

/** Mixes the whole id, then the random tail, so people created a moment apart do not share a colour. */
export function avatarColor(key: string) {
  const mix = (s: string) => {
    let h = 2166136261;
    for (let i = 0; i < s.length; i++) {
      h ^= s.charCodeAt(i);
      h = Math.imul(h, 16777619);
    }
    h = Math.imul(h ^ (h >>> 16), 0x7feb352d);
    h = Math.imul(h ^ (h >>> 15), 0x846ca68b);
    return (h ^ (h >>> 16)) >>> 0;
  };
  const hex = key.replace(/-/g, '');
  const h = mix(key) ^ mix(hex.slice(-12) || key);
  return palette[(h >>> 0) % palette.length];
}

export function lastSeenText(info?: PresenceInfo | null) {
  if (!info) return 'Offline';
  if (info.online) return 'Online now';
  if (!info.lastSeenAt) return 'Offline';
  const sec = Math.max(0, (Date.now() - new Date(info.lastSeenAt).getTime()) / 1000);
  if (sec < 60) return 'Last seen just now';
  if (sec < 3600) return `Last seen ${Math.floor(sec / 60)} min ago`;
  if (sec < 86400) return `Last seen ${Math.floor(sec / 3600)} hr ago`;
  if (sec < 172800) return 'Last seen yesterday';
  return `Last seen ${new Date(info.lastSeenAt).toLocaleDateString()}`;
}

/** Initials on a coloured circle. The colour comes from the person's id, with an optional online dot. */
export function UserAvatar({ id, name, size = 32, showPresence = true, lastSeenAt, online }: {
  id?: string | null; name?: string | null; size?: number; showPresence?: boolean; lastSeenAt?: string | null; online?: boolean;
}) {
  const theme = useTheme();
  const presence = usePresence();
  useEffect(() => {
    if (!id || !showPresence) return undefined;
    return presence.watch(id);
  }, [id, showPresence, presence]);
  const live = id ? presence.get(id) : undefined;
  const info: PresenceInfo | undefined = live ?? (lastSeenAt !== undefined || online !== undefined ? { lastSeenAt: lastSeenAt ?? null, online: !!online } : undefined);
  const dot = showPresence && !!id;
  const bg = avatarColor(id || name || '?');
  const letter = Math.max(10, Math.round(size * 0.38));
  const mark = (
    <Box component="span" sx={{ position: 'relative', width: size, height: size, flexShrink: 0, display: 'inline-flex' }} data-testid="user-avatar" data-user-id={id || undefined}>
      <Box aria-hidden sx={{ width: size, height: size, borderRadius: '50%', bgcolor: bg, color: '#fff', display: 'grid', placeItems: 'center', fontWeight: 700, fontSize: letter, letterSpacing: '0.02em', lineHeight: 1 }}>
        {initials(name)}
      </Box>
      {dot && <Box data-testid="avatar-presence" data-online={info?.online ? 'true' : 'false'} sx={{
        position: 'absolute', right: -1, bottom: -1, width: Math.max(8, Math.round(size * 0.32)), height: Math.max(8, Math.round(size * 0.32)),
        borderRadius: '50%', bgcolor: info?.online ? '#067647' : '#667085', border: '2px solid', borderColor: theme.palette.background.paper,
      }} />}
    </Box>
  );
  const tip = name ? (dot ? `${name} · ${lastSeenText(info)}` : name) : (dot ? lastSeenText(info) : '');
  return tip ? <Tooltip title={tip}>{mark}</Tooltip> : mark;
}

/** Overlapping faces, with a +N circle when there are more people than fit. */
export function AvatarGroup({ people, max = 3, size = 28 }: { people: { id?: string | null; name?: string | null; lastSeenAt?: string | null; online?: boolean }[]; max?: number; size?: number }) {
  const shown = people.slice(0, max);
  const extra = people.length - shown.length;
  return (
    <Box sx={{ display: 'inline-flex', alignItems: 'center' }} data-testid="avatar-group">
      {shown.map((p, i) => <Box key={(p.id || p.name || '') + i} sx={{ ml: i === 0 ? 0 : '-8px', display: 'inline-flex' }}><UserAvatar id={p.id} name={p.name} size={size} lastSeenAt={p.lastSeenAt} online={p.online} /></Box>)}
      {extra > 0 && <Box aria-label={`${extra} more`} sx={{ ml: '-8px', width: size, height: size, borderRadius: '50%', bgcolor: 'action.selected', color: 'text.primary', display: 'grid', placeItems: 'center', fontSize: 12, fontWeight: 700, border: '2px solid', borderColor: 'background.paper' }}>+{extra}</Box>}
    </Box>
  );
}

/** A face beside a name, for lists, history and assigned-to fields. */
export function PersonLabel({ id, name, size = 24, lastSeenAt, online }: { id?: string | null; name?: string | null; size?: number; lastSeenAt?: string | null; online?: boolean }) {
  if (!name) return <>—</>;
  return (
    <Box component="span" sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.75, minWidth: 0, verticalAlign: 'middle' }}>
      <UserAvatar id={id} name={name} size={size} showPresence={!!id} lastSeenAt={lastSeenAt} online={online} />
      <Box component="span" sx={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{name}</Box>
    </Box>
  );
}
