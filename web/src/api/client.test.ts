import { describe, expect, it, vi } from 'vitest';
import { ApiError, createApi, toQuery } from './client';

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });

function api(fetchImpl: typeof fetch, token: string | null = 'tok', onUnauthorized = vi.fn()) {
  return { client: createApi({ getToken: () => token, onUnauthorized, fetchImpl }), onUnauthorized };
}

describe('toQuery', () => {
  it('skips empty values and encodes the rest', () => {
    expect(toQuery({ q: 'a b', page: 2, empty: '', none: undefined, nil: null, on: true, off: false })).toBe('?q=a+b&page=2&on=true&off=false');
    expect(toQuery()).toBe('');
    expect(toQuery({ a: undefined })).toBe('');
  });
});

describe('createApi', () => {
  it('sends the bearer token and reads JSON', async () => {
    const fetchImpl = vi.fn(async () => json(200, { ok: 1 }));
    const { client } = api(fetchImpl as unknown as typeof fetch);

    expect(await client.get('/assets', { q: 'x' })).toEqual({ ok: 1 });

    const [url, init] = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/v1/assets?q=x');
    expect(new Headers(init.headers).get('Authorization')).toBe('Bearer tok');
  });

  it('sends no Authorization header when signed out', async () => {
    const fetchImpl = vi.fn(async () => json(200, {}));
    await api(fetchImpl as unknown as typeof fetch, null).client.get('/me');
    expect(new Headers((fetchImpl.mock.calls[0] as unknown as [string, RequestInit])[1].headers).has('Authorization')).toBe(false);
  });

  it('posts JSON bodies and handles 204 without a body', async () => {
    const fetchImpl = vi.fn(async () => new Response(null, { status: 204 }));
    const result = await api(fetchImpl as unknown as typeof fetch).client.post('/x', { a: 1 });
    expect(result).toBeUndefined();
    const init = (fetchImpl.mock.calls[0] as unknown as [string, RequestInit])[1];
    expect(init.method).toBe('POST');
    expect(init.body).toBe('{"a":1}');
    expect(new Headers(init.headers).get('Content-Type')).toBe('application/json');
  });

  it('turns problem details into an ApiError with field errors', async () => {
    const fetchImpl = vi.fn(async () => json(400, { title: 'Validation failed', errors: { code: ['Code is required.'], 'customFields.tons': ['Must be a number.'] } }));
    const error = await api(fetchImpl as unknown as typeof fetch).client.post('/locations', {}).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    const apiError = error as ApiError;
    expect(apiError.status).toBe(400);
    expect(apiError.field('code')).toBe('Code is required.');
    expect(apiError.field('customFields.tons')).toBe('Must be a number.');
    expect(apiError.field('missing')).toBeUndefined();
  });

  it('classifies conflicts, forbidden and not found', async () => {
    for (const [status, flag] of [[409, 'isConflict'], [403, 'isForbidden'], [404, 'isNotFound']] as const) {
      const error = (await api((async () => json(status, { title: 't' })) as unknown as typeof fetch).client.get('/x').catch((e: unknown) => e)) as ApiError;
      expect(error[flag]).toBe(true);
    }
  });

  it('tells the app when the token is rejected', async () => {
    const { client, onUnauthorized } = api((async () => json(401, { title: 'Unauthorized' })) as unknown as typeof fetch);
    await expect(client.get('/me')).rejects.toMatchObject({ status: 401 });
    expect(onUnauthorized).toHaveBeenCalledOnce();
  });

  it('survives a non-JSON error body such as a proxy page', async () => {
    const { client } = api((async () => new Response('<html>Bad gateway</html>', { status: 502, statusText: 'Bad Gateway' })) as unknown as typeof fetch);
    await expect(client.get('/x')).rejects.toMatchObject({ status: 502, title: 'Bad Gateway' });
  });

  it('reports network failure as status 0', async () => {
    const { client } = api((async () => { throw new TypeError('Failed to fetch'); }) as unknown as typeof fetch);
    await expect(client.get('/x')).rejects.toMatchObject({ status: 0 });
  });

  it('reads the file name from RFC 5987 and plain Content-Disposition headers', async () => {
    const star = api((async () => new Response('x', { headers: { 'Content-Disposition': "attachment; filename=plain.pdf; filename*=UTF-8''%D8%AF%D9%84%D9%8A%D9%84.pdf" } })) as unknown as typeof fetch);
    expect((await star.client.blob('/d')).fileName).toBe('دليل.pdf');
    const plain = api((async () => new Response('x', { headers: { 'Content-Disposition': 'attachment; filename="report.csv"' } })) as unknown as typeof fetch);
    expect((await plain.client.blob('/d')).fileName).toBe('report.csv');
    const none = api((async () => new Response('x')) as unknown as typeof fetch);
    expect((await none.client.blob('/d')).fileName).toBeNull();
  });
});
