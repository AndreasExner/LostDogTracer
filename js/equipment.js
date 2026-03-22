/* js/equipment.js — Equipment-Verwaltung */
(function () {
    'use strict';

    const API = FT_AUTH.getApiBase();
    const roleLevel = FT_AUTH.getRoleLevel();
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
        listEl.innerHTML = items.map(item => {
            const locInfo = item.location ? '📍 ' + esc(item.location) : '';
            const userInfo = item.userName ? '👤 ' + esc(item.userName) : '';
            const commentInfo = item.comment ? esc(item.comment) : '';
            const info = [locInfo, userInfo, commentInfo].filter(Boolean).join(' · ') || 'Keine Details';
            return `
            <div class="eq-card">
                <div class="eq-info">
                    <strong>${esc(item.displayName)}</strong>
                    <small>${info}</small>
                </div>
                <div class="eq-actions">
                    <button class="btn btn-secondary btn-sm" onclick="EQ.edit('${esc(item.rowKey)}','${esc(item.displayName)}','${esc(item.comment || '')}','${esc(item.userName || '')}','${esc(item.location || '')}',${item.latitude || 0},${item.longitude || 0})">${roleLevel >= 3 ? 'Bearbeiten' : 'Standort'}</button>
                    ${roleLevel >= 3 ? `<button class="btn btn-sm" style="background:#ff3b30;color:#fff" onclick="EQ.del('${esc(item.rowKey)}','${esc(item.displayName)}')">L\u00f6schen</button>` : ''}
                </div>
            </div>`;
        }).join('');
    }

    /* ── Create ───────────────────────────────── */
    if (roleLevel >= 3) {
        document.getElementById('addBtn').addEventListener('click', () => {
            document.getElementById('newEqName').value = '';
            document.getElementById('newEqComment').value = '';
            hideError('createEqError');
            openModal(createModal);
        });
    } else {
        document.getElementById('addBtn').style.display = 'none';
    }
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
                comment: document.getElementById('newEqComment').value.trim() || null
            })
        });
        btn.disabled = false;
        if (!res) return;
        if (res.ok) { closeModal(createModal); showToast('Equipment angelegt'); loadEquipment(); }
        else { const d = await res.json().catch(() => ({})); showError('createEqError', d.error || 'Fehler'); }
    });

    /* ── Edit: location mode handling ─────────── */
    const modeButtons = document.querySelectorAll('.loc-mode-btn');
    const locModeOrt = document.getElementById('locModeOrt');
    const locModeMitglied = document.getElementById('locModeMitglied');
    const locModeEinsatz = document.getElementById('locModeEinsatz');
    const resolvedLocEl = document.getElementById('editEqResolvedLoc');
    const memberSelect = document.getElementById('editEqMemberSelect');
    const einsatzSelect = document.getElementById('editEqEinsatzSelect');
    let currentMode = 'ort';
    let cachedUsers = null;
    let cachedEinsatzRecords = null;

    function setMode(mode) {
        currentMode = mode;
        modeButtons.forEach(b => b.classList.toggle('active', b.dataset.mode === mode));
        locModeOrt.style.display = mode === 'ort' ? '' : 'none';
        locModeMitglied.style.display = mode === 'mitglied' ? '' : 'none';
        locModeEinsatz.style.display = mode === 'einsatz' ? '' : 'none';
        resolvedLocEl.style.display = 'none';
        resolvedLocEl.textContent = '';
    }

    modeButtons.forEach(b => b.addEventListener('click', async () => {
        setMode(b.dataset.mode);
        if (b.dataset.mode === 'mitglied') await loadMembers();
        if (b.dataset.mode === 'einsatz') await loadEinsatzRecords();
    }));

    /* Load users with location for "Mitglied" mode */
    async function loadMembers() {
        if (cachedUsers) return;
        memberSelect.innerHTML = '<option value="">Lädt…</option>';
        const res = await apiCall(`${API}/manage/equipment/members`, { headers: FT_AUTH.adminHeaders() });
        if (!res || !res.ok) { memberSelect.innerHTML = '<option value="">Fehler beim Laden</option>'; return; }
        const users = await res.json();
        cachedUsers = users;
        renderMemberOptions();
    }

    function renderMemberOptions() {
        memberSelect.innerHTML = '<option value="">Mitglied wählen…</option>';
        cachedUsers.forEach((u, i) => {
            const opt = document.createElement('option');
            opt.value = i;
            opt.textContent = `${u.displayName} — ${u.location}`;
            memberSelect.appendChild(opt);
        });
    }

    memberSelect.addEventListener('change', () => {
        const idx = memberSelect.value;
        if (idx === '') { clearLocationFields(); return; }
        const u = cachedUsers[parseInt(idx)];
        document.getElementById('editEqLocation').value = u.location;
        document.getElementById('editEqLat').value = u.latitude;
        document.getElementById('editEqLng').value = u.longitude;
        document.getElementById('editEqUserName').value = u.displayName;
        resolvedLocEl.textContent = `📍 ${u.location} (${u.displayName})`;
        resolvedLocEl.style.display = '';
    });

    /* Load GPS records for "Im Einsatz" mode */
    async function loadEinsatzRecords() {
        if (cachedEinsatzRecords) return;
        einsatzSelect.innerHTML = '<option value="">Lädt…</option>';

        // First load categories to find RowKeys for target categories
        const catRes = await apiCall(`${API}/categories`, { headers: FT_AUTH.publicHeaders() });
        if (!catRes || !catRes.ok) { einsatzSelect.innerHTML = '<option value="">Fehler beim Laden</option>'; return; }
        const categories = await catRes.json();
        const targetNames = ['Standort-Falle', 'Futterstelle/Kamera'];
        const targetKeys = categories.filter(c => targetNames.includes(c.displayName)).map(c => c.rowKey);

        if (targetKeys.length === 0) {
            einsatzSelect.innerHTML = '<option value="">Keine passenden Kategorien gefunden</option>';
            cachedEinsatzRecords = [];
            return;
        }

        // Fetch GPS records filtered by those categories
        const categoryParam = targetKeys.join(',');
        const gpsRes = await apiCall(`${API}/manage/gps-records?pageSize=all&category=${encodeURIComponent(categoryParam)}`, { headers: FT_AUTH.adminHeaders() });
        if (!gpsRes || !gpsRes.ok) { einsatzSelect.innerHTML = '<option value="">Fehler beim Laden</option>'; return; }
        const data = await gpsRes.json();
        cachedEinsatzRecords = (data.records || []).filter(r => r.location && r.latitude && r.longitude);
        renderEinsatzOptions();
    }

    function renderEinsatzOptions() {
        einsatzSelect.innerHTML = '<option value="">Einsatzort wählen…</option>';
        cachedEinsatzRecords.forEach((r, i) => {
            const opt = document.createElement('option');
            opt.value = i;
            opt.textContent = `${r.lostDog || '–'} — ${r.location}`;
            einsatzSelect.appendChild(opt);
        });
    }

    einsatzSelect.addEventListener('change', () => {
        const idx = einsatzSelect.value;
        if (idx === '') { clearLocationFields(); return; }
        const r = cachedEinsatzRecords[parseInt(idx)];
        document.getElementById('editEqLocation').value = r.location;
        document.getElementById('editEqLat').value = r.latitude;
        document.getElementById('editEqLng').value = r.longitude;
        document.getElementById('editEqUserName').value = r.lostDog || '';
        resolvedLocEl.textContent = `📍 ${r.location} (${r.lostDog || '–'})`;
        resolvedLocEl.style.display = '';
    });

    function clearLocationFields() {
        document.getElementById('editEqLocation').value = '';
        document.getElementById('editEqLat').value = '';
        document.getElementById('editEqLng').value = '';
        document.getElementById('editEqUserName').value = '';
        resolvedLocEl.style.display = 'none';
        resolvedLocEl.textContent = '';
    }

    /* ── Edit ─────────────────────────────────── */
    let editTarget = '';
    window.EQ = window.EQ || {};
    EQ.edit = function (rowKey, displayName, comment, userName, location, lat, lng) {
        editTarget = rowKey;
        document.getElementById('editEqName').value = displayName;
        document.getElementById('editEqComment').value = comment || '';
        document.getElementById('editEqLocation').value = location;
        document.getElementById('editEqLat').value = lat || '';
        document.getElementById('editEqLng').value = lng || '';
        document.getElementById('editEqUserName').value = userName || '';
        hideError('editEqError');
        // Disable name/comment for PowerUser
        document.getElementById('editEqName').disabled = roleLevel < 3;
        document.getElementById('editEqComment').disabled = roleLevel < 3;
        // Reset to "Ort" mode, clear caches
        cachedUsers = null;
        cachedEinsatzRecords = null;
        setMode('ort');
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
                comment: document.getElementById('editEqComment').value.trim() || null,
                userName: document.getElementById('editEqUserName').value.trim() || null,
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
            // Clear UserName when using manual Ort mode
            document.getElementById('editEqUserName').value = '';
            showToast(`📍 ${city}`);
        } catch { showToast('Fehler bei der Ortssuche', false); }
    }

    document.getElementById('editEqLocationSearch').addEventListener('click', () => searchLocation('editEqLocation', 'editEqLat', 'editEqLng'));

    /* ── Init ─────────────────────────────────── */
    loadEquipment();
})();
