// MarocLIS.Mobile — the few browser calls the .NET side cannot make itself (Services/Browser.cs).
// A file of its own rather than an inline script: the page's Content-Security-Policy allows no
// inline script at all.
'use strict';

window.marocLis = {
  setDocumentLanguage(lang, dir) {
    document.documentElement.lang = lang;
    document.documentElement.dir = dir;
  },

  watchConnectivity(dotnet) {
    const report = () => dotnet.invokeMethodAsync('SetOnline', navigator.onLine);
    window.addEventListener('online', report);
    window.addEventListener('offline', report);
    return navigator.onLine;
  }
};

// The service worker keeps the application's own files for a quick start and an offline notice;
// never the API's answers (service-worker.published.js).
if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('service-worker.js', { updateViaCache: 'none' }).catch(() => { });
}
