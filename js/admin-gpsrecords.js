(function () {
    'use strict';

    const API_BASE = FT_AUTH.getApiBase();

    const filterDogEl = document.getElementById('filterDog');
    const filterNameEl = document.getElementById('filterName');
    const catBtnEl = document.getElementById('filterCategoryBtn');
    const catDropdownEl = document.getElementById('filterCategoryDropdown');
    const catWrapEl = document.getElementById('categoryMultiSelect');
    const sortFieldEl = document.getElementById('sortField');
    const pageSizeEl = document.getElementById('pageSize');
    const selectAllEl = document.getElementById('selectAll');
    const bodyEl = document.getElementById('recordsBody');
    const pageInfoEl = document.getElementById('pageInfo');
    const pageBtnsEl = document.getElementById('pageButtons');
    const deleteBtn = document.getElementById('deleteSelectedBtn');
    const showMapBtn = document.getElementById('showMapBtn');
    const exportCsvBtn = document.getElementById('exportCsvBtn');
    const exportKmlBtn = document.getElementById('exportKmlBtn');
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

    let currentPage = 1;
    let data = { records: [], totalCount: 0, page: 1, pageSize: 20, totalPages: 1, lostDogs: [] };

    // ── Load records ─────────────────────────────────────────────
    async function loadRecords() {
        bodyEl.innerHTML = '<tr><td colspan="10" style="color:#6e6e73;text-align:center;padding:2rem">Lädt…</td></tr>';
        const ps = pageSizeEl.value;
        const dog = filterDogEl.value;
        const name = filterNameEl.value;
        const cat = getSelectedCategories().join(',');
        const params = new URLSearchParams();
        params.set('pageSize', ps);
        params.set('page', currentPage);
        if (dog) params.set('lostDog', dog);
        if (name) params.set('name', name);
        if (cat) params.set('category', cat);

        try {
            const res = await fetch(`${API_BASE}/manage/gps-records?${params}`, { headers: FT_AUTH.adminHeaders() });
            if (res.status === 401) { FT_AUTH.logout(); location.href = 'admin.html'; return; }
            if (!res.ok) throw new Error();
            data = await res.json();
            populateFilter(data.lostDogs, data.names || [], data.categories || []);
            sortRecords();
            renderTable();
            renderPagination();
            updateDeleteButton();
        } catch {
            bodyEl.innerHTML = '<tr><td colspan="10" style="color:#ff3b30;text-align:center;padding:2rem">Fehler beim Laden</td></tr>';
        }
    }

    function populateFilter(dogs, names, categories) {
        const currentDog = filterDogEl.value;
        while (filterDogEl.options.length > 1) filterDogEl.remove(1);
        dogs.forEach(d => {
            const opt = document.createElement('option');
            opt.value = d;
            opt.textContent = d;
            filterDogEl.appendChild(opt);
        });
        filterDogEl.value = currentDog;

        const currentName = filterNameEl.value;
        while (filterNameEl.options.length > 1) filterNameEl.remove(1);
        names.forEach(n => {
            const opt = document.createElement('option');
            opt.value = n;
            opt.textContent = n;
            filterNameEl.appendChild(opt);
        });
        filterNameEl.value = currentName;

        const currentCat = getSelectedCategories();
        catDropdownEl.innerHTML = '';
        categories.forEach(c => {
            const label = document.createElement('label');
            label.className = 'multi-select-item';
            const cb = document.createElement('input');
            cb.type = 'checkbox';
            cb.value = c;
            if (currentCat.includes(c)) cb.checked = true;
            cb.addEventListener('change', () => { updateCatBtnText(); currentPage = 1; loadRecords(); });
            label.appendChild(cb);
            label.appendChild(document.createTextNode(' ' + c));
            catDropdownEl.appendChild(label);
        });
        updateCatBtnText();
    }

    // ── Render table ─────────────────────────────────────────────
    function renderTable() {
        bodyEl.innerHTML = '';
        selectAllEl.checked = false;

        if (data.records.length === 0) {
            bodyEl.innerHTML = '<tr><td colspan="10" style="color:#6e6e73;text-align:center;padding:2rem">Keine Einträge</td></tr>';
            return;
        }

        data.records.forEach(r => {
            const tr = document.createElement('tr');
            const photoCell = r.photoUrl
                ? `<td><img src="${esc(r.photoUrl)}" class="thumb" alt="Foto" onclick="document.getElementById('lightboxImg').src=this.src;document.getElementById('lightbox').classList.remove('hidden');"></td>`
                : '<td class="no-photo">—</td>';
            tr.innerHTML = `
                <td><input type="checkbox" class="row-cb" data-pk="${esc(r.partitionKey)}" data-rk="${esc(r.rowKey)}"></td>
                <td>${esc(r.name)}</td>
                <td>${esc(r.lostDog)}</td>
                <td>${formatDate(r.recordedAt)}</td>
                <td>${esc(r.category || '')}</td>
                <td>${esc(r.comment || '')}</td>
                ${photoCell}
                <td>${r.latitude.toFixed(6)}</td>
                <td>${r.longitude.toFixed(6)}</td>
                <td>${r.accuracy.toFixed(1)} m</td>`;
            bodyEl.appendChild(tr);
        });
    }

    // ── Pagination ───────────────────────────────────────────────
    function renderPagination() {
        pageInfoEl.textContent = `${data.totalCount} Einträge — Seite ${data.page} von ${data.totalPages}`;
        pageBtnsEl.innerHTML = '';

        if (data.totalPages <= 1) return;

        const addBtn = (label, page, disabled, active) => {
            const b = document.createElement('button');
            b.textContent = label;
            b.disabled = disabled;
            if (active) b.classList.add('active');
            b.addEventListener('click', () => { currentPage = page; loadRecords(); });
            pageBtnsEl.appendChild(b);
        };

        addBtn('«', 1, currentPage === 1, false);
        addBtn('‹', currentPage - 1, currentPage === 1, false);

        // show max 5 page numbers around current
        let start = Math.max(1, currentPage - 2);
        let end = Math.min(data.totalPages, start + 4);
        start = Math.max(1, end - 4);

        for (let i = start; i <= end; i++) {
            addBtn(String(i), i, false, i === currentPage);
        }

        addBtn('›', currentPage + 1, currentPage >= data.totalPages, false);
        addBtn('»', data.totalPages, currentPage >= data.totalPages, false);
    }

    // ── Select all / checkboxes ──────────────────────────────────
    selectAllEl.addEventListener('change', () => {
        document.querySelectorAll('.row-cb').forEach(cb => { cb.checked = selectAllEl.checked; });
        updateDeleteButton();
    });
    bodyEl.addEventListener('change', e => {
        if (e.target.classList.contains('row-cb')) updateDeleteButton();
    });

    function getSelected() {
        return [...document.querySelectorAll('.row-cb:checked')].map(cb => ({
            partitionKey: cb.dataset.pk,
            rowKey: cb.dataset.rk
        }));
    }

    function updateDeleteButton() {
        const n = getSelected().length;
        deleteBtn.disabled = n === 0;
        deleteBtn.textContent = n > 0 ? `Ausgewählte löschen (${n})` : 'Ausgewählte löschen';
    }

    // ── Delete selected ──────────────────────────────────────────
    deleteBtn.addEventListener('click', async () => {
        const sel = getSelected();
        if (sel.length === 0) return;
        if (!confirm(`${sel.length} Einträge wirklich löschen?`)) return;

        deleteBtn.disabled = true;
        try {
            const res = await fetch(`${API_BASE}/manage/gps-records/delete`, {
                method: 'POST',
                headers: FT_AUTH.adminHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify(sel)
            });
            if (res.status === 401) { FT_AUTH.logout(); location.href = 'admin.html'; return; }
            if (!res.ok) throw new Error();
            const result = await res.json();
            showToast(`${result.deleted} Einträge gelöscht`);
            await loadRecords();
        } catch {
            showToast('Fehler beim Löschen', true);
        }
    });

    // ── CSV Export ────────────────────────────────────────────────
    exportCsvBtn.addEventListener('click', () => exportData());
    exportKmlBtn.addEventListener('click', () => exportKml());

    async function exportData() {
        // Fetch ALL records (no pagination) for export
        const dog = filterDogEl.value;
        const name = filterNameEl.value;
        const cat = getSelectedCategories().join(',');
        const params = new URLSearchParams();
        params.set('pageSize', 'all');
        if (dog) params.set('lostDog', dog);
        if (name) params.set('name', name);
        if (cat) params.set('category', cat);

        try {
            showToast('Exportiere…');
            const res = await fetch(`${API_BASE}/manage/gps-records?${params}`, { headers: FT_AUTH.adminHeaders() });
            if (res.status === 401) { FT_AUTH.logout(); location.href = 'admin.html'; return; }
            if (!res.ok) throw new Error();
            const allData = await res.json();

            if (allData.records.length === 0) {
                showToast('Keine Daten zum Exportieren', true);
                return;
            }

            const headers = ['Name', 'Hund', 'Kategorie', 'Kommentar', 'Breitengrad', 'L\u00e4ngengrad', 'Genauigkeit', 'Zeitpunkt', 'Foto-URL'];
            const rows = allData.records.map(r => [
                r.name, r.lostDog,
                r.category || '', r.comment || '',
                r.latitude, r.longitude,
                r.accuracy, r.recordedAt,
                r.photoUrl || ''
            ]);

            downloadCSV(headers, rows);
            showToast('Export abgeschlossen');
        } catch {
            showToast('Export fehlgeschlagen', true);
        }
    }

    function downloadCSV(headers, rows) {
        const BOM = '\uFEFF'; // UTF-8 BOM for Excel
        const sep = ';';
        let csv = BOM + headers.join(sep) + '\n';
        rows.forEach(r => {
            csv += r.map(v => `"${String(v).replace(/"/g, '""')}"`).join(sep) + '\n';
        });
        download(csv, 'GPS-Daten.csv', 'text/csv;charset=utf-8');
    }

    function download(content, filename, mimeType) {
        const blob = new Blob([content], { type: mimeType });
        const a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(a.href);
    }

    // ── KML Export ───────────────────────────────────────────────
    async function exportKml() {
        const dog = filterDogEl.value;
        const name = filterNameEl.value;
        const cat = getSelectedCategories().join(',');
        const params = new URLSearchParams();
        params.set('pageSize', 'all');
        if (dog) params.set('lostDog', dog);
        if (name) params.set('name', name);
        if (cat) params.set('category', cat);

        try {
            showToast('KML wird erstellt…');
            const res = await fetch(`${API_BASE}/manage/gps-records?${params}`, { headers: FT_AUTH.adminHeaders() });
            if (res.status === 401) { FT_AUTH.logout(); location.href = 'admin.html'; return; }
            if (!res.ok) throw new Error();
            const allData = await res.json();

            if (allData.records.length === 0) {
                showToast('Keine Daten zum Exportieren', true);
                return;
            }

            // Color palette matching admin-map.js
            const COLORS = [
                'ff0071e3', 'ff3b30ff', 'ff34c759', 'ff9500ff', 'ffaf52de',
                'ff5856d6', 'ffff2d55', 'ff00c7be', 'ffa2845e', 'ff64d2ff'
            ];
            // KML uses aaBBGGRR — convert hex #RRGGBB to KML
            function toKmlColor(hex) {
                const r = hex.slice(0, 2), g = hex.slice(2, 4), b = hex.slice(4, 6);
                return 'ff' + b + g + r;
            }
            const palette = [
                '#0071e3', '#ff3b30', '#34c759', '#ff9500', '#af52de',
                '#5856d6', '#ff2d55', '#00c7be', '#a2845e', '#64d2ff'
            ];
            const dogColors = {};
            let ci = 0;

            // Group records by dog for routes
            const byDog = {};
            allData.records.forEach(r => {
                const d = r.lostDog || 'Unbekannt';
                if (!dogColors[d]) { dogColors[d] = palette[ci % palette.length]; ci++; }
                if (!byDog[d]) byDog[d] = [];
                byDog[d].push(r);
            });

            let kml = '<?xml version="1.0" encoding="UTF-8"?>\n';
            kml += '<kml xmlns="http://www.opengis.net/kml/2.2">\n<Document>\n';
            kml += `<name>FlyerTracker GPS-Daten</name>\n`;
            kml += `<description>Export vom ${new Date().toLocaleDateString('de-DE')}</description>\n`;

            // Styles per dog
            Object.entries(dogColors).forEach(([d, hex]) => {
                const kc = toKmlColor(hex.replace('#', ''));
                const id = escXml(d.replace(/\s+/g, '_'));
                kml += `<Style id="style_${id}"><IconStyle><color>${kc}</color><scale>1.1</scale>`;
                kml += `<Icon><href>http://maps.google.com/mapfiles/kml/paddle/wht-blank.png</href></Icon>`;
                kml += `</IconStyle><LineStyle><color>${kc}</color><width>3</width></LineStyle></Style>\n`;
            });

            // Placemarks
            allData.records.forEach(r => {
                const d = r.lostDog || 'Unbekannt';
                const sid = escXml(d.replace(/\s+/g, '_'));
                const ts = r.recordedAt ? new Date(r.recordedAt).toLocaleString('de-DE') : '';
                kml += '<Placemark>\n';
                kml += `<name>${escXml(r.name || '')} – ${escXml(d)}</name>\n`;
                kml += `<description><![CDATA[Name: ${r.name}<br>Hund: ${d}<br>Kategorie: ${r.category || '–'}<br>Kommentar: ${r.comment || '–'}<br>Zeit: ${ts}<br>Genauigkeit: ${r.accuracy?.toFixed(1) || '?'} m`;
                if (r.photoUrl) kml += `<br><img src="${r.photoUrl}" width="200">`;
                kml += `]]></description>\n`;
                kml += `<styleUrl>#style_${sid}</styleUrl>\n`;
                if (r.recordedAt) kml += `<TimeStamp><when>${r.recordedAt}</when></TimeStamp>\n`;
                kml += `<Point><coordinates>${r.longitude},${r.latitude},0</coordinates></Point>\n`;
                kml += '</Placemark>\n';
            });

            // Route lines per dog
            Object.entries(byDog).forEach(([d, recs]) => {
                if (recs.length < 2) return;
                const sid = escXml(d.replace(/\s+/g, '_'));
                recs.sort((a, b) => (a.recordedAt || '').localeCompare(b.recordedAt || ''));
                kml += '<Placemark>\n';
                kml += `<name>Route: ${escXml(d)}</name>\n`;
                kml += `<styleUrl>#style_${sid}</styleUrl>\n`;
                kml += '<LineString><tessellate>1</tessellate><coordinates>\n';
                recs.forEach(r => { kml += `${r.longitude},${r.latitude},0\n`; });
                kml += '</coordinates></LineString>\n';
                kml += '</Placemark>\n';
            });

            kml += '</Document>\n</kml>';
            download(kml, 'FlyerTracker-GPS.kml', 'application/vnd.google-earth.kml+xml');
            showToast('KML Export abgeschlossen');
        } catch {
            showToast('KML Export fehlgeschlagen', true);
        }
    }

    // ── Sorting ──────────────────────────────────────────────────
    function sortRecords() {
        const sort = sortFieldEl.value;
        const locale = 'de';
        data.records.sort((a, b) => {
            switch (sort) {
                case 'name-asc':  return (a.name || '').localeCompare(b.name || '', locale);
                case 'name-desc': return (b.name || '').localeCompare(a.name || '', locale);
                case 'dog-asc':   return (a.lostDog || '').localeCompare(b.lostDog || '', locale);
                case 'dog-desc':  return (b.lostDog || '').localeCompare(a.lostDog || '', locale);
                case 'time-asc':  return (a.recordedAt || '').localeCompare(b.recordedAt || '');
                case 'time-desc': return (b.recordedAt || '').localeCompare(a.recordedAt || '');
                default:          return 0;
            }
        });
    }

    // ── Map button ───────────────────────────────────────────────
    showMapBtn.addEventListener('click', () => {
        const params = new URLSearchParams();
        const dog = filterDogEl.value;
        const name = filterNameEl.value;
        const cat = getSelectedCategories().join(',');
        if (dog) params.set('lostDog', dog);
        if (name) params.set('name', name);
        if (cat) params.set('category', cat);
        const sort = sortFieldEl.value;
        if (sort) params.set('sort', sort);
        const qs = params.toString();
        window.location.href = 'admin-map.html' + (qs ? '?' + qs : '');
    });

    // ── Events ───────────────────────────────────────────────────
    filterDogEl.addEventListener('change', () => { currentPage = 1; loadRecords(); });
    filterNameEl.addEventListener('change', () => { currentPage = 1; loadRecords(); });
    // Category multi-select: events attached in populateFilter
    sortFieldEl.addEventListener('change', () => { sortRecords(); renderTable(); });
    pageSizeEl.addEventListener('change', () => { currentPage = 1; loadRecords(); });

    // ── Helpers ──────────────────────────────────────────────────
    function esc(s) {
        const d = document.createElement('div');
        d.textContent = s;
        return d.innerHTML;
    }
    function escXml(s) {
        return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }
    function formatDate(iso) {
        if (!iso) return '—';
        try {
            const d = new Date(iso);
            const dd = String(d.getDate()).padStart(2, '0');
            const mm = String(d.getMonth() + 1).padStart(2, '0');
            const yy = String(d.getFullYear()).slice(-2);
            const hh = String(d.getHours()).padStart(2, '0');
            const mi = String(d.getMinutes()).padStart(2, '0');
            return `${dd}.${mm}.${yy} ${hh}:${mi}`;
        } catch { return iso; }
    }
    function showToast(msg, isError) {
        clearTimeout(toastTimeout);
        toastEl.textContent = msg;
        toastEl.className = 'toast' + (isError ? ' error' : '');
        toastTimeout = setTimeout(() => toastEl.classList.add('hidden'), 2500);
    }

    // ── Address Entry ────────────────────────────────────────────
    const addressPanel = document.getElementById('addressPanel');
    const addressInput = document.getElementById('addressInput');
    const searchAddressBtn = document.getElementById('searchAddressBtn');
    const searchResultsEl = document.getElementById('searchResults');
    const miniMapWrap = document.getElementById('miniMapWrap');
    const entryFields = document.getElementById('entryFields');
    const entryNameEl = document.getElementById('entryName');
    const entryDogEl = document.getElementById('entryDog');
    const entryCategoryEl = document.getElementById('entryCategory');
    const entryCommentEl = document.getElementById('entryComment');
    const entryTimestampEl = document.getElementById('entryTimestamp');
    const coordsDisplayEl = document.getElementById('coordsDisplay');
    const saveEntryBtn = document.getElementById('saveEntryBtn');

    let miniMap = null;
    let miniMarker = null;
    let selectedCoords = null;
    let entryDropdownsLoaded = false;

    /** Load Name / Hund / Kategorie dropdowns for the entry form */
    async function loadEntryDropdowns() {
        if (entryDropdownsLoaded) return;
        try {
            const hdrs = FT_AUTH.publicHeaders();
            const [namesRes, dogsRes, catsRes] = await Promise.all([
                fetch(`${API_BASE}/names`, { headers: hdrs }),
                fetch(`${API_BASE}/lost-dogs`, { headers: hdrs }),
                fetch(`${API_BASE}/categories`, { headers: hdrs })
            ]);
            const names = await namesRes.json();
            const dogs = await dogsRes.json();
            const cats = await catsRes.json();

            names.forEach(n => { const o = document.createElement('option'); o.value = n; o.textContent = n; entryNameEl.appendChild(o); });
            dogs.forEach(d => { const o = document.createElement('option'); o.value = d; o.textContent = d; entryDogEl.appendChild(o); });
            cats.forEach(c => { const o = document.createElement('option'); o.value = c; o.textContent = c; entryCategoryEl.appendChild(o); });
            entryDropdownsLoaded = true;
        } catch (e) {
            console.error('Failed to load entry dropdowns', e);
            showToast('Dropdown-Daten konnten nicht geladen werden', true);
        }
    }

    /** Set timestamp field to current local time */
    function setDefaultTimestamp() {
        const now = new Date();
        now.setMinutes(now.getMinutes() - now.getTimezoneOffset());
        entryTimestampEl.value = now.toISOString().slice(0, 16);
    }

    /** Search Nominatim for address (DE + NL) */
    async function searchAddress() {
        const q = addressInput.value.trim();
        if (q.length < 3) { showToast('Bitte mindestens 3 Zeichen eingeben', true); return; }

        searchAddressBtn.disabled = true;
        searchAddressBtn.textContent = '⏳';

        try {
            const params = new URLSearchParams({
                q,
                format: 'json',
                addressdetails: '1',
                countrycodes: 'de,nl',
                limit: '5'
            });
            const res = await fetch(`https://nominatim.openstreetmap.org/search?${params}`, {
                headers: { 'Accept-Language': 'de' }
            });
            const results = await res.json();

            if (results.length === 0) {
                showToast('Keine Ergebnisse gefunden', true);
                searchResultsEl.classList.add('hidden');
                return;
            }

            searchResultsEl.innerHTML = '';
            results.forEach(r => {
                const li = document.createElement('li');
                li.textContent = r.display_name;
                li.addEventListener('click', () => selectResult(r));
                searchResultsEl.appendChild(li);
            });
            searchResultsEl.classList.remove('hidden');
        } catch {
            showToast('Fehler bei der Adresssuche', true);
        } finally {
            searchAddressBtn.disabled = false;
            searchAddressBtn.textContent = 'Suchen';
        }
    }

    /** User picked a result → show map + form fields */
    function selectResult(result) {
        selectedCoords = { lat: parseFloat(result.lat), lon: parseFloat(result.lon) };
        searchResultsEl.classList.add('hidden');
        addressInput.value = result.display_name;

        // Show / init mini map
        miniMapWrap.classList.remove('hidden');
        if (!miniMap) {
            miniMap = L.map('miniMap').setView([selectedCoords.lat, selectedCoords.lon], 16);
            L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
                attribution: '© OpenStreetMap'
            }).addTo(miniMap);
        } else {
            miniMap.setView([selectedCoords.lat, selectedCoords.lon], 16);
        }

        if (miniMarker) miniMarker.remove();
        miniMarker = L.marker([selectedCoords.lat, selectedCoords.lon], { draggable: true }).addTo(miniMap);
        miniMarker.bindPopup(result.display_name).openPopup();
        miniMarker.on('dragend', () => {
            const pos = miniMarker.getLatLng();
            selectedCoords = { lat: pos.lat, lon: pos.lng };
            coordsDisplayEl.textContent = `📍 ${selectedCoords.lat.toFixed(6)}, ${selectedCoords.lon.toFixed(6)}`;
        });

        // Show entry fields
        entryFields.classList.remove('hidden');
        coordsDisplayEl.textContent = `📍 ${selectedCoords.lat.toFixed(6)}, ${selectedCoords.lon.toFixed(6)}`;
        setDefaultTimestamp();
        updateSaveBtn();

        // Leaflet needs resize after container becomes visible
        setTimeout(() => miniMap.invalidateSize(), 150);
    }

    function updateSaveBtn() {
        saveEntryBtn.disabled = !(selectedCoords && entryNameEl.value && entryDogEl.value && entryCategoryEl.value);
    }

    /** Save the entry via existing save-location endpoint */
    async function saveEntry() {
        if (saveEntryBtn.disabled) return;
        saveEntryBtn.disabled = true;
        saveEntryBtn.textContent = 'Wird gespeichert…';

        try {
            const payload = {
                name: entryNameEl.value,
                lostDog: entryDogEl.value,
                category: entryCategoryEl.value,
                comment: entryCommentEl.value.trim(),
                latitude: selectedCoords.lat,
                longitude: selectedCoords.lon,
                accuracy: 0,
                timestamp: new Date(entryTimestampEl.value).toISOString()
            };

            const res = await fetch(`${API_BASE}/save-location`, {
                method: 'POST',
                headers: FT_AUTH.publicHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify(payload)
            });
            if (!res.ok) throw new Error();

            showToast('Eintrag gespeichert ✓');

            // Reset form
            selectedCoords = null;
            addressInput.value = '';
            entryCommentEl.value = '';
            coordsDisplayEl.textContent = '';
            miniMapWrap.classList.add('hidden');
            entryFields.classList.add('hidden');
            searchResultsEl.classList.add('hidden');
            if (miniMarker) { miniMarker.remove(); miniMarker = null; }
            updateSaveBtn();

            // Reload table to show new entry
            await loadRecords();
        } catch {
            showToast('Fehler beim Speichern', true);
        } finally {
            saveEntryBtn.textContent = 'Eintrag speichern';
            updateSaveBtn();
        }
    }

    // Address entry events
    searchAddressBtn.addEventListener('click', searchAddress);
    addressInput.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); searchAddress(); } });
    entryNameEl.addEventListener('change', updateSaveBtn);
    entryDogEl.addEventListener('change', updateSaveBtn);
    entryCategoryEl.addEventListener('change', updateSaveBtn);
    saveEntryBtn.addEventListener('click', saveEntry);

    // Load dropdowns lazily when panel opens
    addressPanel.addEventListener('toggle', () => {
        if (addressPanel.open) loadEntryDropdowns();
    });

    // ── Init ─────────────────────────────────────────────────────
    loadRecords();
})();
