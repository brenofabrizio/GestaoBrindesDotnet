const VERSION = 'gestao-brindes-dotnet-v1';
const STATIC_CACHE = VERSION + '-static';
const OFFLINE_URL = '/gbrindes/offline.html';
const STATIC_ASSETS = [
  '/gbrindes/manifest.webmanifest',
  '/gbrindes/offline.html',
  '/gbrindes/css/offline.css',
  '/gbrindes/js/offline.js',
  '/gbrindes/css/site.css',
  '/gbrindes/css/auth.css',
  '/gbrindes/lib/bootstrap/dist/css/bootstrap.min.css',
  '/gbrindes/lib/bootstrap/dist/js/bootstrap.bundle.min.js',
  '/gbrindes/lib/bootstrap-icons/bootstrap-icons.min.css'
];
self.addEventListener('install', event => {
  event.waitUntil(caches.open(STATIC_CACHE)
    .then(cache => Promise.allSettled(STATIC_ASSETS.map(asset => cache.add(asset))))
    .then(() => self.skipWaiting()));
});
self.addEventListener('activate', event => {
  event.waitUntil(caches.keys()
    .then(keys => Promise.all(keys.filter(key => key !== STATIC_CACHE).map(key => caches.delete(key))))
    .then(() => self.clients.claim()));
});
self.addEventListener('fetch', event => {
  const request = event.request;
  const url = new URL(request.url);
  if (request.method !== 'GET' || url.origin !== self.location.origin || !url.pathname.startsWith('/gbrindes/')) return;
  if (request.mode === 'navigate') {
    event.respondWith(fetch(request, { cache: 'no-store' }).catch(() => caches.match(OFFLINE_URL)));
    return;
  }
  if (url.pathname.startsWith('/gbrindes/api/')) {
    event.respondWith(fetch(request, { cache: 'no-store' }));
    return;
  }
  event.respondWith(caches.match(request).then(cached => cached || fetch(request).then(response => {
    if (response.ok) caches.open(STATIC_CACHE).then(cache => cache.put(request, response.clone()));
    return response;
  })));
});
self.addEventListener('message', event => {
  if (event.data?.type === 'clear-caches') event.waitUntil(caches.delete(STATIC_CACHE));
});
