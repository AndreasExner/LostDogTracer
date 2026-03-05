(function () {
    'use strict';

    const API_BASE = FT_AUTH.getApiBase();

    const listEl = document.getElementById('categoryList');
    const inputEl = document.getElementById('newCategory');
    const svgInputEl = document.getElementById('newSvg');
    const previewEl = document.getElementById('newPreview');
    const addBtn = document.getElementById('addBtn');
    const toastEl = document.getElementById('toast');
    let toastTimeout = null;

    /** Render a marker-shaped SVG preview */
    function markerPreview(svgInner, color) {
        color = color || '#0071e3';
        const inner = svgInner || `<circle cx="12" cy="12" r="5" fill="#fff"/>`;
        return `<svg width="30" height="44" viewBox="0 0 24 36" xmlns="http://www.w3.org/2000/svg">` +
            `<path d="M12 0C5.4 0 0 5.4 0 12c0 9 12 24 12 24s12-15 12-24C24 5.4 18.6 0 12 0z" fill="${color}"/>` +
            inner + `</svg>`;
    }

    // Live preview while typing new SVG
    svgInputEl.addEventListener('input', () => {
        previewEl.innerHTML = markerPreview(svgInputEl.value.trim());
    });
    previewEl.innerHTML = markerPreview('');

    async function loadCategories() {
        listEl.innerHTML = '<li style="color:#6e6e73">Lädt…</li>';
        try {
            const res = await fetch(`${API_BASE}/manage/categories`, { headers: FT_AUTH.adminHeaders() });
            if (res.status === 401) { FT_AUTH.logout(); location.href = 'admin.html'; return; }
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
            li.className = 'category-item';
            li.innerHTML =
                `<div class="svg-preview">${markerPreview(item.svgSymbol)}</div>` +
                `<div style="flex:1">` +
                    `<div class="item-name">${esc(item.name)}</div>` +
                    `<textarea rows="2" data-rk="${esc(item.rowKey)}">${esc(item.svgSymbol || '')}</textarea>` +
                `</div>` +
                `<div class="category-actions">` +
                    `<button class="btn btn-primary btn-sm save-svg-btn" data-rk="${esc(item.rowKey)}">SVG speichern</button>` +
                    `<button class="btn btn-danger btn-sm del-btn" data-rk="${esc(item.rowKey)}" data-name="${esc(item.name)}">Löschen</button>` +
                `</div>`;

            // Live preview on textarea input
            const ta = li.querySelector('textarea');
            const prev = li.querySelector('.svg-preview');
            ta.addEventListener('input', () => {
                prev.innerHTML = markerPreview(ta.value.trim());
            });

            listEl.appendChild(li);
        });
    }

    // Delegate clicks for save-svg and delete buttons
    listEl.addEventListener('click', async (e) => {
        const saveBtn = e.target.closest('.save-svg-btn');
        if (saveBtn) {
            const rk = saveBtn.dataset.rk;
            const ta = listEl.querySelector(`textarea[data-rk="${rk}"]`);
            if (!ta) return;
            saveBtn.disabled = true;
            try {
                const res = await fetch(`${API_BASE}/manage/categories/${encodeURIComponent(rk)}`, {
                    method: 'PUT',
                    headers: FT_AUTH.adminHeaders({ 'Content-Type': 'application/json' }),
                    body: JSON.stringify({ svgSymbol: ta.value })
                });
                if (res.status === 401) { FT_AUTH.logout(); location.href = 'admin.html'; return; }
                if (!res.ok) throw new Error();
                showToast('SVG gespeichert');
            } catch {
                showToast('Fehler beim Speichern', true);
            } finally {
                saveBtn.disabled = false;
            }
        }

        const delBtn = e.target.closest('.del-btn');
        if (delBtn) {
            const rk = delBtn.dataset.rk;
            const name = delBtn.dataset.name;
            if (!confirm(`„${name}" wirklich löschen?`)) return;
            try {
                const res = await fetch(`${API_BASE}/manage/categories/${encodeURIComponent(rk)}`, {
                    method: 'DELETE',
                    headers: FT_AUTH.adminHeaders()
                });
                if (res.status === 401) { FT_AUTH.logout(); location.href = 'admin.html'; return; }
                if (!res.ok) throw new Error();
                showToast(`„${name}" gelöscht`);
                await loadCategories();
            } catch {
                showToast('Fehler beim Löschen', true);
            }
        }
    });

    async function addCategory() {
        const name = inputEl.value.trim();
        if (!name) return;
        addBtn.disabled = true;
        try {
            const res = await fetch(`${API_BASE}/manage/categories`, {
                method: 'POST',
                headers: FT_AUTH.adminHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify({ name, svgSymbol: svgInputEl.value.trim() })
            });
            if (res.status === 401) { FT_AUTH.logout(); location.href = 'admin.html'; return; }
            if (!res.ok) throw new Error();
            inputEl.value = '';
            svgInputEl.value = '';
            previewEl.innerHTML = markerPreview('');
            showToast(`„${name}" hinzugefügt`);
            await loadCategories();
        } catch {
            showToast('Fehler beim Hinzufügen', true);
        } finally {
            addBtn.disabled = false;
            inputEl.focus();
        }
    }

    inputEl.addEventListener('keydown', e => { if (e.key === 'Enter') addCategory(); });
    addBtn.addEventListener('click', addCategory);

    function esc(s) {
        const d = document.createElement('div');
        d.textContent = s;
        return d.innerHTML;
    }

    function showToast(msg, isError) {
        clearTimeout(toastTimeout);
        toastEl.textContent = msg;
        toastEl.className = 'toast' + (isError ? ' error' : '');
        toastTimeout = setTimeout(() => toastEl.classList.add('hidden'), 2500);
    }

    loadCategories();
})();
