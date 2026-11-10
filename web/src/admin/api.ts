/** Thin fetch client for /api/admin: same-origin cookies, double-submit XSRF header, ProblemDetails errors. */

export class ApiError extends Error {
  readonly status: number;
  readonly errors?: Record<string, string[]>;

  constructor(status: number, message: string, errors?: Record<string, string[]>) {
    super(message);
    this.status = status;
    this.errors = errors;
  }
}

const XSRF_COOKIE = 'XSRF-TOKEN';

function readXsrf(): string | undefined {
  return document.cookie
    .split('; ')
    .find((c) => c.startsWith(`${XSRF_COOKIE}=`))
    ?.slice(XSRF_COOKIE.length + 1);
}

/** Fetches a fresh antiforgery token (needed before the first write and again after sign-in). */
export async function refreshXsrf(): Promise<void> {
  await fetch('/api/admin/antiforgery', { credentials: 'same-origin', cache: 'no-store' });
}

async function errorFrom(response: Response): Promise<ApiError> {
  try {
    const problem = (await response.json()) as { title?: string; detail?: string; errors?: Record<string, string[]> };
    const firstError = problem.errors ? Object.values(problem.errors)[0]?.[0] : undefined;
    return new ApiError(response.status, firstError ?? problem.detail ?? problem.title ?? response.statusText, problem.errors);
  } catch {
    return new ApiError(response.status, response.statusText || `Request failed (${response.status})`);
  }
}

export async function api<T>(path: string, init: { method?: string; body?: unknown; signal?: AbortSignal } = {}): Promise<T> {
  const method = init.method ?? 'GET';
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (init.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  if (method !== 'GET') {
    if (!readXsrf()) {
      await refreshXsrf();
    }
    headers['X-XSRF-TOKEN'] = decodeURIComponent(readXsrf() ?? '');
  }

  const send = () =>
    fetch(`/api/admin${path}`, {
      method,
      headers,
      credentials: 'same-origin',
      cache: 'no-store',
      body: init.body === undefined ? undefined : JSON.stringify(init.body),
      signal: init.signal,
    });

  let response = await send();
  if (response.status === 400 && method !== 'GET') {
    // The token may predate a sign-in (tokens are bound to the user): refresh once and retry.
    const retryable = response.clone();
    const text = await retryable.text();
    if (text.includes('X-XSRF-TOKEN')) {
      await refreshXsrf();
      headers['X-XSRF-TOKEN'] = decodeURIComponent(readXsrf() ?? '');
      response = await send();
    }
  }

  if (!response.ok) {
    throw await errorFrom(response);
  }

  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  const exponent = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)));
  const value = bytes / 1024 ** exponent;
  return `${value >= 100 || exponent === 0 ? value.toFixed(0) : value.toFixed(1)} ${units[exponent]}`;
}

export function formatDuration(ms?: number | null): string {
  if (!ms) return '';
  const total = Math.round(ms / 1000);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}` : `${m}:${String(s).padStart(2, '0')}`;
}

export function formatDate(iso?: string | null): string {
  if (!iso) return '';
  return new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}
