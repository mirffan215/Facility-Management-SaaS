/** A failed API call, with the RFC 7807 fields the UI needs. */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly title: string,
    public readonly fieldErrors: Record<string, string[]> = {},
  ) {
    super(title);
    this.name = 'ApiError';
  }

  /** The first message for a field, or undefined. */
  field(name: string): string | undefined {
    return this.fieldErrors[name]?.[0];
  }

  get isConflict() {
    return this.status === 409;
  }

  get isForbidden() {
    return this.status === 403;
  }

  get isNotFound() {
    return this.status === 404;
  }
}

export interface ApiOptions {
  /** Returns the current access token, or null when signed out. */
  getToken: () => string | null;
  /** Called when the server says the token is no longer accepted. */
  onUnauthorized: () => void;
  fetchImpl?: typeof fetch;
}

type Query = Record<string, string | number | boolean | undefined | null>;

export function toQuery(params?: Query): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params ?? {})) {
    if (value !== undefined && value !== null && value !== '') search.set(key, String(value));
  }
  const text = search.toString();
  return text ? `?${text}` : '';
}

export async function toApiError(response: Response): Promise<ApiError> {
  let title = response.statusText || `HTTP ${response.status}`;
  let fieldErrors: Record<string, string[]> = {};
  try {
    const body = (await response.json()) as { title?: string; errors?: Record<string, string[]> };
    if (body.title) title = body.title;
    if (body.errors) fieldErrors = body.errors;
  } catch {
    // Not JSON (e.g. a proxy error page): keep the status text.
  }
  return new ApiError(response.status, title, fieldErrors);
}

export function createApi(options: ApiOptions) {
  const doFetch = options.fetchImpl ?? ((input: RequestInfo | URL, init?: RequestInit) => fetch(input, init));

  async function request(path: string, init: RequestInit = {}): Promise<Response> {
    const token = options.getToken();
    const headers = new Headers(init.headers);
    if (token) headers.set('Authorization', `Bearer ${token}`);
    let response: Response;
    try {
      response = await doFetch(`/api/v1${path}`, { ...init, headers });
    } catch {
      throw new ApiError(0, 'network');
    }
    if (response.status === 401) options.onUnauthorized();
    if (!response.ok) throw await toApiError(response);
    return response;
  }

  const json = (method: string, path: string, body?: unknown) =>
    request(path, {
      method,
      headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });

  /**
   * A call for the offline sync engine: adds the token but never throws for a status and never signs the person out on 401 (their queued work
   * must survive an expired session). Throws only when the network is unreachable.
   */
  async function raw(path: string, init: RequestInit = {}): Promise<Response> {
    const token = options.getToken();
    const headers = new Headers(init.headers);
    if (token) headers.set('Authorization', `Bearer ${token}`);
    else headers.set('X-No-Token', '1'); // lets the caller tell "not signed in" from a server 401
    return doFetch(`/api/v1${path}`, { ...init, headers });
  }

  return {
    raw,
    async get<T>(path: string, params?: Query): Promise<T> {
      const response = await request(path + toQuery(params));
      return (response.status === 204 ? undefined : await response.json()) as T;
    },
    async post<T>(path: string, body?: unknown, params?: Query): Promise<T> {
      const response = await json('POST', path + toQuery(params), body ?? {});
      return (response.status === 204 ? undefined : await response.json()) as T;
    },
    async put<T>(path: string, body: unknown): Promise<T> {
      const response = await json('PUT', path, body);
      return (response.status === 204 ? undefined : await response.json()) as T;
    },
    async delete<T>(path: string): Promise<T> {
      const response = await request(path, { method: 'DELETE' });
      return (response.status === 204 ? undefined : await response.json()) as T;
    },
    async upload<T>(path: string, form: FormData, params?: Query): Promise<T> {
      return (await request(path + toQuery(params), { method: 'POST', body: form })).json() as Promise<T>;
    },
    /** Fetches a file with the bearer token (a plain link could not carry it). */
    async blob(path: string): Promise<{ blob: Blob; fileName: string | null }> {
      const response = await request(path);
      const disposition = response.headers.get('Content-Disposition') ?? '';
      const star = /filename\*=UTF-8''([^;]+)/i.exec(disposition);
      const plain = /filename="?([^";]+)"?/i.exec(disposition);
      return { blob: await response.blob(), fileName: star ? decodeURIComponent(star[1]) : (plain?.[1] ?? null) };
    },
    async text(path: string): Promise<string> {
      return (await request(path)).text();
    },
  };
}

export type Api = ReturnType<typeof createApi>;
