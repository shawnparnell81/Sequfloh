import { useState, type ReactNode } from 'react';
import { Button, Divider, IconButton, ListItemText, Menu, MenuItem, Stack, Tooltip } from '@mui/material';
import MoreHoriz from '@mui/icons-material/MoreHoriz';
import PrintOutlined from '@mui/icons-material/PrintOutlined';
import { Confirm, useDeleteAction } from '../sequfloh/common';
import type { RecordSurface } from './recordSurfaces';
import { FullScreenButton } from './FullScreen';

const LEAVE = 'You have unsaved changes. Leave this page without saving them?';

/**
 * Asks before a link click leaves a page that has unsaved changes. The page marks itself with data-unsaved="true" (RecordActions does that when
 * it is told the record is dirty); only the page in the active tab counts. Closing or reloading the browser tab is covered by useUnsaved.
 */
function installLeaveGuard() {
  if (typeof document === 'undefined' || (window as unknown as { __sqfLeaveGuard?: boolean }).__sqfLeaveGuard) return;
  (window as unknown as { __sqfLeaveGuard?: boolean }).__sqfLeaveGuard = true;
  document.addEventListener('click', (e) => {
    if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
    const a = (e.target as Element | null)?.closest?.('a[href]') as HTMLAnchorElement | null;
    if (!a || a.target === '_blank' || a.hasAttribute('download')) return;
    const url = new URL(a.href, location.href);
    if (url.origin !== location.origin || (url.pathname === location.pathname && url.search === location.search)) return;
    const view = document.querySelector('[data-testid^="tab-view-"][data-active="true"]') ?? document.body;
    const dirty = view.querySelector('[data-unsaved="true"]');
    if (!dirty) return;
    if (!window.confirm(LEAVE)) { e.preventDefault(); e.stopPropagation(); }
  }, true);
}
installLeaveGuard();

/** A rare or destructive action that lives in the More menu. */
export interface MoreAction { label: string; onClick: () => void; danger?: boolean; testid?: string }

export interface DeleteProps { what: string; url: string; to?: string; perm?: string; extra?: string; onDone?: () => void; show?: boolean; label?: string }

/**
 * The one action bar for records: Edit, Save, Cancel, Print, Full screen, then the page's main action (Import, Upload, Release, ...) and More, always in that order and with the same labels. Each action shows
 * only when the page passes it. Delete names the item in its confirmation, is checked against the permissions an administrator assigned (no
 * fixed roles) and is recorded by the server. Save and Cancel know about unsaved changes: Cancel asks before throwing them away, and leaving the
 * page with unsaved changes asks first. Page-specific actions (Release, Convert to work order, Upload drawing, ...) go in children and sit after Full screen, just before More, on every page.
 * Rare and destructive actions (Archive, Delete) never sit in the row: they are in the More menu at the end, Delete last and in red, and each
 * still asks for confirmation.
 */
export function RecordActions({ surface, dirty = false, busy = false, edit, save, cancel, print, importNode, del, more = [], children }: {
  surface: RecordSurface; dirty?: boolean; busy?: boolean;
  edit?: { onClick: () => void; label?: string } | false;
  save?: { onClick: () => void; label?: string; disabled?: boolean } | false;
  cancel?: { onClick: () => void; label?: string; always?: boolean } | false;
  print?: { href?: string; onClick?: () => void; disabled?: boolean; testid?: string } | false;
  importNode?: ReactNode; del?: DeleteProps | false; more?: (MoreAction | false | null | undefined)[]; children?: ReactNode;
}) {
  const [ask, setAsk] = useState(false);
  const [menu, setMenu] = useState<HTMLElement | null>(null);
  const d = useDeleteAction(del || { what: '', url: '', show: false });
  const items = more.filter((m): m is MoreAction => !!m);
  const hasMore = items.length > 0 || d.allowed;
  const pick = (f: () => void) => () => { setMenu(null); f(); };
  return (
    <Stack direction="row" gap={1} flexWrap="wrap" alignItems="center" data-record-actions={surface} data-unsaved={dirty ? 'true' : undefined}>
      {edit && <Button variant="outlined" data-action="edit" disabled={busy} onClick={edit.onClick}>{edit.label ?? 'Edit'}</Button>}
      {save && <Button variant="contained" data-action="save" disabled={busy || save.disabled || !dirty} onClick={save.onClick}>{save.label ?? 'Save'}</Button>}
      {cancel && <Button data-action="cancel" disabled={busy || (!dirty && !cancel.always)} onClick={() => (dirty ? setAsk(true) : cancel.onClick())}>{cancel.label ?? 'Cancel'}</Button>}
      {print && <Button variant="outlined" data-action="print" startIcon={<PrintOutlined />} data-testid={print.testid} {...(print.href ? { href: print.href, target: '_blank', rel: 'noopener' } : { onClick: print.onClick })} disabled={print.disabled || dirty}>Print</Button>}
      <FullScreenButton />
      {importNode && <span data-action="import" style={{ display: 'contents' }}>{importNode}</span>}
      {children && <span data-action="primary" style={{ display: 'contents' }}>{children}</span>}
      {hasMore && <span data-action="more" style={{ display: 'contents' }}>
        <Tooltip title="More actions"><IconButton aria-label="More actions" data-testid="more-actions" aria-haspopup="menu" aria-expanded={!!menu} onClick={(e) => setMenu(e.currentTarget)}
          sx={{ border: 1, borderColor: 'divider', borderRadius: 2 }}><MoreHoriz /></IconButton></Tooltip>
        <Menu anchorEl={menu} open={!!menu} onClose={() => setMenu(null)} anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }} transformOrigin={{ vertical: 'top', horizontal: 'right' }}>
          {items.map((m) => <MenuItem key={m.label} data-testid={m.testid} data-more-action={m.label} onClick={pick(m.onClick)} sx={m.danger ? { color: 'error.main' } : undefined}><ListItemText>{m.label}</ListItemText></MenuItem>)}
          {d.allowed && items.length > 0 && <Divider />}
          {d.allowed && <MenuItem data-action="delete" data-testid="delete-button" onClick={pick(d.start)} sx={{ color: 'error.main' }}><ListItemText>{(del && del.label) || 'Delete'}</ListItemText></MenuItem>}
        </Menu>
      </span>}
      {d.dialog}
      {cancel && <Confirm open={ask} title="Discard unsaved changes?" confirm="Discard changes" danger text="Your changes since the last save will be lost."
        onClose={() => setAsk(false)} onConfirm={async () => { setAsk(false); cancel.onClick(); }} />}
    </Stack>
  );
}
