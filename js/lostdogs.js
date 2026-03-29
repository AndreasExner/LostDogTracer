(function () {
    'use strict';

    const API_BASE = FT_AUTH.getApiBase();

    const listEl = document.getElementById('dogList');
    const addBtn = document.getElementById('addBtn');
    const toastEl = document.getElementById('toast');
    const createModal = document.getElementById('createDogModal');
    const editModal = document.getElementById('editDogModal');
    const shareModal = document.getElementById('shareDogModal');
    let toastTimeout = null;

    function openModal(m) { m.classList.add('open'); }
    function closeModal(m) { m.classList.remove('open'); }
    function showError(id, msg) { const el = document.getElementById(id); el.textContent = msg; el.style.display = 'block'; }
    function hideError(id) { const el = document.getElementById(id); el.textContent = ''; el.style.display = 'none'; }

    async function loadDogs() {
        listEl.innerHTML = '<li style="color:#6e6e73">Lädt…</li>';
        try {
            const res = await fetch(`${API_BASE}/manage/lost-dogs`, { headers: FT_AUTH.adminHeaders() });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) throw new Error();
            const items = await res.json();
            renderList(items);
        } catch {
            listEl.innerHTML = '<li style="color:#ff3b30">Fehler beim Laden</li>';
        }
    }

    function renderList(items) {
        listEl.innerHTML = '';
        if (items.length === 0) {
            listEl.innerHTML = '<li style="color:#6e6e73">Keine Einträge</li>';
            return;
        }
        items.forEach(item => {
            const li = document.createElement('li');
            const display = item.suffix
                ? `${esc(item.displayName)} <span style="color:#6e6e73;font-size:0.875rem">(${esc(item.suffix)})</span>`
                : esc(item.displayName);
            li.innerHTML = `<span class="item-name">${display}</span>`;

            const editBtn = document.createElement('button');
            editBtn.className = 'btn btn-secondary btn-sm';
            editBtn.style.marginRight = '0.5rem';
            editBtn.textContent = 'Bearbeiten';
            editBtn.addEventListener('click', () => openEditDog(item.rowKey, item.displayName));
            li.appendChild(editBtn);

            if (item.suffix) {
                const shareBtn = document.createElement('button');
                shareBtn.className = 'btn btn-secondary btn-sm';
                shareBtn.style.marginRight = '0.5rem';
                shareBtn.textContent = '📤 Teilen';
                shareBtn.addEventListener('click', () => openShareModal(item.rowKey, item.suffix, item.displayName));
                li.appendChild(shareBtn);
            }

            const delBtn = document.createElement('button');
            delBtn.className = 'btn btn-danger btn-sm';
            delBtn.textContent = 'Löschen';
            delBtn.addEventListener('click', () => deleteDog(item.rowKey, item.displayName));
            li.appendChild(delBtn);
            listEl.appendChild(li);
        });
    }

    /* ── Create dog ───────────────────────────── */
    addBtn.addEventListener('click', () => {
        document.getElementById('newDogName').value = '';
        document.getElementById('newDogLocation').value = '';
        hideError('createDogError');
        openModal(createModal);
    });
    document.getElementById('createDogCancel').addEventListener('click', () => closeModal(createModal));
    document.getElementById('createDogSave').addEventListener('click', async () => {
        const name = document.getElementById('newDogName').value.trim();
        const location = document.getElementById('newDogLocation').value.trim();
        if (!name) { showError('createDogError', 'Name ist ein Pflichtfeld.'); return; }

        const btn = document.getElementById('createDogSave');
        btn.disabled = true;
        try {
            const res = await fetch(`${API_BASE}/manage/lost-dogs`, {
                method: 'POST',
                headers: FT_AUTH.adminHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify({ name, location })
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) throw new Error();
            closeModal(createModal);
            const displayName = location ? `${name}, ${location}` : name;
            showToast(`\u201E${displayName}\u201C hinzugefügt`);
            await loadDogs();
        } catch {
            showError('createDogError', 'Fehler beim Anlegen.');
        } finally {
            btn.disabled = false;
        }
    });

    /* ── Edit dog ─────────────────────────────── */
    let editTarget = '';
    function openEditDog(rowKey, displayName) {
        editTarget = rowKey;
        document.getElementById('editDogTitle').textContent = rowKey;
        document.getElementById('editDogDisplayName').value = displayName;
        hideError('editDogError');
        openModal(editModal);
    }
    document.getElementById('editDogCancel').addEventListener('click', () => closeModal(editModal));
    document.getElementById('editDogSave').addEventListener('click', async () => {
        const displayName = document.getElementById('editDogDisplayName').value.trim();
        if (!displayName) { showError('editDogError', 'Anzeigename darf nicht leer sein.'); return; }

        const btn = document.getElementById('editDogSave');
        btn.disabled = true;
        try {
            const res = await fetch(`${API_BASE}/manage/lost-dogs/${encodeURIComponent(editTarget)}`, {
                method: 'PUT',
                headers: FT_AUTH.adminHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify({ displayName })
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) throw new Error();
            closeModal(editModal);
            showToast('Anzeigename aktualisiert');
            await loadDogs();
        } catch {
            showError('editDogError', 'Fehler beim Speichern.');
        } finally {
            btn.disabled = false;
        }
    });

    /* ── Delete dog ───────────────────────────── */
    async function deleteDog(rowKey, displayName) {
        if (!confirm(`\u201E${displayName}\u201C wirklich löschen?`)) return;
        try {
            const res = await fetch(`${API_BASE}/manage/lost-dogs/${encodeURIComponent(rowKey)}`, {
                method: 'DELETE',
                headers: FT_AUTH.adminHeaders()
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) throw new Error();
            showToast(`\u201E${displayName}\u201C gelöscht`);
            await loadDogs();
        } catch {
            showToast('Fehler beim Löschen', true);
        }
    }

    /* ── Share modal ───────────────────────────── */
    let shareRowKey = '';
    let shareSuffix = '';
    let shareDisplayName = '';

    function openShareModal(rowKey, suffix, displayName) {
        shareRowKey = rowKey;
        shareSuffix = suffix;
        shareDisplayName = displayName;
        document.getElementById('shareDogTitle').textContent = displayName;
        document.getElementById('shareGuestCopy').textContent = '📋 Helfer-Link kopieren';
        document.getElementById('shareOwnerCopy').textContent = '📋 Besitzer-Link erzeugen & kopieren';
        document.getElementById('shareOwnerStatus').style.display = 'none';
        document.getElementById('shareOwnerRegenerate').checked = false;
        openModal(shareModal);
    }

    document.getElementById('shareDogClose').addEventListener('click', () => closeModal(shareModal));

    document.getElementById('shareGuestCopy').addEventListener('click', async () => {
        const url = `${window.location.origin}/guest-home.html?key=${encodeURIComponent(shareSuffix)}`;
        try {
            await navigator.clipboard.writeText(url);
            document.getElementById('shareGuestCopy').textContent = '✓ Kopiert';
            showToast(`Helfer-Link für \u201E${shareDisplayName}\u201C kopiert`);
        } catch {
            prompt('Link kopieren:', url);
        }
    });

    document.getElementById('shareOwnerCopy').addEventListener('click', async () => {
        const btn = document.getElementById('shareOwnerCopy');
        const status = document.getElementById('shareOwnerStatus');
        const regenerate = document.getElementById('shareOwnerRegenerate').checked;

        if (regenerate && !confirm('Bisherigen Besitzer-Link wirklich ungültig machen?')) return;

        btn.disabled = true;
        btn.textContent = '⏳ Wird erzeugt…';

        try {
            const apiUrl = `${API_BASE}/manage/lost-dogs/${encodeURIComponent(shareRowKey)}/owner-key` + (regenerate ? '?force=true' : '');
            const res = await fetch(apiUrl, {
                method: 'POST',
                headers: FT_AUTH.adminHeaders()
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) throw new Error();
            const data = await res.json();
            const ownerUrl = `${window.location.origin}/owner-home.html?key=${encodeURIComponent(data.ownerKey)}`;
            await navigator.clipboard.writeText(ownerUrl);
            btn.textContent = '✓ Kopiert';
            status.textContent = '✓ Besitzer-Link wurde in die Zwischenablage kopiert';
            status.style.display = 'block';
            showToast(`Besitzer-Link für \u201E${shareDisplayName}\u201C kopiert`);
        } catch {
            btn.textContent = '❌ Fehler';
            status.textContent = 'Fehler beim Erzeugen des Links';
            status.style.display = 'block';
            status.style.color = '#ff3b30';
        } finally {
            btn.disabled = false;
        }
    });

    function esc(s) {
        return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
    }

    function showToast(msg, isError) {
        clearTimeout(toastTimeout);
        toastEl.textContent = msg;
        toastEl.className = 'toast' + (isError ? ' error' : '');
        toastTimeout = setTimeout(() => toastEl.classList.add('hidden'), 2500);
    }

    loadDogs();
})();
