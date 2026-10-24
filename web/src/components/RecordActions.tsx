import { useState, type ReactNode } from 'react';
import { Button, Stack } from '@mui/material';
import PrintOutlined from '@mui/icons-material/PrintOutlined';
import { Confirm, DeleteButton } from '../sequfloh/common';
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

export interface DeleteProps { what: string; url: string; to?: string; perm?: string; extra?: string; onDone?: () => void; show?: boolean; label?: string }

/**
 * The one action bar for records: Edit, Save, Cancel, Print, Import, Delete and Full screen, always in that order and with the same labels. Each action shows
 * only when the page passes it. Delete names the item in its confirmation, is checked against the permissions an administrator assigned (no
 * fixed roles) and is recorded by the server. Save and Cancel know about unsaved changes: Cancel asks before throwing them away, and leaving the
 * page with unsaved changes asks first. Page-specific actions (Release, Convert to work order, ...) go in children, before the standard ones.
 */
export function RecordActions({ surface, dirty = false, busy = false, edit, save, cancel, print, importNode, del, children }: {
  surface: RecordSurface; dirty?: boolean; busy?: boolean;
  edit?: { onClick: () => void; label?: string } | false;
  save?: { onClick: () => void; label?: string; disabled?: boolean } | false;
  cancel?: { onClick: () => void; label?: string; always?: boolean } | false;
  print?: { href?: string; onClick?: () => void; disabled?: boolean; testid?: string } | false;
  importNode?: ReactNode; del?: DeleteProps | false; children?: ReactNode;
}) {
  const [ask, setAsk] = useState(false);
  return (
    <Stack direction="row" gap={1} flexWrap="wrap" alignItems="center" data-record-actions={surface} data-unsaved={dirty ? 'true' : undefined}>
      {children}
      {edit && <Button variant="outlined" data-action="edit" disabled={busy} onClick={edit.onClick}>{edit.label ?? 'Edit'}</Button>}
      {save && <Button variant="contained" data-action="save" disabled={busy || save.disabled || !dirty} onClick={save.onClick}>{save.label ?? 'Save'}</Button>}
      {cancel && <Button data-action="cancel" disabled={busy || (!dirty && !cancel.always)} onClick={() => (dirty ? setAsk(true) : cancel.onClick())}>{cancel.label ?? 'Cancel'}</Button>}
      {print && <Button variant="outlined" data-action="print" startIcon={<PrintOutlined />} data-testid={print.testid} {...(print.href ? { href: print.href, target: '_blank', rel: 'noopener' } : { onClick: print.onClick })} disabled={print.disabled || dirty}>Print</Button>}
      {importNode && <span data-action="import" style={{ display: 'contents' }}>{importNode}</span>}
      {del && <span data-action="delete" style={{ display: 'contents' }}><DeleteButton {...del} /></span>}
      <FullScreenButton />
      {cancel && <Confirm open={ask} title="Discard unsaved changes?" confirm="Discard changes" danger text="Your changes since the last save will be lost."
        onClose={() => setAsk(false)} onConfirm={async () => { setAsk(false); cancel.onClick(); }} />}
    </Stack>
  );
}
