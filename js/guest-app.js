(function () {
    'use strict';

    const IS_LOCAL = window.location.hostname === 'localhost' || window.location.hostname === '127.0.0.1';
    const API_BASE = IS_LOCAL ? 'http://localhost:7071/api' : '/api';
    const API_KEY = IS_LOCAL ? 'lostdogtracer-dev-key-2026' : '%%PROD_API_KEY%%';
    const STORAGE_KEY_CATEGORY = 'lostdogtracer_guest_category';
    const STORAGE_KEY_UUID = 'lostdogtracer_guest_uuid';
    const STORAGE_KEY_TOKEN = 'lostdogtracer_guest_token';
    const STORAGE_KEY_NICK = 'lostdogtracer_guest_nick';

    // ── Read key + token from URL ────────────────────────────────
    const urlParams = new URLSearchParams(window.location.search);
    const guestKey = urlParams.get('key') || '';
    const urlToken = urlParams.get('token') || '';

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
    let resolvedDogRowKey = '';
    let guestToken = '';
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

        // ── Guest token handling ─────────────────────────────────
        await ensureGuestToken();
        updateGreeting();

        await loadGuestCategory();
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
        categoryEl.disabled = true;
        commentEl.disabled = true;
        photoBtnEl.disabled = true;
        if (editBtnEl) editBtnEl.disabled = true;
        if (mapBtnEl) mapBtnEl.disabled = true;
        if (detail) showToast(detail, true);
    }

    // ── Guest token: UUID + registration ─────────────────────────
    function getOrCreateUuid() {
        let uuid = localStorage.getItem(STORAGE_KEY_UUID);
        if (!uuid) {
            uuid = crypto.randomUUID ? crypto.randomUUID() : ([1e7]+-1e3+-4e3+-8e3+-1e11).replace(/[018]/g, c =>
                (c ^ crypto.getRandomValues(new Uint8Array(1))[0] & 15 >> c / 4).toString(16)
            );
            localStorage.setItem(STORAGE_KEY_UUID, uuid);
        }
        return uuid;
    }

    async function ensureGuestToken() {
        // 1. Token from URL → store and use
        if (urlToken) {
            localStorage.setItem(STORAGE_KEY_TOKEN, urlToken);
            guestToken = urlToken;
            return;
        }

        // 2. Token already in localStorage
        const storedToken = localStorage.getItem(STORAGE_KEY_TOKEN);
        if (storedToken) {
            guestToken = storedToken;
            // Ensure URL contains token for bookmarking
            if (!urlToken) history.replaceState(null, '', buildPersonalLink());
            return;
        }

        // 3. No token yet — register immediately, then show link dialog
        const uuid = getOrCreateUuid();
        await registerGuest(uuid);
    }

    async function registerGuest(uuid) {
        try {
            const res = await fetch(`${API_BASE}/guest/register`, {
                method: 'POST',
                headers: { 'X-API-Key': API_KEY, 'Content-Type': 'application/json' },
                body: JSON.stringify({ uuid, dogKey: guestKey })
            });
            if (res.ok) {
                const data = await res.json();
                guestToken = data.token;
                localStorage.setItem(STORAGE_KEY_TOKEN, guestToken);
                history.replaceState(null, '', buildPersonalLink());
                if (!data.existing) showPersonalLinkDialog(uuid);
            }
        } catch {
            // Offline or error — continue without token
        }
    }

    function buildPersonalLink() {
        const base = location.origin + location.pathname;
        const params = new URLSearchParams();
        params.set('key', guestKey);
        params.set('token', guestToken);
        return base + '?' + params;
    }

    function showPersonalLinkDialog(uuid) {
        const link = buildPersonalLink();
        const overlay = document.createElement('div');
        overlay.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,.35);z-index:100;display:flex;align-items:center;justify-content:center;';
        overlay.innerHTML = `
            <div style="background:#fff;border-radius:14px;padding:2rem;max-width:440px;width:90%;box-shadow:0 8px 32px rgba(0,0,0,.2);max-height:90vh;overflow-y:auto;">
                <h3 style="margin:0 0 1rem;font-size:1.125rem;">Dein persönlicher Link</h3>
                <p style="font-size:0.8125rem;font-weight:600;color:#1d1d1f;margin:0 0 0.25rem;">Warum bekomme ich einen persönlichen Link?</p>
                <p style="font-size:0.8125rem;color:#6e6e73;margin:0 0 0.75rem;">Du möchtest uns helfen Flyer aufzuhängen? Das ist super. Diese App hilft dir dabei, die Flyer später zu finden um sie wieder zu entfernen. Dafür ist es wichtig, dass dir die Standorte angezeigt werden, die du selbst geflyert hast.</p>
                <p style="font-size:0.8125rem;font-weight:600;color:#1d1d1f;margin:0 0 0.25rem;">Werden von mir persönliche Daten gespeichert?</p>
                <p style="font-size:0.8125rem;color:#6e6e73;margin:0 0 0.75rem;">Nein. Der Link enthält einen zufällig generierten Schlüssel, der nur mit dem Standort des Flyers verknüpft ist. Du musst – und sollst – keine persönlichen Daten in der App eingeben. Dein „Spitzname" kann frei erfunden sein.</p>
                <p style="font-size:0.8125rem;font-weight:600;color:#1d1d1f;margin:0 0 0.25rem;">Was mache ich mit dem Link?</p>
                <p style="font-size:0.8125rem;color:#6e6e73;margin:0 0 1rem;">Teile ihn dir als Mail oder WhatsApp, oder speichere ihn als Favorit. Gib ihn bitte nicht an andere weiter.</p>
                <input type="text" id="guestNickname" placeholder="Spitzname (optional)" autocomplete="off" maxlength="30" style="width:100%;padding:0.75rem 1rem;font-size:1rem;border:1px solid #d2d2d7;border-radius:10px;outline:none;margin-bottom:0.75rem;">
                <input type="text" readonly value="${link.replace(/"/g, '&quot;')}" id="guestLinkInput" style="width:100%;padding:0.75rem 1rem;font-size:0.8125rem;border:1px solid #d2d2d7;border-radius:10px;outline:none;margin-bottom:0.75rem;background:#f5f5f7;">
                <div style="display:flex;gap:0.5rem;justify-content:flex-end;flex-wrap:wrap;">
                    ${navigator.share ? '<button class="btn btn-secondary btn-sm" id="guestLinkShare" style="padding:0.5rem 1rem;">Link teilen</button>' : ''}
                    <button class="btn btn-secondary btn-sm" id="guestLinkCopy" style="padding:0.5rem 1rem;">Link kopieren</button>
                    <button class="btn btn-primary btn-sm" id="guestLinkClose" style="padding:0.5rem 1rem;">Weiter</button>
                </div>
            </div>`;
        document.body.appendChild(overlay);

        if (navigator.share) {
            document.getElementById('guestLinkShare').addEventListener('click', () => {
                navigator.share({
                    title: 'LostDogTracer – Mein Link',
                    text: 'Mein persönlicher Link für die Hundesuche:',
                    url: link
                }).catch(() => {});
            });
        }

        document.getElementById('guestLinkCopy').addEventListener('click', () => {
            const inp = document.getElementById('guestLinkInput');
            inp.select();
            navigator.clipboard.writeText(inp.value).then(() => {
                document.getElementById('guestLinkCopy').textContent = '✓ Kopiert';
            }).catch(() => {
                document.execCommand('copy');
                document.getElementById('guestLinkCopy').textContent = '✓ Kopiert';
            });
        });
        document.getElementById('guestLinkClose').addEventListener('click', async () => {
            const nickname = document.getElementById('guestNickname').value.trim();
            if (nickname && uuid) {
                localStorage.setItem(STORAGE_KEY_NICK, nickname);
                try {
                    await fetch(`${API_BASE}/guest/nickname`, {
                        method: 'PUT',
                        headers: { 'X-API-Key': API_KEY, 'Content-Type': 'application/json' },
                        body: JSON.stringify({ uuid, nickName: nickname })
                    });
                } catch { /* best effort */ }
            }
            overlay.remove();
            updateGreeting();
        });
    }

    async function updateGreeting() {
        const el = document.getElementById('guestGreeting');
        if (!el) return;
        let nick = localStorage.getItem(STORAGE_KEY_NICK);
        // If no local nickname but we have a token, fetch from backend
        if (!nick && guestToken) {
            try {
                const res = await fetch(`${API_BASE}/guest/nickname?token=${encodeURIComponent(guestToken)}`, {
                    headers: { 'X-API-Key': API_KEY }
                });
                if (res.ok) {
                    const data = await res.json();
                    if (data.nickName) {
                        nick = data.nickName;
                        localStorage.setItem(STORAGE_KEY_NICK, nick);
                    }
                }
            } catch { /* continue without nickname */ }
        }
        el.textContent = nick ? `Hallo, ${nick}!` : 'Hallo, Gast-Helfer*in!';
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
            resolvedDogName = data.displayName;
            resolvedDogRowKey = data.rowKey;
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
        if (!file || !file.type.startsWith('image/')) return;

        // Show preview via FileReader (safe data-URL, no taint)
        const reader = new FileReader();
        reader.onload = function () {
            previewImgEl.src = reader.result;
        };
        reader.readAsDataURL(file);
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

    // ── Load guest category from config ────────────────────────────
    const categoryDisplayEl = document.getElementById('categoryDisplay');

    async function loadGuestCategory() {
        try {
            // Wait for config from theme.js or fetch directly
            let cfg = window.FT_CONFIG;
            if (!cfg) {
                const res = await fetch(`${API_BASE}/config`, { headers: { 'X-API-Key': API_KEY } });
                if (res.ok) cfg = await res.json();
            }
            const catRowKey = cfg?.guestCategoryRowKey || '';
            if (!catRowKey) return;

            categoryEl.value = catRowKey;

            // Resolve displayName from categories API
            const catRes = await fetch(`${API_BASE}/categories`, { headers: { 'X-API-Key': API_KEY } });
            if (catRes.ok) {
                const cats = await catRes.json();
                const match = cats.find(c => c.rowKey === catRowKey);
                if (match) categoryDisplayEl.textContent = match.displayName;
                else categoryDisplayEl.textContent = catRowKey;
            }
        } catch {
            categoryDisplayEl.textContent = '(Fehler)';
        }
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
        if (guestKey) params.set('key', guestKey);
        if (guestToken) params.set('token', guestToken);
        window.location.href = 'guest-records.html?' + params;
    }

    function onShowMap() {
        const params = new URLSearchParams();
        params.set('lostDog', resolvedDogRowKey);
        if (guestKey) params.set('key', guestKey);
        if (guestToken) params.set('token', guestToken);
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
                name: 'GUEST',
                lostDog: resolvedDogRowKey,
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
                if (guestToken) fd.append('guestToken', guestToken);
                fd.append('photo', selectedPhotoBlob, 'photo.jpg');

                const res = await fetch(`${API_BASE}/save-location`, {
                    method: 'POST',
                    headers: { 'X-API-Key': API_KEY },
                    body: fd
                });
                if (!res.ok) throw new Error('Speichern fehlgeschlagen');
            } else {
                const payload = { ...entry };
                if (guestToken) payload.guestToken = guestToken;
                const res = await fetch(`${API_BASE}/save-location`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json', 'X-API-Key': API_KEY },
                    body: JSON.stringify(payload)
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
