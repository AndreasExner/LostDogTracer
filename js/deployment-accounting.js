(function () {
    'use strict';

    const API_BASE = FT_AUTH.getApiBase();

    const filterUserEl = document.getElementById('filterUser');
    const filterDogEl = document.getElementById('filterDog');
    const filterFromEl = document.getElementById('filterFrom');
    const filterToEl = document.getElementById('filterTo');
    const resetBtn = document.getElementById('resetFiltersBtn');
    const bodyEl = document.getElementById('recordsBody');
    const totalDurationEl = document.getElementById('totalDuration');
    const totalKmEl = document.getElementById('totalKm');
    const sortDateEl = document.getElementById('sortDate');
    const exportCsvBtn = document.getElementById('exportCsvBtn');
    const toastEl = document.getElementById('toast');
    let toastTimeout = null;

    let allRecords = [];
    let sortAsc = false; // default: newest first

    // ── Load records ─────────────────────────────────────────────
    async function loadRecords() {
        bodyEl.innerHTML = '<tr><td colspan="8" style="color:#6e6e73;text-align:center;padding:2rem">Lädt…</td></tr>';
        const params = new URLSearchParams();
        if (filterUserEl.value) params.set('user', filterUserEl.value);
        if (filterDogEl.value) params.set('dog', filterDogEl.value);
        if (filterFromEl.value) params.set('from', filterFromEl.value);
        if (filterToEl.value) params.set('to', filterToEl.value);

        try {
            const res = await fetch(`${API_BASE}/deployments/accounting?${params}`, { headers: FT_AUTH.adminHeaders() });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (res.status === 403) { showToast('Keine Berechtigung', true); return; }
            if (!res.ok) throw new Error();
            const data = await res.json();

            populateFilters(data.users || [], data.lostDogs || []);
            allRecords = data.records || [];
            sortAndRender();
        } catch {
            bodyEl.innerHTML = '<tr><td colspan="7" style="color:#ff3b30;text-align:center;padding:2rem">Fehler beim Laden</td></tr>';
        }
    }

    function populateFilters(users, dogs) {
        const curUser = filterUserEl.value;
        while (filterUserEl.options.length > 1) filterUserEl.remove(1);
        users.forEach(u => {
            const opt = document.createElement('option');
            opt.value = u.username;
            opt.textContent = u.displayName;
            filterUserEl.appendChild(opt);
        });
        filterUserEl.value = curUser;

        const curDog = filterDogEl.value;
        while (filterDogEl.options.length > 1) filterDogEl.remove(1);
        dogs.forEach(d => {
            const opt = document.createElement('option');
            opt.value = d.rowKey;
            opt.textContent = d.displayName;
            filterDogEl.appendChild(opt);
        });
        filterDogEl.value = curDog;
    }

    function sortAndRender() {
        allRecords.sort((a, b) => {
            const da = a.startTime || '';
            const db = b.startTime || '';
            return sortAsc ? da.localeCompare(db) : db.localeCompare(da);
        });
        renderTable(allRecords);
    }

    function renderTable(records) {
        bodyEl.innerHTML = '';
        let sumDuration = 0;
        let sumKm = 0;

        if (records.length === 0) {
            bodyEl.innerHTML = '<tr><td colspan="8" style="color:#6e6e73;text-align:center;padding:2rem">Keine Einträge</td></tr>';
            totalDurationEl.textContent = '—';
            totalKmEl.textContent = '—';
            return;
        }

        records.forEach(r => {
            const tr = document.createElement('tr');
            const km = r.kmDriven != null ? r.kmDriven + ' km' : '—';
            const dur = formatDuration(r.duration);
            tr.innerHTML =
                `<td>${esc(r.userDisplay)}</td>` +
                `<td>${formatDate(r.startTime)}</td>` +
                `<td>${esc(r.lostDog)}</td>` +
                `<td>${esc(r.activity || '—')}</td>` +
                `<td>${formatTime(r.startTime)}</td>` +
                `<td>${formatTime(r.endTime)}</td>` +
                `<td>${dur}</td>` +
                `<td>${km}</td>`;
            bodyEl.appendChild(tr);

            sumDuration += r.duration || 0;
            if (r.kmDriven != null) sumKm += r.kmDriven;
        });

        totalDurationEl.textContent = formatDuration(sumDuration);
        totalKmEl.textContent = sumKm > 0 ? sumKm + ' km' : '—';
    }

    // ── Sort toggle ──────────────────────────────────────────────
    sortDateEl.addEventListener('click', () => {
        sortAsc = !sortAsc;
        sortDateEl.textContent = sortAsc ? 'Datum ↑' : 'Datum ↓';
        sortAndRender();
    });

    // ── Filters ──────────────────────────────────────────────────
    filterUserEl.addEventListener('change', () => loadRecords());
    filterDogEl.addEventListener('change', () => loadRecords());
    filterFromEl.addEventListener('change', () => loadRecords());
    filterToEl.addEventListener('change', () => loadRecords());
    resetBtn.addEventListener('click', () => {
        filterUserEl.value = '';
        filterDogEl.value = '';
        filterFromEl.value = '';
        filterToEl.value = '';
        loadRecords();
    });

    // ── Export CSV ────────────────────────────────────────────────
    exportCsvBtn.addEventListener('click', () => {
        if (allRecords.length === 0) { showToast('Keine Daten zum Exportieren', true); return; }

        const sep = ';';
        const header = ['Helfer', 'Datum', 'Hund', 'Tätigkeit', 'Beginn', 'Ende', 'Dauer (min)', 'Km'].join(sep);
        const rows = allRecords.map(r => [
            r.userDisplay,
            formatDate(r.startTime),
            r.lostDog,
            r.activity || '',
            formatTime(r.startTime),
            formatTime(r.endTime),
            r.duration || 0,
            r.kmDriven != null ? r.kmDriven : ''
        ].join(sep));

        const bom = '\uFEFF';
        const csv = bom + header + '\n' + rows.join('\n');
        downloadFile(csv, 'einsatzzeiten.csv', 'text/csv;charset=utf-8;');
    });

    function downloadFile(content, filename, mimeType) {
        const blob = new Blob([content], { type: mimeType });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        a.click();
        URL.revokeObjectURL(url);
    }

    // ── Helpers ──────────────────────────────────────────────────
    function esc(s) {
        return String(s || '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
    }

    function formatDate(iso) {
        if (!iso) return '—';
        try {
            return new Date(iso).toLocaleDateString('de-DE', { day: '2-digit', month: '2-digit', year: '2-digit' });
        } catch { return iso; }
    }

    function formatTime(iso) {
        if (!iso) return '—';
        try {
            return new Date(iso).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' });
        } catch { return iso; }
    }

    function formatDuration(min) {
        if (!min && min !== 0) return '—';
        const h = Math.floor(min / 60);
        const m = min % 60;
        if (h > 0) return `${h}h ${m}min`;
        return `${m}min`;
    }

    function showToast(msg, isError) {
        clearTimeout(toastTimeout);
        toastEl.textContent = msg;
        toastEl.className = 'toast' + (isError ? ' error' : '');
        toastTimeout = setTimeout(() => toastEl.classList.add('hidden'), 2500);
    }

    // ── Init ─────────────────────────────────────────────────────
    loadRecords();
})();
