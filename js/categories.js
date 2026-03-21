(function () {
    'use strict';

    const API_BASE = FT_AUTH.getApiBase();

    const listEl = document.getElementById('categoryList');
    const inputEl = document.getElementById('newCategory');
    const newIconKeyEl = document.getElementById('newIconKey');
    const newPickerEl = document.getElementById('newIconPicker');
    const previewEl = document.getElementById('newPreview');
    const addBtn = document.getElementById('addBtn');
    const toastEl = document.getElementById('toast');
    let toastTimeout = null;

    /** Render a marker-shaped SVG preview from an icon key */
    function markerPreview(iconKey, color) {
        color = color || '#0071e3';
        const inner = resolveIconSvg(iconKey);
        return '<svg width="30" height="44" viewBox="0 0 24 36" xmlns="http://www.w3.org/2000/svg">' +
            '<path d="M12 0C5.4 0 0 5.4 0 12c0 9 12 24 12 24s12-15 12-24C24 5.4 18.6 0 12 0z" fill="' + color + '"/>' +
            inner + '</svg>';
    }

    /** Build an icon picker into a container element, call onChange(key) on selection */
    function buildIconPicker(container, selectedKey, onChange) {
        container.innerHTML = '';
        Object.keys(SVG_ICONS).forEach(function (key) {
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'icon-picker-btn' + (key === selectedKey ? ' selected' : '');
            btn.title = SVG_ICONS[key].label;
            btn.innerHTML = '<svg width="24" height="24" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">' +
                '<rect width="24" height="24" rx="4" fill="#0071e3"/>' +
                SVG_ICONS[key].svg + '</svg>';
            btn.addEventListener('click', function () {
                container.querySelectorAll('.icon-picker-btn').forEach(function (b) { b.classList.remove('selected'); });
                btn.classList.add('selected');
                onChange(key);
            });
            container.appendChild(btn);
        });
    }

    // Build icon picker for new category
    buildIconPicker(newPickerEl, 'default', function (key) {
        newIconKeyEl.value = key;
        previewEl.innerHTML = markerPreview(key);
    });
    previewEl.innerHTML = markerPreview('default');

    async function loadCategories() {
        listEl.innerHTML = '<li style="color:#6e6e73">Lädt…</li>';
        try {
            const res = await fetch(`${API_BASE}/manage/categories`, { headers: FT_AUTH.adminHeaders() });
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
            const currentIcon = (SVG_ICONS[item.svgSymbol] ? item.svgSymbol : 'default');
            const li = document.createElement('li');
            li.className = 'category-item';

            const prev = document.createElement('div');
            prev.className = 'svg-preview';
            prev.innerHTML = markerPreview(currentIcon);

            const mid = document.createElement('div');
            mid.style.flex = '1';
            const nameDiv = document.createElement('div');
            nameDiv.className = 'item-name';
            nameDiv.textContent = item.displayName;
            const picker = document.createElement('div');
            picker.className = 'icon-picker';

            mid.appendChild(nameDiv);
            mid.appendChild(picker);

            const actions = document.createElement('div');
            actions.className = 'category-actions';

            const saveBtn = document.createElement('button');
            saveBtn.className = 'btn btn-primary btn-sm save-svg-btn';
            saveBtn.textContent = 'Icon speichern';

            const delBtn = document.createElement('button');
            delBtn.className = 'btn btn-danger btn-sm del-btn';
            delBtn.textContent = 'Löschen';

            actions.appendChild(saveBtn);
            actions.appendChild(delBtn);

            li.appendChild(prev);
            li.appendChild(mid);
            li.appendChild(actions);

            let selectedIcon = currentIcon;
            buildIconPicker(picker, currentIcon, function (key) {
                selectedIcon = key;
                prev.innerHTML = markerPreview(key);
            });

            saveBtn.addEventListener('click', async function () {
                saveBtn.disabled = true;
                try {
                    const res = await fetch(`${API_BASE}/manage/categories/${encodeURIComponent(item.rowKey)}`, {
                        method: 'PUT',
                        headers: FT_AUTH.adminHeaders({ 'Content-Type': 'application/json' }),
                        body: JSON.stringify({ svgSymbol: selectedIcon })
                    });
                    if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
                    if (!res.ok) throw new Error();
                    showToast('Icon gespeichert');
                } catch {
                    showToast('Fehler beim Speichern', true);
                } finally {
                    saveBtn.disabled = false;
                }
            });

            delBtn.addEventListener('click', async function () {
                if (!confirm('\u201E' + item.displayName + '\u201C wirklich löschen?')) return;
                try {
                    const res = await fetch(`${API_BASE}/manage/categories/${encodeURIComponent(item.rowKey)}`, {
                        method: 'DELETE',
                        headers: FT_AUTH.adminHeaders()
                    });
                    if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
                    if (!res.ok) throw new Error();
                    showToast('\u201E' + item.displayName + '\u201C gelöscht');
                    await loadCategories();
                } catch {
                    showToast('Fehler beim Löschen', true);
                }
            });

            listEl.appendChild(li);
        });
    }

    async function addCategory() {
        const name = inputEl.value.trim();
        if (!name) {
            inputEl.style.borderColor = '#ff3b30';
            inputEl.style.boxShadow = '0 0 0 3px rgba(255,59,48,.12)';
            inputEl.focus();
            setTimeout(() => { inputEl.style.borderColor = ''; inputEl.style.boxShadow = ''; }, 2000);
            return;
        }
        addBtn.disabled = true;
        try {
            const res = await fetch(`${API_BASE}/manage/categories`, {
                method: 'POST',
                headers: FT_AUTH.adminHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify({ name, svgSymbol: newIconKeyEl.value })
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) throw new Error();
            inputEl.value = '';
            newIconKeyEl.value = 'default';
            buildIconPicker(newPickerEl, 'default', function (key) {
                newIconKeyEl.value = key;
                previewEl.innerHTML = markerPreview(key);
            });
            previewEl.innerHTML = markerPreview('default');
            showToast('\u201E' + name + '\u201C hinzugefügt');
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

    function showToast(msg, isError) {
        clearTimeout(toastTimeout);
        toastEl.textContent = msg;
        toastEl.className = 'toast' + (isError ? ' error' : '');
        toastTimeout = setTimeout(() => toastEl.classList.add('hidden'), 2500);
    }

    loadCategories();
})();
