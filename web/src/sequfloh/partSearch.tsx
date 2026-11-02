import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { get } from '../api';
import { S, qs } from './common';


export const PART_SEARCH_PAGE = 25;

/** The numbers a picker can put on an order, in the order they are preferred when several match. NAPA PN, NAPA US # and NAPA CA # count as one NAPA number. */
const NUMBER_FIELDS: [string, string][] = [['orNumber', 'OR #'], ['napaPn', 'NAPA'], ['napaUs', 'NAPA'], ['napaCa', 'NAPA'], ['nissanVa', 'Nissan VA #'], ['dmaPn', 'DMA PN'], ['cross', 'Cross'], ['jhPn', 'JH PN']];

type Numbered = { partNumber: string; drawingNumber?: string | null; upc?: string | null; attributes?: string | Record<string, string> | null };

/**
 * The number of the part that matches what was typed: an exact match first, then a partial one; the part number first, then the other numbers.
 * Null when the part number itself matches or nothing does, so the part number is used.
 */
export function matchedNumber(p: Numbered, text: string): { number: string; label: string } | null {
  const t = text.trim().toLowerCase();
  if (!t) return null;
  let a: Record<string, string> = {};
  try { a = typeof p.attributes === 'string' ? JSON.parse(p.attributes) : (p.attributes ?? {}); } catch { /* not JSON */ }
  const all: [string, string][] = [[p.partNumber, ''], ...NUMBER_FIELDS.map(([k, l]) => [a?.[k] ?? '', l] as [string, string]), [p.drawingNumber ?? '', 'DWG #'], [p.upc ?? '', 'UPC']];
  const vals = all.map(([v, l]) => [String(v ?? '').trim(), l] as [string, string]).filter(([v]) => v && v !== '/');
  const hit = vals.find(([v]) => v.toLowerCase() === t) ?? vals.find(([v]) => v.toLowerCase().includes(t));
  return hit && hit[1] ? { number: hit[0], label: hit[1] } : null;
}

/** A search result with the matched number as its part number (what goes on the order); the Part Master number is kept in masterPartNumber. */
export type Matched<T> = T & { masterPartNumber?: string; matchedOn?: string };
function withMatch<T extends Numbered>(p: T, text: string): Matched<T> {
  const m = matchedNumber(p, text);
  return m ? { ...p, partNumber: m.number, masterPartNumber: p.partNumber, matchedOn: m.label } : p;
}

/** Text typed into a part picker, settled for a moment so each keystroke does not query the server. */
export function useSettled(text: string, ms = 250) {
  const [v, setV] = useState(text);
  useEffect(() => { const t = setTimeout(() => setV(text), ms); return () => clearTimeout(t); }, [text, ms]);
  return v;
}

/**
 * Server-side part search over every part in every family. The server matches the OR #, DWG #, name, description and every part-number field
 * (NAPA, DMA, JH, Nissan and so on), so any part can be found however many there are; only the closest page is sent.
 */
export function usePartSearch<T>(text: string, extra: Record<string, string | undefined> = {}, enabled = true) {
  const q = useSettled(text.trim());
  // nothing is listed until the user types; then the server searches every number field as they type
  const r = useQuery({
    queryKey: ['sqf-part-search', q, extra], enabled: enabled && q.length > 0,
    queryFn: () => get<{ items: T[]; total: number }>(`${S}/parts?${qs({ ...extra, q: q || undefined, pageSize: PART_SEARCH_PAGE, sort: 'partNumber:asc' })}`),
  });
  const total = q ? r.data?.total ?? 0 : 0, items = (q ? r.data?.items ?? [] : []).map((p) => withMatch(p as T & Numbered, q)) as Matched<T>[];
  const hint = total > items.length ? `Showing ${items.length} of ${total.toLocaleString('en-US')} parts. Type a part number, DWG #, NAPA, DMA or JH PN to find any part.` : undefined;
  // still typing (not yet settled) counts as searching, so "No matching part" is shown only after the server answered
  const loading = (text.trim().length > 0 && text.trim() !== q) || (q.length > 0 && r.isFetching && !r.data);
  return { items, total, loading, hint };
}

/** The other numbers of a part, shown under its OR # so a match on a NAPA or DMA PN is visible. */
export function otherNumbers(p: { drawingNumber?: string | null; attributes?: string | Record<string, string> | null }) {
  let a: Record<string, string> = {};
  try { a = typeof p.attributes === 'string' ? JSON.parse(p.attributes) : (p.attributes ?? {}); } catch { /* not JSON */ }
  return [p.drawingNumber, ...Object.entries(a ?? {}).filter(([k]) => k !== 'keyApplication' && k !== 'position' && k !== 'boxSize').map(([, v]) => v)].filter(Boolean).join(' · ');
}
