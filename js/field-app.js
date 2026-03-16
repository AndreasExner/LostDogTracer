/* js/field-app.js — Feldarbeit Standorterfassung (mit Auth + Offline) */
(function () {
    'use strict';

    const API_BASE = FT_AUTH.getApiBase();
    const API_KEY_HDR = FT_AUTH.publicHeaders();
    const STORAGE_KEY_NAME = 'lostdogtracer_field_userName';
    const STORAGE_KEY_LOCATION = 'lostdogtracer_field_lostDog';
    const STORAGE_KEY_CATEGORY = 'lostdogtracer_field_category';

    const userNameEl = document.getElementById('userName');
    const lostDogEl = document.getElementById('lostDog');
    const categoryEl = document.getElementById('category');
    const commentEl = document.getElementById('comment');
    const saveBtnEl = document.getElementById('saveBtn');
    const toastEl = document.getElementById('toast');
    const photoBtnEl = document.getElementById('photoBtn');
    const photoInputEl = document.getElementById('photoInput');
    const photoPreviewEl = document.getElementById('photoPreview');
    const previewImgEl = document.getElementById('previewImg');
    const removePhotoBtnEl = document.getElementById('removePhotoBtn');
    const editBtnEl = document.getElementById('editBtn');
    const mapBtnEl = document.getElementById('mapBtn');

    let toastTimeout = null;
    let selectedPhotoBlob = null;
    const charCounterEl = document.getElementById('charCounter');

    function updateCharCounter() {
        const len = commentEl.value.length;
        const max = 40;
        charCounterEl.textContent = `${len} / ${max}`;
        charCounterEl.classList.toggle('warn', len >= 30 && len < max);
        charCounterEl.classList.toggle('full', len >= max);
    }

    async function init() {
        await Promise.all([loadNames(), loadLostDogs(), loadCategories()]);
        restoreSelections();
        updateButtonState();

        userNameEl.addEventListener('change', onSelectionChange);
        lostDogEl.addEventListener('change', onSelectionChange);
        categoryEl.addEventListener('change', onSelectionChange);
        saveBtnEl.addEventListener('click', onSaveLocation);
        commentEl.addEventListener('input', updateCharCounter);

        editBtnEl.addEventListener('click', onEditRecords);
        mapBtnEl.addEventListener('click', onShowMap);

        photoBtnEl.addEventListener('click', () => photoInputEl.click());
        photoInputEl.addEventListener('change', onPhotoSelected);
        removePhotoBtnEl.addEventListener('click', removePhoto);
    }

    async function onPhotoSelected() {
        const file = photoInputEl.files[0];
        if (!file || !file.type.startsWith('image/')) return;
        const reader = new FileReader();
        reader.onload = function () { previewImgEl.src = reader.result; };
        reader.readAsDataURL(file);
        photoPreviewEl.classList.remove('hidden');
        photoBtnEl.textContent = '📷 Foto ändern';
        try { selectedPhotoBlob = await compressImage(file, 1200, 0.8); }
        catch { selectedPhotoBlob = file; }
    }

    function removePhoto() {
        selectedPhotoBlob = null;
        photoInputEl.value = '';
        photoPreviewEl.classList.add('hidden');
        photoBtnEl.textContent = '📷 Foto hinzufügen (optional)';
        previewImgEl.src = '';
    }

    function compressImage(file, maxDim, quality) {
        return new Promise((resolve, reject) => {
            const img = new Image();
            img.onload = () => {
                let w = img.width, h = img.height;
                if (w > maxDim || h > maxDim) {
                    if (w > h) { h = Math.round(h * maxDim / w); w = maxDim; }
                    else { w = Math.round(w * maxDim / h); h = maxDim; }
                }
                const canvas = document.createElement('canvas');
                canvas.width = w; canvas.height = h;
                canvas.getContext('2d').drawImage(img, 0, 0, w, h);
                canvas.toBlob(blob => blob ? resolve(blob) : reject(new Error('toBlob failed')), 'image/jpeg', quality);
                URL.revokeObjectURL(img.src);
            };
            img.onerror = reject;
            img.src = URL.createObjectURL(file);
        });
    }

    async function loadNames() {
        try {
            userNameEl.classList.add('loading');
            const res = await fetch(`${API_BASE}/user-names`, { headers: API_KEY_HDR });
            if (!res.ok) throw new Error();
            const names = await res.json();
            if (typeof FT_OFFLINE !== 'undefined') FT_OFFLINE.saveDropdownData('field_names', names);
            populateSelect(userNameEl, names);
        } catch {
            if (typeof FT_OFFLINE !== 'undefined') {
                const cached = await FT_OFFLINE.getDropdownData('field_names');
                if (cached) { populateSelect(userNameEl, cached); return; }
            }
            showToast('Namen konnten nicht geladen werden', true);
        } finally { userNameEl.classList.remove('loading'); }
    }

    async function loadLostDogs() {
        try {
            lostDogEl.classList.add('loading');
            const res = await fetch(`${API_BASE}/lost-dogs`, { headers: API_KEY_HDR });
            if (!res.ok) throw new Error();
            const dogs = await res.json();
            if (typeof FT_OFFLINE !== 'undefined') FT_OFFLINE.saveDropdownData('field_lostDogs', dogs);
            populateSelect(lostDogEl, dogs);
        } catch {
            if (typeof FT_OFFLINE !== 'undefined') {
                const cached = await FT_OFFLINE.getDropdownData('field_lostDogs');
                if (cached) { populateSelect(lostDogEl, cached); return; }
            }
            showToast('Hunde konnten nicht geladen werden', true);
        } finally { lostDogEl.classList.remove('loading'); }
    }

    async function loadCategories() {
        try {
            categoryEl.classList.add('loading');
            const res = await fetch(`${API_BASE}/categories`, { headers: API_KEY_HDR });
            if (!res.ok) throw new Error();
            const cats = await res.json();
            const catNames = cats.map(c => c.name || c);
            if (typeof FT_OFFLINE !== 'undefined') FT_OFFLINE.saveDropdownData('field_categories', catNames);
            populateSelect(categoryEl, catNames);
        } catch {
            if (typeof FT_OFFLINE !== 'undefined') {
                const cached = await FT_OFFLINE.getDropdownData('field_categories');
                if (cached) { populateSelect(categoryEl, cached); return; }
            }
            showToast('Kategorien konnten nicht geladen werden', true);
        } finally { categoryEl.classList.remove('loading'); }
    }

    function populateSelect(el, items) {
        items.forEach(item => { const o = document.createElement('option'); o.value = item; o.textContent = item; el.appendChild(o); });
    }

    function restoreSelections() {
        const n = localStorage.getItem(STORAGE_KEY_NAME);
        const l = localStorage.getItem(STORAGE_KEY_LOCATION);
        const c = localStorage.getItem(STORAGE_KEY_CATEGORY);
        if (n) userNameEl.value = n;
        if (l) lostDogEl.value = l;
        if (c) categoryEl.value = c;
    }

    function persistSelections() {
        localStorage.setItem(STORAGE_KEY_NAME, userNameEl.value);
        localStorage.setItem(STORAGE_KEY_LOCATION, lostDogEl.value);
        localStorage.setItem(STORAGE_KEY_CATEGORY, categoryEl.value);
    }

    function onSelectionChange() { persistSelections(); updateButtonState(); }

    function updateButtonState() {
        saveBtnEl.disabled = !(userNameEl.value && lostDogEl.value && categoryEl.value);
        const ok = !!(userNameEl.value && lostDogEl.value);
        editBtnEl.disabled = !ok;
        mapBtnEl.disabled = !ok;
        [userNameEl, lostDogEl, categoryEl].forEach(el => el.classList.toggle('missing', !el.value));
    }

    function onEditRecords() {
        const p = new URLSearchParams();
        p.set('name', userNameEl.value);
        p.set('lostDog', lostDogEl.value);
        window.location.href = 'field-records.html?' + p;
    }

    function onShowMap() {
        const p = new URLSearchParams();
        p.set('name', userNameEl.value);
        p.set('lostDog', lostDogEl.value);
        window.location.href = 'field-map.html?' + p;
    }

    async function onSaveLocation() {
        if (saveBtnEl.disabled) return;
        saveBtnEl.classList.add('saving');
        saveBtnEl.textContent = 'WIRD GESPEICHERT…';

        try {
            const position = await getCurrentPosition();
            const entry = {
                name: userNameEl.value, lostDog: lostDogEl.value,
                category: categoryEl.value, comment: commentEl.value.trim(),
                latitude: position.coords.latitude, longitude: position.coords.longitude,
                accuracy: position.coords.accuracy, timestamp: new Date().toISOString()
            };

            let photoBase64 = null;
            if (selectedPhotoBlob) photoBase64 = await blobToBase64(selectedPhotoBlob);

            if (!navigator.onLine) {
                await FT_OFFLINE.queueEntry({ ...entry, photoBase64 });
                showToast('📶 Offline gespeichert — wird bei Verbindung übertragen');
                removePhoto(); commentEl.value = ''; updateCharCounter(); updateStatusBadge();
                return;
            }

            if (selectedPhotoBlob) {
                const fd = new FormData();
                fd.append('name', entry.name); fd.append('lostDog', entry.lostDog);
                fd.append('category', entry.category); fd.append('comment', entry.comment);
                fd.append('latitude', entry.latitude.toString()); fd.append('longitude', entry.longitude.toString());
                fd.append('accuracy', entry.accuracy.toString()); fd.append('timestamp', entry.timestamp);
                fd.append('photo', selectedPhotoBlob, 'photo.jpg');
                const res = await fetch(`${API_BASE}/save-location`, { method: 'POST', headers: API_KEY_HDR, body: fd });
                if (!res.ok) throw new Error();
            } else {
                const res = await fetch(`${API_BASE}/save-location`, {
                    method: 'POST',
                    headers: { ...API_KEY_HDR, 'Content-Type': 'application/json' },
                    body: JSON.stringify(entry)
                });
                if (!res.ok) throw new Error();
            }

            showToast('Standort gespeichert ✓');
            removePhoto(); commentEl.value = ''; updateCharCounter();
        } catch (err) {
            if (err.code === 1) showToast('GPS-Zugriff verweigert.', true);
            else if (err.code === 2) showToast('Standort nicht verfügbar.', true);
            else if (err.code === 3) showToast('GPS-Zeitüberschreitung.', true);
            else showToast('Fehler beim Speichern.', true);
        } finally {
            saveBtnEl.classList.remove('saving');
            saveBtnEl.textContent = 'STANDORT SPEICHERN';
        }
    }

    function getCurrentPosition() {
        return new Promise((resolve, reject) => {
            if (!navigator.geolocation) { reject(new Error('Geolocation nicht unterstützt')); return; }
            navigator.geolocation.getCurrentPosition(resolve, reject, { enableHighAccuracy: true, timeout: 15000, maximumAge: 0 });
        });
    }

    function showToast(msg, isError = false) {
        clearTimeout(toastTimeout);
        toastEl.textContent = msg;
        toastEl.className = isError ? 'toast error' : 'toast';
        toastTimeout = setTimeout(() => toastEl.classList.add('hidden'), 3000);
    }

    function blobToBase64(blob) {
        return new Promise((resolve, reject) => { const r = new FileReader(); r.onloadend = () => resolve(r.result); r.onerror = reject; r.readAsDataURL(blob); });
    }
    function base64ToBlob(b64) {
        const parts = b64.split(','); const mime = parts[0].match(/:(.*?);/)[1];
        const bytes = atob(parts[1]); const arr = new Uint8Array(bytes.length);
        for (let i = 0; i < bytes.length; i++) arr[i] = bytes.charCodeAt(i);
        return new Blob([arr], { type: mime });
    }

    async function syncPendingEntries() {
        if (typeof FT_OFFLINE === 'undefined') return;
        const pending = await FT_OFFLINE.getPendingEntries();
        if (!pending.length) return;
        let synced = 0;
        for (const e of pending) {
            try {
                if (e.photoBase64) {
                    const fd = new FormData();
                    fd.append('name', e.name); fd.append('lostDog', e.lostDog);
                    fd.append('category', e.category); fd.append('comment', e.comment || '');
                    fd.append('latitude', e.latitude.toString()); fd.append('longitude', e.longitude.toString());
                    fd.append('accuracy', e.accuracy.toString()); fd.append('timestamp', e.timestamp);
                    fd.append('photo', base64ToBlob(e.photoBase64), 'photo.jpg');
                    const r = await fetch(`${API_BASE}/save-location`, { method: 'POST', headers: API_KEY_HDR, body: fd });
                    if (!r.ok) continue;
                } else {
                    const r = await fetch(`${API_BASE}/save-location`, {
                        method: 'POST', headers: { ...API_KEY_HDR, 'Content-Type': 'application/json' },
                        body: JSON.stringify({ name: e.name, lostDog: e.lostDog, category: e.category, comment: e.comment || '', latitude: e.latitude, longitude: e.longitude, accuracy: e.accuracy, timestamp: e.timestamp })
                    });
                    if (!r.ok) continue;
                }
                await FT_OFFLINE.removeEntry(e.id); synced++;
            } catch { break; }
        }
        if (synced > 0) showToast(`✓ ${synced} Offline-Eintr${synced === 1 ? 'ag' : 'äge'} übertragen`);
        updateStatusBadge();
    }

    const BADGE_STYLE = 'position:fixed;top:0.6rem;left:0.75rem;font-size:0.75rem;font-weight:700;padding:0.3rem 0.6rem;border-radius:20px;z-index:5000;box-shadow:0 2px 8px rgba(0,0,0,0.15);';
    function getOrCreateBadge() { let b = document.getElementById('statusBadge'); if (!b) { b = document.createElement('div'); b.id = 'statusBadge'; document.body.appendChild(b); } return b; }
    function removeBadge() { const b = document.getElementById('statusBadge'); if (b) b.remove(); }

    async function updateStatusBadge() {
        const cnt = (typeof FT_OFFLINE !== 'undefined') ? await FT_OFFLINE.pendingCount() : 0;
        if (cnt > 0) { const b = getOrCreateBadge(); b.style.cssText = BADGE_STYLE + 'background:var(--warning);color:#fff;'; b.textContent = `📶 ${cnt} ausstehend`; }
        else if (!navigator.onLine) { const b = getOrCreateBadge(); b.style.cssText = BADGE_STYLE + 'background:var(--danger);color:#fff;'; b.textContent = '⚡ Offline'; }
        else removeBadge();
    }

    window.addEventListener('online', () => { showToast('Verbindung wiederhergestellt'); updateStatusBadge(); syncPendingEntries(); });
    window.addEventListener('offline', () => { showToast('Keine Internetverbindung — Einträge werden lokal gespeichert', true); updateStatusBadge(); });

    document.addEventListener('DOMContentLoaded', () => { init(); updateStatusBadge(); if (navigator.onLine) syncPendingEntries(); });
})();
