/* js/equipment.js — Equipment-Verwaltung */
(function () {
    'use strict';

    const API = FT_AUTH.getApiBase();
    const listEl = document.getElementById('eqList');
    const createModal = document.getElementById('createEqModal');
    const editModal = document.getElementById('editEqModal');
    const toastEl = document.getElementById('toast');
    let toastTimeout = null;

    function showToast(msg, ok = true) { clearTimeout(toastTimeout); toastEl.textContent = msg; toastEl.className = 'toast' + (ok ? '' : ' error'); toastTimeout = setTimeout(() => toastEl.classList.add('hidden'), 2500); }
    function showError(id, msg) { const el = document.getElementById(id); el.textContent = msg; el.style.display = 'block'; }
    function hideError(id) { const el = document.getElementById(id); el.textContent = ''; el.style.display = 'none'; }
    function openModal(m) { m.classList.add('open'); }
    function closeModal(m) { m.classList.remove('open'); }
    function esc(s) { return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]); }

    async function apiCall(url, opts = {}) {
        try { const res = await fetch(url, opts); if (res.status === 401) { FT_AUTH.sessionExpired(); return null; } return res; }
        catch { showToast('Netzwerkfehler', false); return null; }
    }

    /* ── Load list ───────────────────────────── */
    async function loadEquipment() {
        listEl.innerHTML = '<p style="color:#6e6e73;text-align:center;padding:2rem">Lädt…</p>';
        const res = await apiCall(`${API}/manage/equipment`, { headers: FT_AUTH.adminHeaders() });
        if (!res) return;
        if (!res.ok) { showToast('Fehler beim Laden', false); return; }
        const items = await res.json();
        renderList(items);
    }

    function renderList(items) {
        if (!items.length) {
            listEl.innerHTML = '<p style="color:#6e6e73;text-align:center;padding:2rem">Kein Equipment vorhanden.</p>';
            return;
        }
        listEl.innerHTML = items.map(item => `
            <div class="eq-card">
                <div class="eq-info">
                    <strong>${esc(item.displayName)}</strong>
                    <small>${item.location ? '📍 ' + esc(item.location) : 'Kein Standort'}</small>
                </div>
                <div class="eq-actions">
                    <button class="btn btn-secondary btn-sm" onclick="EQ.edit('${esc(item.rowKey)}','${esc(item.displayName)}','${esc(item.location || '')}',${item.latitude || 0},${item.longitude || 0})">Bearbeiten</button>
                    <button class="btn btn-sm" style="background:#ff3b30;color:#fff" onclick="EQ.del('${esc(item.rowKey)}','${esc(item.displayName)}')">Löschen</button>
                </div>
            </div>`).join('');
    }

    /* ── Create ───────────────────────────────── */
    document.getElementById('addBtn').addEventListener('click', () => {
        document.getElementById('newEqName').value = '';
        document.getElementById('newEqLocation').value = '';
        document.getElementById('newEqLat').value = '';
        document.getElementById('newEqLng').value = '';
        hideError('createEqError');
        openModal(createModal);
    });
    document.getElementById('createEqCancel').addEventListener('click', () => closeModal(createModal));
    document.getElementById('createEqSave').addEventListener('click', async () => {
        const displayName = document.getElementById('newEqName').value.trim();
        if (!displayName) { showError('createEqError', 'Bezeichnung ist Pflicht.'); return; }
        const btn = document.getElementById('createEqSave');
        btn.disabled = true;
        const res = await apiCall(`${API}/manage/equipment`, {
            method: 'POST',
            headers: { ...FT_AUTH.adminHeaders(), 'Content-Type': 'application/json' },
            body: JSON.stringify({
                displayName,
                location: document.getElementById('newEqLocation').value.trim() || null,
                latitude: parseFloat(document.getElementById('newEqLat').value) || null,
                longitude: parseFloat(document.getElementById('newEqLng').value) || null
            })
        });
        btn.disabled = false;
        if (!res) return;
        if (res.ok) { closeModal(createModal); showToast('Equipment angelegt'); loadEquipment(); }
        else { const d = await res.json().catch(() => ({})); showError('createEqError', d.error || 'Fehler'); }
    });

    /* ── Edit ─────────────────────────────────── */
    let editTarget = '';
    window.EQ = window.EQ || {};
    EQ.edit = function (rowKey, displayName, location, lat, lng) {
        editTarget = rowKey;
        document.getElementById('editEqName').value = displayName;
        document.getElementById('editEqLocation').value = location;
        document.getElementById('editEqLat').value = lat || '';
        document.getElementById('editEqLng').value = lng || '';
        hideError('editEqError');
        openModal(editModal);
    };
    document.getElementById('editEqCancel').addEventListener('click', () => closeModal(editModal));
    document.getElementById('editEqSave').addEventListener('click', async () => {
        const displayName = document.getElementById('editEqName').value.trim();
        if (!displayName) { showError('editEqError', 'Bezeichnung darf nicht leer sein.'); return; }
        const btn = document.getElementById('editEqSave');
        btn.disabled = true;
        const res = await apiCall(`${API}/manage/equipment/${encodeURIComponent(editTarget)}`, {
            method: 'PUT',
            headers: { ...FT_AUTH.adminHeaders(), 'Content-Type': 'application/json' },
            body: JSON.stringify({
                displayName,
                location: document.getElementById('editEqLocation').value.trim() || null,
                latitude: parseFloat(document.getElementById('editEqLat').value) || null,
                longitude: parseFloat(document.getElementById('editEqLng').value) || null
            })
        });
        btn.disabled = false;
        if (!res) return;
        if (res.ok) { closeModal(editModal); showToast('Equipment aktualisiert'); loadEquipment(); }
        else { const d = await res.json().catch(() => ({})); showError('editEqError', d.error || 'Fehler'); }
    });

    /* ── Delete ───────────────────────────────── */
    EQ.del = async function (rowKey, displayName) {
        if (!confirm(`"${displayName}" wirklich löschen?`)) return;
        const res = await apiCall(`${API}/manage/equipment/${encodeURIComponent(rowKey)}`, {
            method: 'DELETE', headers: FT_AUTH.adminHeaders()
        });
        if (!res) return;
        if (res.ok) { showToast('Equipment gelöscht'); loadEquipment(); }
        else { showToast('Fehler beim Löschen', false); }
    };

    /* ── Location search (Nominatim, city-level) ── */
    async function searchLocation(inputId, latId, lngId) {
        const input = document.getElementById(inputId);
        const q = input.value.trim();
        if (q.length < 2) { showToast('Mindestens 2 Zeichen', false); return; }
        try {
            const params = new URLSearchParams({ q, format: 'json', addressdetails: '1', countrycodes: 'de,nl', limit: '5', featuretype: 'city' });
            const res = await fetch(`https://nominatim.openstreetmap.org/search?${params}`, { headers: { 'Accept-Language': 'de' } });
            const results = await res.json();
            if (results.length === 0) { showToast('Kein Ort gefunden', false); return; }
            const r = results[0];
            const city = r.address?.city || r.address?.town || r.address?.village || r.address?.municipality || r.display_name.split(',')[0];
            input.value = city;
            document.getElementById(latId).value = r.lat;
            document.getElementById(lngId).value = r.lon;
            showToast(`📍 ${city}`);
        } catch { showToast('Fehler bei der Ortssuche', false); }
    }

    document.getElementById('newEqLocationSearch').addEventListener('click', () => searchLocation('newEqLocation', 'newEqLat', 'newEqLng'));
    document.getElementById('editEqLocationSearch').addEventListener('click', () => searchLocation('editEqLocation', 'editEqLat', 'editEqLng'));

    /* ── Init ─────────────────────────────────── */
    loadEquipment();
})();
