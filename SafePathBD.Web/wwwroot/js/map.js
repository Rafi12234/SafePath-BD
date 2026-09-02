/* SafePath BD — map workspace.
   Leaflet + OpenStreetMap rendering, geolocation, place selection and the
   emergency-services layer. All network access goes through SafePath endpoints. */
(function () {
    "use strict";

    var root = document.querySelector("[data-map-root]");
    if (!root || typeof L === "undefined") {
        return;
    }

    var config = JSON.parse(root.getAttribute("data-map-config") || "{}");
    var SPM = window.SafePathMap;
    var reduceMotion = SPM.prefersReducedMotion();
    var toast = window.SafePathToast || { show: function () {} };

    var state = {
        map: null,
        emergencyLayer: null,
        reportLayer: null,
        markersById: {},
        services: [],
        reports: [],
        reportMarkersById: {},
        selectedId: null,
        activeType: null,
        userLocation: null,
        userMarker: null,
        pickedMarker: null,
        startMarker: null,
        endMarker: null,
        start: null,
        end: null,
        emergencyVisible: true,
        reportsVisible: false
    };

    var el = {
        loading: root.querySelector("[data-map-loading]"),
        drawer: root.querySelector("[data-drawer]"),
        drawerTitle: root.querySelector("[data-drawer-title]"),
        drawerSubtitle: root.querySelector("[data-drawer-subtitle]"),
        drawerBody: root.querySelector("[data-drawer-body]"),
        pointCard: root.querySelector("[data-point-card]"),
        pointTitle: root.querySelector("[data-point-title]"),
        pointCoords: root.querySelector("[data-point-coords]"),
        locateBtn: root.querySelector("[data-locate]"),
        resetBtn: root.querySelector("[data-reset-view]"),
        layerBtn: root.querySelector("[data-toggle-emergency]"),
        reportsBtn: root.querySelector("[data-toggle-reports]"),
        filters: root.querySelectorAll("[data-filter]")
    };

    /* ---------------------------------------------------------------- utils */

    var svg = SPM.svg;
    var escapeHtml = SPM.escapeHtml;
    var formatCoords = SPM.formatCoords;
    var formatDistance = SPM.formatDistance;
    var getJson = SPM.getJson;

    function styleFor(typeName) {
        return SPM.serviceStyle(typeName);
    }

    /* ----------------------------------------------------------------- map */

    function initMap() {
        state.map = SPM.createMap(root.querySelector("[data-map-canvas]"), {
            lat: config.lat,
            lng: config.lng,
            zoom: config.zoom
        });

        state.emergencyLayer = L.layerGroup().addTo(state.map);
        state.reportLayer = L.layerGroup();

        state.map.whenReady(function () {
            window.setTimeout(function () {
                el.loading.classList.add("is-hidden");
            }, reduceMotion ? 0 : 250);
        });

        state.map.on("click", onMapClick);
        state.map.on("moveend", function () {
            if (state.reportsVisible) {
                loadReports();
            }
        });
    }

    function flyTo(lat, lng, zoom) {
        SPM.flyTo(state.map, lat, lng, zoom);
    }

    /* ------------------------------------------------------------- markers */

    function pinIcon(kind, paths, colorVar) {
        return SPM.pinIcon(kind, paths, colorVar);
    }

    function locateIcon() {
        return SPM.locateIcon();
    }

    function setSingleMarker(existing, lat, lng, icon, title) {
        if (existing) {
            state.map.removeLayer(existing);
        }
        return L.marker([lat, lng], { icon: icon, title: title, keyboard: true }).addTo(state.map);
    }

    /* ---------------------------------------------------------- geolocation */

    function locateUser(options) {
        var opts = options || {};

        if (!("geolocation" in navigator)) {
            toast.error("This browser does not support location sharing. You can still pick a point on the map.");
            return;
        }

        el.locateBtn.classList.add("is-busy");
        el.locateBtn.setAttribute("aria-busy", "true");

        navigator.geolocation.getCurrentPosition(
            function (position) {
                el.locateBtn.classList.remove("is-busy");
                el.locateBtn.removeAttribute("aria-busy");

                var lat = position.coords.latitude;
                var lng = position.coords.longitude;
                state.userLocation = { lat: lat, lng: lng };

                state.userMarker = setSingleMarker(state.userMarker, lat, lng, locateIcon(), "Your location");
                flyTo(lat, lng, Math.max(state.map.getZoom(), 14));

                if (!opts.silent) {
                    toast.success("Location found.");
                }

                loadNearby(lat, lng);
            },
            function (error) {
                el.locateBtn.classList.remove("is-busy");
                el.locateBtn.removeAttribute("aria-busy");

                if (error.code === error.PERMISSION_DENIED) {
                    toast.warning("Location access was denied. Tap the map to choose a point instead.");
                } else if (error.code === error.TIMEOUT) {
                    toast.warning("Finding your location took too long. Try again or tap the map.");
                } else {
                    toast.warning("Your location is unavailable right now. Tap the map to choose a point.");
                }
            },
            { enableHighAccuracy: true, timeout: 10000, maximumAge: 60000 }
        );
    }

    /* -------------------------------------------------- map point selection */

    async function onMapClick(event) {
        var lat = event.latlng.lat;
        var lng = event.latlng.lng;

        state.pickedMarker = setSingleMarker(
            state.pickedMarker, lat, lng,
            pinIcon("picked", '<path d="M12 8v5M12 16h.01" />'),
            "Selected point");

        el.pointCard.hidden = false;
        el.pointTitle.textContent = "Looking up this place…";
        el.pointCoords.textContent = formatCoords(lat, lng);
        el.pointCard.dataset.lat = lat;
        el.pointCard.dataset.lng = lng;

        try {
            var place = await getJson("/api/v1/locations/reverse?lat=" + lat + "&lng=" + lng);
            el.pointTitle.textContent = place.addressLine || place.displayName;
            el.pointCard.dataset.label = place.addressLine || place.displayName;
        } catch (error) {
            // Coordinates remain usable even when the geocoder is unreachable.
            el.pointTitle.textContent = "Address unavailable";
            el.pointCard.dataset.label = formatCoords(lat, lng);
            toast.info("Address information could not be loaded. The selected coordinates are still available.");
        }
    }

    /* ------------------------------------------------ emergency services UI */

    function renderSkeletons(count) {
        var html = "";
        for (var i = 0; i < count; i++) {
            html += '<div class="skeleton-card">' +
                '<span class="skeleton skeleton-glyph"></span>' +
                '<div><span class="skeleton skeleton-line" style="display:block;width:70%"></span>' +
                '<span class="skeleton skeleton-line" style="display:block;width:45%"></span></div></div>';
        }
        el.drawerBody.innerHTML = '<div class="svc-list">' + html + "</div>";
    }

    function renderEmpty(message, hint) {
        el.drawerBody.innerHTML =
            '<div class="empty-state">' +
            '<span class="card-icon" aria-hidden="true">' + svg('<path d="M12 21s7-5.2 7-11a7 7 0 1 0-14 0c0 5.8 7 11 7 11Z" /><circle cx="12" cy="10" r="2.4" />', 20) + "</span>" +
            "<h3 class=\"t-h3\">" + escapeHtml(message) + "</h3>" +
            '<p class="t-body">' + escapeHtml(hint) + "</p>" +
            "</div>";
    }

    function renderList() {
        if (!state.services.length) {
            renderEmpty(
                "No emergency services found here",
                "Nothing is registered within the current search area. Try a different service type, move the map, or use your location again.");
            return;
        }

        var html = state.services.map(function (s) {
            var style = styleFor(s.serviceTypeName);
            var place = s.areaName || s.city || s.district || "";

            return '<button class="svc-card" type="button" data-service="' + s.emergencyServiceId + '" style="--svc-color: ' + style.color + '">' +
                '<span class="svc-glyph" aria-hidden="true">' + svg(style.icon) + "</span>" +
                "<span>" +
                '<span class="svc-name">' + escapeHtml(s.serviceName) + "</span>" +
                '<span class="svc-meta">' +
                "<span>" + escapeHtml(s.serviceTypeName) + "</span>" +
                (place ? "<span>" + escapeHtml(place) + "</span>" : "") +
                (s.straightLineDistanceKm !== null ? '<span class="svc-distance">' + formatDistance(s.straightLineDistanceKm) + "</span>" : "") +
                (s.is24Hours ? "<span>24 hours</span>" : "") +
                "</span></span></button>";
        }).join("");

        el.drawerBody.innerHTML = '<div class="svc-list">' + html + "</div>";

        el.drawerBody.querySelectorAll("[data-service]").forEach(function (card) {
            card.addEventListener("click", function () {
                selectService(Number(card.getAttribute("data-service")));
            });
        });
    }

    function renderDetail(service) {
        var style = styleFor(service.serviceTypeName);
        var address = [service.addressLine, service.areaName, service.city, service.district]
            .filter(Boolean).join(", ");

        var contact = "";
        if (service.emergencyPhone) {
            contact += '<a href="tel:' + escapeHtml(service.emergencyPhone) + '">' +
                svg('<path d="M22 16.9v3a2 2 0 0 1-2.2 2 19.8 19.8 0 0 1-8.6-3.1 19.5 19.5 0 0 1-6-6A19.8 19.8 0 0 1 2.1 4.2 2 2 0 0 1 4.1 2h3a2 2 0 0 1 2 1.7c.1 1 .4 1.9.7 2.8a2 2 0 0 1-.5 2.1L8.1 9.9a16 16 0 0 0 6 6l1.3-1.2a2 2 0 0 1 2.1-.5c.9.3 1.8.6 2.8.7a2 2 0 0 1 1.7 2Z" />', 16) +
                "<span>Emergency line · " + escapeHtml(service.emergencyPhone) + "</span></a>";
        }
        if (service.phone) {
            contact += '<a href="tel:' + escapeHtml(service.phone) + '">' +
                svg('<path d="M22 16.9v3a2 2 0 0 1-2.2 2 19.8 19.8 0 0 1-8.6-3.1 19.5 19.5 0 0 1-6-6A19.8 19.8 0 0 1 2.1 4.2 2 2 0 0 1 4.1 2h3a2 2 0 0 1 2 1.7c.1 1 .4 1.9.7 2.8a2 2 0 0 1-.5 2.1L8.1 9.9a16 16 0 0 0 6 6l1.3-1.2a2 2 0 0 1 2.1-.5c.9.3 1.8.6 2.8.7a2 2 0 0 1 1.7 2Z" />', 16) +
                "<span>" + escapeHtml(service.phone) + "</span></a>";
        }

        el.drawerBody.innerHTML =
            '<div class="svc-detail">' +
            '<div class="svc-detail-head">' +
            '<span class="svc-glyph" style="--svc-color: ' + style.color + '" aria-hidden="true">' + svg(style.icon) + "</span>" +
            "<div><h3 class=\"t-h3\">" + escapeHtml(service.serviceName) + "</h3>" +
            '<p class="t-meta">' + escapeHtml(service.serviceTypeName) + "</p></div></div>" +

            '<div class="cluster">' +
            (service.isVerified ? '<span class="chip chip--safe">Verified</span>' : '<span class="chip chip--muted">Unverified</span>') +
            (service.is24Hours ? '<span class="chip chip--accent">Open 24 hours</span>' : "") +
            (service.straightLineDistanceKm !== null
                ? '<span class="chip chip--plain chip--muted">' + formatDistance(service.straightLineDistanceKm) + " away (straight line)</span>"
                : "") +
            "</div>" +

            (address ? '<p class="t-body">' + escapeHtml(address) + "</p>" : "") +
            (service.openingHours ? '<p class="t-meta">Opening hours: ' + escapeHtml(service.openingHours) + "</p>" : "") +
            (contact ? '<div class="svc-contact">' + contact + "</div>" : '<p class="t-meta">No contact number is recorded for this facility.</p>') +

            '<button class="btn btn-secondary" type="button" data-back>Back to results</button>' +
            "</div>";

        el.drawerBody.querySelector("[data-back]").addEventListener("click", function () {
            openDrawer("Emergency services", subtitleForResults());
            renderList();
            highlightMarker(null);
        });
    }

    function subtitleForResults() {
        var suffix = state.activeType ? state.activeType : "All types";
        return state.services.length + " found · " + suffix;
    }

    function openDrawer(title, subtitle) {
        el.drawerTitle.textContent = title;
        el.drawerSubtitle.textContent = subtitle || "";
        el.drawer.classList.add("is-open");
        el.drawer.setAttribute("aria-hidden", "false");
    }

    function closeDrawer() {
        el.drawer.classList.remove("is-open");
        el.drawer.setAttribute("aria-hidden", "true");
        highlightMarker(null);
    }

    /* ----------------------------------------------------- emergency layer */

    function renderMarkers() {
        state.emergencyLayer.clearLayers();
        state.markersById = {};

        state.services.forEach(function (s, index) {
            var style = styleFor(s.serviceTypeName);
            var marker = L.marker([s.latitude, s.longitude], {
                icon: pinIcon("svc", style.icon, style.color),
                title: s.serviceName,
                riseOnHover: true
            });

            marker.bindPopup(
                '<div class="sp-popup"><strong>' + escapeHtml(s.serviceName) + "</strong>" +
                "<span>" + escapeHtml(s.serviceTypeName) +
                (s.straightLineDistanceKm !== null ? " · " + formatDistance(s.straightLineDistanceKm) : "") +
                "</span></div>");

            marker.on("click", function () {
                selectService(s.emergencyServiceId);
            });

            // Light stagger keeps the layer from popping in all at once.
            if (reduceMotion) {
                marker.addTo(state.emergencyLayer);
            } else {
                window.setTimeout(function () {
                    marker.addTo(state.emergencyLayer);
                }, Math.min(index, 20) * 35);
            }

            state.markersById[s.emergencyServiceId] = marker;
        });
    }

    function highlightMarker(id) {
        Object.keys(state.markersById).forEach(function (key) {
            var element = state.markersById[key].getElement();
            if (element) {
                element.classList.toggle("is-selected", String(key) === String(id));
            }
        });
        state.selectedId = id;
    }

    function selectService(id) {
        var service = state.services.filter(function (s) {
            return s.emergencyServiceId === id;
        })[0];

        if (!service) {
            return;
        }

        highlightMarker(id);
        openDrawer(service.serviceName, service.serviceTypeName);
        renderDetail(service);
        flyTo(service.latitude, service.longitude, Math.max(state.map.getZoom(), 15));
    }

    async function loadNearby(lat, lng) {
        openDrawer("Emergency services", "Searching nearby…");
        renderSkeletons(4);

        var url = "/api/v1/emergency-services/nearby?lat=" + lat + "&lng=" + lng + "&radiusKm=25&limit=40";
        if (state.activeType) {
            url += "&type=" + encodeURIComponent(state.activeType);
        }

        try {
            state.services = await getJson(url);
            renderMarkers();
            openDrawer("Emergency services", subtitleForResults());
            renderList();
        } catch (error) {
            state.services = [];
            state.emergencyLayer.clearLayers();
            el.drawerBody.innerHTML =
                '<div class="empty-state">' +
                '<span class="card-icon card-icon--danger" aria-hidden="true">' + svg('<path d="M12 9v4M12 17h.01" /><circle cx="12" cy="12" r="9" />', 20) + "</span>" +
                '<h3 class="t-h3">Couldn\'t load emergency services</h3>' +
                '<p class="t-body">Something went wrong while searching. Check your connection and try again.</p>' +
                '<button class="btn btn-secondary" type="button" data-retry>Try again</button></div>';

            el.drawerBody.querySelector("[data-retry]").addEventListener("click", function () {
                loadNearby(lat, lng);
            });
        }
    }

    function currentOrigin() {
        if (state.userLocation) {
            return state.userLocation;
        }
        var center = state.map.getCenter();
        return { lat: center.lat, lng: center.lng };
    }

    /* ------------------------------------------------- verified report layer */

    function reportSubtitle(report) {
        if (report.reportType === "ACCIDENT") {
            return [report.accidentTypeName, report.severityName].filter(Boolean).join(" · ");
        }
        return [report.hazardTypeName, report.riskLevel].filter(Boolean).join(" · ");
    }

    function renderReportMarkers() {
        state.reportLayer.clearLayers();
        state.reportMarkersById = {};

        state.reports.forEach(function (report, index) {
            var marker = L.marker([report.latitude, report.longitude], {
                icon: SPM.reportIcon(report.reportType, SPM.reportAccent(report)),
                title: report.title,
                riseOnHover: true
            });

            marker.on("click", function () {
                selectReport(report.reportId);
            });

            if (reduceMotion) {
                marker.addTo(state.reportLayer);
            } else {
                window.setTimeout(function () {
                    marker.addTo(state.reportLayer);
                }, Math.min(index, 20) * 30);
            }

            state.reportMarkersById[report.reportId] = marker;
        });
    }

    function renderReportList() {
        if (!state.reports.length) {
            el.drawerBody.innerHTML =
                '<div class="empty-state">' +
                '<span class="card-icon" aria-hidden="true">' + svg(SPM.HAZARD_GLYPH, 20) + "</span>" +
                '<h3 class="t-h3">No verified reports in this area</h3>' +
                '<p class="t-body">Only reports that a moderator has verified appear on the public map. ' +
                "Pan or zoom out to widen the search.</p></div>";
            return;
        }

        var html = state.reports.map(function (r) {
            var accent = SPM.reportAccent(r);
            var isAccident = r.reportType === "ACCIDENT";

            return '<button class="svc-card" type="button" data-report="' + r.reportId + '" style="--svc-color: ' + accent + '">' +
                '<span class="svc-glyph" aria-hidden="true">' + svg(isAccident ? SPM.ACCIDENT_GLYPH : SPM.HAZARD_GLYPH) + "</span>" +
                "<span>" +
                '<span class="svc-name">' + escapeHtml(r.title) + "</span>" +
                '<span class="svc-meta">' +
                "<span>" + (isAccident ? "Accident" : "Hazard") + "</span>" +
                (reportSubtitle(r) ? "<span>" + escapeHtml(reportSubtitle(r)) + "</span>" : "") +
                (r.areaName ? "<span>" + escapeHtml(r.areaName) + "</span>" : "") +
                "</span></span></button>";
        }).join("");

        el.drawerBody.innerHTML = '<div class="svc-list">' + html + "</div>";

        el.drawerBody.querySelectorAll("[data-report]").forEach(function (card) {
            card.addEventListener("click", function () {
                selectReport(Number(card.getAttribute("data-report")));
            });
        });
    }

    function selectReport(id) {
        var report = state.reports.filter(function (r) {
            return r.reportId === id;
        })[0];

        if (!report) {
            return;
        }

        var accent = SPM.reportAccent(report);
        var isAccident = report.reportType === "ACCIDENT";
        var reported = new Date(report.reportedAt).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });

        openDrawer(report.title, isAccident ? "Verified accident" : "Verified hazard");

        el.drawerBody.innerHTML =
            '<div class="svc-detail">' +
            '<div class="svc-detail-head">' +
            '<span class="svc-glyph" style="--svc-color: ' + accent + '" aria-hidden="true">' +
            svg(isAccident ? SPM.ACCIDENT_GLYPH : SPM.HAZARD_GLYPH) + "</span>" +
            "<div><h3 class=\"t-h3\">" + escapeHtml(report.title) + "</h3>" +
            '<p class="t-meta">' + escapeHtml(reportSubtitle(report) || (isAccident ? "Accident" : "Hazard")) + "</p></div></div>" +

            (report.thumbnailImageId
                ? '<img class="report-thumb" src="/Reports/Image/' + encodeURIComponent(report.thumbnailImageId) + '" alt="" loading="lazy" />'
                : "") +

            '<div class="cluster">' +
            '<span class="chip chip--safe">Verified</span>' +
            (report.areaName ? '<span class="chip chip--muted chip--plain">' + escapeHtml(report.areaName) + "</span>" : "") +
            '<span class="chip chip--muted chip--plain">Reported ' + escapeHtml(reported) + "</span>" +
            "</div>" +

            // Aggregate community signal only; the API never returns who voted.
            ((report.confirmCount || report.disputeCount)
                ? '<div class="cluster"><span class="trust-pip trust-pip--confirm">' + report.confirmCount + " confirm</span>" +
                  '<span class="trust-pip trust-pip--dispute">' + report.disputeCount + " dispute</span></div>"
                : "") +

            '<a class="btn btn-secondary" href="/Reports/Details/' + report.reportId + '">View full report</a>' +
            '<button class="btn btn-ghost" type="button" data-back>Back to results</button>' +
            "</div>";

        el.drawerBody.querySelector("[data-back]").addEventListener("click", function () {
            openDrawer("Verified reports", state.reports.length + " in this area");
            renderReportList();
        });

        flyTo(report.latitude, report.longitude, Math.max(state.map.getZoom(), 15));
    }

    async function loadReports() {
        var bounds = state.map.getBounds();
        var url = "/api/v1/map/reports?minLat=" + bounds.getSouth() + "&minLng=" + bounds.getWest() +
            "&maxLat=" + bounds.getNorth() + "&maxLng=" + bounds.getEast() + "&limit=200";

        try {
            state.reports = await getJson(url);
            renderReportMarkers();

            if (el.drawer.classList.contains("is-open") && el.drawerTitle.textContent === "Verified reports") {
                el.drawerSubtitle.textContent = state.reports.length + " in this area";
                renderReportList();
            }
        } catch (error) {
            state.reports = [];
            state.reportLayer.clearLayers();
            toast.warning("Verified reports could not be loaded for this area.");
        }
    }

    /* ---------------------------------------------------- location search */

    function initPlaceField(fieldRoot) {
        var input = fieldRoot.querySelector("input");
        var list = fieldRoot.querySelector("[data-suggest]");
        var role = fieldRoot.getAttribute("data-place-field");
        var timer = null;
        var activeIndex = -1;
        var results = [];

        function close() {
            list.classList.remove("is-open");
            input.setAttribute("aria-expanded", "false");
            activeIndex = -1;
        }

        function status(message, busy) {
            list.innerHTML = '<div class="suggest-status">' +
                (busy ? svg('<path d="M12 3a9 9 0 1 0 9 9" />', 14) : "") +
                "<span>" + escapeHtml(message) + "</span></div>";
            list.classList.add("is-open");
            input.setAttribute("aria-expanded", "true");
        }

        function paint() {
            if (!results.length) {
                status("No matching places found.", false);
                return;
            }

            list.innerHTML = results.map(function (r, i) {
                return '<button class="suggest-item" type="button" role="option" data-index="' + i + '">' +
                    "<strong>" + escapeHtml(r.shortName) + "</strong>" +
                    "<span>" + escapeHtml(r.displayName) + "</span></button>";
            }).join("");

            list.classList.add("is-open");
            input.setAttribute("aria-expanded", "true");

            list.querySelectorAll(".suggest-item").forEach(function (item) {
                item.addEventListener("click", function () {
                    choose(results[Number(item.getAttribute("data-index"))]);
                });
            });
        }

        function choose(result) {
            input.value = result.shortName;
            close();
            applyPlace(role, result.latitude, result.longitude, result.shortName);
        }

        function move(delta) {
            var items = list.querySelectorAll(".suggest-item");
            if (!items.length) {
                return;
            }
            activeIndex = (activeIndex + delta + items.length) % items.length;
            items.forEach(function (item, i) {
                item.classList.toggle("is-active", i === activeIndex);
            });
            items[activeIndex].scrollIntoView({ block: "nearest" });
        }

        input.addEventListener("input", function () {
            window.clearTimeout(timer);
            var value = input.value.trim();

            if (value.length < 2) {
                close();
                return;
            }

            status("Searching…", true);

            // Debounced so a provider request is not issued on every keystroke.
            timer = window.setTimeout(async function () {
                try {
                    results = await getJson("/api/v1/locations/search?q=" + encodeURIComponent(value) + "&limit=6");
                    paint();
                } catch (error) {
                    results = [];
                    status("Location search is unavailable right now.", false);
                }
            }, 400);
        });

        input.addEventListener("keydown", function (event) {
            if (event.key === "ArrowDown") { event.preventDefault(); move(1); }
            else if (event.key === "ArrowUp") { event.preventDefault(); move(-1); }
            else if (event.key === "Escape") { close(); }
            else if (event.key === "Enter" && activeIndex >= 0) {
                event.preventDefault();
                choose(results[activeIndex]);
            }
        });

        input.addEventListener("blur", function () {
            window.setTimeout(close, 160);
        });
    }

    function applyPlace(role, lat, lng, label) {
        if (role === "start") {
            state.start = { lat: lat, lng: lng, label: label };
            state.startMarker = setSingleMarker(state.startMarker, lat, lng,
                pinIcon("start", '<circle cx="12" cy="12" r="4" />'), "Start: " + label);
        } else {
            state.end = { lat: lat, lng: lng, label: label };
            state.endMarker = setSingleMarker(state.endMarker, lat, lng,
                pinIcon("end", '<path d="M7 20V5m0 0 9 3-9 3" />'), "Destination: " + label);
        }

        if (state.start && state.end) {
            state.map.fitBounds(
                L.latLngBounds([[state.start.lat, state.start.lng], [state.end.lat, state.end.lng]]),
                { padding: [90, 90], animate: !reduceMotion });
        } else {
            flyTo(lat, lng, Math.max(state.map.getZoom(), 14));
        }
    }

    /* ------------------------------------------------------------- wiring */

    function initControls() {
        el.locateBtn.addEventListener("click", function () { locateUser(); });

        el.resetBtn.addEventListener("click", function () {
            flyTo(config.lat, config.lng, config.zoom);
        });

        el.layerBtn.addEventListener("click", function () {
            state.emergencyVisible = !state.emergencyVisible;
            el.layerBtn.classList.toggle("is-active", state.emergencyVisible);
            el.layerBtn.setAttribute("aria-pressed", String(state.emergencyVisible));

            if (state.emergencyVisible) {
                state.emergencyLayer.addTo(state.map);
            } else {
                state.map.removeLayer(state.emergencyLayer);
                closeDrawer();
            }
        });

        el.drawer.querySelector("[data-drawer-close]").addEventListener("click", closeDrawer);

        el.reportsBtn.addEventListener("click", async function () {
            state.reportsVisible = !state.reportsVisible;
            el.reportsBtn.classList.toggle("is-active", state.reportsVisible);
            el.reportsBtn.setAttribute("aria-pressed", String(state.reportsVisible));

            if (!state.reportsVisible) {
                state.map.removeLayer(state.reportLayer);
                return;
            }

            state.reportLayer.addTo(state.map);
            openDrawer("Verified reports", "Loading…");
            renderSkeletons(3);
            await loadReports();
            openDrawer("Verified reports", state.reports.length + " in this area");
            renderReportList();
        });

        el.filters.forEach(function (chip) {
            chip.addEventListener("click", function () {
                var value = chip.getAttribute("data-filter");
                state.activeType = value === "all" ? null : value;

                el.filters.forEach(function (other) {
                    var active = other === chip;
                    other.classList.toggle("is-active", active);
                    other.setAttribute("aria-pressed", String(active));
                });

                var origin = currentOrigin();
                loadNearby(origin.lat, origin.lng);
            });
        });

        root.querySelectorAll("[data-place-field]").forEach(initPlaceField);

        root.querySelector("[data-use-current-start]").addEventListener("click", function () {
            if (!state.userLocation) {
                locateUser({ silent: true });
                toast.info("Finding your location to use as the start point…");
                return;
            }
            var input = root.querySelector('[data-place-field="start"] input');
            input.value = "My location";
            applyPlace("start", state.userLocation.lat, state.userLocation.lng, "My location");
        });

        root.querySelector("[data-swap]").addEventListener("click", function () {
            var startInput = root.querySelector('[data-place-field="start"] input');
            var endInput = root.querySelector('[data-place-field="end"] input');
            var swap = startInput.value;
            startInput.value = endInput.value;
            endInput.value = swap;

            var start = state.start;
            state.start = state.end;
            state.end = start;

            if (state.start) { applyPlace("start", state.start.lat, state.start.lng, state.start.label); }
            if (state.end) { applyPlace("end", state.end.lat, state.end.lng, state.end.label); }
        });

        el.pointCard.querySelector("[data-set-start]").addEventListener("click", function () {
            usePickedPoint("start");
        });
        el.pointCard.querySelector("[data-set-end]").addEventListener("click", function () {
            usePickedPoint("end");
        });
        el.pointCard.querySelector("[data-point-dismiss]").addEventListener("click", function () {
            el.pointCard.hidden = true;
            if (state.pickedMarker) {
                state.map.removeLayer(state.pickedMarker);
                state.pickedMarker = null;
            }
        });
    }

    function usePickedPoint(role) {
        var lat = Number(el.pointCard.dataset.lat);
        var lng = Number(el.pointCard.dataset.lng);
        var label = el.pointCard.dataset.label || formatCoords(lat, lng);

        root.querySelector('[data-place-field="' + role + '"] input').value = label;
        applyPlace(role, lat, lng, label);

        el.pointCard.hidden = true;
        if (state.pickedMarker) {
            state.map.removeLayer(state.pickedMarker);
            state.pickedMarker = null;
        }
    }

    initMap();
    initControls();
})();


