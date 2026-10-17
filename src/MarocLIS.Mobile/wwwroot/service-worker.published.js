// MarocLIS.Mobile — the service worker installed on the biologists' phones.
//
// It keeps the APPLICATION and nothing else: the framework files, the page, the stylesheet, the
// fonts and the icons, listed with their hashes in service-worker-assets.js at publish time. The
// application then opens quickly and, without a network, can still say « Hors connexion ».
//
// It never keeps an answer of the API. Every /api request goes straight to the network and is
// never written to a cache: patient data does not stay on the phone, and a screen never shows a
// result older than the moment it was opened. The API answers no-store on top of that.

self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => {
  if (isApi(event.request)) return; // the browser fetches it itself, from the network only
  event.respondWith(onFetch(event));
});

const cacheNamePrefix = 'maroclis-shell-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;

// What may be kept: the application's own files, by extension.
const offlineAssetsInclude = [/\.dll$/, /\.wasm$/, /\.html$/, /\.js$/, /\.json$/, /\.css$/, /\.woff2$/, /\.png$/, /\.svg$/, /\.webmanifest$/];
const offlineAssetsExclude = [/^service-worker\.js$/, /^api\//];

const base = '/';
const baseUrl = new URL(base, self.origin);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

/** The API: never cached, never answered from a cache. */
function isApi(request) {
  const url = new URL(request.url);
  return url.origin === self.origin && (url.pathname === '/api' || url.pathname.startsWith('/api/'));
}

async function onInstall() {
  const assetsRequests = self.assetsManifest.assets
    .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
    .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
    .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
  const cache = await caches.open(cacheName);
  await cache.addAll(assetsRequests);
  await self.skipWaiting();
}

async function onActivate() {
  const cacheKeys = await caches.keys();
  await Promise.all(cacheKeys
    .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
    .map(key => caches.delete(key)));
  await self.clients.claim();
}

async function onFetch(event) {
  if (event.request.method !== 'GET') return fetch(event.request);

  // A screen's address (/dossier/…) is the application's page.
  const shouldServeIndexHtml = event.request.mode === 'navigate'
    && !manifestUrlList.some(url => url === event.request.url);

  const request = shouldServeIndexHtml ? 'index.html' : event.request;
  const cache = await caches.open(cacheName);
  const cached = await cache.match(request);
  return cached || fetch(event.request);
}
