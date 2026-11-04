import { useEffect, useRef, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Alert, Autocomplete, Box, Button, Dialog, DialogActions, DialogContent, IconButton, Link, List, ListItemButton, ListItemText, MenuItem, Paper, Stack, TextField, Tooltip, Typography } from '@mui/material';
import EditOutlined from '@mui/icons-material/EditOutlined';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post, put } from '../api';
import { useAuth } from '../auth';
import { FormDialogTitle } from '../components/FullScreen';
import { fmtDateTime } from '../components/Status';
import { ModuleWorkspace } from '../layout/ModuleWorkspace';

interface Person { id: string; displayName: string; email: string }
interface Participant { id: string; displayName: string }
interface ConversationRow { id: string; isGroup: boolean; heading: string; lastMessageAt: string; preview: string | null; unread: boolean; participants: Participant[] }
interface ChatMessage { id: string; senderId: string; senderName: string; body: string | null; createdAt: string; editedAt: string | null; deleted: boolean; linkKind: string | null; linkLabel: string | null; linkPath: string | null }
interface Thread { id: string; isGroup: boolean; heading: string; participants: Participant[]; canSend: boolean; messages: ChatMessage[] }
interface RecordHit { id: string; label: string; kind: string }

const kinds = [
  { value: '', label: 'No link' },
  { value: 'work-order', label: 'Work order' },
  { value: 'part', label: 'Part' },
  { value: 'drawing', label: 'Drawing' },
];

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
      <TextField select size="small" label="Link" value={kind} onChange={(e) => { onKind(e.target.value); onRecord(null); setText(''); }} sx={{ minWidth: 160 }} data-testid="message-link-kind">
        {kinds.map((k) => <MenuItem key={k.value} value={k.value}>{k.label}</MenuItem>)}
      </TextField>
      {kind !== '' && <Autocomplete size="small" sx={{ flex: 1, minWidth: 220 }} options={q.data ?? []} value={record} loading={q.isFetching}
        onChange={(_, v) => onRecord(v)} onInputChange={(_, v, reason) => { if (reason === 'input') setText(v); }} filterOptions={(x) => x}
        isOptionEqualToValue={(a, b) => a.id === b.id} getOptionLabel={(o) => o.label}
        noOptionsText={text.trim() ? 'No matching record' : 'Type a number or name'}
        renderInput={(p) => <TextField {...p} label={kinds.find((k) => k.value === kind)?.label ?? 'Record'} data-testid="message-link-record" />} />}
    </Stack>
  );
}

function Composer({ onSend, testId }: { onSend: (body: string, linkKind: string, linkId: string | null) => Promise<void>; testId: string }) {
  const [body, setBody] = useState('');
  const [kind, setKind] = useState('');
  const [record, setRecord] = useState<RecordHit | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const send = async () => {
    setError(null);
    setBusy(true);
    try {
      await onSend(body, kind, record?.id ?? null);
      setBody(''); setKind(''); setRecord(null);
    } catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  };
  return (
    <Stack gap={1} sx={{ p: 1.5, borderTop: 1, borderColor: 'divider' }}>
      {error && <Alert severity="error">{error}</Alert>}
      <TextField multiline minRows={2} maxRows={6} size="small" label="Message" value={body} onChange={(e) => setBody(e.target.value)} inputProps={{ 'data-testid': testId }} />
      <LinkFields kind={kind} onKind={setKind} record={record} onRecord={setRecord} />
      <Box sx={{ display: 'flex', justifyContent: 'flex-end' }}>
        <Button variant="contained" disabled={busy || !body.trim()} onClick={send} data-testid={testId + '-send'}>Send</Button>
      </Box>
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

  return (
    <ModuleWorkspace crumbs={[{ label: 'Messages' }, { label: inbox ? 'Inbox' : 'All conversations' }]} title={inbox ? 'Messages' : 'All conversations'}
      subtitle={inbox ? 'Write to one person, or start a named group. A conversation stays unread until you open it.' : 'Every conversation you are allowed to review. This view is read only.'}
      actions={inbox && allowed ? <Button variant="contained" data-testid="new-message" onClick={() => setCreating(true)}>New message</Button> : undefined}>
      {!allowed ? <Alert severity="warning">{inbox ? 'You do not have permission to use messages. Ask an administrator for messages.use.' : 'You do not have permission to review every conversation. Ask an administrator for messages.admin.'}</Alert> : <>
        {list.error && <Alert severity="error" sx={{ mb: 2 }}>{(list.error as Error).message}</Alert>}
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '320px minmax(0, 1fr)' }, gap: 2, alignItems: 'stretch', minHeight: { md: 'calc(100vh - 260px)' } }}>
          <Paper variant="outlined" sx={{ display: 'flex', flexDirection: 'column', minHeight: 360, maxHeight: { md: 'calc(100vh - 260px)' }, overflow: 'hidden' }}>
            <Typography variant="subtitle2" sx={{ px: 2, py: 1.25, borderBottom: 1, borderColor: 'divider' }}>{inbox ? 'Conversations' : 'Conversations in your sites'}</Typography>
            <List dense disablePadding data-testid="conversation-list" sx={{ overflow: 'auto', flex: 1 }}>
              {(list.data ?? []).length === 0 && <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>{list.isLoading ? 'Loading…' : 'No conversations yet.'}</Typography>}
              {(list.data ?? []).map((c) => (
                <ListItemButton key={c.id} selected={c.id === selected} onClick={() => setSelected(c.id)} data-testid="conversation-item" data-unread={c.unread ? 'true' : 'false'} data-conversation-id={c.id}
                  sx={{ alignItems: 'flex-start', borderLeft: 3, borderColor: c.unread ? 'primary.main' : 'transparent', bgcolor: c.unread ? 'action.selected' : undefined }}>
                  <ListItemText
                    primary={c.heading}
                    secondary={c.preview || 'No messages yet'}
                    slotProps={{ primary: { fontWeight: c.unread ? 700 : 500, noWrap: true }, secondary: { noWrap: true } }} />
                  <Typography variant="caption" color="text.secondary" sx={{ ml: 1, whiteSpace: 'nowrap' }}>{fmtDateTime(c.lastMessageAt)}</Typography>
                </ListItemButton>
              ))}
            </List>
          </Paper>
          <Paper variant="outlined" sx={{ display: 'flex', flexDirection: 'column', minHeight: 360, maxHeight: { md: 'calc(100vh - 260px)' }, overflow: 'hidden' }} data-testid="message-thread">
            {!selected && <Stack sx={{ flex: 1, alignItems: 'center', justifyContent: 'center', p: 3 }}><Typography color="text.secondary">Select a conversation{inbox ? ', or start a new one' : ''}.</Typography></Stack>}
            {selected && thread.error && <Alert severity="error" sx={{ m: 2 }}>{(thread.error as Error).message}</Alert>}
            {selected && thread.data && <ThreadView thread={thread.data} mine={me?.id} readOnly={!inbox || !thread.data.canSend} onChanged={refresh} />}
            {inbox && selected && thread.data?.canSend && <Composer testId="reply-body" onSend={async (body, linkKind, linkId) => {
              await post(`/api/messages/conversations/${selected}/messages`, { body, linkKind: linkKind || null, linkId });
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
  return (
    <Stack sx={{ flex: 1, minHeight: 0 }}>
      <Box sx={{ px: 2, py: 1.25, borderBottom: 1, borderColor: 'divider' }}>
        <Typography variant="subtitle1" fontWeight={700}>{thread.heading}</Typography>
        <Typography variant="caption" color="text.secondary">{thread.participants.map((p) => p.displayName).join(', ')}</Typography>
      </Box>
      {error && <Alert severity="error" sx={{ m: 1 }}>{error}</Alert>}
      <Stack ref={log} gap={1.25} sx={{ flex: 1, overflow: 'auto', p: 2 }} data-testid="message-log">
        {thread.messages.map((msg) => {
          const own = msg.senderId === mine;
          return (
            <Stack key={msg.id} data-testid="message-item" data-own={own ? 'true' : 'false'} data-deleted={msg.deleted ? 'true' : 'false'} sx={{ maxWidth: '80%', alignSelf: own ? 'flex-end' : 'flex-start' }}>
              <Typography variant="caption" color="text.secondary" data-testid="message-meta">{msg.senderName} · <span data-testid="message-when">{fmtDateTime(msg.createdAt)}</span>{msg.editedAt && !msg.deleted ? ' · edited' : ''}</Typography>
              <Paper variant="outlined" sx={{ px: 1.5, py: 1, bgcolor: own ? 'action.selected' : 'background.paper' }}>
                {editing?.id === msg.id ? (
                  <Stack gap={1}>
                    <TextField multiline size="small" value={draft} onChange={(e) => setDraft(e.target.value)} inputProps={{ 'data-testid': 'edit-message-body' }} />
                    <Stack direction="row" gap={1} justifyContent="flex-end">
                      <Button size="small" onClick={() => setEditing(null)}>Cancel</Button>
                      <Button size="small" variant="contained" onClick={async () => { setError(null); try { await put(`/api/messages/messages/${msg.id}`, { body: draft }); setEditing(null); onChanged(); } catch (e) { setError((e as Error).message); } }}>Save</Button>
                    </Stack>
                  </Stack>
                ) : <>
                    {msg.deleted && <Typography variant="caption" color="text.secondary" fontStyle="italic" sx={{ display: 'block' }}>{msg.body ? 'Deleted' : 'This message was deleted.'}</Typography>}
                    {msg.body && <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>{msg.body}</Typography>}
                    {msg.linkPath && msg.linkLabel && <Link component={RouterLink} to={msg.linkPath} data-testid="message-link" sx={{ display: 'inline-block', mt: 0.5 }}>{msg.linkLabel}</Link>}
                  </>}
              </Paper>
              {own && !readOnly && !msg.deleted && editing?.id !== msg.id && (
                <Stack direction="row" gap={0.5} justifyContent="flex-end">
                  <Tooltip title="Edit"><IconButton size="small" aria-label="Edit message" data-testid="edit-message" onClick={() => { setEditing(msg); setDraft(msg.body ?? ''); }}><EditOutlined fontSize="small" /></IconButton></Tooltip>
                  <Tooltip title="Delete"><IconButton size="small" aria-label="Delete message" data-testid="delete-message" onClick={() => setRemoving(msg)}><DeleteOutline fontSize="small" /></IconButton></Tooltip>
                </Stack>
              )}
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
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const group = picked.length >= 2;
  const send = async () => {
    setError(null);
    setBusy(true);
    try {
      const r = await post<{ id: string }>('/api/messages/conversations', {
        recipientIds: picked.map((p) => p.id), title: group ? title : null, body, linkKind: kind || null, linkId: record?.id ?? null,
      });
      onCreated(r.id);
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
            renderInput={(p) => <TextField {...p} label="To" placeholder="Choose people" data-testid="message-recipients" />}
            renderOption={(props, o) => <li {...props}><Box><Typography variant="body2">{o.displayName}</Typography><Typography variant="caption" color="text.secondary">{o.email}</Typography></Box></li>} />
          {group && <TextField label="Group name" value={title} onChange={(e) => setTitle(e.target.value)} inputProps={{ 'data-testid': 'group-name' }} helperText="A named thread for several people." />}
          <TextField multiline minRows={3} label="Message" value={body} onChange={(e) => setBody(e.target.value)} inputProps={{ 'data-testid': 'new-message-body' }} />
          <LinkFields kind={kind} onKind={setKind} record={record} onRecord={setRecord} />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={busy || picked.length === 0 || !body.trim() || (group && !title.trim())} onClick={send} data-testid="new-message-send">Send</Button>
      </DialogActions>
    </Dialog>
  );
}
