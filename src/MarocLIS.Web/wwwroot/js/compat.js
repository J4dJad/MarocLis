// MarocLIS Web — what older browsers need before blazor.web.js, and the word to the ones too old.
//
// Written in ES5 on purpose: it must run in the very browsers it turns away (Internet Explorer,
// an old Chrome or Firefox left on a Windows 7 workstation). Loaded before blazor.web.js.
//
// The browsers MarocLIS Web supports are the last ones Windows 7, 8 and 8.1 receive: Chrome and
// Edge 109, Firefox ESR 115 (the one the « Poste Web » setup installs), and anything newer.
(function () {
    "use strict";

    // .NET 10's blazor.web.js calls Promise.withResolvers (Chrome 119, Firefox 121) when a circuit
    // pauses, resumes or is disconnected: without it, a dropped network would leave the screen
    // stuck instead of reconnecting. The standard behaviour, in a few lines.
    if (typeof Promise === "function" && typeof Promise.withResolvers !== "function") {
        Promise.withResolvers = function () {
            var result = {};
            result.promise = new this(function (resolve, reject) {
                result.resolve = resolve;
                result.reject = reject;
            });
            return result;
        };
    }

    function versionOf(pattern) {
        var match = pattern.exec(navigator.userAgent);
        return match ? parseInt(match[1], 10) : null;
    }

    function supports(property, value) {
        try { return window.CSS.supports(property, value); } catch (e) { return false; }
    }

    function tooOld() {
        // What every screen relies on: the circuit's WebSocket, promises and fetch, the logical
        // CSS properties the right-to-left Arabic layout is written with.
        if (!window.WebSocket || typeof Promise !== "function" || typeof window.fetch !== "function") return true;
        if (!window.CSS || typeof window.CSS.supports !== "function") return true;
        if (!supports("inset-inline-start", "0") || !supports("display", "grid")) return true;
        if (typeof Array.prototype.at !== "function") return true;

        // And the versions themselves, for the browsers that say theirs.
        var edge = versionOf(/\bEdg\/(\d+)/);
        if (edge !== null) return edge < 109;
        if (/\bEdge\/\d+/.test(navigator.userAgent)) return true; // the former Edge (EdgeHTML)
        var firefox = versionOf(/\bFirefox\/(\d+)/);
        if (firefox !== null) return firefox < 115;
        var chrome = versionOf(/\bChrom(?:e|ium)\/(\d+)/);
        if (chrome !== null) return chrome < 109;
        return false;
    }

    if (!tooOld()) return;

    function show() {
        var notices = document.querySelectorAll("[data-state='browser-too-old']");
        for (var i = 0; i < notices.length; i++) notices[i].removeAttribute("hidden");
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", show);
    else show();
})();
