(function () {
    'use strict';

    // In production (SWA), API is at /api. Locally, Azure Functions runs on port 7071.
    const IS_LOCAL = window.location.hostname === 'localhost' || window.location.hostname === '127.0.0.1';
    const API_BASE = IS_LOCAL ? 'http://localhost:7071/api' : '/api';
    const API_KEY = IS_LOCAL ? 'lostdogtracer-dev-key-2026' : '%%PROD_API_KEY%%';
    const STORAGE_KEY_NAME = 'lostdogtracer_userName';
    const STORAGE_KEY_LOCATION = 'lostdogtracer_lostDog';
    const STORAGE_KEY_CATEGORY = 'lostdogtracer_category';

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
    let selectedPhotoBlob = null; // compressed Blob ready for upload
    const charCounterEl = document.getElementById('charCounter');

    // ── Character counter ────────────────────────────────────────────
    function updateCharCounter() {
        const len = commentEl.value.length;
        const max = 40;
        charCounterEl.textContent = `${len} / ${max}`;
        charCounterEl.classList.toggle('warn', len >= 30 && len < max);
        charCounterEl.classList.toggle('full', len >= max);
    }

    // ── Initialisation ───────────────────────────────────────────────
    async function init() {
        await Promise.all([loadNames(), loadLostDogs(), loadCategories()]);
        restoreSelections();
        updateButtonState();

        userNameEl.addEventListener('change', onSelectionChange);
        lostDogEl.addEventListener('change', onSelectionChange);
        categoryEl.addEventListener('change', onSelectionChange);
        saveBtnEl.addEventListener('click', onSaveLocation);
        commentEl.addEventListener('input', updateCharCounter);

        // Edit / Map buttons
        editBtnEl.addEventListener('click', onEditRecords);
        mapBtnEl.addEventListener('click', onShowMap);

        // Photo handling
        photoBtnEl.addEventListener('click', () => photoInputEl.click());
        photoInputEl.addEventListener('change', onPhotoSelected);
        removePhotoBtnEl.addEventListener('click', removePhoto);
    }

    // ── Photo handling ───────────────────────────────────────────────
    async function onPhotoSelected() {
        const file = photoInputEl.files[0];
        if (!file || !file.type.startsWith('image/')) return;

        // Show preview via FileReader (safe data-URL, no taint)
        const reader = new FileReader();
        reader.onload = function () {
            previewImgEl.src = reader.result;
        };
        reader.readAsDataURL(file);
        photoPreviewEl.classList.remove('hidden');
        photoBtnEl.textContent = '📷 Foto ändern';

        // Compress in background
        try {
            selectedPhotoBlob = await compressImage(file, 1200, 0.8);
        } catch {
            // Fallback: use original file
            selectedPhotoBlob = file;
        }
    }

    function removePhoto() {
        selectedPhotoBlob = null;
        photoInputEl.value = '';
        photoPreviewEl.classList.add('hidden');
        photoBtnEl.textContent = '📷 Foto hinzufügen (optional)';
        previewImgEl.src = '';
    }

    /**
     * Compress an image file using canvas.
     * Resizes to maxDim (longest side) and converts to JPEG at given quality.
     * Returns a Blob.
     */
    function compressImage(file, maxDim, quality) {
        return new Promise((resolve, reject) => {
            const img = new Image();
            img.onload = () => {
                let w = img.width;
                let h = img.height;
                if (w > maxDim || h > maxDim) {
                    if (w > h) { h = Math.round(h * maxDim / w); w = maxDim; }
                    else { w = Math.round(w * maxDim / h); h = maxDim; }
                }
                const canvas = document.createElement('canvas');
                canvas.width = w;
                canvas.height = h;
                const ctx = canvas.getContext('2d');
                ctx.drawImage(img, 0, 0, w, h);
                canvas.toBlob(blob => {
                    if (blob) resolve(blob);
                    else reject(new Error('Canvas toBlob failed'));
                }, 'image/jpeg', quality);
                URL.revokeObjectURL(img.src);
            };
            img.onerror = reject;
            img.src = URL.createObjectURL(file);
        });
    }

    // ── Load dropdown data (with offline fallback) ─────────────────
    async function loadNames() {
        try {
            userNameEl.classList.add('loading');
            const res = await fetch(`${API_BASE}/names`, { headers: { 'X-API-Key': API_KEY } });
            if (!res.ok) throw new Error('Fehler beim Laden der Namen');
            const names = await res.json();
            if (typeof FT_OFFLINE !== 'undefined') FT_OFFLINE.saveDropdownData('names', names);
            populateSelect(userNameEl, names);
        } catch (err) {
            console.error(err);
            // Try offline cache
            if (typeof FT_OFFLINE !== 'undefined') {
                const cached = await FT_OFFLINE.getDropdownData('names');
                if (cached) { populateSelect(userNameEl, cached); return; }
            }
            showToast('Namen konnten nicht geladen werden', true);
        } finally {
            userNameEl.classList.remove('loading');
        }
    }

    async function loadLostDogs() {
        try {
            lostDogEl.classList.add('loading');
            const res = await fetch(`${API_BASE}/lost-dogs`, { headers: { 'X-API-Key': API_KEY } });
            if (!res.ok) throw new Error('Fehler beim Laden der Hunde');
            const dogs = await res.json();
            if (typeof FT_OFFLINE !== 'undefined') FT_OFFLINE.saveDropdownData('lostDogs', dogs);
            populateSelect(lostDogEl, dogs);
        } catch (err) {
            console.error(err);
            if (typeof FT_OFFLINE !== 'undefined') {
                const cached = await FT_OFFLINE.getDropdownData('lostDogs');
                if (cached) { populateSelect(lostDogEl, cached); return; }
            }
            showToast('Hunde konnten nicht geladen werden', true);
        } finally {
            lostDogEl.classList.remove('loading');
        }
    }

    async function loadCategories() {
        try {
            categoryEl.classList.add('loading');
            const res = await fetch(`${API_BASE}/categories`, { headers: { 'X-API-Key': API_KEY } });
            if (!res.ok) throw new Error('Fehler beim Laden der Kategorien');
            const cats = await res.json();
            const catNames = cats.map(c => c.name || c);
            if (typeof FT_OFFLINE !== 'undefined') FT_OFFLINE.saveDropdownData('categories', catNames);
            populateSelect(categoryEl, catNames);
        } catch (err) {
            console.error(err);
            if (typeof FT_OFFLINE !== 'undefined') {
                const cached = await FT_OFFLINE.getDropdownData('categories');
                if (cached) { populateSelect(categoryEl, cached); return; }
            }
            showToast('Kategorien konnten nicht geladen werden', true);
        } finally {
            categoryEl.classList.remove('loading');
        }
    }

    function populateSelect(selectEl, items) {
        items.forEach(item => {
            const opt = document.createElement('option');
            opt.value = item;
            opt.textContent = item;
            selectEl.appendChild(opt);
        });
    }

    // ── Selection persistence (localStorage) ─────────────────────────
    function restoreSelections() {
        const savedName = localStorage.getItem(STORAGE_KEY_NAME);
        const savedLoc = localStorage.getItem(STORAGE_KEY_LOCATION);
        const savedCat = localStorage.getItem(STORAGE_KEY_CATEGORY);
        if (savedName) userNameEl.value = savedName;
        if (savedLoc) lostDogEl.value = savedLoc;
        if (savedCat) categoryEl.value = savedCat;
    }

    function persistSelections() {
        localStorage.setItem(STORAGE_KEY_NAME, userNameEl.value);
        localStorage.setItem(STORAGE_KEY_LOCATION, lostDogEl.value);
        localStorage.setItem(STORAGE_KEY_CATEGORY, categoryEl.value);
    }

    function onSelectionChange() {
        persistSelections();
        updateButtonState();
    }

    function updateButtonState() {
        saveBtnEl.disabled = !(userNameEl.value && lostDogEl.value && categoryEl.value);
        const hasNameAndDog = !!(userNameEl.value && lostDogEl.value);
        editBtnEl.disabled = !hasNameAndDog;
        mapBtnEl.disabled = !hasNameAndDog;

        // Highlight missing required fields
        [userNameEl, lostDogEl, categoryEl].forEach(el => {
            el.classList.toggle('missing', !el.value);
        });
    }

    function onEditRecords() {
        const params = new URLSearchParams();
        params.set('name', userNameEl.value);
        params.set('lostDog', lostDogEl.value);
        window.location.href = 'my-records.html?' + params;
    }

    function onShowMap() {
        const params = new URLSearchParams();
        params.set('name', userNameEl.value);
        params.set('lostDog', lostDogEl.value);
        window.location.href = 'my-map.html?' + params;
    }

    // ── Save GPS location (with offline fallback) ──────────────────
    async function onSaveLocation() {
        if (saveBtnEl.disabled) return;

        saveBtnEl.classList.add('saving');
        saveBtnEl.textContent = 'WIRD GESPEICHERT…';

        try {
            const position = await getCurrentPosition();
            const entry = {
                name: userNameEl.value,
                lostDog: lostDogEl.value,
                category: categoryEl.value,
                comment: commentEl.value.trim(),
                latitude: position.coords.latitude,
                longitude: position.coords.longitude,
                accuracy: position.coords.accuracy,
                timestamp: new Date().toISOString()
            };

            // Convert photo blob to base64 for IndexedDB storage if needed
            let photoBase64 = null;
            if (selectedPhotoBlob) {
                photoBase64 = await blobToBase64(selectedPhotoBlob);
            }

            if (!navigator.onLine) {
                // ── Offline: queue for later ──
                await FT_OFFLINE.queueEntry({ ...entry, photoBase64 });
                showToast('📶 Offline gespeichert — wird bei Verbindung übertragen');
                removePhoto();
                commentEl.value = '';
                updateCharCounter();
                updateStatusBadge();
                return;
            }

            // ── Online: send immediately ──
            if (selectedPhotoBlob) {
                const fd = new FormData();
                fd.append('name', entry.name);
                fd.append('lostDog', entry.lostDog);
                fd.append('category', entry.category);
                fd.append('comment', entry.comment);
                fd.append('latitude', entry.latitude.toString());
                fd.append('longitude', entry.longitude.toString());
                fd.append('accuracy', entry.accuracy.toString());
                fd.append('timestamp', entry.timestamp);
                fd.append('photo', selectedPhotoBlob, 'photo.jpg');

                const res = await fetch(`${API_BASE}/save-location`, {
                    method: 'POST',
                    headers: { 'X-API-Key': API_KEY },
                    body: fd
                });
                if (!res.ok) throw new Error('Speichern fehlgeschlagen');
            } else {
                const res = await fetch(`${API_BASE}/save-location`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json', 'X-API-Key': API_KEY },
                    body: JSON.stringify(entry)
                });
                if (!res.ok) throw new Error('Speichern fehlgeschlagen');
            }

            showToast('Standort gespeichert ✓');
            removePhoto();
            commentEl.value = '';
            updateCharCounter();
        } catch (err) {
            console.error(err);
            if (err.code === 1) {
                showToast('GPS-Zugriff verweigert. Bitte Standort freigeben.', true);
            } else if (err.code === 2) {
                showToast('Standort nicht verfügbar.', true);
            } else if (err.code === 3) {
                showToast('GPS-Zeitüberschreitung.', true);
            } else {
                showToast('Fehler beim Speichern.', true);
            }
        } finally {
            saveBtnEl.classList.remove('saving');
            saveBtnEl.textContent = 'STANDORT SPEICHERN';
        }
    }

    function getCurrentPosition() {
        return new Promise((resolve, reject) => {
            if (!navigator.geolocation) {
                reject(new Error('Geolocation wird nicht unterstützt'));
                return;
            }
            navigator.geolocation.getCurrentPosition(resolve, reject, {
                enableHighAccuracy: true,
                timeout: 15000,
                maximumAge: 0
            });
        });
    }

    // ── Toast ────────────────────────────────────────────────────────
    function showToast(message, isError = false) {
        if (toastTimeout) clearTimeout(toastTimeout);
        toastEl.textContent = message;
        toastEl.className = isError ? 'toast error' : 'toast';
        toastTimeout = setTimeout(() => {
            toastEl.classList.add('hidden');
        }, 3000);
    }

    // ── Blob to Base64 (for IndexedDB photo storage) ─────────────
    function blobToBase64(blob) {
        return new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onloadend = () => resolve(reader.result);
            reader.onerror = reject;
            reader.readAsDataURL(blob);
        });
    }

    function base64ToBlob(base64) {
        const parts = base64.split(',');
        const mime = parts[0].match(/:(.*?);/)[1];
        const bytes = atob(parts[1]);
        const arr = new Uint8Array(bytes.length);
        for (let i = 0; i < bytes.length; i++) arr[i] = bytes.charCodeAt(i);
        return new Blob([arr], { type: mime });
    }

    // ── Offline Queue Sync ───────────────────────────────────────
    async function syncPendingEntries() {
        if (typeof FT_OFFLINE === 'undefined') return;
        const pending = await FT_OFFLINE.getPendingEntries();
        if (pending.length === 0) return;

        let synced = 0;
        for (const entry of pending) {
            try {
                if (entry.photoBase64) {
                    const blob = base64ToBlob(entry.photoBase64);
                    const fd = new FormData();
                    fd.append('name', entry.name);
                    fd.append('lostDog', entry.lostDog);
                    fd.append('category', entry.category);
                    fd.append('comment', entry.comment || '');
                    fd.append('latitude', entry.latitude.toString());
                    fd.append('longitude', entry.longitude.toString());
                    fd.append('accuracy', entry.accuracy.toString());
                    fd.append('timestamp', entry.timestamp);
                    fd.append('photo', blob, 'photo.jpg');

                    const res = await fetch(`${API_BASE}/save-location`, {
                        method: 'POST',
                        headers: { 'X-API-Key': API_KEY },
                        body: fd
                    });
                    if (!res.ok) continue;
                } else {
                    const res = await fetch(`${API_BASE}/save-location`, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json', 'X-API-Key': API_KEY },
                        body: JSON.stringify({
                            name: entry.name,
                            lostDog: entry.lostDog,
                            category: entry.category,
                            comment: entry.comment || '',
                            latitude: entry.latitude,
                            longitude: entry.longitude,
                            accuracy: entry.accuracy,
                            timestamp: entry.timestamp
                        })
                    });
                    if (!res.ok) continue;
                }
                await FT_OFFLINE.removeEntry(entry.id);
                synced++;
            } catch { /* Network still down, stop trying */ break; }
        }
        if (synced > 0) {
            showToast(`✓ ${synced} Offline-Eintr${synced === 1 ? 'ag' : 'äge'} übertragen`);
        }
        updateStatusBadge();
    }

    // ── Status badge (Offline / Pending) ────────────────────────
    const BADGE_STYLE = 'position:fixed;top:0.6rem;left:0.75rem;font-size:0.75rem;font-weight:700;padding:0.3rem 0.6rem;border-radius:20px;z-index:5000;box-shadow:0 2px 8px rgba(0,0,0,0.15);';

    function getOrCreateBadge() {
        let badge = document.getElementById('statusBadge');
        if (!badge) {
            badge = document.createElement('div');
            badge.id = 'statusBadge';
            document.body.appendChild(badge);
        }
        return badge;
    }

    function removeBadge() {
        const badge = document.getElementById('statusBadge');
        if (badge) badge.remove();
    }

    async function updateStatusBadge() {
        const pendingCount = (typeof FT_OFFLINE !== 'undefined') ? await FT_OFFLINE.pendingCount() : 0;

        if (pendingCount > 0) {
            // Show pending count
            const badge = getOrCreateBadge();
            badge.style.cssText = BADGE_STYLE + 'background:var(--warning);color:#fff;';
            badge.textContent = `📶 ${pendingCount} ausstehend`;
        } else if (!navigator.onLine) {
            // Show offline indicator
            const badge = getOrCreateBadge();
            badge.style.cssText = BADGE_STYLE + 'background:var(--danger);color:#fff;';
            badge.textContent = '⚡ Offline';
        } else {
            removeBadge();
        }
    }

    // ── Online / Offline events ──────────────────────────────────
    window.addEventListener('online', () => {
        showToast('Verbindung wiederhergestellt');
        updateStatusBadge();
        syncPendingEntries();
    });
    window.addEventListener('offline', () => {
        showToast('Keine Internetverbindung — Einträge werden lokal gespeichert', true);
        updateStatusBadge();
    });

    // ── Start ────────────────────────────────────────────────────────
    document.addEventListener('DOMContentLoaded', () => {
        init();
        updateStatusBadge();
        if (navigator.onLine) syncPendingEntries();
    });
})();
