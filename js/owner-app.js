(function () {
    'use strict';

    const IS_LOCAL = window.location.hostname === 'localhost' || window.location.hostname === '127.0.0.1';
    const API_BASE = IS_LOCAL ? 'http://localhost:7071/api' : '/api';
    const API_KEY = IS_LOCAL ? 'lostdogtracer-dev-key-2026' : '%%PROD_API_KEY%%';

    // ── Read owner key from URL ──────────────────────────────────
    const urlParams = new URLSearchParams(window.location.search);
    const ownerKey = urlParams.get('key') || '';

    const dogNameEl = document.getElementById('dogName');
    const categoryEl = document.getElementById('category');
    const categoryDisplayEl = document.getElementById('categoryDisplay');
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
    const greetingEl = document.getElementById('ownerGreeting');

    let toastTimeout = null;
    let selectedPhotoBlob = null;
    let resolvedDogName = '';
    let resolvedDogRowKey = '';
    const charCounterEl = document.getElementById('charCounter');

    function updateCharCounter() {
        const len = commentEl.value.length;
        const max = 40;
        charCounterEl.textContent = `${len} / ${max}`;
        charCounterEl.classList.toggle('warn', len >= 30 && len < max);
        charCounterEl.classList.toggle('full', len >= max);
    }

    // ── Initialisation ───────────────────────────────────────────
    async function init() {
        const valid = await resolveDogByOwnerKey();
        if (!valid) {
            if (!ownerKey) setInvalidState('Kein Key in der URL');
            return;
        }

        greetingEl.textContent = `Hallo Besitzer*in von ${resolvedDogName}`;

        await loadFlyerCategory();
        updateButtonState();

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
        commentEl.disabled = true;
        photoBtnEl.disabled = true;
        if (editBtnEl) editBtnEl.disabled = true;
        if (mapBtnEl) mapBtnEl.disabled = true;
        if (detail) showToast(detail, true);
    }

    // ── Resolve dog via owner key ────────────────────────────────
    async function resolveDogByOwnerKey() {
        if (!ownerKey) return false;
        try {
            const url = `${API_BASE}/lost-dogs/by-owner-key/${encodeURIComponent(ownerKey)}`;
            const res = await fetch(url, { headers: { 'X-API-Key': API_KEY } });
            if (!res.ok) {
                let detail = `API ${res.status}`;
                try { const body = await res.json(); detail += ': ' + (body.error || JSON.stringify(body)); } catch {}
                setInvalidState(detail);
                return false;
            }
            const data = await res.json();
            resolvedDogName = data.displayName;
            resolvedDogRowKey = data.rowKey;
            dogNameEl.textContent = resolvedDogName;
            return true;
        } catch (err) {
            setInvalidState('Netzwerkfehler: ' + err.message);
            return false;
        }
    }

    // ── Load "Flyer/Handzettel" category ─────────────────────────
    async function loadFlyerCategory() {
        try {
            const res = await fetch(`${API_BASE}/categories`, { headers: { 'X-API-Key': API_KEY } });
            if (!res.ok) return;
            const cats = await res.json();
            const flyer = cats.find(c => (c.displayName || c.name || '').toLowerCase().includes('flyer'));
            if (flyer) {
                categoryEl.value = flyer.rowKey;
                categoryDisplayEl.textContent = flyer.displayName || flyer.name;
            }
        } catch { /* use empty */ }
    }

    function updateButtonState() {
        saveBtnEl.disabled = !(resolvedDogName && categoryEl.value);
        const hasDog = !!resolvedDogName;
        if (editBtnEl) editBtnEl.disabled = !hasDog;
        if (mapBtnEl) mapBtnEl.disabled = !hasDog;
    }

    function onEditRecords() {
        const params = new URLSearchParams();
        params.set('lostDog', resolvedDogRowKey);
        params.set('key', ownerKey);
        window.location.href = 'owner-records.html?' + params;
    }

    function onShowMap() {
        const params = new URLSearchParams();
        params.set('lostDog', resolvedDogRowKey);
        params.set('key', ownerKey);
        window.location.href = 'owner-map.html?' + params;
    }

    // ── Photo handling ───────────────────────────────────────────
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

    // ── Save GPS location ────────────────────────────────────────
    async function onSaveLocation() {
        if (saveBtnEl.disabled) return;
        saveBtnEl.classList.add('saving');
        saveBtnEl.textContent = 'WIRD GESPEICHERT…';

        try {
            const position = await getCurrentPosition();
            const entry = {
                name: 'OWNER',
                lostDog: resolvedDogRowKey,
                category: categoryEl.value,
                comment: commentEl.value.trim(),
                latitude: position.coords.latitude,
                longitude: position.coords.longitude,
                accuracy: position.coords.accuracy,
                timestamp: new Date().toISOString(),
                ownerKey: ownerKey
            };

            if (selectedPhotoBlob) {
                const fd = new FormData();
                Object.entries(entry).forEach(([k, v]) => fd.append(k, String(v)));
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
            if (err.code === 1) showToast('GPS-Zugriff verweigert. Bitte Standort freigeben.', true);
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
            navigator.geolocation.getCurrentPosition(resolve, reject, {
                enableHighAccuracy: true, timeout: 15000, maximumAge: 0
            });
        });
    }

    function showToast(message, isError) {
        if (toastTimeout) clearTimeout(toastTimeout);
        toastEl.textContent = message;
        toastEl.className = isError ? 'toast error' : 'toast';
        toastTimeout = setTimeout(() => toastEl.classList.add('hidden'), 3000);
    }

    document.addEventListener('DOMContentLoaded', () => init());
})();
