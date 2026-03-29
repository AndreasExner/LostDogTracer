(function () {
    'use strict';

    const API_BASE = FT_AUTH.getApiBase();

    const dogSelectEl = document.getElementById('dogSelect');
    const activitySelectEl = document.getElementById('activitySelect');
    const activeInfoEl = document.getElementById('activeInfo');
    const activeStartEl = document.getElementById('activeStart');
    const activeDogEl = document.getElementById('activeDog');
    const kmStartRow = document.getElementById('kmStartRow');
    const kmStartCheck = document.getElementById('kmStartCheck');
    const kmStartInput = document.getElementById('kmStartInput');
    const kmEndRow = document.getElementById('kmEndRow');
    const kmEndCheck = document.getElementById('kmEndCheck');
    const kmEndInput = document.getElementById('kmEndInput');
    const deployBtn = document.getElementById('deployBtn');
    const manualDogEl = document.getElementById('manualDog');
    const manualStartEl = document.getElementById('manualStart');
    const manualEndEl = document.getElementById('manualEnd');
    const manualKmDrivenEl = document.getElementById('manualKmDriven');
    const manualActivityEl = document.getElementById('manualActivity');
    const manualSaveBtn = document.getElementById('manualSaveBtn');
    const toastEl = document.getElementById('toast');
    let toastTimeout = null;

    let isActive = false;
    const STORAGE_KEY_DOG = 'lostdogtracer_deploy_dog';

    // ── Load dogs ────────────────────────────────────────────────
    async function loadDogs() {
        try {
            const res = await fetch(`${API_BASE}/lost-dogs`, { headers: FT_AUTH.publicHeaders() });
            if (!res.ok) throw new Error();
            const dogs = await res.json();
            [dogSelectEl, manualDogEl].forEach(sel => {
                while (sel.options.length > 1) sel.remove(1);
                dogs.forEach(d => {
                    const opt = document.createElement('option');
                    opt.value = d.rowKey || d;
                    opt.textContent = d.displayName || d;
                    sel.appendChild(opt);
                });
            });
            // Restore saved dog
            const saved = localStorage.getItem(STORAGE_KEY_DOG);
            if (saved) dogSelectEl.value = saved;
        } catch {
            showToast('Hundeliste konnte nicht geladen werden', true);
        }
    }

    // ── Check deployment status ──────────────────────────────────
    async function checkStatus() {
        try {
            const res = await fetch(`${API_BASE}/deployments/status`, { headers: FT_AUTH.adminHeaders() });
            if (!res.ok) throw new Error();
            const status = await res.json();

            if (status.active) {
                isActive = true;
                dogSelectEl.value = status.dog;
                dogSelectEl.disabled = true;
                if (status.activity) activitySelectEl.value = status.activity;
                activitySelectEl.disabled = true;
                activeInfoEl.classList.remove('hidden');
                activeStartEl.textContent = formatDateTime(status.startTime);
                activeDogEl.textContent = ' — ' + (dogSelectEl.selectedOptions[0]?.textContent || status.dog);

                // Show km end row only if km start was provided, pre-fill with km start
                kmStartRow.classList.add('hidden');
                if (status.kmStart != null) {
                    kmEndRow.classList.remove('hidden');
                    kmEndInput.value = status.kmStart;
                    kmEndCheck.checked = true;
                    kmEndInput.classList.remove('hidden');
                } else {
                    kmEndRow.classList.add('hidden');
                }

                deployBtn.textContent = '🛑 Einsatz beenden';
                deployBtn.className = 'btn-deploy stop';
                deployBtn.disabled = false;
            } else {
                isActive = false;
                dogSelectEl.disabled = false;
                activitySelectEl.disabled = false;
                activeInfoEl.classList.add('hidden');
                kmStartRow.classList.remove('hidden');
                kmEndRow.classList.add('hidden');
                deployBtn.textContent = '🚀 Einsatz starten';
                deployBtn.className = 'btn-deploy start';
                updateStartBtn();
            }
        } catch {
            showToast('Status konnte nicht geladen werden', true);
        }
    }

    function updateStartBtn() {
        deployBtn.disabled = !dogSelectEl.value;
    }

    // ── Start deployment ─────────────────────────────────────────
    async function startDeployment() {
        const dog = dogSelectEl.value;
        if (!dog) return;

        localStorage.setItem(STORAGE_KEY_DOG, dog);
        deployBtn.disabled = true;
        deployBtn.textContent = '⏳ Wird gestartet…';

        const payload = { dog };
        if (activitySelectEl.value) payload.activity = activitySelectEl.value;
        if (kmStartCheck.checked && kmStartInput.value)
            payload.kmStart = parseInt(kmStartInput.value, 10);

        try {
            const res = await fetch(`${API_BASE}/deployments/start`, {
                method: 'POST',
                headers: FT_AUTH.adminHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify(payload)
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(err.error || 'Fehler');
            }
            showToast('Einsatz gestartet');
            await checkStatus();
        } catch (err) {
            showToast(err.message || 'Fehler beim Starten', true);
            deployBtn.disabled = false;
            deployBtn.textContent = '🚀 Einsatz starten';
        }
    }

    // ── End deployment ───────────────────────────────────────────
    async function endDeployment() {
        deployBtn.disabled = true;
        deployBtn.textContent = '⏳ Wird beendet…';

        const payload = {};
        if (kmEndCheck.checked && kmEndInput.value)
            payload.kmEnd = parseInt(kmEndInput.value, 10);

        try {
            const res = await fetch(`${API_BASE}/deployments/end`, {
                method: 'POST',
                headers: FT_AUTH.adminHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify(payload)
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(err.error || 'Fehler');
            }
            const data = await res.json();
            const parts = [];
            parts.push(`Dauer: ${data.duration} Min.`);
            if (data.kmDriven != null) parts.push(`${data.kmDriven} km`);
            showToast('Einsatz beendet — ' + parts.join(', '));
            kmEndCheck.checked = false;
            kmEndInput.classList.add('hidden');
            kmEndInput.value = '';
            kmStartCheck.checked = false;
            kmStartInput.classList.add('hidden');
            kmStartInput.value = '';
            await checkStatus();
        } catch (err) {
            showToast(err.message || 'Fehler beim Beenden', true);
            deployBtn.disabled = false;
            deployBtn.textContent = '🛑 Einsatz beenden';
        }
    }

    // ── Manual entry ─────────────────────────────────────────────
    function updateManualBtn() {
        manualSaveBtn.disabled = !(manualDogEl.value && manualStartEl.value && manualEndEl.value);
    }

    async function saveManualEntry() {
        manualSaveBtn.disabled = true;
        manualSaveBtn.textContent = 'Wird gespeichert…';

        const payload = {
            dog: manualDogEl.value,
            startTime: new Date(manualStartEl.value).toISOString(),
            endTime: new Date(manualEndEl.value).toISOString()
        };
        if (manualKmDrivenEl.value) payload.kmDriven = parseInt(manualKmDrivenEl.value, 10);
        if (manualActivityEl.value) payload.activity = manualActivityEl.value;

        try {
            const res = await fetch(`${API_BASE}/deployments`, {
                method: 'POST',
                headers: FT_AUTH.adminHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify(payload)
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(err.error || 'Fehler');
            }
            showToast('Einsatz gespeichert');
            manualDogEl.value = '';
            manualStartEl.value = '';
            manualEndEl.value = '';
            manualKmDrivenEl.value = '';
            manualActivityEl.value = '';
            document.getElementById('manualPanel').open = false;
        } catch (err) {
            showToast(err.message || 'Fehler beim Speichern', true);
        } finally {
            manualSaveBtn.textContent = 'Eintrag speichern';
            updateManualBtn();
        }
    }

    // ── Events ───────────────────────────────────────────────────
    dogSelectEl.addEventListener('change', () => {
        localStorage.setItem(STORAGE_KEY_DOG, dogSelectEl.value);
        updateStartBtn();
    });

    kmStartCheck.addEventListener('change', () => {
        kmStartInput.classList.toggle('hidden', !kmStartCheck.checked);
        if (kmStartCheck.checked) kmStartInput.focus();
    });
    kmEndCheck.addEventListener('change', () => {
        kmEndInput.classList.toggle('hidden', !kmEndCheck.checked);
        if (kmEndCheck.checked) kmEndInput.focus();
    });

    deployBtn.addEventListener('click', () => {
        if (isActive) endDeployment();
        else startDeployment();
    });

    manualDogEl.addEventListener('change', updateManualBtn);
    manualStartEl.addEventListener('change', updateManualBtn);
    manualEndEl.addEventListener('change', updateManualBtn);
    manualSaveBtn.addEventListener('click', saveManualEntry);

    // ── Helpers ──────────────────────────────────────────────────
    function formatDateTime(iso) {
        if (!iso) return '—';
        try {
            return new Date(iso).toLocaleString('de-DE', {
                day: '2-digit', month: '2-digit', year: '2-digit',
                hour: '2-digit', minute: '2-digit'
            });
        } catch { return iso; }
    }

    function showToast(msg, isError) {
        clearTimeout(toastTimeout);
        toastEl.textContent = msg;
        toastEl.className = 'toast' + (isError ? ' error' : '');
        toastTimeout = setTimeout(() => toastEl.classList.add('hidden'), 3000);
    }

    // ── Init ─────────────────────────────────────────────────────
    if (FT_AUTH.isAccountant()) {
        document.getElementById('accountingBtn').classList.remove('hidden');
    }
    loadDogs().then(() => checkStatus());
})();
