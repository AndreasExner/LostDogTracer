(function () {
    'use strict';

    const API_BASE = FT_AUTH.getApiBase();

    const listEl = document.getElementById('dogList');
    const inputEl = document.getElementById('newDog');
    const addBtn = document.getElementById('addBtn');
    const toastEl = document.getElementById('toast');
    let toastTimeout = null;

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
                ? `${esc(item.location)} <span style="color:#6e6e73;font-size:0.875rem">(${esc(item.suffix)})</span>`
                : esc(item.location);
            li.innerHTML = `<span class="item-name">${display}</span>`;

            if (item.suffix) {
                const linkBtn = document.createElement('button');
                linkBtn.className = 'btn btn-secondary btn-sm';
                linkBtn.style.marginRight = '0.5rem';
                linkBtn.textContent = '🔗 Link';
                linkBtn.addEventListener('click', () => copyGuestLink(item.suffix, item.location));
                li.appendChild(linkBtn);
            }

            const btn = document.createElement('button');
            btn.className = 'btn btn-danger btn-sm';
            btn.textContent = 'Löschen';
            btn.addEventListener('click', () => deleteDog(item.rowKey, item.location));
            li.appendChild(btn);
            listEl.appendChild(li);
        });
    }

    async function addDog() {
        const location = inputEl.value.trim();
        if (!location) {
            inputEl.style.borderColor = '#ff3b30';
            inputEl.style.boxShadow = '0 0 0 3px rgba(255,59,48,.12)';
            inputEl.focus();
            setTimeout(() => { inputEl.style.borderColor = ''; inputEl.style.boxShadow = ''; }, 2000);
            return;
        }
        addBtn.disabled = true;
        try {
            const res = await fetch(`${API_BASE}/manage/lost-dogs`, {
                method: 'POST',
                headers: FT_AUTH.adminHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify({ location })
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) throw new Error();
            inputEl.value = '';
            showToast(`\u201E${location}\u201C hinzugefügt`);
            await loadDogs();
        } catch {
            showToast('Fehler beim Hinzufügen', true);
        } finally {
            addBtn.disabled = false;
            inputEl.focus();
        }
    }

    async function deleteDog(rowKey, location) {
        if (!confirm(`\u201E${location}\u201C wirklich löschen?`)) return;
        try {
            const res = await fetch(`${API_BASE}/manage/lost-dogs/${encodeURIComponent(rowKey)}`, {
                method: 'DELETE',
                headers: FT_AUTH.adminHeaders()
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) throw new Error();
            showToast(`\u201E${location}\u201C gelöscht`);
            await loadDogs();
        } catch {
            showToast('Fehler beim Löschen', true);
        }
    }

    inputEl.addEventListener('keydown', e => { if (e.key === 'Enter') addDog(); });
    addBtn.addEventListener('click', addDog);

    async function copyGuestLink(suffix, location) {
        const url = `${window.location.origin}/guest-home.html?key=${encodeURIComponent(suffix)}`;
        try {
            await navigator.clipboard.writeText(url);
            showToast(`Link für \u201E${location}\u201C kopiert`);
        } catch {
            prompt('Link kopieren:', url);
        }
    }

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
