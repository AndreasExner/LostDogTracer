(function () {
    'use strict';

    const API_BASE = FT_AUTH.getApiBase();

    const filterDogEl = document.getElementById('filterDog');
    const bodyEl = document.getElementById('recordsBody');
    const editModal = document.getElementById('editModal');
    const editDogEl = document.getElementById('editDog');
    const editStartEl = document.getElementById('editStart');
    const editEndEl = document.getElementById('editEnd');
    const editKmStartEl = document.getElementById('editKmStart');
    const editKmEndEl = document.getElementById('editKmEnd');
    const editSaveBtn = document.getElementById('editSaveBtn');
    const editCancelBtn = document.getElementById('editCancelBtn');
    const toastEl = document.getElementById('toast');
    let toastTimeout = null;
    let editRowKey = null;

    // ── Load records ─────────────────────────────────────────────
    async function loadRecords() {
        bodyEl.innerHTML = '<tr><td colspan="6" style="color:#6e6e73;text-align:center;padding:2rem">Lädt…</td></tr>';
        const dog = filterDogEl.value;
        const params = new URLSearchParams();
        if (dog) params.set('dog', dog);

        try {
            const res = await fetch(`${API_BASE}/deployments?${params}`, { headers: FT_AUTH.adminHeaders() });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) throw new Error();
            const data = await res.json();

            // Populate dog filter
            const currentDog = filterDogEl.value;
            while (filterDogEl.options.length > 1) filterDogEl.remove(1);
            (data.lostDogs || []).forEach(d => {
                const opt = document.createElement('option');
                opt.value = d.rowKey;
                opt.textContent = d.displayName;
                filterDogEl.appendChild(opt);
            });
            filterDogEl.value = currentDog;

            // Populate edit dropdown
            while (editDogEl.options.length > 1) editDogEl.remove(1);
            (data.lostDogs || []).forEach(d => {
                const opt = document.createElement('option');
                opt.value = d.rowKey;
                opt.textContent = d.displayName;
                editDogEl.appendChild(opt);
            });

            renderTable(data.records || []);
        } catch {
            bodyEl.innerHTML = '<tr><td colspan="6" style="color:#ff3b30;text-align:center;padding:2rem">Fehler beim Laden</td></tr>';
        }
    }

    function renderTable(records) {
        bodyEl.innerHTML = '';
        if (records.length === 0) {
            bodyEl.innerHTML = '<tr><td colspan="6" style="color:#6e6e73;text-align:center;padding:2rem">Keine Einträge</td></tr>';
            return;
        }
        records.forEach(r => {
            const tr = document.createElement('tr');
            const km = r.kmDriven != null ? r.kmDriven + ' km' : '—';
            const dur = formatDuration(r.duration);
            tr.innerHTML =
                `<td>${esc(r.lostDog)}</td>` +
                `<td>${formatDateTime(r.startTime)}</td>` +
                `<td>${formatDateTime(r.endTime)}</td>` +
                `<td>${dur}</td>` +
                `<td>${km}</td>` +
                `<td style="white-space:nowrap;">` +
                    `<button class="btn btn-primary btn-sm edit-btn" data-rk="${esc(r.rowKey)}" data-dog="${esc(r.lostDogKey)}" data-start="${esc(r.startTime)}" data-end="${esc(r.endTime)}" data-kms="${r.kmStart ?? ''}" data-kme="${r.kmEnd ?? ''}">✏️</button> ` +
                    `<button class="btn btn-danger btn-sm del-btn" data-rk="${esc(r.rowKey)}" data-dog="${esc(r.lostDog)}">🗑️</button>` +
                `</td>`;
            bodyEl.appendChild(tr);
        });
    }

    // ── Edit ─────────────────────────────────────────────────────
    bodyEl.addEventListener('click', e => {
        const editBtn = e.target.closest('.edit-btn');
        if (editBtn) {
            editRowKey = editBtn.dataset.rk;
            editDogEl.value = editBtn.dataset.dog;
            editStartEl.value = toLocalInput(editBtn.dataset.start);
            editEndEl.value = toLocalInput(editBtn.dataset.end);
            editKmStartEl.value = editBtn.dataset.kms || '';
            editKmEndEl.value = editBtn.dataset.kme || '';
            editModal.classList.remove('hidden');
            return;
        }

        const delBtn = e.target.closest('.del-btn');
        if (delBtn) {
            const rk = delBtn.dataset.rk;
            const dog = delBtn.dataset.dog;
            if (!confirm(`Einsatz „${dog}" wirklich löschen?`)) return;
            deleteRecord(rk);
        }
    });

    editCancelBtn.addEventListener('click', () => editModal.classList.add('hidden'));

    editSaveBtn.addEventListener('click', async () => {
        if (!editRowKey) return;
        editSaveBtn.disabled = true;
        editSaveBtn.textContent = '⏳…';

        const payload = {
            dog: editDogEl.value || undefined,
            startTime: editStartEl.value ? new Date(editStartEl.value).toISOString() : undefined,
            endTime: editEndEl.value ? new Date(editEndEl.value).toISOString() : undefined,
            kmStart: editKmStartEl.value ? parseInt(editKmStartEl.value, 10) : null,
            kmEnd: editKmEndEl.value ? parseInt(editKmEndEl.value, 10) : null
        };

        try {
            const res = await fetch(`${API_BASE}/deployments/${encodeURIComponent(editRowKey)}`, {
                method: 'PUT',
                headers: FT_AUTH.adminHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify(payload)
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) throw new Error();
            editModal.classList.add('hidden');
            showToast('Einsatz aktualisiert');
            await loadRecords();
        } catch {
            showToast('Fehler beim Speichern', true);
        } finally {
            editSaveBtn.disabled = false;
            editSaveBtn.textContent = 'Speichern';
        }
    });

    async function deleteRecord(rowKey) {
        try {
            const res = await fetch(`${API_BASE}/deployments/${encodeURIComponent(rowKey)}`, {
                method: 'DELETE',
                headers: FT_AUTH.adminHeaders()
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) throw new Error();
            showToast('Einsatz gelöscht');
            await loadRecords();
        } catch {
            showToast('Fehler beim Löschen', true);
        }
    }

    // ── Events ───────────────────────────────────────────────────
    filterDogEl.addEventListener('change', () => loadRecords());

    // ── Helpers ──────────────────────────────────────────────────
    function esc(s) {
        return String(s || '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
    }

    function formatDateTime(iso) {
        if (!iso) return '—';
        try {
            return new Date(iso).toLocaleString('de-DE', {
                day: '2-digit', month: '2-digit', year: '2-digit',
                hour: '2-digit', minute: '2-digit'
            });
        } catch { return iso; }
    }

    function formatDuration(min) {
        if (!min && min !== 0) return '—';
        const h = Math.floor(min / 60);
        const m = min % 60;
        if (h > 0) return `${h}h ${m}min`;
        return `${m}min`;
    }

    function toLocalInput(iso) {
        if (!iso) return '';
        try {
            const d = new Date(iso);
            d.setMinutes(d.getMinutes() - d.getTimezoneOffset());
            return d.toISOString().slice(0, 16);
        } catch { return ''; }
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
