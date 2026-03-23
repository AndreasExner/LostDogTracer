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

    // Read lostDog + token from URL params
    const urlParams = new URLSearchParams(window.location.search);
    const filterDog = urlParams.get('lostDog') || '';
    const guestToken = urlParams.get('token') || localStorage.getItem('lostdogtracer_guest_token') || '';
    let guestCategoryKey = '';

    let currentPage = 1;
    let data = { records: [], totalCount: 0, page: 1, pageSize: 20, totalPages: 1 };

    if (!filterDog) {
        filterInfoEl.textContent = '⚠️ Kein Hund ausgewählt';
        bodyEl.innerHTML = '<tr><td colspan="5" style="color:#ff3b30;text-align:center;padding:2rem">Bitte zuerst einen Hund auf der Startseite auswählen.</td></tr>';
        sortFieldEl.disabled = true;
        pageSizeEl.disabled = true;
    } else {
        resolveFilterInfo();
        loadGuestCategoryThenInit();
    }

    async function loadGuestCategoryThenInit() {
        try {
            const cfg = window.FT_CONFIG || await fetch(`${API_BASE}/config`, { headers: FT_AUTH.publicHeaders() }).then(r => r.ok ? r.json() : null);
            guestCategoryKey = cfg?.guestCategoryRowKey || '';
        } catch { /* continue without filter */ }
        init();
    }

    async function resolveFilterInfo() {
        let dogDisplay = filterDog;
        try {
            const dogsRes = await fetch(`${API_BASE}/lost-dogs`, { headers: FT_AUTH.publicHeaders() });
            if (dogsRes.ok) {
                const dogs = await dogsRes.json();
                const match = dogs.find(d => d.rowKey === filterDog);
                if (match) dogDisplay = match.displayName;
            }
        } catch { /* use raw key as fallback */ }
        filterInfoEl.textContent = dogDisplay;
    }

    function init() {
        const ownerFilterEl = document.getElementById('ownerFilter');
        sortFieldEl.addEventListener('change', () => { sortRecords(); renderTable(); });
        pageSizeEl.addEventListener('change', () => { currentPage = 1; loadRecords(); });
        ownerFilterEl.addEventListener('change', () => { sortRecords(); renderTable(); renderPagination(); updateDeleteButton(); });
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

    // ── Load records ─────────────────────────────────────────────
    async function loadRecords() {
        bodyEl.innerHTML = '<tr><td colspan="5" style="color:#6e6e73;text-align:center;padding:2rem">Lädt…</td></tr>';
        const ps = pageSizeEl.value;
        const params = new URLSearchParams();
        params.set('pageSize', ps);
        params.set('page', currentPage);
        params.set('lostDog', filterDog);
        if (guestToken) params.set('guestToken', guestToken);

        try {
            const res = await fetch(`${API_BASE}/my-records?${params}`, {
                headers: FT_AUTH.publicHeaders()
            });
            if (!res.ok) throw new Error();
            data = await res.json();
            // Filter by guest category
            if (guestCategoryKey) {
                data.records = data.records.filter(r => r.categoryKey === guestCategoryKey);
                data.totalCount = data.records.length;
            }
            sortRecords();
            renderTable();
            renderPagination();
            updateDeleteButton();
        } catch {
            bodyEl.innerHTML = '<tr><td colspan="5" style="color:#ff3b30;text-align:center;padding:2rem">Fehler beim Laden</td></tr>';
        }
    }

    // ── Render table ─────────────────────────────────────────────
    function getFilteredRecords() {
        const showMine = document.getElementById('ownerFilter').value === 'mine';
        return showMine ? data.records.filter(r => r.isOwner) : data.records;
    }

    function renderTable() {
        bodyEl.innerHTML = '';
        selectAllEl.checked = false;
        const filtered = getFilteredRecords();

        if (filtered.length === 0) {
            bodyEl.innerHTML = '<tr><td colspan="5" style="color:#6e6e73;text-align:center;padding:2rem">Keine Eintr\u00e4ge</td></tr>';
            return;
        }

        filtered.forEach(r => {
            const tr = document.createElement('tr');
            const photoCell = r.photoUrl
                ? `<td><img src="${esc(r.photoUrl)}" class="thumb" alt="Foto" onclick="document.getElementById('lightboxImg').src=this.src;document.getElementById('lightbox').classList.remove('hidden');"></td>`
                : '<td class="no-photo">—</td>';
            const cbCell = r.isOwner
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

    // ── Pagination ───────────────────────────────────────────────
    function renderPagination() {
        const filtered = getFilteredRecords();
        pageInfoEl.textContent = `${filtered.length} Einträge`;
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

        let start = Math.max(1, currentPage - 2);
        let end = Math.min(data.totalPages, start + 4);
        start = Math.max(1, end - 4);

        for (let i = start; i <= end; i++) {
            addBtn(String(i), i, false, i === currentPage);
        }

        addBtn('›', currentPage + 1, currentPage >= data.totalPages, false);
        addBtn('»', data.totalPages, currentPage >= data.totalPages, false);
    }

    // ── Select / delete ──────────────────────────────────────────
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

    async function deleteSelected() {
        const sel = getSelected();
        if (sel.length === 0) return;
        if (!confirm(`${sel.length} Einträge wirklich löschen?`)) return;

        deleteBtn.disabled = true;
        try {
            const res = await fetch(`${API_BASE}/my-records/delete`, {
                method: 'POST',
                headers: FT_AUTH.publicHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify({ lostDog: filterDog, guestToken: guestToken || null, keys: sel })
            });
            if (!res.ok) throw new Error();
            const result = await res.json();
            showToast(`${result.deleted} Einträge gelöscht`);
            await loadRecords();
        } catch {
            showToast('Fehler beim Löschen', true);
        }
    }

    // ── Sorting ──────────────────────────────────────────────────
    function sortRecords() {
        const sort = sortFieldEl.value;
        data.records.sort((a, b) => {
            switch (sort) {
                case 'time-asc':  return (a.recordedAt || '').localeCompare(b.recordedAt || '');
                case 'time-desc': return (b.recordedAt || '').localeCompare(a.recordedAt || '');
                default:          return 0;
            }
        });
    }

    // ── Helpers ──────────────────────────────────────────────────
    function esc(s) {
        return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
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
})();
