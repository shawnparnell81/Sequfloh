/** Position from a part's attributes JSON, shown beside the part number because several parts can share one part number. */
export function partPosition(attributes?: string | Record<string, string> | null): string {
  if (!attributes) return '';
  try { const a = typeof attributes === 'string' ? JSON.parse(attributes) : attributes; return typeof a?.position === 'string' ? a.position : ''; } catch { return ''; }
}
/** "Part number (Position)" when the part has a Position, else the part number. */
export const partNo = (p: { partNumber: string; attributes?: string | Record<string, string> | null; matchedOn?: string; masterPartNumber?: string }) => {
  const pos = partPosition(p.attributes);
  // a search match on another number shows that number, what it is and the Part Master number, for example "NP91056C (NAPA) · 81056C"
  const n = p.matchedOn ? `${p.partNumber} (${p.matchedOn}) · ${p.masterPartNumber}` : p.partNumber;
  return pos ? `${n} (${pos})` : n;
};
/** What a part is: its description, else its name (imports set the name to the product family for every row). */
export const partDesc = (p: { name?: string | null; description?: string | null }) => p.description?.trim() || p.name?.trim() || '';
/** A part picker option: the real part number first, then what the part is. */
export const partOption = (p: { partNumber: string; matchedOn?: string; masterPartNumber?: string; name?: string | null; description?: string | null; attributes?: string | Record<string, string> | null }) => {
  const d = partDesc(p);
  return d ? `${partNo(p)} — ${d}` : partNo(p);
};
