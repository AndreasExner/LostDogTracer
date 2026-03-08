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
})();
