(function () {
    'use strict';

    // In production (SWA), API is at /api. Locally, Azure Functions runs on port 7071.
    const IS_LOCAL = window.location.hostname === 'localhost' || window.location.hostname === '127.0.0.1';
    const API_BASE = IS_LOCAL ? 'http://localhost:7071/api' : '/api';
    const API_KEY = IS_LOCAL ? 'flyertracker-dev-key-2026' : 'ft-prod-key-8bffad18b4db499c';
    const STORAGE_KEY_NAME = 'flyertracker_userName';
    const STORAGE_KEY_LOCATION = 'flyertracker_lostDog';
    const STORAGE_KEY_CATEGORY = 'flyertracker_category';

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

    // ── Initialisation ───────────────────────────────────────────────
    async function init() {
        await Promise.all([loadNames(), loadLostDogs(), loadCategories()]);
        restoreSelections();
        updateButtonState();

        userNameEl.addEventListener('change', onSelectionChange);
        lostDogEl.addEventListener('change', onSelectionChange);
        categoryEl.addEventListener('change', onSelectionChange);
        saveBtnEl.addEventListener('click', onSaveLocation);

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
        if (!file) return;

        // Show preview immediately with original
        previewImgEl.src = URL.createObjectURL(file);
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
        if (previewImgEl.src.startsWith('blob:')) {
            URL.revokeObjectURL(previewImgEl.src);
        }
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

    // ── Load dropdown data ───────────────────────────────────────────
    async function loadNames() {
        try {
            userNameEl.classList.add('loading');
            const res = await fetch(`${API_BASE}/names`, { headers: { 'X-API-Key': API_KEY } });
            if (!res.ok) throw new Error('Fehler beim Laden der Namen');
            const names = await res.json();
            names.forEach(n => {
                const opt = document.createElement('option');
                opt.value = n;
                opt.textContent = n;
                userNameEl.appendChild(opt);
            });
        } catch (err) {
            console.error(err);
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
            dogs.forEach(d => {
                const opt = document.createElement('option');
                opt.value = d;
                opt.textContent = d;
                lostDogEl.appendChild(opt);
            });
        } catch (err) {
            console.error(err);
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
            cats.forEach(c => {
                const opt = document.createElement('option');
                opt.value = c;
                opt.textContent = c;
                categoryEl.appendChild(opt);
            });
        } catch (err) {
            console.error(err);
            showToast('Kategorien konnten nicht geladen werden', true);
        } finally {
            categoryEl.classList.remove('loading');
        }
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

    // ── Save GPS location ────────────────────────────────────────────
    async function onSaveLocation() {
        if (saveBtnEl.disabled) return;

        saveBtnEl.classList.add('saving');
        saveBtnEl.textContent = 'WIRD GESPEICHERT…';

        try {
            const position = await getCurrentPosition();

            if (selectedPhotoBlob) {
                // ── Multipart upload (with photo) ──
                const fd = new FormData();
                fd.append('name', userNameEl.value);
                fd.append('lostDog', lostDogEl.value);
                fd.append('category', categoryEl.value);
                fd.append('comment', commentEl.value.trim());
                fd.append('latitude', position.coords.latitude.toString());
                fd.append('longitude', position.coords.longitude.toString());
                fd.append('accuracy', position.coords.accuracy.toString());
                fd.append('timestamp', new Date().toISOString());
                fd.append('photo', selectedPhotoBlob, 'photo.jpg');

                const res = await fetch(`${API_BASE}/save-location`, {
                    method: 'POST',
                    headers: { 'X-API-Key': API_KEY },
                    body: fd
                });
                if (!res.ok) throw new Error('Speichern fehlgeschlagen');
            } else {
                // ── JSON upload (no photo, backward compatible) ──
                const payload = {
                    name: userNameEl.value,
                    lostDog: lostDogEl.value,
                    category: categoryEl.value,
                    comment: commentEl.value.trim(),
                    latitude: position.coords.latitude,
                    longitude: position.coords.longitude,
                    accuracy: position.coords.accuracy,
                    timestamp: new Date().toISOString()
                };

                const res = await fetch(`${API_BASE}/save-location`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json', 'X-API-Key': API_KEY },
                    body: JSON.stringify(payload)
                });
                if (!res.ok) throw new Error('Speichern fehlgeschlagen');
            }

            showToast('Standort gespeichert ✓');
            removePhoto(); // reset photo after successful save
            commentEl.value = ''; // reset comment after successful save
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

    // ── Start ────────────────────────────────────────────────────────
    document.addEventListener('DOMContentLoaded', init);
})();
