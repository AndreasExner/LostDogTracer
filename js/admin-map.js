(function () {
    'use strict';

    const API_BASE = FT_AUTH.getApiBase();

    const markerCountEl = document.getElementById('markerCount');
    const filterDogEl = document.getElementById('filterDog');
    const filterNameEl = document.getElementById('filterName');
    const catBtnEl = document.getElementById('filterCategoryBtn');
    const catDropdownEl = document.getElementById('filterCategoryDropdown');
    const catWrapEl = document.getElementById('categoryMultiSelect');
    const legendEl = document.getElementById('legend');
    const toggleCirclesEl = document.getElementById('toggleCircles');
    const toggleRoutesEl = document.getElementById('toggleRoutes');
    const toastEl = document.getElementById('toast');
    let toastTimeout = null;

    // ── Category multi-select helpers ────────────────────────────
    function getSelectedCategories() {
        return [...catDropdownEl.querySelectorAll('input:checked')].map(cb => cb.value);
    }
    function updateCatBtnText() {
        const sel = getSelectedCategories();
        if (sel.length === 0) catBtnEl.textContent = 'Alle Kategorien';
        else if (sel.length === 1) catBtnEl.textContent = sel[0];
        else catBtnEl.textContent = sel.length + ' Kategorien';
    }
    catBtnEl.addEventListener('click', e => { e.stopPropagation(); catDropdownEl.classList.toggle('hidden'); });
    document.addEventListener('click', e => { if (!catWrapEl.contains(e.target)) catDropdownEl.classList.add('hidden'); });

    // ── Color palette for different dogs ─────────────────────────
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
            // Document / flyer icon
            `<rect x="7" y="5" width="10" height="13" rx="1" fill="none" stroke="#fff" stroke-width="1.5"/>` +
            `<line x1="9.5" y1="9" x2="14.5" y2="9" stroke="#fff" stroke-width="1.2"/>` +
            `<line x1="9.5" y1="12" x2="14.5" y2="12" stroke="#fff" stroke-width="1.2"/>` +
            `<line x1="9.5" y1="15" x2="12.5" y2="15" stroke="#fff" stroke-width="1.2"/>`,
        'Sichtung':
            // Eye icon
            `<ellipse cx="12" cy="12" rx="6" ry="4" fill="none" stroke="#fff" stroke-width="1.5"/>` +
            `<circle cx="12" cy="12" r="2" fill="#fff"/>`,
        'Entlaufort':
            // Paw print icon
            `<circle cx="9" cy="9" r="1.5" fill="#fff"/>` +
            `<circle cx="15" cy="9" r="1.5" fill="#fff"/>` +
            `<circle cx="7" cy="13" r="1.3" fill="#fff"/>` +
            `<circle cx="17" cy="13" r="1.3" fill="#fff"/>` +
            `<ellipse cx="12" cy="15" rx="3" ry="2.2" fill="#fff"/>`,
        'Standort Falle':
            // Crosshair / target icon
            `<circle cx="12" cy="12" r="5" fill="none" stroke="#fff" stroke-width="1.5"/>` +
            `<circle cx="12" cy="12" r="1.5" fill="#fff"/>` +
            `<line x1="12" y1="5" x2="12" y2="8" stroke="#fff" stroke-width="1.3"/>` +
            `<line x1="12" y1="16" x2="12" y2="19" stroke="#fff" stroke-width="1.3"/>` +
            `<line x1="5" y1="12" x2="8" y2="12" stroke="#fff" stroke-width="1.3"/>` +
            `<line x1="16" y1="12" x2="19" y2="12" stroke="#fff" stroke-width="1.3"/>`
    };

    /** Create a colored marker icon with a category-specific inner symbol */
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

    // ── Read filter from URL params ──────────────────────────────
    const urlParams = new URLSearchParams(window.location.search);
    let filterDog = urlParams.get('lostDog') || '';
    let filterName = urlParams.get('name') || '';
    let filterCategory = urlParams.get('category') || '';
    if (filterDog) filterDogEl.value = filterDog;
    if (filterName) filterNameEl.value = filterName;
    // Category from URL will be applied after first data load

    // ── Init map ─────────────────────────────────────────────────
    const map = L.map('map').setView([51.1657, 10.4515], 6);

    // Base layers (layer switcher)
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

    // Google Maps layers (via GoogleMutant)
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

    // Scale bar
    L.control.scale({ metric: true, imperial: false, position: 'bottomleft' }).addTo(map);

    // Marker cluster
    const clusterGroup = L.markerClusterGroup({
        maxClusterRadius: 40,
        spiderfyOnMaxZoom: true,
        showCoverageOnHover: false
    });
    map.addLayer(clusterGroup);

    // Optional overlay layers
    const circlesLayer = L.layerGroup();
    const routesLayer = L.layerGroup();

    // ── Filter change handler ────────────────────────────────────
    function onFilterChange() {
        filterDog = filterDogEl.value;
        filterName = filterNameEl.value;
        filterCategory = getSelectedCategories().join(',');
        // Reset color mapping for consistency
        Object.keys(dogColorMap).forEach(k => delete dogColorMap[k]);
        colorIdx = 0;
        // Clear layers
        clusterGroup.clearLayers();
        circlesLayer.clearLayers();
        routesLayer.clearLayers();
        legendEl.innerHTML = '';
        loadAndDisplay();
    }
    filterDogEl.addEventListener('change', onFilterChange);
    filterNameEl.addEventListener('change', onFilterChange);
    // Category multi-select: events attached in loadAndDisplay populate step

    // ── Toggle handlers ──────────────────────────────────────────
    toggleCirclesEl.addEventListener('change', () => {
        if (toggleCirclesEl.checked) {
            map.addLayer(circlesLayer);
        } else {
            map.removeLayer(circlesLayer);
        }
    });

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
            if (filterDog) params.set('lostDog', filterDog);
            if (filterName) params.set('name', filterName);
            if (filterCategory) params.set('category', filterCategory);

            const res = await fetch(`${API_BASE}/manage/gps-records?${params}`, {
                headers: FT_AUTH.adminHeaders()
            });
            if (res.status === 401) { FT_AUTH.logout(); location.href = 'admin.html'; return; }
            if (!res.ok) throw new Error();

            const data = await res.json();
            const records = data.records || [];

            // Update info badge
            markerCountEl.textContent = `${records.length} Standort${records.length !== 1 ? 'e' : ''}`;

            // Populate dog filter dropdown (keep current selection)
            const currentDogs = data.lostDogs || [];
            const currentVal = filterDogEl.value;
            while (filterDogEl.options.length > 1) filterDogEl.remove(1);
            currentDogs.forEach(d => {
                const opt = document.createElement('option');
                opt.value = d; opt.textContent = d;
                filterDogEl.appendChild(opt);
            });
            filterDogEl.value = currentVal;

            // Populate name filter dropdown (keep current selection)
            const currentNames = data.names || [];
            const currentNameVal = filterNameEl.value;
            while (filterNameEl.options.length > 1) filterNameEl.remove(1);
            currentNames.forEach(n => {
                const opt = document.createElement('option');
                opt.value = n; opt.textContent = n;
                filterNameEl.appendChild(opt);
            });
            filterNameEl.value = currentNameVal;

            // Populate category filter dropdown (keep current selection)
            const currentCats = data.categories || [];
            const currentCatSel = getSelectedCategories();
            catDropdownEl.innerHTML = '';
            currentCats.forEach(c => {
                const label = document.createElement('label');
                label.className = 'multi-select-item';
                const cb = document.createElement('input');
                cb.type = 'checkbox';
                cb.value = c;
                // On first load, pre-select from URL param
                if (currentCatSel.length > 0) {
                    if (currentCatSel.includes(c)) cb.checked = true;
                } else if (filterCategory && filterCategory.split(',').includes(c)) {
                    cb.checked = true;
                }
                cb.addEventListener('change', () => { updateCatBtnText(); onFilterChange(); });
                label.appendChild(cb);
                label.appendChild(document.createTextNode(' ' + c));
                catDropdownEl.appendChild(label);
            });
            updateCatBtnText();

            if (records.length === 0) {
                showToast('Keine GPS-Daten vorhanden', true);
                return;
            }

            // Group records by dog for polylines
            const dogRecords = {};

            // Add markers + accuracy circles
            const bounds = [];
            records.forEach(r => {
                if (!r.latitude || !r.longitude) return;

                const color = getDogColor(r.lostDog);

                // ── Marker ──
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

                const editBtnHtml = `<div style="margin-top:6px;"><button class="btn btn-primary btn-sm edit-marker-btn" data-pk="${escHtml(r.partitionKey)}" data-rk="${escHtml(r.rowKey)}" style="font-size:0.75rem;padding:3px 10px;">✏️ Bearbeiten</button></div>`;

                marker.bindPopup(
                    `<strong>${escHtml(r.name)}</strong><br>` +
                    `🐕 ${escHtml(r.lostDog)}` +
                    categoryHtml + commentHtml + `<br>` +
                    `📍 ${r.latitude.toFixed(6)}, ${r.longitude.toFixed(6)}<br>` +
                    `🎯 ±${r.accuracy.toFixed(0)} m<br>` +
                    `🕐 ${formatDate(r.recordedAt)}` +
                    photoHtml + navHtml + editBtnHtml,
                    { maxWidth: 280 }
                );

                // Store record data on marker for editing
                marker._ftRecord = r;

                clusterGroup.addLayer(marker);
                bounds.push([r.latitude, r.longitude]);

                // ── Accuracy circle ──
                if (r.accuracy > 0) {
                    const circle = L.circle([r.latitude, r.longitude], {
                        radius: r.accuracy,
                        color: color,
                        fillColor: color,
                        fillOpacity: 0.08,
                        weight: 1.5,
                        opacity: 0.4,
                        interactive: false
                    });
                    circlesLayer.addLayer(circle);
                }

                // ── Collect for polylines ──
                if (!dogRecords[r.lostDog]) dogRecords[r.lostDog] = [];
                dogRecords[r.lostDog].push({
                    lat: r.latitude,
                    lng: r.longitude,
                    time: r.recordedAt || ''
                });
            });

            // ── Build polylines per dog (sorted by time) ──
            Object.entries(dogRecords).forEach(([dog, points]) => {
                if (points.length < 2) return;

                // Sort chronologically
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

                // Arrow decorations via small direction markers
                polyline.bindTooltip(`Route: ${escHtml(dog)}`, { sticky: true });
                routesLayer.addLayer(polyline);
            });

            // Build legend
            renderLegend();

            // Fit map to markers
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

    // ── Edit marker position ─────────────────────────────────────
    const editBar = document.getElementById('editBar');
    const editCoordsHint = document.getElementById('editCoordsHint');
    const editSaveBtn = document.getElementById('editSaveBtn');
    const editCancelBtn = document.getElementById('editCancelBtn');

    let editingMarker = null;
    let editOriginalLatLng = null;
    let editRecord = null;

    function updateCoordsHint() {
        if (!editingMarker) return;
        const pos = editingMarker.getLatLng();
        editCoordsHint.textContent = `📍 ${pos.lat.toFixed(6)}, ${pos.lng.toFixed(6)}`;
    }

    // Delegate click on edit button inside popup
    document.addEventListener('click', (e) => {
        const btn = e.target.closest('.edit-marker-btn');
        if (!btn) return;
        e.preventDefault();

        const pk = btn.dataset.pk;
        const rk = btn.dataset.rk;

        let targetMarker = null;
        clusterGroup.eachLayer(m => {
            if (m._ftRecord && m._ftRecord.partitionKey === pk && m._ftRecord.rowKey === rk) {
                targetMarker = m;
            }
        });
        if (!targetMarker) return;

        map.closePopup();

        editingMarker = targetMarker;
        editRecord = targetMarker._ftRecord;
        editOriginalLatLng = targetMarker.getLatLng();

        targetMarker.dragging.enable();
        targetMarker.on('drag', updateCoordsHint);
        updateCoordsHint();
        editBar.classList.remove('hidden');
    });

    function cancelEdit() {
        if (editingMarker) {
            editingMarker.setLatLng(editOriginalLatLng);
            editingMarker.dragging.disable();
            editingMarker.off('drag', updateCoordsHint);
        }
        editingMarker = null;
        editRecord = null;
        editBar.classList.add('hidden');
    }

    editCancelBtn.addEventListener('click', cancelEdit);

    editSaveBtn.addEventListener('click', async () => {
        if (!editingMarker || !editRecord) return;

        const newPos = editingMarker.getLatLng();
        const payload = {
            keys: [{ partitionKey: editRecord.partitionKey, rowKey: editRecord.rowKey }],
            latitude: newPos.lat,
            longitude: newPos.lng
        };

        editSaveBtn.disabled = true;
        editSaveBtn.textContent = 'Speichert…';

        try {
            const res = await fetch(`${API_BASE}/manage/gps-records/update`, {
                method: 'POST',
                headers: FT_AUTH.adminHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify(payload)
            });
            if (res.status === 401) { FT_AUTH.logout(); location.href = 'admin.html'; return; }
            if (!res.ok) throw new Error();

            showToast('Position aktualisiert');

            editingMarker.dragging.disable();
            editingMarker.off('drag', updateCoordsHint);
            editingMarker = null;
            editRecord = null;
            editBar.classList.add('hidden');

            // Reload map data
            clusterGroup.clearLayers();
            circlesLayer.clearLayers();
            routesLayer.clearLayers();
            legendEl.innerHTML = '';
            Object.keys(dogColorMap).forEach(k => delete dogColorMap[k]);
            colorIdx = 0;
            await loadAndDisplay();
        } catch {
            showToast('Fehler beim Speichern', true);
        } finally {
            editSaveBtn.disabled = false;
            editSaveBtn.textContent = 'Speichern';
        }
    });

    // ── Start ────────────────────────────────────────────────────
    loadAndDisplay();
})();
