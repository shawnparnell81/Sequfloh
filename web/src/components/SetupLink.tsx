import { forwardRef, useMemo, type HTMLAttributes, type MouseEvent, type ReactNode } from 'react';
import { Box, GlobalStyles, Link, Typography } from '@mui/material';
import AddIcon from '@mui/icons-material/Add';
import { useQueryClient, type QueryKey } from '@tanstack/react-query';
import { useAuth } from '../auth';

/**
 * Setup lists that feed pickers across the app. Each entry names the page where the items are added,
 * the permission that page needs, and the cached queries to refresh when the user comes back.
 */
export type SetupKind = 'site' | 'department' | 'user' | 'sqfPart';
interface SetupList { one: string; many: string; path: string; permission: string | string[]; refresh: QueryKey[] }
export const setupLists: Record<SetupKind, SetupList> = {
  site: { one: 'site', many: 'sites', path: '/admin/sites', permission: 'admin.organization', refresh: [['sites']] },
  department: { one: 'department', many: 'departments', path: '/admin/sites', permission: 'admin.organization', refresh: [['sites']] },
  user: { one: 'user', many: 'users', path: '/admin/users', permission: 'admin.users', refresh: [['userlookup']] },
  sqfPart: { one: 'part', many: 'parts', path: '/sequfloh/parts', permission: ['sequfloh.edit_parts', 'parts.edit'], refresh: [['sqf-part-pick']] },
};

/** Closes the open picker list (and its invisible backdrop) so it does not cover the new tab. */
function closePicker(from: HTMLElement) {
  const list = from.closest('[role="listbox"], .MuiModal-root, .MuiPopover-root');
  list?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
  (document.activeElement as HTMLElement | null)?.blur?.();
}

const cap = (s: string) => s[0].toUpperCase() + s.slice(1);

/** The add page opens in a new browser tab so the form being filled in is never lost. */
function useSetupLink(kind: SetupKind) {
  const { can } = useAuth();
  const qc = useQueryClient();
  const def = setupLists[kind];
  const open = (e: MouseEvent) => {
    e.stopPropagation();
    closePicker(e.currentTarget as HTMLElement);
    // the add page opens in a new browser tab; the list is fetched again when the user comes back
    const refresh = () => { def.refresh.forEach((k) => qc.invalidateQueries({ queryKey: k })); };
    window.addEventListener('focus', refresh, { once: true });
  };
  return { def, allowed: ([] as string[]).concat(def.permission).every(can), open };
}

function AddLink({ kind, def, open }: { kind: SetupKind; def: SetupList; open: (e: MouseEvent) => void }) {
  return (
    <Link href={def.path} target="_blank" rel="noopener" onClick={open} onMouseDown={(e) => e.stopPropagation()} underline="hover" data-testid={`setup-add-${kind}`}
      sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.5, fontSize: 13, fontWeight: 600 }}>
      <AddIcon sx={{ fontSize: 16 }} />Add {def.one}…
    </Link>
  );
}

/* The row is appended after the options but shown first: its list becomes a column and the row takes the first place. */
const topRow = { 'ul:has(> [data-setup-row])': { display: 'flex', flexDirection: 'column' } } as const;

/**
 * Row placed inside a picker's option list. It is not an option: it ignores the select's click handler,
 * sits at the top of the list, shows "No parts yet" when the list is empty, and keeps the add shortcut once items exist.
 */
function SetupRow({ kind, count }: { kind: SetupKind; count: number; value?: string }) {
  const { def, allowed, open } = useSetupLink(kind);
  if (count > 0 && !allowed) return null;
  return (
    <Box role="presentation" data-setup-row="" data-testid={`setup-row-${kind}`} onClick={(e) => e.stopPropagation()} onKeyDown={(e) => e.stopPropagation()}
      sx={{ order: -1, px: 2, py: 1, borderBottom: count > 0 ? 1 : 0, borderColor: 'divider', display: 'flex', flexDirection: 'column', gap: 0.5 }}>
      <GlobalStyles styles={topRow} />
      {count === 0 && <Typography variant="body2" color="text.secondary">No {def.many} yet</Typography>}
      {allowed ? <AddLink kind={kind} def={def} open={open} />
        : <Typography variant="caption" color="text.secondary">Ask an administrator to add {def.many}.</Typography>}
    </Box>
  );
}
SetupRow.muiSkipListHighlight = true;

/** Children to append to a select's options: `{setupOptions('part', parts.data)}`. */
export const setupOptions = (kind: SetupKind, rows: readonly unknown[] | undefined | null): ReactNode[] =>
  [<SetupRow key={`setup-${kind}`} kind={kind} count={rows?.length ?? 0} value="__setup__" />];

/**
 * Listbox for searchable pickers that puts the add shortcut at the top of the open list.
 * Pair it with `noOptionsText={<SetupHint ... count={0} />}` so an empty list shows the note.
 */
export function useSetupListbox(kind: SetupKind) {
  return useMemo(() => forwardRef<HTMLUListElement, HTMLAttributes<HTMLElement> & { ownerState?: unknown }>(function SetupListbox({ children, ownerState: _ownerState, ...props }, ref) {
    return <ul {...props} ref={ref}><li role="presentation"><SetupRow kind={kind} count={1} /></li>{children}</ul>;
  }), [kind]);
}

/** Same shortcut as a line, for searchable pickers and anything that is not a plain select. */
export function SetupHint({ kind, count, fallback }: { kind: SetupKind; count: number; fallback?: string }) {
  const { def, allowed, open } = useSetupLink(kind);
  if (count > 0 && !allowed) return fallback ? <span>{fallback}</span> : null;
  return (
    <Box component="span" data-testid={`setup-hint-${kind}`} sx={{ display: 'inline-flex', flexWrap: 'wrap', gap: 1, alignItems: 'center' }}>
      {count === 0 && <span>No {def.many} yet.</span>}
      {allowed ? <AddLink kind={kind} def={def} open={open} /> : <span>Ask an administrator to add {def.many}.</span>}
    </Box>
  );
}

export const setupLabel = (kind: SetupKind) => cap(setupLists[kind].one);
