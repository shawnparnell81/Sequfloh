import { useEffect, useRef, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Alert, Autocomplete, Box, Button, Chip, Dialog, DialogActions, DialogContent, IconButton, Link, List, ListItemButton, MenuItem, Paper, Popover, Stack, TextField, Tooltip, Typography } from '@mui/material';
import AttachFileOutlined from '@mui/icons-material/AttachFileOutlined';
import LinkOutlined from '@mui/icons-material/LinkOutlined';
import EditOutlined from '@mui/icons-material/EditOutlined';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post, put } from '../api';
import { useAuth } from '../auth';
import { EmojiButton, expandEmoji } from '../components/Emoji';
import { FormDialogTitle } from '../components/FullScreen';
import { StatusChip, fmtDateTime } from '../components/Status';
import { AvatarGroup, PersonLabel, UserAvatar } from '../components/UserAvatar';
import { ModuleWorkspace } from '../layout/ModuleWorkspace';

interface Person { id: string; displayName: string; email: string; lastSeenAt?: string | null; online?: boolean }
interface Participant { id: string; displayName: string; lastSeenAt?: string | null; online?: boolean }
interface ConversationRow { id: string; isGroup: boolean; heading: string; lastMessageAt: string; preview: string | null; unread: boolean; participants: Participant[] }
interface Attachment { id: string; fileName: string; contentType: string; sizeBytes: number; image: boolean }
interface Reaction { emoji: string; count: number; mine: boolean; names: string[] }
interface LinkCard { kind: string; id: string; number?: string | null; workOrder?: string | null; part?: string | null; partName?: string | null; status?: string | null; supplier?: string | null; path: string }
interface ChatMessage { id: string; senderId: string; senderName: string; body: string | null; createdAt: string; editedAt: string | null; deleted: boolean; linkKind: string | null; linkLabel: string | null; linkPath: string | null; linkCard?: LinkCard | null; attachments?: Attachment[]; reactions?: Reaction[] }
interface Thread { id: string; isGroup: boolean; heading: string; participants: Participant[]; canSend: boolean; messages: ChatMessage[] }
interface RecordHit { id: string; label: string; kind: string }

const kinds = [
  { value: '', label: 'No other link' },
  { value: 'part', label: 'Part' },
  { value: 'drawing', label: 'Drawing' },
];
const maxFiles = 5;
const maxBytes = 10 * 1024 * 1024;

function whenShort(iso: string) {
  const d = new Date(iso);
  const same = d.toDateString() === new Date().toDateString();
  return same ? d.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' }) : d.toLocaleDateString([], { month: 'short', day: 'numeric' });
}

function LinkFields({ kind, onKind, record, onRecord }: {
  kind: string; onKind: (k: string) => void; record: RecordHit | null; onRecord: (r: RecordHit | null) => void;
}) {
  const [text, setText] = useState('');
  const q = useQuery({
    queryKey: ['message-records', kind, text],
    queryFn: () => get<RecordHit[]>(`/api/messages/records?kind=${encodeURIComponent(kind)}&q=${encodeURIComponent(text)}`),
    enabled: kind !== '' && text.trim().length > 0,
  });
  return (
    <Stack direction={{ xs: 'column', sm: 'row' }} gap={1}>
      <TextField select size="small" label="Also link" value={kind} onChange={(e) => { onKind(e.target.value); onRecord(null); setText(''); }} sx={{ minWidth: 160 }} inputProps={{ 'data-testid': 'message-link-kind' }}>
        {kinds.map((k) => <MenuItem key={k.value} value={k.value}>{k.label}</MenuItem>)}
      </TextField>
      {kind !== '' && <Autocomplete size="small" sx={{ flex: 1, minWidth: 200 }} options={q.data ?? []} value={record} loading={q.isFetching}
        onChange={(_, v) => onRecord(v)} onInputChange={(_, v, reason) => { if (reason === 'input') setText(v); }} filterOptions={(x) => x}
        isOptionEqualToValue={(a, b) => a.id === b.id} getOptionLabel={(o) => o.label}
        noOptionsText={text.trim() ? 'No matching record' : 'Type a number or name'}
        renderInput={(p) => <TextField {...p} label={kinds.find((k) => k.value === kind)?.label ?? 'Record'} inputProps={{ ...p.inputProps, 'data-testid': 'message-link-record' }} />} />}
    </Stack>
  );
}

function WorkOrderField({ value, onChange }: { value: RecordHit | null; onChange: (r: RecordHit | null) => void }) {
  const [text, setText] = useState('');
  const q = useQuery({
    queryKey: ['message-records', 'work-order', text],
    queryFn: () => get<RecordHit[]>(`/api/messages/records?kind=work-order&q=${encodeURIComponent(text)}`),
    enabled: text.trim().length > 0,
  });
  return (
    <Autocomplete size="small" options={q.data ?? []} value={value} loading={q.isFetching} filterOptions={(x) => x}
      onChange={(_, v) => onChange(v)} onInputChange={(_, v, reason) => { if (reason === 'input') setText(v); }}
      isOptionEqualToValue={(a, b) => a.id === b.id} getOptionLabel={(o) => o.label}
        noOptionsText={text.trim() ? 'No work order with that number' : 'Type a work order number'}
      renderInput={(p) => <TextField {...p} label="Work order" placeholder="Search by number" autoFocus inputProps={{ ...p.inputProps, 'data-testid': 'message-work-order' }} />} />
  );
}

function LinkPicker({ order, onOrder, kind, onKind, record, onRecord }: {
  order: RecordHit | null; onOrder: (r: RecordHit | null) => void; kind: string; onKind: (k: string) => void; record: RecordHit | null; onRecord: (r: RecordHit | null) => void;
}) {
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const close = () => setAnchor(null);
  return (
    <>
      <Tooltip title="Link a work order">
        <IconButton size="small" aria-label="Link a work order" data-testid="link-work-order" color={order || record ? 'primary' : 'inherit'} onClick={(e) => setAnchor(e.currentTarget)}>
          <LinkOutlined fontSize="small" />
        </IconButton>
      </Tooltip>
      <Popover open={!!anchor} anchorEl={anchor} onClose={close} anchorOrigin={{ vertical: 'top', horizontal: 'left' }} transformOrigin={{ vertical: 'bottom', horizontal: 'left' }}
        slotProps={{ paper: { sx: { p: 1.5, width: 340 } } }}>
        <Stack gap={1.5} data-testid="link-picker">
          <WorkOrderField value={order} onChange={(v) => { onOrder(v); if (v) { onKind(''); onRecord(null); close(); } }} />
          <LinkFields kind={kind} onKind={(k) => { onKind(k); if (k) onOrder(null); }} record={record} onRecord={(v) => { onRecord(v); if (v) { onOrder(null); close(); } }} />
        </Stack>
      </Popover>
    </>
  );
}

function RecordCard({ card, label, path }: { card?: LinkCard | null; label?: string | null; path?: string | null }) {
  if (!path) return null;
  const kind = card?.kind === 'part' ? 'Part' : card?.kind === 'drawing' ? 'Drawing' : 'Work order';
  return (
    <Box component={RouterLink} to={card?.path || path} data-testid="message-link" sx={{ display: 'block', mt: 1, p: 1.25, borderRadius: 2, textDecoration: 'none', color: 'text.primary', bgcolor: 'background.paper', border: '1px solid', borderColor: 'divider', '&:hover': { borderColor: 'primary.main' } }}>
      <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, letterSpacing: '0.04em', textTransform: 'uppercase' }}>{kind}</Typography>
      <Typography sx={{ fontWeight: 800 }}>{card?.number || label}</Typography>
      {(card?.part || card?.partName) && <Typography variant="body2" color="text.secondary">{[card?.part, card?.partName].filter(Boolean).join(' · ')}</Typography>}
      <Stack direction="row" gap={0.75} alignItems="center" flexWrap="wrap" sx={{ mt: 0.75 }}>
        {card?.status && <StatusChip status={card.status} />}
        {card?.kind === 'work-order' && <Typography variant="caption" color="text.secondary">Supplier {card.supplier || '—'}</Typography>}
      </Stack>
    </Box>
  );
}

function FilesOf({ rows, light }: { rows: Attachment[]; light?: boolean }) {
  if (!rows.length) return null;
  return (
    <Stack gap={0.75} sx={{ mt: 1 }}>
      {rows.map((a) => a.image ? (
        <Box key={a.id} component="img" src={`/api/messages/files/${a.id}`} alt={a.fileName} data-testid="message-attachment" data-kind="image"
          sx={{ alignSelf: 'flex-start', width: 'auto', height: 'auto', maxWidth: 220, maxHeight: 140, objectFit: 'contain', borderRadius: 2, display: 'block', bgcolor: '#fff' }} />
      ) : (
        <Link key={a.id} href={`/api/messages/files/${a.id}`} data-testid="message-attachment" data-kind="file" underline="hover" sx={{ fontWeight: 700, color: light ? '#E0E7FF' : 'primary.main' }}>
          {a.fileName}
        </Link>
      ))}
    </Stack>
  );
}

function Composer({ onSend, testId }: { onSend: (body: string, linkKind: string, linkId: string | null, files: File[]) => Promise<void>; testId: string }) {
  const [body, setBody] = useState('');
  const [kind, setKind] = useState('');
  const [record, setRecord] = useState<RecordHit | null>(null);
  const [order, setOrder] = useState<RecordHit | null>(null);
  const [files, setFiles] = useState<File[]>([]);
  const [over, setOver] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const input = useRef<HTMLInputElement>(null);
  const add = (list: File[]) => {
    const next = [...files];
    for (const file of list) {
      if (next.length >= maxFiles) { setError(`A message can include up to ${maxFiles} files.`); break; }
      if (file.size > maxBytes) { setError(`${file.name} is over 10 MB.`); continue; }
      next.push(file);
    }
    setFiles(next);
  };
  const send = async () => {
    setError(null);
    setBusy(true);
    try {
      const linkKind = order ? 'work-order' : kind;
      const linkId = order ? order.id : record?.id ?? null;
      await onSend(expandEmoji(body), linkKind, linkId, files);
      setBody(''); setKind(''); setRecord(null); setOrder(null); setFiles([]);
    } catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  };
  return (
    <Stack gap={1} sx={{ p: 1.5, borderTop: 1, borderColor: 'divider', bgcolor: over ? 'action.hover' : undefined, borderRadius: over ? 2 : 0 }}
      onDragOver={(e) => { e.preventDefault(); setOver(true); }} onDragLeave={() => setOver(false)}
      onDrop={(e) => { e.preventDefault(); setOver(false); add([...e.dataTransfer.files]); }}>
      {error && <Alert severity="error" sx={{ py: 0 }}>{error}</Alert>}
      {(order || record || files.length > 0) && <Stack direction="row" gap={0.5} flexWrap="wrap">
        {order && <Chip size="small" color="primary" label={order.label} onDelete={() => setOrder(null)} data-testid="message-link-chip" />}
        {record && <Chip size="small" label={record.label} onDelete={() => { setRecord(null); setKind(''); }} />}
        {files.map((f, i) => <Chip key={f.name + i} size="small" variant="outlined" label={f.name} onDelete={() => setFiles(files.filter((_, j) => j !== i))} />)}
      </Stack>}
      <Stack direction="row" gap={0.5} alignItems="flex-end">
        <TextField multiline minRows={1} maxRows={4} size="small" fullWidth placeholder="Message" value={body} onChange={(e) => setBody(expandEmoji(e.target.value))} inputProps={{ 'data-testid': testId, 'aria-label': 'Message' }} />
        <input ref={input} hidden type="file" multiple data-testid="message-files" onChange={(e) => { add([...(e.target.files ?? [])]); e.target.value = ''; }} />
        <Tooltip title="Attach files"><IconButton size="small" aria-label="Attach files" onClick={() => input.current?.click()}><AttachFileOutlined fontSize="small" /></IconButton></Tooltip>
        <EmojiButton onPick={(e) => setBody((b) => (b ? b + e : e))} />
        <LinkPicker order={order} onOrder={setOrder} kind={kind} onKind={setKind} record={record} onRecord={setRecord} />
        <Button variant="contained" disabled={busy || (!body.trim() && files.length === 0)} onClick={send} data-testid={testId + '-send'}>Send</Button>
      </Stack>
    </Stack>
  );
}

export function MessagesPage() { return <MessagesScreen mode="inbox" />; }
export function AllMessagesPage() { return <MessagesScreen mode="all" />; }

function MessagesScreen({ mode }: { mode: 'inbox' | 'all' }) {
  const inbox = mode === 'inbox';
  const { can, me } = useAuth();
  const allowed = inbox ? can('messages.use') : can('messages.admin');
  const qc = useQueryClient();
  const [selected, setSelected] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const list = useQuery({
    queryKey: [inbox ? 'messages' : 'messages-admin'],
    queryFn: () => get<ConversationRow[]>(inbox ? '/api/messages/conversations' : '/api/messages/admin/conversations'),
    enabled: allowed,
    refetchInterval: allowed ? 8_000 : false,
    refetchIntervalInBackground: false,
  });
  const thread = useQuery({
    queryKey: [inbox ? 'message-thread' : 'message-admin-thread', selected],
    queryFn: () => get<Thread>(inbox ? `/api/messages/conversations/${selected}` : `/api/messages/admin/conversations/${selected}`),
    enabled: allowed && !!selected,
    refetchInterval: allowed && selected ? 4_000 : false,
    refetchIntervalInBackground: false,
  });
  const lastId = thread.data?.messages.at(-1)?.id;
  useEffect(() => {
    if (!inbox || !selected || !lastId) return;
    let stop = false;
    post(`/api/messages/conversations/${selected}/read`).then(() => {
      if (stop) return;
      qc.invalidateQueries({ queryKey: ['messages-unread'] });
      qc.invalidateQueries({ queryKey: ['messages'] });
    }).catch(() => undefined);
    return () => { stop = true; };
  }, [inbox, selected, lastId, qc]);

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['messages'] });
    qc.invalidateQueries({ queryKey: ['messages-admin'] });
    qc.invalidateQueries({ queryKey: ['messages-unread'] });
    if (selected) {
      qc.invalidateQueries({ queryKey: ['message-thread', selected] });
      qc.invalidateQueries({ queryKey: ['message-admin-thread', selected] });
    }
  };
  const send = async (url: string, body: string, linkKind: string, linkId: string | null, files: File[], extra?: Record<string, string>) => {
    if (files.length === 0 && !extra) {
      await post(url, { body, linkKind: linkKind || null, linkId });
      return;
    }
    const fd = new FormData();
    fd.set('body', body);
    if (linkKind) fd.set('linkKind', linkKind);
    if (linkId) fd.set('linkId', linkId);
    for (const [k, v] of Object.entries(extra ?? {})) if (k === 'recipientIds') v.split(',').forEach((id) => fd.append('recipientIds', id)); else fd.set(k, v);
    files.forEach((f) => fd.append('files', f));
    await post(url, fd);
  };

  return (
    <ModuleWorkspace crumbs={[{ label: 'Messages' }, { label: inbox ? 'Inbox' : 'All conversations' }]} title={inbox ? 'Messages' : 'All conversations'}
      subtitle={inbox ? 'Write to one person, or start a named group. A conversation stays unread until you open it.' : 'Every conversation you are allowed to review. This view is read only.'}
      actions={inbox && allowed ? <Button variant="contained" data-testid="new-message" onClick={() => setCreating(true)}>New message</Button> : undefined}>
      {!allowed ? <Alert severity="warning">{inbox ? 'You do not have permission to use messages. Ask an administrator for messages.use.' : 'You do not have permission to review every conversation. Ask an administrator for messages.admin.'}</Alert> : <>
        {list.error && <Alert severity="error" sx={{ mb: 2 }}>{(list.error as Error).message}</Alert>}
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '320px minmax(0, 1fr)' }, gap: 2, alignItems: 'stretch', minHeight: { md: 'calc(100vh - 280px)' } }}>
          <Paper variant="outlined" sx={{ display: 'flex', flexDirection: 'column', minHeight: 360, maxHeight: { md: 'calc(100vh - 280px)' }, overflow: 'hidden' }}>
            <Typography variant="subtitle2" sx={{ px: 2, py: 1.25, borderBottom: 1, borderColor: 'divider' }}>{inbox ? 'Conversations' : 'Conversations in your sites'}</Typography>
            <List dense disablePadding data-testid="conversation-list" sx={{ overflow: 'auto', flex: 1 }}>
              {(list.data ?? []).length === 0 && <Box sx={{ p: 3, textAlign: 'center' }}><Typography sx={{ fontSize: 28 }} aria-hidden>☕</Typography><Typography variant="body2" color="text.secondary">{list.isLoading ? 'Loading…' : 'No conversations yet. The floor is quiet. Suspiciously quiet.'}</Typography></Box>}
              {(list.data ?? []).map((c) => {
                const faces = c.isGroup ? c.participants : c.participants.filter((p) => p.id !== me?.id);
                return (
                  <ListItemButton key={c.id} selected={c.id === selected} onClick={() => setSelected(c.id)} data-testid="conversation-item" data-unread={c.unread ? 'true' : 'false'} data-conversation-id={c.id}
                    sx={{ alignItems: 'flex-start', gap: 1.25, py: 1.25, borderLeft: 3, borderColor: c.unread ? 'primary.main' : 'transparent' }}>
                    {c.isGroup ? <AvatarGroup people={faces.map((p) => ({ id: p.id, name: p.displayName, lastSeenAt: p.lastSeenAt, online: p.online }))} size={36} /> : <UserAvatar id={faces[0]?.id} name={faces[0]?.displayName || c.heading} size={36} lastSeenAt={faces[0]?.lastSeenAt} online={faces[0]?.online} />}
                    <Box sx={{ minWidth: 0, flex: 1 }}>
                      <Typography noWrap sx={{ fontWeight: c.unread ? 800 : 600, fontSize: 14 }}>{c.heading}</Typography>
                      <Typography noWrap variant="body2" color="text.secondary">{c.preview || 'No messages yet'}</Typography>
                    </Box>
                    <Stack alignItems="flex-end" gap={0.5}>
                      <Typography variant="caption" color="text.secondary" sx={{ whiteSpace: 'nowrap' }}>{whenShort(c.lastMessageAt)}</Typography>
                      {c.unread && <Box data-testid="unread-dot" sx={{ width: 8, height: 8, borderRadius: '50%', bgcolor: 'primary.main' }} />}
                    </Stack>
                  </ListItemButton>
                );
              })}
            </List>
          </Paper>
          <Paper variant="outlined" sx={{ display: 'flex', flexDirection: 'column', minHeight: 360, maxHeight: { md: 'calc(100vh - 280px)' }, overflow: 'hidden' }} data-testid="message-thread">
            {!selected && <Stack sx={{ flex: 1, alignItems: 'center', justifyContent: 'center', p: 3 }}><Typography sx={{ fontSize: 32 }} aria-hidden>💬</Typography><Typography color="text.secondary">Select a conversation{inbox ? ', or start a new one. Someone is probably waiting on a part number.' : ''}.</Typography></Stack>}
            {selected && thread.error && <Alert severity="error" sx={{ m: 2 }}>{(thread.error as Error).message}</Alert>}
            {selected && thread.data && <ThreadView thread={thread.data} mine={me?.id} readOnly={!inbox || !thread.data.canSend} onChanged={refresh} />}
            {inbox && selected && thread.data?.canSend && <Composer testId="reply-body" onSend={async (body, linkKind, linkId, files) => {
              await send(files.length ? `/api/messages/conversations/${selected}/messages/with-files` : `/api/messages/conversations/${selected}/messages`, body, linkKind, linkId, files);
              refresh();
            }} />}
          </Paper>
        </Box>
        {creating && <NewMessageDialog onClose={() => setCreating(false)} onCreated={(id) => { setCreating(false); setSelected(id); refresh(); }} />}
      </>}
    </ModuleWorkspace>
  );
}

function ThreadView({ thread, mine, readOnly, onChanged }: { thread: Thread; mine?: string; readOnly: boolean; onChanged: () => void }) {
  const [editing, setEditing] = useState<ChatMessage | null>(null);
  const log = useRef<HTMLDivElement>(null);
  const tail = thread.messages.at(-1)?.id;
  useEffect(() => { const el = log.current; if (el) el.scrollTop = el.scrollHeight; }, [thread.id, tail]);
  const [removing, setRemoving] = useState<ChatMessage | null>(null);
  const [draft, setDraft] = useState('');
  const [error, setError] = useState<string | null>(null);
  const react = async (id: string, emoji: string) => {
    setError(null);
    try { await post(`/api/messages/messages/${id}/reactions`, { emoji }); onChanged(); }
    catch (e) { setError((e as Error).message); }
  };
  return (
    <Stack sx={{ flex: 1, minHeight: 0 }}>
      <Stack direction="row" gap={1.25} alignItems="center" sx={{ px: 2, py: 1.25, borderBottom: 1, borderColor: 'divider' }}>
        <AvatarGroup people={thread.participants.map((p) => ({ id: p.id, name: p.displayName, lastSeenAt: p.lastSeenAt, online: p.online }))} size={32} />
        <Box sx={{ minWidth: 0 }}>
          <Typography variant="subtitle1" fontWeight={800} noWrap>{thread.heading}</Typography>
          <Typography variant="caption" color="text.secondary" noWrap>{thread.participants.map((p) => p.displayName).join(', ')}</Typography>
        </Box>
      </Stack>
      {error && <Alert severity="error" sx={{ m: 1 }}>{error}</Alert>}
      <Stack ref={log} gap={1.5} sx={{ flex: 1, overflow: 'auto', p: 2, bgcolor: 'action.hover' }} data-testid="message-log">
        {thread.messages.length === 0 && <Typography variant="body2" color="text.secondary" sx={{ textAlign: 'center', py: 4 }}>No messages yet. Say hello before the coffee gets cold.</Typography>}
        {thread.messages.map((msg) => {
          const own = msg.senderId === mine;
          return (
            <Stack key={msg.id} direction="row" gap={1} data-testid="message-item" data-own={own ? 'true' : 'false'} data-deleted={msg.deleted ? 'true' : 'false'} sx={{ maxWidth: '86%', alignSelf: own ? 'flex-end' : 'flex-start', flexDirection: own ? 'row-reverse' : 'row' }}>
              <UserAvatar id={msg.senderId} name={msg.senderName} size={32} />
              <Stack sx={{ minWidth: 0 }}>
                <Typography variant="caption" color="text.secondary" data-testid="message-meta" sx={{ px: 0.5, textAlign: own ? 'right' : 'left' }}>{msg.senderName} · <span data-testid="message-when">{fmtDateTime(msg.createdAt)}</span>{msg.editedAt && !msg.deleted ? ' · edited' : ''}</Typography>
                <Box sx={{ px: 1.5, py: 1, borderRadius: own ? '16px 16px 4px 16px' : '16px 16px 16px 4px', bgcolor: own ? 'primary.main' : 'background.paper', color: own ? '#fff' : 'text.primary', border: own ? 0 : 1, borderColor: 'divider', boxShadow: '0 1px 2px rgba(30,27,75,0.06)' }}>
                  {editing?.id === msg.id ? (
                    <Stack gap={1}>
                      <TextField multiline size="small" value={draft} onChange={(e) => setDraft(e.target.value)} inputProps={{ 'data-testid': 'edit-message-body' }} sx={{ bgcolor: 'background.paper', borderRadius: 1 }} />
                      <Stack direction="row" gap={1} justifyContent="flex-end">
                        <Button size="small" onClick={() => setEditing(null)} sx={{ color: own ? '#fff' : undefined }}>Cancel</Button>
                        <Button size="small" variant="contained" onClick={async () => { setError(null); try { await put(`/api/messages/messages/${msg.id}`, { body: draft }); setEditing(null); onChanged(); } catch (e) { setError((e as Error).message); } }}>Save</Button>
                      </Stack>
                    </Stack>
                  ) : <>
                    {msg.deleted && <Typography variant="caption" sx={{ display: 'block', fontStyle: 'italic', opacity: 0.8 }}>{msg.body ? 'Deleted' : 'This message was deleted.'}</Typography>}
                    {msg.body && <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>{msg.body}</Typography>}
                    <FilesOf rows={msg.attachments ?? []} light={own} />
                    {(msg.linkPath || msg.linkCard) && <RecordCard card={msg.linkCard} label={msg.linkLabel} path={msg.linkPath} />}
                  </>}
                </Box>
                {!msg.deleted && (msg.reactions?.length || !readOnly) && (
                  <Stack direction="row" gap={0.5} flexWrap="wrap" alignItems="center" sx={{ mt: 0.5, justifyContent: own ? 'flex-end' : 'flex-start' }}>
                    {(msg.reactions ?? []).map((r) => (
                      <Tooltip key={r.emoji} title={r.names.join(', ') || r.emoji}>
                        <Button size="small" data-testid="message-reaction" data-emoji={r.emoji} onClick={() => !readOnly && react(msg.id, r.emoji)}
                          sx={{ minWidth: 0, px: 1, py: 0.25, borderRadius: 999, fontSize: 13, bgcolor: r.mine ? 'primary.main' : 'background.paper', color: r.mine ? '#fff' : 'text.primary', border: 1, borderColor: r.mine ? 'primary.main' : 'divider' }}>
                          {r.emoji} {r.count}
                        </Button>
                      </Tooltip>
                    ))}
                    {!readOnly && <EmojiButton label="React" onPick={(e) => react(msg.id, e)} />}
                  </Stack>
                )}
                {own && !readOnly && !msg.deleted && editing?.id !== msg.id && (
                  <Stack direction="row" gap={0.5} justifyContent="flex-end">
                    <Tooltip title="Edit"><IconButton size="small" aria-label="Edit message" data-testid="edit-message" onClick={() => { setEditing(msg); setDraft(msg.body ?? ''); }}><EditOutlined fontSize="small" /></IconButton></Tooltip>
                    <Tooltip title="Delete"><IconButton size="small" aria-label="Delete message" data-testid="delete-message" onClick={() => setRemoving(msg)}><DeleteOutline fontSize="small" /></IconButton></Tooltip>
                  </Stack>
                )}
              </Stack>
            </Stack>
          );
        })}
      </Stack>
      {removing && <Dialog open onClose={() => setRemoving(null)}>
        <FormDialogTitle>Delete this message?</FormDialogTitle>
        <DialogContent><Typography variant="body2">It will be removed from the conversation. The audit trail keeps who deleted it and what it said.</Typography></DialogContent>
        <DialogActions>
          <Button onClick={() => setRemoving(null)}>Cancel</Button>
          <Button color="error" variant="contained" data-testid="confirm-delete-message" onClick={async () => { setError(null); try { await del(`/api/messages/messages/${removing.id}`); setRemoving(null); onChanged(); } catch (e) { setError((e as Error).message); setRemoving(null); } }}>Delete</Button>
        </DialogActions>
      </Dialog>}
    </Stack>
  );
}

function NewMessageDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (id: string) => void }) {
  const [who, setWho] = useState('');
  const people = useQuery({ queryKey: ['message-people', who], queryFn: () => get<Person[]>(`/api/messages/people?q=${encodeURIComponent(who)}`) });
  const [picked, setPicked] = useState<Person[]>([]);
  const [title, setTitle] = useState('');
  const [body, setBody] = useState('');
  const [kind, setKind] = useState('');
  const [record, setRecord] = useState<RecordHit | null>(null);
  const [order, setOrder] = useState<RecordHit | null>(null);
  const [files, setFiles] = useState<File[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const group = picked.length >= 2;
  const send = async () => {
    setError(null);
    setBusy(true);
    try {
      const linkKind = order ? 'work-order' : kind;
      const linkId = order ? order.id : record?.id ?? null;
      const text = expandEmoji(body);
      let id: string;
      if (files.length === 0) {
        const r = await post<{ id: string }>('/api/messages/conversations', { recipientIds: picked.map((p) => p.id), title: group ? title : null, body: text, linkKind: linkKind || null, linkId });
        id = r.id;
      } else {
        const fd = new FormData();
        picked.forEach((p) => fd.append('recipientIds', p.id));
        if (group) fd.set('title', title);
        fd.set('body', text);
        if (linkKind) fd.set('linkKind', linkKind);
        if (linkId) fd.set('linkId', linkId);
        files.forEach((f) => fd.append('files', f));
        id = (await post<{ id: string }>('/api/messages/conversations/with-files', fd)).id;
      }
      onCreated(id);
    } catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  };
  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm">
      <FormDialogTitle>New message</FormDialogTitle>
      <DialogContent>
        <Stack gap={2} sx={{ mt: 1 }}>
          {error && <Alert severity="error">{error}</Alert>}
          <Autocomplete multiple options={people.data ?? []} value={picked} onChange={(_, v) => setPicked(v)} loading={people.isLoading} filterOptions={(x) => x}
            onInputChange={(_, v, reason) => { if (reason === 'input') setWho(v); }}
            isOptionEqualToValue={(a, b) => a.id === b.id} getOptionLabel={(o) => o.displayName}
            renderInput={(p) => <TextField {...p} label="To" placeholder="Choose people" inputProps={{ ...p.inputProps, 'data-testid': 'message-recipients' }} />}
            renderOption={(props, o) => { const { key, ...rest } = props; return <li key={key} {...rest}><PersonLabel id={o.id} name={o.displayName} /><Typography variant="caption" color="text.secondary" sx={{ ml: 1 }}>{o.email}</Typography></li>; }}
            renderTags={(value, getTagProps) => value.map((o, i) => { const { key, ...tag } = getTagProps({ index: i }); return <Box key={key} component="span" {...tag} sx={{ display: 'inline-flex', mr: 0.5 }}><PersonLabel id={o.id} name={o.displayName} size={20} /></Box>; })} />
          {group && <TextField label="Group name" value={title} onChange={(e) => setTitle(e.target.value)} inputProps={{ 'data-testid': 'group-name' }} helperText="A named thread for several people." />}
          {(order || record || files.length > 0) && <Stack direction="row" gap={0.5} flexWrap="wrap">
            {order && <Chip size="small" color="primary" label={order.label} onDelete={() => setOrder(null)} data-testid="message-link-chip" />}
            {record && <Chip size="small" label={record.label} onDelete={() => { setRecord(null); setKind(''); }} />}
            {files.map((f) => <Chip key={f.name} size="small" variant="outlined" label={f.name} onDelete={() => setFiles(files.filter((x) => x !== f))} />)}
          </Stack>}
          <TextField multiline minRows={2} label="Message" value={body} onChange={(e) => setBody(expandEmoji(e.target.value))} inputProps={{ 'data-testid': 'new-message-body' }} />
          <input ref={input} hidden type="file" multiple onChange={(e) => { setFiles([...(e.target.files ?? [])].slice(0, maxFiles)); e.target.value = ''; }} />
          <Stack direction="row" gap={0.5} alignItems="center">
            <Tooltip title="Attach files"><IconButton size="small" aria-label="Attach files" onClick={() => input.current?.click()}><AttachFileOutlined fontSize="small" /></IconButton></Tooltip>
            <EmojiButton onPick={(e) => setBody((b) => (b ? b + e : e))} />
            <LinkPicker order={order} onOrder={setOrder} kind={kind} onKind={setKind} record={record} onRecord={setRecord} />
          </Stack>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={busy || picked.length === 0 || (!body.trim() && files.length === 0) || (group && !title.trim())} onClick={send} data-testid="new-message-send">Send</Button>
      </DialogActions>
    </Dialog>
  );
}
