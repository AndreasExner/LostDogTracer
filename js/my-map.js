(function () {
    'use strict';

    const API_BASE = FT_AUTH.getApiBase();

    const markerCountEl = document.getElementById('markerCount');
    const filterInfoEl = document.getElementById('filterInfo');
    const legendEl = document.getElementById('legend');
    const toggleRoutesEl = document.getElementById('toggleRoutes');
    const toastEl = document.getElementById('toast');
    let toastTimeout = null;

    // Read name + lostDog from URL params
    const urlParams = new URLSearchParams(window.location.search);
    const filterName = urlParams.get('name') || '';
    const filterDog = urlParams.get('lostDog') || '';

    if (!filterName || !filterDog) {
        filterInfoEl.textContent = '⚠️ Kein Name/Hund ausgewählt';
        markerCountEl.textContent = '—';
        document.getElementById('map').innerHTML = '<p style="padding:2rem;text-align:center;color:#ff3b30">Bitte zuerst Name und Hund auf der Startseite auswählen.</p>';
        // Don't init map
        return;
    }

    filterInfoEl.textContent = `${filterName} / ${filterDog}`;

    // ── Color palette ────────────────────────────────────────────
    const COLORS = [
        '#0071e3', '#ff3b30', '#34c759', '#ff9500', '#af52de',
        '#5856d6', '#ff2d55', '#00c7be', '#a2845e', '#64d2ff'
    ];
    const dogColorMap = {};
    let colorIdx = 0;

    function getDogColor(dogName) {
        if (!dogColorMap[dogName]) {
            dogColorMap[dogName] = COLORS[colorIdx % COLORS.length];
            colorIdx++;
        }
        return dogColorMap[dogName];
    }

    /** SVG inner symbols per category (white, centered at cx=12 cy=12) */
    const CATEGORY_SYMBOLS = {
        'Flyer/Handzettel':
            `<rect x="7" y="5" width="10" height="13" rx="1" fill="none" stroke="#fff" stroke-width="1.5"/>` +
            `<line x1="9.5" y1="9" x2="14.5" y2="9" stroke="#fff" stroke-width="1.2"/>` +
            `<line x1="9.5" y1="12" x2="14.5" y2="12" stroke="#fff" stroke-width="1.2"/>` +
            `<line x1="9.5" y1="15" x2="12.5" y2="15" stroke="#fff" stroke-width="1.2"/>`,
        'Sichtung':
            `<ellipse cx="12" cy="12" rx="6" ry="4" fill="none" stroke="#fff" stroke-width="1.5"/>` +
            `<circle cx="12" cy="12" r="2" fill="#fff"/>`,
        'Entlaufort':
            `<circle cx="9" cy="9" r="1.5" fill="#fff"/>` +
            `<circle cx="15" cy="9" r="1.5" fill="#fff"/>` +
            `<circle cx="7" cy="13" r="1.3" fill="#fff"/>` +
            `<circle cx="17" cy="13" r="1.3" fill="#fff"/>` +
            `<ellipse cx="12" cy="15" rx="3" ry="2.2" fill="#fff"/>`,
        'Standort Falle':
            `<circle cx="12" cy="12" r="5" fill="none" stroke="#fff" stroke-width="1.5"/>` +
            `<circle cx="12" cy="12" r="1.5" fill="#fff"/>` +
            `<line x1="12" y1="5" x2="12" y2="8" stroke="#fff" stroke-width="1.3"/>` +
            `<line x1="12" y1="16" x2="12" y2="19" stroke="#fff" stroke-width="1.3"/>` +
            `<line x1="5" y1="12" x2="8" y2="12" stroke="#fff" stroke-width="1.3"/>` +
            `<line x1="16" y1="12" x2="19" y2="12" stroke="#fff" stroke-width="1.3"/>`
    };

    function colorIcon(color, category) {
        const inner = CATEGORY_SYMBOLS[category] || `<circle cx="12" cy="12" r="5" fill="#fff"/>`;
        return L.divIcon({
            className: '',
            html: `<svg width="24" height="36" viewBox="0 0 24 36" xmlns="http://www.w3.org/2000/svg">
                <path d="M12 0C5.4 0 0 5.4 0 12c0 9 12 24 12 24s12-15 12-24C24 5.4 18.6 0 12 0z" fill="${color}"/>
                ${inner}
            </svg>`,
            iconSize: [24, 36],
            iconAnchor: [12, 36],
            popupAnchor: [0, -32]
        });
    }

    // ── Init map ─────────────────────────────────────────────────
    const map = L.map('map').setView([51.1657, 10.4515], 6);

    const osmLayer = L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
        maxZoom: 19
    });
    const satelliteLayer = L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', {
        attribution: '&copy; Esri, Maxar, Earthstar',
        maxZoom: 19
    });
    const topoLayer = L.tileLayer('https://{s}.tile.opentopomap.org/{z}/{x}/{y}.png', {
        attribution: '&copy; OpenTopoMap (CC-BY-SA)',
        maxZoom: 17
    });

    const googleRoads = L.gridLayer.googleMutant({ type: 'roadmap', maxZoom: 21 });
    const googleSat   = L.gridLayer.googleMutant({ type: 'satellite', maxZoom: 21 });
    const googleHybrid = L.gridLayer.googleMutant({ type: 'hybrid', maxZoom: 21 });

    osmLayer.addTo(map);

    L.control.layers({
        'OSM Straße': osmLayer,
        'OSM Topographisch': topoLayer,
        'Esri Satellit': satelliteLayer,
        'Google Straße': googleRoads,
        'Google Satellit': googleSat,
        'Google Hybrid': googleHybrid
    }, null, { position: 'topright', collapsed: true }).addTo(map);

    L.control.scale({ metric: true, imperial: false, position: 'bottomleft' }).addTo(map);

    const clusterGroup = L.markerClusterGroup({
        maxClusterRadius: 40,
        spiderfyOnMaxZoom: true,
        showCoverageOnHover: false
    });
    map.addLayer(clusterGroup);

    const routesLayer = L.layerGroup();

    toggleRoutesEl.addEventListener('change', () => {
        if (toggleRoutesEl.checked) {
            map.addLayer(routesLayer);
        } else {
            map.removeLayer(routesLayer);
        }
    });

    // ── Load & display records ───────────────────────────────────
    async function loadAndDisplay() {
        try {
            const params = new URLSearchParams();
            params.set('pageSize', 'all');
            params.set('name', filterName);
            params.set('lostDog', filterDog);

            const res = await fetch(`${API_BASE}/my-records?${params}`, {
                headers: FT_AUTH.publicHeaders()
            });
            if (!res.ok) throw new Error();

            const data = await res.json();
            const records = data.records || [];

            markerCountEl.textContent = `${records.length} Standort${records.length !== 1 ? 'e' : ''}`;

            if (records.length === 0) {
                showToast('Keine GPS-Daten vorhanden', true);
                return;
            }

            const dogRecords = {};
            const bounds = [];

            records.forEach(r => {
                if (!r.latitude || !r.longitude) return;

                const color = getDogColor(r.lostDog);

                const marker = L.marker([r.latitude, r.longitude], {
                    icon: colorIcon(color, r.category)
                });

                const photoHtml = r.photoUrl
                    ? `<br><img src="${escHtml(r.photoUrl)}" style="max-width:180px;max-height:120px;border-radius:6px;margin-top:6px;" alt="Foto">`
                    : '';

                const navHtml = `<div class="nav-chooser">` +
                    `<a class="nav-google" href="https://www.google.com/maps/dir/?api=1&destination=${r.latitude},${r.longitude}" target="_blank" rel="noopener">Google Maps</a>` +
                    `<a class="nav-apple" href="https://maps.apple.com/?daddr=${r.latitude},${r.longitude}" target="_blank" rel="noopener">Apple Maps</a>` +
                    `<a class="nav-waze" href="https://waze.com/ul?ll=${r.latitude},${r.longitude}&navigate=yes" target="_blank" rel="noopener">Waze</a>` +
                    `</div>`;

                const categoryHtml = r.category ? `<br>🏷️ ${escHtml(r.category)}` : '';
                const commentHtml = r.comment ? `<br>💬 ${escHtml(r.comment)}` : '';

                marker.bindPopup(
                    `<strong>${escHtml(r.name)}</strong><br>` +
                    `🐕 ${escHtml(r.lostDog)}` +
                    categoryHtml + commentHtml + `<br>` +
                    `📍 ${r.latitude.toFixed(6)}, ${r.longitude.toFixed(6)}<br>` +
                    `🎯 ±${r.accuracy.toFixed(0)} m<br>` +
                    `🕐 ${formatDate(r.recordedAt)}` +
                    photoHtml + navHtml,
                    { maxWidth: 280 }
                );

                clusterGroup.addLayer(marker);
                bounds.push([r.latitude, r.longitude]);

                if (!dogRecords[r.lostDog]) dogRecords[r.lostDog] = [];
                dogRecords[r.lostDog].push({
                    lat: r.latitude,
                    lng: r.longitude,
                    time: r.recordedAt || ''
                });
            });

            // Build polylines per dog
            Object.entries(dogRecords).forEach(([dog, points]) => {
                if (points.length < 2) return;
                points.sort((a, b) => a.time.localeCompare(b.time));
                const latlngs = points.map(p => [p.lat, p.lng]);
                const color = getDogColor(dog);

                const polyline = L.polyline(latlngs, {
                    color: color,
                    weight: 3,
                    opacity: 0.7,
                    dashArray: '8 6',
                    lineJoin: 'round'
                });
                polyline.bindTooltip(`Route: ${escHtml(dog)}`, { sticky: true });
                routesLayer.addLayer(polyline);
            });

            renderLegend();

            if (bounds.length > 0) {
                map.fitBounds(bounds, { padding: [40, 40], maxZoom: 15 });
            }

        } catch (err) {
            console.error(err);
            showToast('Fehler beim Laden der GPS-Daten', true);
        }
    }

    function renderLegend() {
        legendEl.innerHTML = '';
        Object.entries(dogColorMap)
            .sort(([a], [b]) => a.localeCompare(b, 'de'))
            .forEach(([dog, color]) => {
                const item = document.createElement('span');
                item.className = 'legend-item';
                item.innerHTML = `<span class="legend-dot" style="background:${color}"></span> ${escHtml(dog)}`;
                legendEl.appendChild(item);
            });
    }

    // ── Helpers ──────────────────────────────────────────────────
    function escHtml(s) {
        const d = document.createElement('div');
        d.textContent = s || '';
        return d.innerHTML;
    }

    function formatDate(iso) {
        if (!iso) return '—';
        try {
            return new Date(iso).toLocaleString('de-DE', {
                day: '2-digit', month: '2-digit', year: 'numeric',
                hour: '2-digit', minute: '2-digit', second: '2-digit'
            });
        } catch { return iso; }
    }

    function showToast(msg, isError) {
        clearTimeout(toastTimeout);
        toastEl.textContent = msg;
        toastEl.className = 'toast' + (isError ? ' error' : '');
        toastTimeout = setTimeout(() => toastEl.classList.add('hidden'), 3000);
    }

    // ── Start ────────────────────────────────────────────────────
    loadAndDisplay();
})();
