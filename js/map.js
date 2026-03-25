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
    const toggleEquipmentEl = document.getElementById('toggleEquipment');
    const toggleRoutesEl = document.getElementById('toggleRoutes');
    const toastEl = document.getElementById('toast');
    let toastTimeout = null;

    // ── Category multi-select helpers ────────────────────────────
    function getSelectedCategories() {
        return [...catDropdownEl.querySelectorAll('input:checked')].map(cb => cb.value);
    }
    function updateCatBtnText() {
        const checked = [...catDropdownEl.querySelectorAll('input:checked')];
        if (checked.length === 0) catBtnEl.textContent = 'Alle Kategorien';
        else if (checked.length === 1) catBtnEl.textContent = checked[0].parentElement.textContent.trim();
        else catBtnEl.textContent = checked.length + ' Kategorien';
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

    /** Category SVG symbols – loaded from API */
    let categorySymbols = {};

    /** Create a colored marker icon with a category-specific inner symbol */
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
    const equipmentLayer = L.layerGroup();
    const routesLayer = L.layerGroup();
    let equipmentLoaded = false;

    // ── Filter change handler (debounced) ────────────────────────
    let filterTimer = null;
    let catDropdownBuilt = false;
    function onFilterChange() {
        clearTimeout(filterTimer);
        filterTimer = setTimeout(() => {
            filterDog = filterDogEl.value;
            filterName = filterNameEl.value;
            filterCategory = getSelectedCategories().join(',');
            Object.keys(dogColorMap).forEach(k => delete dogColorMap[k]);
            colorIdx = 0;
            clusterGroup.clearLayers();
            routesLayer.clearLayers();
            legendEl.innerHTML = '';
            loadAndDisplay();
        }, 500);
    }
    filterDogEl.addEventListener('change', onFilterChange);
    filterNameEl.addEventListener('change', onFilterChange);
    // Category multi-select: events attached in loadAndDisplay populate step

    // ── Toggle handlers ──────────────────────────────────────────
    toggleEquipmentEl.addEventListener('change', async () => {
        if (toggleEquipmentEl.checked) {
            if (!equipmentLoaded) await loadEquipmentMarkers();
            map.addLayer(equipmentLayer);
        } else {
            map.removeLayer(equipmentLayer);
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
            if (filterDog) params.set('lostDog', filterDog);
            if (filterName) params.set('name', filterName);
            if (filterCategory) params.set('category', filterCategory);

            const res = await fetch(`${API_BASE}/manage/gps-records?${params}`, {
                headers: FT_AUTH.adminHeaders()
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
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
                opt.value = d.rowKey || d; opt.textContent = d.displayName || d;
                filterDogEl.appendChild(opt);
            });
            filterDogEl.value = currentVal;

            // Populate name filter dropdown (keep current selection)
            const currentNames = data.names || [];
            const currentNameVal = filterNameEl.value;
            while (filterNameEl.options.length > 1) filterNameEl.remove(1);
            currentNames.forEach(n => {
                const opt = document.createElement('option');
                opt.value = n.rowKey || n; opt.textContent = n.displayName || n;
                filterNameEl.appendChild(opt);
            });
            filterNameEl.value = currentNameVal;

            // Populate category filter dropdown (only on first load — load ALL categories, not just filtered ones)
            if (!catDropdownBuilt) {
                try {
                    const allCatsRes = await fetch(`${API_BASE}/categories`, { headers: FT_AUTH.publicHeaders() });
                    if (allCatsRes.ok) {
                        const allCats = await allCatsRes.json();
                        catDropdownEl.innerHTML = '';
                        allCats.forEach(c => {
                            const key = c.rowKey || c;
                            const display = c.displayName || c;
                            const label = document.createElement('label');
                            label.className = 'multi-select-item';
                            const cb = document.createElement('input');
                            cb.type = 'checkbox';
                            cb.value = key;
                            if (filterCategory && filterCategory.split(',').includes(key)) {
                                cb.checked = true;
                            }
                            cb.addEventListener('change', () => { updateCatBtnText(); onFilterChange(); });
                            label.appendChild(cb);
                            label.appendChild(document.createTextNode(' ' + display));
                            catDropdownEl.appendChild(label);
                        });
                        updateCatBtnText();
                        catDropdownBuilt = true;
                    }
                } catch { /* use data.categories as fallback */ }
            }

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

    // ── Edit marker position ─────────────────────────────────────
    const editBar = document.getElementById('editBar');
    const editCoordsHint = document.getElementById('editCoordsHint');
    const editSaveBtn = document.getElementById('editSaveBtn');
    const editCancelBtn = document.getElementById('editCancelBtn');
    const editDeleteBtn = document.getElementById('editDeleteBtn');

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

    editDeleteBtn.addEventListener('click', async () => {
        if (!editRecord) return;
        if (!confirm('Diesen Eintrag wirklich löschen?')) return;

        editDeleteBtn.disabled = true;
        editDeleteBtn.textContent = 'Löscht…';

        try {
            const res = await fetch(`${API_BASE}/manage/gps-records/delete`, {
                method: 'POST',
                headers: FT_AUTH.adminHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify([{ partitionKey: editRecord.partitionKey, rowKey: editRecord.rowKey }])
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) throw new Error();

            showToast('Eintrag gelöscht');
            cancelEdit();

            clusterGroup.clearLayers();
            routesLayer.clearLayers();
            legendEl.innerHTML = '';
            Object.keys(dogColorMap).forEach(k => delete dogColorMap[k]);
            colorIdx = 0;
            await loadAndDisplay();
        } catch {
            showToast('Fehler beim Löschen', true);
        } finally {
            editDeleteBtn.disabled = false;
            editDeleteBtn.textContent = 'Löschen';
        }
    });

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
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) throw new Error();

            showToast('Position aktualisiert');

            editingMarker.dragging.disable();
            editingMarker.off('drag', updateCoordsHint);
            editingMarker = null;
            editRecord = null;
            editBar.classList.add('hidden');

            // Reload map data
            clusterGroup.clearLayers();
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

    // ── Equipment layer ──────────────────────────────────────────
    function equipmentIcon() {
        return L.divIcon({
            className: '',
            html: `<svg width="28" height="28" viewBox="0 0 28 28" xmlns="http://www.w3.org/2000/svg">
                <rect x="2" y="2" width="24" height="24" rx="4" fill="#6e6e73" stroke="#fff" stroke-width="2"/>
                <circle cx="14" cy="14" r="5" fill="none" stroke="#fff" stroke-width="1.5"/>
                <circle cx="14" cy="14" r="1.5" fill="#fff"/>
                <line x1="14" y1="7" x2="14" y2="9.5" stroke="#fff" stroke-width="1.3"/>
                <line x1="14" y1="18.5" x2="14" y2="21" stroke="#fff" stroke-width="1.3"/>
                <line x1="7" y1="14" x2="9.5" y2="14" stroke="#fff" stroke-width="1.3"/>
                <line x1="18.5" y1="14" x2="21" y2="14" stroke="#fff" stroke-width="1.3"/>
            </svg>`,
            iconSize: [28, 28],
            iconAnchor: [14, 14],
            popupAnchor: [0, -14]
        });
    }

    async function loadEquipmentMarkers() {
        try {
            const res = await fetch(`${API_BASE}/manage/equipment`, {
                headers: FT_AUTH.adminHeaders()
            });
            if (!res.ok) { showToast('Equipment konnte nicht geladen werden', true); return; }
            const items = await res.json();
            equipmentLayer.clearLayers();

            items.forEach(item => {
                if (!item.latitude || !item.longitude) return;
                const locHtml = item.location ? `<br>📍 ${escHtml(item.location)}` : '';
                const userHtml = item.userName ? `<br>👤 ${escHtml(item.userName)}` : '';
                const commentHtml = item.comment ? `<br>💬 ${escHtml(item.comment)}` : '';

                const marker = L.marker([item.latitude, item.longitude], { icon: equipmentIcon() });
                marker.bindPopup(
                    `<strong>⚙️ ${escHtml(item.displayName)}</strong>` +
                    locHtml + userHtml + commentHtml,
                    { maxWidth: 250 }
                );
                equipmentLayer.addLayer(marker);
            });

            equipmentLoaded = true;
        } catch {
            showToast('Fehler beim Laden des Equipments', true);
        }
    }
})();
