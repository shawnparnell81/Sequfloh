export class ApiError extends Error {
  constructor(public status: number, message: string, public errors?: Record<string, string[]>, public code?: string, public body?: Record<string, unknown>) {
    super(message);
  }
}

/** Calls the API with the session cookie. A free host may be waking up, so a request that cannot connect is tried again a few times. */
export async function api<T = unknown>(method: string, url: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {};
  const isForm = body instanceof FormData;
  if (body !== undefined && !isForm) headers['Content-Type'] = 'application/json';
  const send = () => fetch(url, { method, headers, credentials: 'same-origin', body: body === undefined ? undefined : isForm ? body : JSON.stringify(body) });
  let r: Response | null = null;
  for (let attempt = 0; ; attempt++) {
    try {
      r = await send();
      if ((r.status === 502 || r.status === 503 || r.status === 504) && method === 'GET' && attempt < 4) { await new Promise((ok) => setTimeout(ok, 3000 * (attempt + 1))); continue; }
      break;
    } catch (e) {
      if (attempt >= 4 || method !== 'GET') throw e;
      await new Promise((ok) => setTimeout(ok, 3000 * (attempt + 1)));
    }
  }
  if (r.status === 401 && !url.startsWith('/api/auth/')) window.dispatchEvent(new Event('sqf:signed-out'));
  if (!r.ok) {
    const p = await r.json().catch(() => ({}));
    throw new ApiError(r.status, p.title ?? `Request failed (${r.status})`, p.errors, p.code, p);
  }
  if (method !== 'GET') window.dispatchEvent(new Event('sqf:saved'));
  if (r.status === 204) return undefined as T;
  const text = await r.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const get = <T,>(url: string) => api<T>('GET', url);
export const post = <T = unknown,>(url: string, body?: unknown) => api<T>('POST', url, body ?? {});
export const put = <T = unknown,>(url: string, body: unknown) => api<T>('PUT', url, body);
export const del = <T = unknown,>(url: string) => api<T>('DELETE', url);

/** POST that returns a file (PDF previews and exports). */
export async function postBlob(url: string, body: unknown): Promise<Blob> {
  const r = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, credentials: 'same-origin', body: JSON.stringify(body) });
  if (!r.ok) {
    const p = await r.json().catch(() => ({}));
    throw new ApiError(r.status, p.title ?? `Request failed (${r.status})`, p.errors, p.code, p);
  }
  return r.blob();
}

/** GET that returns a file (rendered previews). */
export async function getBlob(url: string): Promise<Blob> {
  const r = await fetch(url, { credentials: 'same-origin' });
  if (!r.ok) {
    const p = await r.json().catch(() => ({}));
    throw new ApiError(r.status, p.title ?? `Request failed (${r.status})`, p.errors, p.code, p);
  }
  return r.blob();
}
