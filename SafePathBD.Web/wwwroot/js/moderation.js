/* SafePath BD — moderation review workspace: location preview and the decision dialog. */
(function () {
    "use strict";

    function initMap() {
        const host = document.querySelector("[data-review-map]");
        if (!host || typeof L === "undefined" || !window.SafePathMap) {
            return;
        }

        const lat = parseFloat(host.getAttribute("data-lat"));
        const lng = parseFloat(host.getAttribute("data-lng"));
        if (!isFinite(lat) || !isFinite(lng)) {
            return;
        }

        const map = window.SafePathMap.createMap(host, { lat: lat, lng: lng, zoom: 16 });
        map.scrollWheelZoom.disable();

        L.marker([lat, lng], {
            icon: window.SafePathMap.reportIcon(host.getAttribute("data-type"), host.getAttribute("data-accent")),
            keyboard: false
        }).addTo(map);
    }

    function initDecisions() {
        const modal = document.querySelector("[data-decision-modal]");
        const panel = document.querySelector("[data-decision-panel]");
        if (!modal || !panel) {
            return;
        }

        const form = modal.querySelector("[data-decision-form]");
        const target = modal.querySelector("[data-decision-target]");
        const title = modal.querySelector("[data-decision-title]");
        const hint = modal.querySelector("[data-decision-hint]");
        const note = modal.querySelector("#decision-note");
        const noteError = modal.querySelector("[data-note-error]");
        const optional = modal.querySelector("[data-note-optional]");
        const submit = modal.querySelector("[data-decision-submit]");

        let requiresNote = false;
        let lastTrigger = null;

        function open(button) {
            lastTrigger = button;
            requiresNote = button.getAttribute("data-requires-note") === "true";

            target.value = button.getAttribute("data-decision");
            title.textContent = button.getAttribute("data-label");
            hint.textContent = button.getAttribute("data-hint");
            submit.textContent = button.getAttribute("data-label");
            optional.hidden = requiresNote;
            noteError.hidden = true;
            note.value = "";

            modal.hidden = false;
            requestAnimationFrame(function () {
                modal.classList.add("is-open");
                note.focus();
            });

            document.addEventListener("keydown", onKeydown);
        }

        function close() {
            modal.classList.remove("is-open");
            document.removeEventListener("keydown", onKeydown);

            window.setTimeout(function () {
                modal.hidden = true;
                if (lastTrigger) {
                    lastTrigger.focus();
                }
            }, 180);
        }

        function onKeydown(event) {
            if (event.key === "Escape") {
                close();
                return;
            }

            // Keep focus inside the dialog while it is open.
            if (event.key !== "Tab") {
                return;
            }

            const focusable = modal.querySelectorAll("button, textarea, [href]");
            if (!focusable.length) {
                return;
            }

            const first = focusable[0];
            const last = focusable[focusable.length - 1];

            if (event.shiftKey && document.activeElement === first) {
                event.preventDefault();
                last.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
                event.preventDefault();
                first.focus();
            }
        }

        panel.addEventListener("click", function (event) {
            const button = event.target.closest("[data-decision]");
            if (button) {
                open(button);
            }
        });

        modal.querySelectorAll("[data-decision-cancel]").forEach(function (button) {
            button.addEventListener("click", close);
        });

        form.addEventListener("submit", function (event) {
            if (requiresNote && !note.value.trim()) {
                event.preventDefault();
                noteError.hidden = false;
                note.focus();
                return;
            }

            // The server re-checks the transition; this only stops an accidental double submit.
            submit.disabled = true;
            submit.classList.add("is-loading");
        });
    }

    document.addEventListener("DOMContentLoaded", function () {
        initMap();
        initDecisions();
    });
})();
