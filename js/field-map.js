/* js/field-map.js — Feld-Karte (identisch zu my-map.js) */
(function () {
    'use strict';

    const API_BASE = FT_AUTH.getApiBase();

    const markerCountEl = document.getElementById('markerCount');
    const filterInfoEl = document.getElementById('filterInfo');
    const legendEl = document.getElementById('legend');
    const toggleRoutesEl = document.getElementById('toggleRoutes');
    const toggleLocationEl = document.getElementById('toggleLocation');
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
        return;
    }

    // Resolve display names for header
    (async function resolveFilterInfo() {
        let nameDisplay = filterName;
        let dogDisplay = filterDog;
        try {
            const [verifyRes, dogsRes] = await Promise.all([
                fetch(`${API_BASE}/auth/verify`, { headers: FT_AUTH.adminHeaders() }),
                fetch(`${API_BASE}/lost-dogs`, { headers: FT_AUTH.publicHeaders() })
            ]);
            if (verifyRes.ok) {
                const v = await verifyRes.json();
                if (v.displayName) nameDisplay = v.displayName;
            }
            if (dogsRes.ok) {
                const dogs = await dogsRes.json();
                const match = dogs.find(d => d.rowKey === filterDog);
                if (match) dogDisplay = match.displayName;
            }
        } catch {}
        filterInfoEl.textContent = `${nameDisplay} / ${dogDisplay}`;
    })();

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

    /** Category SVG symbols – loaded from API */
    let categorySymbols = {};

    function colorIcon(color, category) {
        const iconKey = categorySymbols[category] || 'default';
        const inner = resolveIconSvg(iconKey);
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
        attribution: '&copy; <a href="https://opentopomap.org">OpenTopoMap</a> (CC-BY-SA)',
        maxZoom: 17
    });
    const cartoPositron = L.tileLayer('https://{s}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png', {
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OSM</a> &copy; <a href="https://carto.com/attributions">CARTO</a>',
        maxZoom: 20
    });
    const cartoDarkMatter = L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png', {
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OSM</a> &copy; <a href="https://carto.com/attributions">CARTO</a>',
        maxZoom: 20
    });

    osmLayer.addTo(map);

    L.control.layers({
        'OSM Straße': osmLayer,
        'OpenTopoMap': topoLayer,
        'CARTO Positron': cartoPositron,
        'CARTO Dark Matter': cartoDarkMatter,
        'Esri Satellit': satelliteLayer
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
    let cachedCategorySymbols = null;
    async function loadAndDisplay() {
        try {
            // Load category SVG symbols (cached)
            if (!cachedCategorySymbols) {
                try {
                    const catRes = await fetch(`${API_BASE}/categories`, { headers: FT_AUTH.publicHeaders() });
                    if (catRes.ok) {
                        const cats = await catRes.json();
                        cachedCategorySymbols = {};
                        cats.forEach(c => { if (c.svgSymbol) cachedCategorySymbols[c.displayName] = c.svgSymbol; });
                    }
                } catch { /* use defaults */ }
            }
            categorySymbols = cachedCategorySymbols || {};

            const params = new URLSearchParams();
            params.set('pageSize', 'all');
            const showMine = document.getElementById('scopeFilter').value === 'mine';
            if (showMine) params.set('name', filterName);
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

                const deleteBtnHtml = r.partitionKey === filterName
                    ? `<div style="margin-top:6px;"><button class="popup-delete-btn" data-pk="${escHtml(r.partitionKey)}" data-rk="${escHtml(r.rowKey)}">L\u00f6schen</button></div>`
                    : '';

                marker.bindPopup(
                    `<strong>${escHtml(r.name)}</strong><br>` +
                    `🐕 ${escHtml(r.lostDog)}` +
                    categoryHtml + commentHtml + `<br>` +
                    `📍 ${r.latitude.toFixed(6)}, ${r.longitude.toFixed(6)}<br>` +
                    `🎯 ±${r.accuracy.toFixed(0)} m<br>` +
                    `🕐 ${formatDate(r.recordedAt)}` +
                    photoHtml + navHtml + deleteBtnHtml,
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
        return (s || '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
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
    document.getElementById('scopeFilter').addEventListener('change', () => {
        clusterGroup.clearLayers();
        routesLayer.clearLayers();
        legendEl.innerHTML = '';
        Object.keys(dogColorMap).forEach(k => delete dogColorMap[k]);
        colorIdx = 0;
        loadAndDisplay();
    });
    // ── Delete handler (event delegation on map popups) ──────────
    document.addEventListener('click', async e => {
        const btn = e.target.closest('.popup-delete-btn');
        if (!btn) return;
        const pk = btn.dataset.pk;
        const rk = btn.dataset.rk;
        if (!confirm('Diesen Eintrag wirklich löschen?')) return;
        btn.disabled = true;
        btn.textContent = '⏳…';
        try {
            const res = await fetch(`${API_BASE}/my-records/delete`, {
                method: 'POST',
                headers: FT_AUTH.publicHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify({ name: filterName, lostDog: filterDog, keys: [{ partitionKey: pk, rowKey: rk }] })
            });
            if (!res.ok) throw new Error();
            showToast('Eintrag gelöscht');
            // Reload map data
            clusterGroup.clearLayers();
            routesLayer.clearLayers();
            legendEl.innerHTML = '';
            Object.keys(dogColorMap).forEach(k => delete dogColorMap[k]);
            colorIdx = 0;
            await loadAndDisplay();
        } catch {
            showToast('Fehler beim Löschen', true);
            btn.disabled = false;
            btn.textContent = '🗑️ Löschen';
        }
    });

    // ── Live Location Tracking ───────────────────────────────────
    let liveMarker = null;
    let liveCircle = null;
    let liveWatchId = null;

    const liveIcon = L.divIcon({
        className: '',
        html: '<div class="live-location-dot"></div>',
        iconSize: [18, 18],
        iconAnchor: [9, 9]
    });

    function startTracking() {
        if (!navigator.geolocation) {
            showToast('Geolocation wird nicht unterstützt', true);
            toggleLocationEl.checked = false;
            return;
        }
        liveWatchId = navigator.geolocation.watchPosition(
            pos => {
                const lat = pos.coords.latitude;
                const lng = pos.coords.longitude;
                const acc = pos.coords.accuracy;

                if (liveMarker) {
                    liveMarker.setLatLng([lat, lng]);
                    liveCircle.setLatLng([lat, lng]).setRadius(acc);
                } else {
                    liveMarker = L.marker([lat, lng], { icon: liveIcon, zIndexOffset: 1000 })
                        .bindPopup('📍 Mein Standort')
                        .addTo(map);
                    liveCircle = L.circle([lat, lng], {
                        radius: acc,
                        className: 'live-accuracy-circle',
                        interactive: false
                    }).addTo(map);
                    // Center map on first fix
                    map.setView([lat, lng], Math.max(map.getZoom(), 15));
                }
            },
            err => {
                showToast('Standort nicht verfügbar', true);
                toggleLocationEl.checked = false;
                stopTracking();
            },
            { enableHighAccuracy: true, maximumAge: 5000, timeout: 15000 }
        );
    }

    function stopTracking() {
        if (liveWatchId !== null) {
            navigator.geolocation.clearWatch(liveWatchId);
            liveWatchId = null;
        }
        if (liveMarker) { map.removeLayer(liveMarker); liveMarker = null; }
        if (liveCircle) { map.removeLayer(liveCircle); liveCircle = null; }
    }

    toggleLocationEl.addEventListener('change', () => {
        if (toggleLocationEl.checked) {
            startTracking();
        } else {
            stopTracking();
        }
    });
})();
