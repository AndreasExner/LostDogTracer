/* js/admin-backup.js — Backup Export & Import */
(function () {
    'use strict';

    const API_BASE = FT_AUTH.getApiBase();
    const exportBtn = document.getElementById('exportBtn');
    const exportStatus = document.getElementById('exportStatus');
    const importFile = document.getElementById('importFile');
    const importBtn = document.getElementById('importBtn');
    const importStatus = document.getElementById('importStatus');
    const toastEl = document.getElementById('toast');
    let toastTimeout = null;

    // ── Export ────────────────────────────────────────────────────
    exportBtn.addEventListener('click', async () => {
        exportBtn.disabled = true;
        exportBtn.textContent = '⏳ Wird exportiert…';
        exportStatus.textContent = '';

        try {
            const res = await fetch(`${API_BASE}/manage/backup`, {
                headers: FT_AUTH.adminHeaders()
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) throw new Error('Export fehlgeschlagen');

            const data = await res.json();
            const json = JSON.stringify(data, null, 2);
            const blob = new Blob([json], { type: 'application/json' });
            const url = URL.createObjectURL(blob);

            const a = document.createElement('a');
            const date = new Date().toISOString().slice(0, 10);
            a.href = url;
            a.download = `lostdogtracer-backup-${date}.json`;
            a.click();
            URL.revokeObjectURL(url);

            // Count records
            const tables = data.tables || {};
            const total = Object.values(tables).reduce((sum, rows) => sum + rows.length, 0);
            exportStatus.textContent = `✓ ${total} Einträge exportiert`;
            showToast('Backup erfolgreich heruntergeladen');
        } catch (err) {
            console.error(err);
            showToast('Fehler beim Export', true);
        } finally {
            exportBtn.disabled = false;
            exportBtn.textContent = '📥 Backup herunterladen';
        }
    });

    // ── Import ───────────────────────────────────────────────────
    importFile.addEventListener('change', () => {
        importBtn.disabled = !importFile.files.length;
        importStatus.textContent = '';
    });

    importBtn.addEventListener('click', async () => {
        const file = importFile.files[0];
        if (!file) return;

        if (!confirm('Achtung: Bestehende Einträge mit gleicher ID werden überschrieben.\n\nFortfahren?')) return;

        importBtn.disabled = true;
        importBtn.textContent = '⏳ Wird importiert…';
        importStatus.textContent = '';

        try {
            const text = await file.text();
            // Validate JSON structure
            const parsed = JSON.parse(text);
            if (!parsed.tables) throw new Error('Ungültiges Backup-Format');

            const res = await fetch(`${API_BASE}/manage/restore`, {
                method: 'POST',
                headers: FT_AUTH.adminHeaders({ 'Content-Type': 'application/json' }),
                body: text
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(err.error || 'Import fehlgeschlagen');
            }

            const result = await res.json();
            const restored = result.restored || {};
            const details = Object.entries(restored)
                .map(([table, count]) => `${table}: ${count}`)
                .join(', ');
            importStatus.textContent = `✓ Wiederhergestellt — ${details}`;
            showToast('Daten erfolgreich wiederhergestellt');
        } catch (err) {
            console.error(err);
            importStatus.textContent = '';
            showToast(err.message || 'Fehler beim Import', true);
        } finally {
            importBtn.disabled = false;
            importBtn.textContent = '📤 Wiederherstellen';
            importFile.value = '';
        }
    });

    // ── Toast ────────────────────────────────────────────────────
    function showToast(msg, isError = false) {
        if (toastTimeout) clearTimeout(toastTimeout);
        toastEl.textContent = msg;
        toastEl.className = isError ? 'toast error' : 'toast';
        toastTimeout = setTimeout(() => toastEl.classList.add('hidden'), 3000);
    }

    // ── Cleanup ──────────────────────────────────────────────────
    const cleanupDaysEl = document.getElementById('cleanupDays');
    const cleanupDogEl = document.getElementById('cleanupDog');
    const cleanupPreviewBtn = document.getElementById('cleanupPreviewBtn');
    const cleanupPreviewEl = document.getElementById('cleanupPreview');
    const cleanupExecBtn = document.getElementById('cleanupExecBtn');
    const cleanupStatusEl = document.getElementById('cleanupStatus');

    // Only init cleanup if elements exist (maintenance.html)
    if (cleanupPreviewBtn) {
        // Load dog list for filter
        (async function loadDogs() {
            try {
                const res = await fetch(`${API_BASE}/lost-dogs`, { headers: FT_AUTH.publicHeaders() });
                if (res.ok) {
                    const dogs = await res.json();
                    dogs.forEach(d => {
                        const opt = document.createElement('option');
                        opt.value = d.rowKey;
                        opt.textContent = d.displayName;
                        cleanupDogEl.appendChild(opt);
                    });
                }
            } catch { /* ignore */ }
        })();

        cleanupPreviewBtn.addEventListener('click', async () => {
            cleanupPreviewBtn.disabled = true;
            cleanupPreviewBtn.textContent = '⏳…';
            cleanupExecBtn.style.display = 'none';
            cleanupPreviewEl.style.display = 'none';
            cleanupStatusEl.textContent = '';

            const params = new URLSearchParams();
            params.set('olderThanDays', cleanupDaysEl.value);
            if (cleanupDogEl.value) params.set('lostDog', cleanupDogEl.value);

            try {
                const res = await fetch(`${API_BASE}/manage/cleanup/preview?${params}`, {
                    headers: FT_AUTH.adminHeaders()
                });
                if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
                if (!res.ok) throw new Error();

                const data = await res.json();
                if (data.recordCount === 0) {
                    cleanupPreviewEl.textContent = `Keine Einträge älter als ${data.olderThanDays} Tage (Stichtag: ${data.cutoffDate})`;
                    cleanupPreviewEl.style.display = '';
                } else {
                    cleanupPreviewEl.innerHTML =
                        `<strong>${data.recordCount} Einträge</strong> und <strong>${data.photoCount} Fotos</strong> ` +
                        `älter als ${data.olderThanDays} Tage (Stichtag: ${data.cutoffDate})` +
                        (data.lostDog ? ` — gefiltert nach Hund` : '');
                    cleanupPreviewEl.style.display = '';
                    cleanupExecBtn.style.display = '';
                }
            } catch {
                showToast('Fehler bei der Vorschau', true);
            } finally {
                cleanupPreviewBtn.disabled = false;
                cleanupPreviewBtn.textContent = 'Vorschau';
            }
        });

        cleanupExecBtn.addEventListener('click', async () => {
            if (!confirm(`Alte Einträge und Fotos wirklich unwiderruflich löschen?\n\nDiese Aktion kann nicht rückgängig gemacht werden.`)) return;

            cleanupExecBtn.disabled = true;
            cleanupExecBtn.textContent = '⏳ Wird bereinigt…';
            cleanupStatusEl.textContent = '';

            const params = new URLSearchParams();
            params.set('olderThanDays', cleanupDaysEl.value);
            if (cleanupDogEl.value) params.set('lostDog', cleanupDogEl.value);

            try {
                const res = await fetch(`${API_BASE}/manage/cleanup?${params}`, {
                    method: 'POST',
                    headers: FT_AUTH.adminHeaders()
                });
                if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
                if (!res.ok) throw new Error();

                const data = await res.json();
                cleanupStatusEl.textContent = `✓ ${data.deletedRecords} Einträge und ${data.deletedPhotos} Fotos gelöscht`;
                cleanupPreviewEl.style.display = 'none';
                cleanupExecBtn.style.display = 'none';
                showToast(data.message);
            } catch {
                showToast('Fehler bei der Bereinigung', true);
            } finally {
                cleanupExecBtn.disabled = false;
                cleanupExecBtn.textContent = 'Bereinigung durchführen';
            }
        });
    }
})();
