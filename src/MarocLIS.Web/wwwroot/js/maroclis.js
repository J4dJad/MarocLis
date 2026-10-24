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
        },

        // Hands the operator a file the server produced (a stored report), as the workstation
        // opens it in the PDF viewer.
        download(fileName, contentType, bytes) {
            const url = URL.createObjectURL(new Blob([bytes], { type: contentType }));
            const link = document.createElement("a");
            link.href = url;
            link.download = fileName;
            document.body.appendChild(link);
            link.click();
            link.remove();
            setTimeout(() => URL.revokeObjectURL(url), 10000);
        },

        // Copies a number to the clipboard, as a double click on the accession number does on
        // the workstation.
        copy(text) {
            return navigator.clipboard ? navigator.clipboard.writeText(text) : Promise.resolve();
        },

        // Puts the cursor in the first box a selector names, its text selected: the first value
        // still to enter when a file opens, the scan box after a decision.
        focusFirst(selector) {
            const box = document.querySelector(selector);
            if (!box) return false;
            box.focus();
            if (typeof box.select === "function") box.select();
            return true;
        }
    };

    // The keys of the workstation's action bars — F3, F8, F9, F10, F12, Ctrl+S — press the button
    // that carries them (data-shortcut), when it is on the page and not disabled; the browser
    // keeps its own keys otherwise. A dialog open keeps them to its own buttons.
    document.addEventListener("keydown", (e) => {
        if (e.altKey || e.metaKey) return;
        const name = (e.ctrlKey ? "Ctrl+" : "") + (e.key.length === 1 ? e.key.toUpperCase() : e.key);
        if (!/^(F\d{1,2}|Ctrl\+S)$/.test(name)) return;

        const scope = document.querySelector(".modal-backdrop:last-of-type") || document;
        const button = scope.querySelector(`[data-shortcut="${name}"]:not([disabled])`);
        if (!button) return;

        e.preventDefault();
        button.click();
    });

    // « Saisie des résultats »: Enter and Shift+Enter step down and up the values to enter, as in
    // the workstation's grid; past the last one the cursor goes to « Enregistrer ». A tube scanned
    // into a value box ends with Enter too, and lands on the next line instead of submitting.
    document.addEventListener("keydown", (e) => {
        if (e.key !== "Enter" || !(e.target instanceof Element)) return;
        const box = e.target.closest("[data-entry-value]");
        if (!box) return;

        e.preventDefault();
        const boxes = [...document.querySelectorAll("[data-entry-value]:not([readonly])")];
        const next = boxes[boxes.indexOf(box) + (e.shiftKey ? -1 : 1)];
        if (next) {
            next.focus();
            next.select();
        } else if (!e.shiftKey) {
            document.querySelector('[data-action="Action.Save"]')?.focus();
        }
    });
})();
