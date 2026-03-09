(function () {
    'use strict';

    const IS_LOCAL = window.location.hostname === 'localhost' || window.location.hostname === '127.0.0.1';
    const API_BASE = IS_LOCAL ? 'http://localhost:7071/api' : '/api';
    const API_KEY = IS_LOCAL ? 'lostdogtracer-dev-key-2026' : '%%PROD_API_KEY%%';
    const STORAGE_KEY_CATEGORY = 'lostdogtracer_guest_category';

    // ── Read key from URL ────────────────────────────────────────
    const urlParams = new URLSearchParams(window.location.search);
    const guestKey = urlParams.get('key') || '';

    const dogNameEl = document.getElementById('dogName');
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
    let resolvedDogName = '';
    const charCounterEl = document.getElementById('charCounter');

    // ── Character counter ────────────────────────────────────────
    function updateCharCounter() {
        const len = commentEl.value.length;
        const max = 40;
        charCounterEl.textContent = `${len} / ${max}`;
        charCounterEl.classList.toggle('warn', len >= 30 && len < max);
        charCounterEl.classList.toggle('full', len >= max);
    }

    // ── Initialisation ───────────────────────────────────────────
    async function init() {
        const valid = await resolveDogByKey();
        if (!valid) {
            if (!guestKey) setInvalidState('Kein Key in der URL');
            return;
        }

        await loadCategories();
        restoreCategory();
        updateButtonState();

        categoryEl.addEventListener('change', () => {
            localStorage.setItem(STORAGE_KEY_CATEGORY, categoryEl.value);
            updateButtonState();
        });
        saveBtnEl.addEventListener('click', onSaveLocation);
        commentEl.addEventListener('input', updateCharCounter);

        photoBtnEl.addEventListener('click', () => photoInputEl.click());
        photoInputEl.addEventListener('change', onPhotoSelected);
        removePhotoBtnEl.addEventListener('click', removePhoto);

        editBtnEl.addEventListener('click', onEditRecords);
        mapBtnEl.addEventListener('click', onShowMap);
    }

    function setInvalidState(detail) {
        dogNameEl.textContent = 'Unbekannter Hund';
        dogNameEl.style.borderColor = '#ff3b30';
        dogNameEl.style.color = '#ff3b30';
        dogNameEl.style.boxShadow = '0 0 0 3px rgba(255,59,48,.12)';
        saveBtnEl.disabled = true;
        categoryEl.disabled = true;
        commentEl.disabled = true;
        photoBtnEl.disabled = true;
        if (editBtnEl) editBtnEl.disabled = true;
        if (mapBtnEl) mapBtnEl.disabled = true;
        if (detail) showToast(detail, true);
    }

    // ── Resolve dog name via key ─────────────────────────────────
    async function resolveDogByKey() {
        if (!guestKey) return false;
        try {
            const url = `${API_BASE}/lost-dogs/by-key/${encodeURIComponent(guestKey)}`;
            const res = await fetch(url, {
                headers: { 'X-API-Key': API_KEY }
            });
            if (!res.ok) {
                let detail = `API ${res.status}`;
                try { const body = await res.json(); detail += ': ' + (body.error || JSON.stringify(body)); } catch {}
                console.error('resolveDogByKey failed:', detail, 'URL:', url);
                setInvalidState(detail);
                return false;
            }
            const data = await res.json();
            resolvedDogName = data.location;
            dogNameEl.textContent = resolvedDogName;
            return true;
        } catch (err) {
            console.error('resolveDogByKey exception:', err);
            setInvalidState('Netzwerkfehler: ' + err.message);
            return false;
        }
    }

    // ── Photo handling ───────────────────────────────────────────
    async function onPhotoSelected() {
        const file = photoInputEl.files[0];
        if (!file) return;

        previewImgEl.src = URL.createObjectURL(file);
        photoPreviewEl.classList.remove('hidden');
        photoBtnEl.textContent = '📷 Foto ändern';

        try {
            selectedPhotoBlob = await compressImage(file, 1200, 0.8);
        } catch {
            selectedPhotoBlob = file;
        }
    }

    function removePhoto() {
        selectedPhotoBlob = null;
        photoInputEl.value = '';
        photoPreviewEl.classList.add('hidden');
        photoBtnEl.textContent = '📷 Foto hinzufügen (optional)';
        if (previewImgEl.src.startsWith('blob:')) {
            URL.revokeObjectURL(previewImgEl.src);
        }
        previewImgEl.src = '';
    }

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

    // ── Load categories ──────────────────────────────────────────
    async function loadCategories() {
        try {
            categoryEl.classList.add('loading');
            const res = await fetch(`${API_BASE}/categories`, { headers: { 'X-API-Key': API_KEY } });
            if (!res.ok) throw new Error('Fehler beim Laden der Kategorien');
            const cats = await res.json();
            const catNames = cats.map(c => c.name || c);
            catNames.forEach(name => {
                const opt = document.createElement('option');
                opt.value = name;
                opt.textContent = name;
                categoryEl.appendChild(opt);
            });
        } catch (err) {
            console.error(err);
            showToast('Kategorien konnten nicht geladen werden', true);
        } finally {
            categoryEl.classList.remove('loading');
        }
    }

    function restoreCategory() {
        const saved = localStorage.getItem(STORAGE_KEY_CATEGORY);
        if (saved) categoryEl.value = saved;
    }

    function updateButtonState() {
        saveBtnEl.disabled = !(resolvedDogName && categoryEl.value);
        const hasDog = !!resolvedDogName;
        if (editBtnEl) editBtnEl.disabled = !hasDog;
        if (mapBtnEl) mapBtnEl.disabled = !hasDog;
    }

    function onEditRecords() {
        const params = new URLSearchParams();
        params.set('name', 'HALTER*IN');
        params.set('lostDog', resolvedDogName);
        if (guestKey) params.set('key', guestKey);
        window.location.href = 'guest-records.html?' + params;
    }

    function onShowMap() {
        const params = new URLSearchParams();
        params.set('name', 'HALTER*IN');
        params.set('lostDog', resolvedDogName);
        if (guestKey) params.set('key', guestKey);
        window.location.href = 'guest-map.html?' + params;
    }

    // ── Save GPS location ────────────────────────────────────────
    async function onSaveLocation() {
        if (saveBtnEl.disabled) return;

        saveBtnEl.classList.add('saving');
        saveBtnEl.textContent = 'WIRD GESPEICHERT…';

        try {
            const position = await getCurrentPosition();
            const entry = {
                name: 'HALTER*IN',
                lostDog: resolvedDogName,
                category: categoryEl.value,
                comment: commentEl.value.trim(),
                latitude: position.coords.latitude,
                longitude: position.coords.longitude,
                accuracy: position.coords.accuracy,
                timestamp: new Date().toISOString()
            };

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

    // ── Toast ────────────────────────────────────────────────────
    function showToast(message, isError = false) {
        if (toastTimeout) clearTimeout(toastTimeout);
        toastEl.textContent = message;
        toastEl.className = isError ? 'toast error' : 'toast';
        toastTimeout = setTimeout(() => {
            toastEl.classList.add('hidden');
        }, 3000);
    }

    // ── Start ────────────────────────────────────────────────────
    document.addEventListener('DOMContentLoaded', () => {
        init();
    });
})();
