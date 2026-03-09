// ── LostDogTracer Service Worker ──────────────────────────────────
const CACHE_NAME = 'lostdogtracer-v1';

const STATIC_ASSETS = [
    '/',
    '/index.html',
    '/my-home.html',
    '/guest-home.html',
    '/guest-records.html',
    '/guest-map.html',
    '/css/shared.css',
    '/css/style.css',
    '/css/admin.css',
    '/css/map.css',
    '/js/theme.js',
    '/js/app.js',
    '/js/auth.js',
    '/js/main-nav.js',
    '/js/offline-store.js',
    '/manifest.json'
];

// Install: cache static assets
self.addEventListener('install', event => {
    event.waitUntil(
        caches.open(CACHE_NAME)
            .then(cache => cache.addAll(STATIC_ASSETS))
            .then(() => self.skipWaiting())
    );
});

// Activate: clean old caches
self.addEventListener('activate', event => {
    event.waitUntil(
        caches.keys().then(keys =>
            Promise.all(keys.filter(k => k !== CACHE_NAME).map(k => caches.delete(k)))
        ).then(() => self.clients.claim())
    );
});

// Fetch: Network-first for API, Cache-first for static assets
self.addEventListener('fetch', event => {
    const url = new URL(event.request.url);

    // Don't cache API calls or POST requests
    if (url.pathname.startsWith('/api') || event.request.method !== 'GET') {
        return; // Let the browser handle it normally
    }

    // For static assets: try cache first, then network
    event.respondWith(
        caches.match(event.request).then(cached => {
            if (cached) return cached;
            return fetch(event.request).then(response => {
                // Cache new static resources dynamically
                if (response.ok && response.type === 'basic') {
                    const clone = response.clone();
                    caches.open(CACHE_NAME).then(cache => cache.put(event.request, clone));
                }
                return response;
            });
        }).catch(() => {
            // Offline fallback for navigation requests
            if (event.request.mode === 'navigate') {
                return caches.match('/my-home.html');
            }
        })
    );
});
