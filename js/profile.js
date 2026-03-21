/* js/profile.js — Eigenes Profil verwalten */
(function () {
    'use strict';

    const API = FT_AUTH.getApiBase();
    const toastEl = document.getElementById('toast');
    let toastTimeout = null;

    function showToast(msg, ok = true) {
        clearTimeout(toastTimeout);
        toastEl.textContent = msg;
        toastEl.className = 'toast' + (ok ? '' : ' error');
        toastTimeout = setTimeout(() => toastEl.classList.add('hidden'), 2500);
    }

    /* ── Load current user info ──────────────── */
    async function loadProfile() {
        try {
            const res = await fetch(`${API}/auth/verify`, { headers: FT_AUTH.adminHeaders() });
            if (!res.ok) { FT_AUTH.sessionExpired(); return; }
            const data = await res.json();
            document.getElementById('profileUsername').textContent = data.username || '—';
            document.getElementById('profileRole').textContent = data.role || 'User';

            // Load display name from user list (own entry)
            const usersRes = await fetch(`${API}/manage/users`, { headers: FT_AUTH.adminHeaders() });
            if (usersRes.ok) {
                const users = await usersRes.json();
                const me = users.find(u => u.username === data.username);
                if (me) document.getElementById('profileDisplayName').value = me.displayName || '';
            }
        } catch {
            showToast('Profil konnte nicht geladen werden', false);
        }
    }

    /* ── Save display name ───────────────────── */
    document.getElementById('saveDisplayNameBtn').addEventListener('click', async () => {
        const displayName = document.getElementById('profileDisplayName').value.trim();
        if (!displayName) { showToast('Anzeigename darf nicht leer sein', false); return; }

        const btn = document.getElementById('saveDisplayNameBtn');
        btn.disabled = true;
        try {
            const res = await fetch(`${API}/auth/update-profile`, {
                method: 'POST',
                headers: { ...FT_AUTH.adminHeaders(), 'Content-Type': 'application/json' },
                body: JSON.stringify({ displayName })
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (res.ok) {
                showToast('Anzeigename gespeichert');
            } else {
                const data = await res.json().catch(() => ({}));
                showToast(data.error || 'Fehler beim Speichern', false);
            }
        } catch {
            showToast('Netzwerkfehler', false);
        } finally {
            btn.disabled = false;
        }
    });

    /* ── Change password ─────────────────────── */
    document.getElementById('savePasswordBtn').addEventListener('click', async () => {
        const oldPw = document.getElementById('profileOldPw').value.trim();
        const newPw = document.getElementById('profileNewPw').value.trim();
        const newPw2 = document.getElementById('profileNewPw2').value.trim();

        if (!oldPw || !newPw) { showToast('Bitte alle Felder ausfüllen', false); return; }
        if (newPw.length < 8) { showToast('Mindestens 8 Zeichen', false); return; }
        if (newPw !== newPw2) { showToast('Neue Kennwörter stimmen nicht überein', false); return; }

        const btn = document.getElementById('savePasswordBtn');
        btn.disabled = true;
        try {
            const res = await fetch(`${API}/auth/change-password`, {
                method: 'POST',
                headers: { ...FT_AUTH.adminHeaders(), 'Content-Type': 'application/json' },
                body: JSON.stringify({ oldPassword: oldPw, newPassword: newPw })
            });
            if (res.status === 401) { FT_AUTH.sessionExpired(); return; }
            if (res.ok) {
                showToast('Kennwort geändert');
                document.getElementById('profileOldPw').value = '';
                document.getElementById('profileNewPw').value = '';
                document.getElementById('profileNewPw2').value = '';
            } else {
                const data = await res.json().catch(() => ({}));
                showToast(data.error || 'Fehler beim Ändern', false);
            }
        } catch {
            showToast('Netzwerkfehler', false);
        } finally {
            btn.disabled = false;
        }
    });

    loadProfile();
})();
