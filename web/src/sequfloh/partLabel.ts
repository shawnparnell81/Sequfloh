/** Position from a part's attributes JSON, shown beside the OR # because several parts can share one OR #. */
export function partPosition(attributes?: string | Record<string, string> | null): string {
  if (!attributes) return '';
  try { const a = typeof attributes === 'string' ? JSON.parse(attributes) : attributes; return typeof a?.position === 'string' ? a.position : ''; } catch { return ''; }
}
/** "OR # (Position)" when the part has a Position, else the OR #. */
export const partNo = (p: { partNumber: string; attributes?: string | Record<string, string> | null }) => {
  const pos = partPosition(p.attributes);
  return pos ? `${p.partNumber} (${pos})` : p.partNumber;
};
