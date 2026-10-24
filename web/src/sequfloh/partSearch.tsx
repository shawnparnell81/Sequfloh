import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { get } from '../api';
import { S, qs } from './common';


export const PART_SEARCH_PAGE = 25;

/** Text typed into a part picker, settled for a moment so each keystroke does not query the server. */
export function useSettled(text: string, ms = 250) {
  const [v, setV] = useState(text);
  useEffect(() => { const t = setTimeout(() => setV(text), ms); return () => clearTimeout(t); }, [text, ms]);
  return v;
}

/**
 * Server-side part search over every part in every family. The server matches the OR #, SENSEN #, name, description and every part-number field
 * (NAPA, DMA, JH, Nissan and so on), so any part can be found however many there are; only the closest page is sent.
 */
export function usePartSearch<T>(text: string, extra: Record<string, string | undefined> = {}, enabled = true) {
  const q = useSettled(text.trim());
  const r = useQuery({
    queryKey: ['sqf-part-search', q, extra], enabled,
    queryFn: () => get<{ items: T[]; total: number }>(`${S}/parts?${qs({ ...extra, q: q || undefined, pageSize: PART_SEARCH_PAGE, sort: 'partNumber:asc' })}`),
  });
  const total = r.data?.total ?? 0, items = r.data?.items ?? [];
  const hint = total > items.length ? `Showing ${items.length} of ${total.toLocaleString('en-US')} parts. Type an OR #, SENSEN #, NAPA, DMA or JH PN to find any part.` : undefined;
  return { items, total, loading: r.isLoading, hint };
}

/** The other numbers of a part, shown under its OR # so a match on a NAPA or DMA PN is visible. */
export function otherNumbers(p: { drawingNumber?: string | null; attributes?: string | Record<string, string> | null }) {
  let a: Record<string, string> = {};
  try { a = typeof p.attributes === 'string' ? JSON.parse(p.attributes) : (p.attributes ?? {}); } catch { /* not JSON */ }
  return [p.drawingNumber, ...Object.entries(a ?? {}).filter(([k]) => k !== 'keyApplication' && k !== 'position' && k !== 'boxSize').map(([, v]) => v)].filter(Boolean).join(' · ');
}
