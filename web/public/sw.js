// Keeps the app itself on the device so it opens without a connection. It never touches /api (data is the app's job: see src/offline) and
// never caches anything from another origin. Plain JavaScript on purpose: no build step, no dependencies, small enough to read.
const CACHE = 'fms-shell-v1';

self.addEventListener('install', (event) => {
  event.waitUntil(
    (async () => {
      const cache = await caches.open(CACHE);
      // The page and the files it names (the build puts them under /static/), so the very first visit is enough.
      const page = await fetch('/', { cache: 'reload' });
      const html = await page.clone().text();
      await cache.put('/', page);
      const files = [...new Set([...html.matchAll(/(?:src|href)="(\/(?:static\/|icon-|apple-touch|manifest)[^"]*)"/g)].map((m) => m[1]))];
      await Promise.all(files.map((f) => cache.add(f).catch(() => undefined)));
      await self.skipWaiting();
    })(),
  );
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    (async () => {
      for (const key of await caches.keys()) if (key !== CACHE) await caches.delete(key);
      await self.clients.claim();
    })(),
  );
});

self.addEventListener('fetch', (event) => {
  const request = event.request;
  const url = new URL(request.url);
  if (request.method !== 'GET' || url.origin !== self.location.origin || url.pathname.startsWith('/api/') || url.pathname.startsWith('/health/')) return;

  // Pages: the network when there is one (so a new release is picked up), the saved page when there is not.
  if (request.mode === 'navigate') {
    event.respondWith(
      (async () => {
        const cache = await caches.open(CACHE);
        try {
          const response = await fetch(request);
          if (response.ok && response.headers.get('content-type')?.includes('text/html')) await cache.put('/', response.clone());
          return response;
        } catch {
          return (await cache.match('/')) ?? Response.error();
        }
      })(),
    );
    return;
  }

  // Built files have names that change with their content, so a saved copy is always right: use it first, save new ones as they appear.
  if (url.pathname.startsWith('/static/') || /^\/(icon-|apple-touch|manifest)/.test(url.pathname)) {
    event.respondWith(
      (async () => {
        const cache = await caches.open(CACHE);
        const saved = await cache.match(request);
        if (saved) return saved;
        const response = await fetch(request);
        if (response.ok) await cache.put(request, response.clone());
        return response;
      })(),
    );
  }
});
