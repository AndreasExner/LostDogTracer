/* js/users.js — Benutzerverwaltung */
(function () {
    const API = FT_AUTH.getApiBase();

    /* ── DOM refs ─────────────────────────────── */
    const userList        = document.getElementById('userList');

    const createUserModal = document.getElementById('createUserModal');
    const resetPwModal    = document.getElementById('resetPwModal');
    const editUserModal   = document.getElementById('editUserModal');

    let currentUsername = '';

    /* ── Helpers ──────────────────────────────── */
    function showToast(msg, ok = true) {
        const t = document.getElementById('toast');
        t.textContent = msg;
        t.className = 'toast' + (ok ? '' : ' error');
        setTimeout(() => t.className = 'toast hidden', 2500);
    }

    function showError(id, msg) {
        const el = document.getElementById(id);
        el.textContent = msg; el.style.display = 'block';
    }
    function hideError(id) {
        const el = document.getElementById(id);
        el.textContent = ''; el.style.display = 'none';
    }

    function openModal(modal) { modal.classList.add('open'); }
    function closeModal(modal) { modal.classList.remove('open'); }

    async function apiCall(url, opts = {}) {
        try {
            const res = await fetch(url, opts);
            if (res.status === 401) { FT_AUTH.sessionExpired(); return null; }
            return res;
        } catch (e) {
            showToast('Netzwerkfehler', false);
            return null;
        }
    }

    /* ── Load current username ────────────────── */
    async function loadCurrentUser() {
        const res = await apiCall(`${API}/auth/verify`, { headers: FT_AUTH.adminHeaders() });
        if (!res) return;
        const data = await res.json();
        currentUsername = data.username || '';
    }

    /* ── Load user list ──────────────────────── */
    async function loadUsers() {
        const res = await apiCall(`${API}/manage/users`, { headers: FT_AUTH.adminHeaders() });
        if (!res) return;
        if (!res.ok) { showToast('Fehler beim Laden', false); return; }
        const users = await res.json();
        renderUsers(users);
    }

    function renderUsers(users) {
        if (!users.length) {
            userList.innerHTML = '<p style="color:#6e6e73;text-align:center;padding:2rem">Keine Benutzer.</p>';
            return;
        }
        const myLevel = (typeof FT_AUTH !== 'undefined') ? FT_AUTH.getRoleLevel() : 1;
        const isAdmin = myLevel >= 4;
        userList.innerHTML = users.map(u => {
            const isSelf = u.username.toLowerCase() === currentUsername.toLowerCase();
            const created = u.createdAt ? new Date(u.createdAt).toLocaleDateString('de-DE') : '—';
            const lastLogin = u.lastLogin ? new Date(u.lastLogin).toLocaleString('de-DE') : 'Nie';
            const role = u.role || 'User';
            const editBtn = isAdmin ? `<button class="btn btn-secondary btn-sm" onclick="AdminUsers.editUser('${esc(u.username)}','${esc(u.displayName || u.username)}','${esc(role)}')">Bearbeiten</button>` : '';
            const pwBtn = isAdmin ? `<button class="btn btn-secondary btn-sm" onclick="AdminUsers.resetPw('${esc(u.username)}')">Kennwort</button>` : '';
            const delBtn = (isAdmin && !isSelf) ? `<button class="btn btn-sm" style="background:#ff3b30;color:#fff" onclick="AdminUsers.deleteUser('${esc(u.username)}','${esc(u.displayName || u.username)}')">Löschen</button>` : '';
            return `
            <div class="user-card">
                <div class="user-info">
                    <strong>${esc(u.displayName || u.username)}</strong>
                    <small>@${esc(u.username)} · ${esc(role)} · Erstellt: ${created} · Letzter Login: ${lastLogin}</small>
                </div>
                <div class="user-actions">
                    ${editBtn}${pwBtn}${delBtn}
                </div>
            </div>`;
        }).join('');
    }

    function esc(s) { return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;').replace(/'/g,'&#39;'); }

    /* ── Create user ─────────────────────────── */
    document.getElementById('addUserBtn').addEventListener('click', () => {
        document.getElementById('newUsername').value = '';
        document.getElementById('newDisplayName').value = '';
        document.getElementById('newUserPw').value = '';
        // Manager: hide role dropdown (can only assign "User")
        const roleSelect = document.getElementById('newUserRole');
        if (FT_AUTH.getRoleLevel() < 4) {
            roleSelect.value = 'User';
            roleSelect.style.display = 'none';
        } else {
            roleSelect.style.display = '';
        }
        hideError('createUserError');
        openModal(createUserModal);
    });
    document.getElementById('createUserCancel').addEventListener('click', () => closeModal(createUserModal));
    document.getElementById('createUserSave').addEventListener('click', async () => {
        const username = document.getElementById('newUsername').value.trim();
        const displayName = document.getElementById('newDisplayName').value.trim();
        const password = document.getElementById('newUserPw').value.trim();

        if (!username || !password) { showError('createUserError', 'Benutzername und Kennwort sind Pflicht.'); return; }
        if (password.length < 8) { showError('createUserError', 'Kennwort: mindestens 8 Zeichen.'); return; }

        const res = await apiCall(`${API}/manage/users`, {
            method: 'POST',
            headers: { ...FT_AUTH.adminHeaders(), 'Content-Type': 'application/json' },
            body: JSON.stringify({ username, displayName: displayName || username, password, role: document.getElementById('newUserRole').value })
        });
        if (!res) return;
        if (res.ok) {
            closeModal(createUserModal);
            showToast('Benutzer angelegt');
            loadUsers();
        } else {
            const data = await res.json().catch(() => ({}));
            showError('createUserError', data.error || 'Fehler beim Anlegen.');
        }
    });

    /* ── Reset password (admin action) ────────── */
    let resetTarget = '';
    window.AdminUsers = window.AdminUsers || {};

    AdminUsers.resetPw = function (username) {
        resetTarget = username;
        document.getElementById('resetPwUser').textContent = username;
        document.getElementById('resetNewPw').value = '';
        hideError('resetPwError');
        openModal(resetPwModal);
    };
    document.getElementById('resetPwCancel').addEventListener('click', () => closeModal(resetPwModal));
    document.getElementById('resetPwSave').addEventListener('click', async () => {
        const pw = document.getElementById('resetNewPw').value.trim();
        if (!pw || pw.length < 8) { showError('resetPwError', 'Mindestens 8 Zeichen.'); return; }

        const res = await apiCall(`${API}/manage/users/${encodeURIComponent(resetTarget)}/reset-password`, {
            method: 'POST',
            headers: { ...FT_AUTH.adminHeaders(), 'Content-Type': 'application/json' },
            body: JSON.stringify({ newPassword: pw })
        });
        if (!res) return;
        if (res.ok) {
            closeModal(resetPwModal);
            showToast('Kennwort zurückgesetzt');
        } else {
            const data = await res.json().catch(() => ({}));
            showError('resetPwError', data.error || 'Fehler beim Zurücksetzen.');
        }
    });

    /* ── Delete user ─────────────────────────── */
    AdminUsers.deleteUser = async function (username, display) {
        if (!confirm(`Benutzer "${display}" wirklich löschen?`)) return;
        const res = await apiCall(`${API}/manage/users/${encodeURIComponent(username)}`, {
            method: 'DELETE',
            headers: FT_AUTH.adminHeaders()
        });
        if (!res) return;
        if (res.ok) {
            showToast('Benutzer gelöscht');
            loadUsers();
        } else {
            const data = await res.json().catch(() => ({}));
            showToast(data.error || 'Fehler beim Löschen', false);
        }
    };
    /* ── Edit user (role + displayName) ─────── */
    let editTarget = '';
    AdminUsers.editUser = function (username, displayName, role) {
        editTarget = username;
        document.getElementById('editUserName').textContent = username;
        document.getElementById('editDisplayName').value = displayName;
        document.getElementById('editUserRole').value = role;
        hideError('editUserError');
        openModal(editUserModal);
    };
    document.getElementById('editUserCancel').addEventListener('click', () => closeModal(editUserModal));
    document.getElementById('editUserSave').addEventListener('click', async () => {
        const displayName = document.getElementById('editDisplayName').value.trim();
        const role = document.getElementById('editUserRole').value;
        if (!displayName) { showError('editUserError', 'Anzeigename darf nicht leer sein.'); return; }

        const res = await apiCall(`${API}/manage/users/${encodeURIComponent(editTarget)}`, {
            method: 'PUT',
            headers: { ...FT_AUTH.adminHeaders(), 'Content-Type': 'application/json' },
            body: JSON.stringify({ displayName, role })
        });
        if (!res) return;
        if (res.ok) {
            closeModal(editUserModal);
            showToast('Benutzer aktualisiert');
            loadUsers();
        } else {
            const data = await res.json().catch(() => ({}));
            showError('editUserError', data.error || 'Fehler beim Speichern.');
        }
    });
    /* ── Init ────────────────────────────────── */
    loadCurrentUser().then(() => loadUsers());
})();
