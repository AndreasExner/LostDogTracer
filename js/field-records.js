/* js/field-records.js — Feld-Einträge (identisch zu my-records.js) */
(function () {
    'use strict';

    const API_BASE = FT_AUTH.getApiBase();

    const sortFieldEl = document.getElementById('sortField');
    const pageSizeEl = document.getElementById('pageSize');
    const selectAllEl = document.getElementById('selectAll');
    const bodyEl = document.getElementById('recordsBody');
    const pageInfoEl = document.getElementById('pageInfo');
    const pageBtnsEl = document.getElementById('pageButtons');
    const deleteBtn = document.getElementById('deleteSelectedBtn');
    const filterInfoEl = document.getElementById('filterInfo');
    const toastEl = document.getElementById('toast');
    let toastTimeout = null;

    const urlParams = new URLSearchParams(window.location.search);
    const filterName = urlParams.get('name') || '';
    const filterDog = urlParams.get('lostDog') || '';

    let currentPage = 1;
    let data = { records: [], totalCount: 0, page: 1, pageSize: 20, totalPages: 1 };

    if (!filterName || !filterDog) {
        filterInfoEl.textContent = '⚠️ Kein Name/Hund ausgewählt';
        bodyEl.innerHTML = '<tr><td colspan="5" style="color:#ff3b30;text-align:center;padding:2rem">Bitte zuerst Name und Hund auf der Startseite auswählen.</td></tr>';
        sortFieldEl.disabled = true;
        pageSizeEl.disabled = true;
    } else {
        // Resolve display names for header
        resolveFilterInfo();
        init();
    }

    async function resolveFilterInfo() {
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
        } catch { /* use raw keys as fallback */ }
        filterInfoEl.textContent = `${nameDisplay} / ${dogDisplay}`;
    }

    function init() {
        const scopeEl = document.getElementById('scopeFilter');
        sortFieldEl.addEventListener('change', () => { sortRecords(); renderTable(); });
        pageSizeEl.addEventListener('change', () => { currentPage = 1; loadRecords(); });
        scopeEl.addEventListener('change', () => { currentPage = 1; loadRecords(); });
        selectAllEl.addEventListener('change', () => {
            document.querySelectorAll('.row-cb').forEach(cb => { cb.checked = selectAllEl.checked; });
            updateDeleteButton();
        });
        bodyEl.addEventListener('change', e => {
            if (e.target.classList.contains('row-cb')) updateDeleteButton();
        });
        deleteBtn.addEventListener('click', deleteSelected);
        loadRecords();
    }

    async function loadRecords() {
        bodyEl.innerHTML = '<tr><td colspan="5" style="color:#6e6e73;text-align:center;padding:2rem">Lädt…</td></tr>';
        const ps = pageSizeEl.value;
        const showMine = document.getElementById('scopeFilter').value === 'mine';
        const params = new URLSearchParams();
        params.set('pageSize', ps);
        params.set('page', currentPage);
        if (showMine) params.set('name', filterName);
        params.set('lostDog', filterDog);

        try {
            const res = await fetch(`${API_BASE}/my-records?${params}`, { headers: FT_AUTH.publicHeaders() });
            if (!res.ok) throw new Error();
            data = await res.json();
            sortRecords();
            renderTable();
            renderPagination();
            updateDeleteButton();
        } catch {
            bodyEl.innerHTML = '<tr><td colspan="5" style="color:#ff3b30;text-align:center;padding:2rem">Fehler beim Laden</td></tr>';
        }
    }

    function renderTable() {
        bodyEl.innerHTML = '';
        selectAllEl.checked = false;
        if (data.records.length === 0) {
            bodyEl.innerHTML = '<tr><td colspan="5" style="color:#6e6e73;text-align:center;padding:2rem">Keine Einträge</td></tr>';
            return;
        }
        data.records.forEach(r => {
            const tr = document.createElement('tr');
            const photoCell = r.photoUrl
                ? `<td><img src="${esc(r.photoUrl)}" class="thumb" alt="Foto" onclick="document.getElementById('lightboxImg').src=this.src;document.getElementById('lightbox').classList.remove('hidden');"></td>`
                : '<td class="no-photo">—</td>';
            const isOwn = r.partitionKey === filterName;
            const cbCell = isOwn
                ? `<td><input type="checkbox" class="row-cb" data-pk="${esc(r.partitionKey)}" data-rk="${esc(r.rowKey)}"></td>`
                : '<td></td>';
            tr.innerHTML = `
                ${cbCell}
                <td>${formatDate(r.recordedAt)}</td>
                <td>${esc(r.category || '')}</td>
                <td>${esc(r.comment || '')}</td>
                ${photoCell}`;
            bodyEl.appendChild(tr);
        });
    }

    function renderPagination() {
        pageInfoEl.textContent = `${data.totalCount} Einträge — Seite ${data.page} von ${data.totalPages}`;
        pageBtnsEl.innerHTML = '';
        if (data.totalPages <= 1) return;
        const addBtn = (label, page, disabled, active) => {
            const b = document.createElement('button'); b.textContent = label; b.disabled = disabled;
            if (active) b.classList.add('active');
            b.addEventListener('click', () => { currentPage = page; loadRecords(); });
            pageBtnsEl.appendChild(b);
        };
        addBtn('«', 1, currentPage === 1, false);
        addBtn('‹', currentPage - 1, currentPage === 1, false);
        let start = Math.max(1, currentPage - 2);
        let end = Math.min(data.totalPages, start + 4);
        start = Math.max(1, end - 4);
        for (let i = start; i <= end; i++) addBtn(String(i), i, false, i === currentPage);
        addBtn('›', currentPage + 1, currentPage >= data.totalPages, false);
        addBtn('»', data.totalPages, currentPage >= data.totalPages, false);
    }

    function getSelected() {
        return [...document.querySelectorAll('.row-cb:checked')].map(cb => ({ partitionKey: cb.dataset.pk, rowKey: cb.dataset.rk }));
    }
    function updateDeleteButton() {
        const n = getSelected().length;
        deleteBtn.disabled = n === 0;
        deleteBtn.textContent = n > 0 ? `Ausgewählte löschen (${n})` : 'Ausgewählte löschen';
    }

    async function deleteSelected() {
        const sel = getSelected();
        if (sel.length === 0) return;
        if (!confirm(`${sel.length} Einträge wirklich löschen?`)) return;
        deleteBtn.disabled = true;
        try {
            const res = await fetch(`${API_BASE}/my-records/delete`, {
                method: 'POST',
                headers: FT_AUTH.publicHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify({ name: filterName, lostDog: filterDog, keys: sel })
            });
            if (!res.ok) throw new Error();
            const result = await res.json();
            showToast(`${result.deleted} Einträge gelöscht`);
            await loadRecords();
        } catch { showToast('Fehler beim Löschen', true); }
    }

    function sortRecords() {
        const sort = sortFieldEl.value;
        data.records.sort((a, b) => {
            switch (sort) {
                case 'time-asc': return (a.recordedAt || '').localeCompare(b.recordedAt || '');
                case 'time-desc': return (b.recordedAt || '').localeCompare(a.recordedAt || '');
                default: return 0;
            }
        });
    }

    function esc(s) { return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]); }
    function formatDate(iso) {
        if (!iso) return '—';
        try { const d = new Date(iso); return `${String(d.getDate()).padStart(2,'0')}.${String(d.getMonth()+1).padStart(2,'0')}.${String(d.getFullYear()).slice(-2)} ${String(d.getHours()).padStart(2,'0')}:${String(d.getMinutes()).padStart(2,'0')}`; }
        catch { return iso; }
    }
    function showToast(msg, isError) {
        clearTimeout(toastTimeout); toastEl.textContent = msg;
        toastEl.className = 'toast' + (isError ? ' error' : '');
        toastTimeout = setTimeout(() => toastEl.classList.add('hidden'), 2500);
    }
})();
