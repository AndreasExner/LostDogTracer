(function () {
    'use strict';

    const API_BASE = FT_AUTH.getApiBase();

    const markerCountEl = document.getElementById('markerCount');
    const filterInfoEl = document.getElementById('filterInfo');
    const legendEl = document.getElementById('legend');
    const toastEl = document.getElementById('toast');
    let toastTimeout = null;

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

    /** Create a colored circle marker icon */
    function colorIcon(color) {
        return L.divIcon({
            className: '',
            html: `<svg width="24" height="36" viewBox="0 0 24 36" xmlns="http://www.w3.org/2000/svg">
                <path d="M12 0C5.4 0 0 5.4 0 12c0 9 12 24 12 24s12-15 12-24C24 5.4 18.6 0 12 0z" fill="${color}"/>
                <circle cx="12" cy="12" r="5" fill="#fff"/>
            </svg>`,
            iconSize: [24, 36],
            iconAnchor: [12, 36],
            popupAnchor: [0, -32]
        });
    }

    // ── Read filter from URL params ──────────────────────────────
    const urlParams = new URLSearchParams(window.location.search);
    const filterDog = urlParams.get('lostDog') || '';
    const sortField = urlParams.get('sort') || '';

    // ── Init map ─────────────────────────────────────────────────
    const map = L.map('map').setView([51.1657, 10.4515], 6); // Deutschland-Mitte

    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
        maxZoom: 19
    }).addTo(map);

    const clusterGroup = L.markerClusterGroup({
        maxClusterRadius: 40,
        spiderfyOnMaxZoom: true,
        showCoverageOnHover: false
    });
    map.addLayer(clusterGroup);

    // ── Load & display records ───────────────────────────────────
    async function loadAndDisplay() {
        try {
            const params = new URLSearchParams();
            params.set('pageSize', 'all');
            if (filterDog) params.set('lostDog', filterDog);

            const res = await fetch(`${API_BASE}/manage/gps-records?${params}`, {
                headers: FT_AUTH.adminHeaders()
            });
            if (res.status === 401) { FT_AUTH.logout(); location.href = 'admin.html'; return; }
            if (!res.ok) throw new Error();

            const data = await res.json();
            const records = data.records || [];

            // Update info badges
            markerCountEl.textContent = `${records.length} Standort${records.length !== 1 ? 'e' : ''}`;
            filterInfoEl.textContent = filterDog ? `Hund: ${filterDog}` : 'Alle Hunde';

            if (records.length === 0) {
                showToast('Keine GPS-Daten vorhanden', true);
                return;
            }

            // Add markers
            const bounds = [];
            records.forEach(r => {
                if (!r.latitude || !r.longitude) return;

                const color = getDogColor(r.lostDog);
                const marker = L.marker([r.latitude, r.longitude], {
                    icon: colorIcon(color)
                });

                const photoHtml = r.photoUrl
                    ? `<br><img src="${escHtml(r.photoUrl)}" style="max-width:180px;max-height:120px;border-radius:6px;margin-top:6px;" alt="Foto">`
                    : '';

                marker.bindPopup(
                    `<strong>${escHtml(r.name)}</strong><br>` +
                    `🐕 ${escHtml(r.lostDog)}<br>` +
                    `📍 ${r.latitude.toFixed(6)}, ${r.longitude.toFixed(6)}<br>` +
                    `🎯 ±${r.accuracy.toFixed(0)} m<br>` +
                    `🕐 ${formatDate(r.recordedAt)}` +
                    photoHtml,
                    { maxWidth: 260 }
                );

                clusterGroup.addLayer(marker);
                bounds.push([r.latitude, r.longitude]);
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

    // ── Start ────────────────────────────────────────────────────
    loadAndDisplay();
})();
