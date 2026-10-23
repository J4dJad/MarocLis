// MarocLIS Web — the little the page does by itself.
(function () {
    "use strict";

    const events = ["mousemove", "mousedown", "keydown", "wheel", "touchstart", "scroll"];

    window.marocLis = {
        // Tells the server, at most once per throttle period, that the operator is at the page:
        // reading a list, scrolling, typing in a field the server does not hear yet. The session's
        // idle timeout counts this as activity, as the workstation counts a mouse move.
        watchActivity(relay, throttleMs) {
            let last = 0;
            let pending = false;

            const notify = () => {
                const now = Date.now();
                if (pending || now - last < throttleMs) return;
                last = now;
                pending = true;
                relay.invokeMethodAsync("Activity")
                    .catch(() => { })
                    .finally(() => { pending = false; });
            };

            for (const name of events) {
                window.addEventListener(name, notify, { passive: true, capture: true });
            }
        }
    };
})();
