/**
 * LostDogTracer v2 – Rollen-Verwaltung
 * CRUD for tenant-specific roles with permission checkboxes.
 */
(function () {
    'use strict';

    const API_BASE = FT_AUTH.getApiBase();
    const roleListEl = document.getElementById('roleList');
    const toastEl = document.getElementById('toast');
    const addRoleBtnEl = document.getElementById('addRoleBtn');
    const newRoleIdEl = document.getElementById('newRoleId');
    const newRoleNameEl = document.getElementById('newRoleName');
    const newRolePermsEl = document.getElementById('newRolePerms');

    let allPermissions = [];
    let toastTimeout = null;

    // Permission labels (German)
    const permLabels = {
        'gps.own': 'GPS: Eigene Einträge',
        'gps.read': 'GPS: Alle lesen',
        'gps.write': 'GPS: Erstellen',
        'gps.delete': 'GPS: Löschen',
        'dogs.read': 'Hunde: Lesen',
        'dogs.write': 'Hunde: Verwalten',
        'dogs.owner': 'Hunde: Owner-Keys',
        'categories.read': 'Kategorien: Lesen',
        'categories.write': 'Kategorien: Verwalten',
        'users.read': 'Benutzer: Lesen',
        'users.write': 'Benutzer: Erstellen',
        'users.admin': 'Benutzer: Voll verwalten',
        'equipment.read': 'Equipment: Lesen',
        'equipment.write': 'Equipment: Verwalten',
        'equipment.location': 'Equipment: Standort',
        'deployments.own': 'Einsätze: Eigene',
        'deployments.manage': 'Einsätze: Alle + Abrechnung',
        'config.admin': 'Konfiguration',
        'maintenance.admin': 'Wartung (Backup/Cleanup)'
    };

    async function init() {
        if (!await FT_AUTH.requirePermission('users.admin')) return;
        await loadRoles();
        addRoleBtnEl.addEventListener('click', createRole);
    }

    function showToast(msg, type) {
        toastEl.textContent = msg;
        toastEl.className = 'toast ' + (type || 'success');
        clearTimeout(toastTimeout);
        toastTimeout = setTimeout(() => { toastEl.className = 'toast'; }, 3000);
    }

    function buildPermCheckboxes(container, selectedPerms, idPrefix) {
        container.innerHTML = '';
        allPermissions.forEach(perm => {
            const label = document.createElement('label');
            label.className = 'perm-label';
            const cb = document.createElement('input');
            cb.type = 'checkbox';
            cb.value = perm;
            cb.checked = selectedPerms.includes(perm);
            cb.dataset.perm = perm;
            label.appendChild(cb);
            label.appendChild(document.createTextNode(' ' + (permLabels[perm] || perm)));
            container.appendChild(label);
        });
    }

    async function loadRoles() {
        const res = await FT_AUTH.apiFetch('/manage/roles', { admin: true });
        if (!res.ok) { showToast(res.error, 'error'); return; }

        allPermissions = res.data.allPermissions || [];
        const roles = res.data.roles || [];

        roleListEl.innerHTML = '';
        roles.forEach(role => {
            const card = document.createElement('div');
            card.className = 'card';
            card.style.marginBottom = '1rem';

            const header = document.createElement('div');
            header.style.cssText = 'display:flex;justify-content:space-between;align-items:center;margin-bottom:0.75rem;';
            header.innerHTML = `<strong>${role.displayName}</strong> <span style="font-size:0.8rem;color:var(--text-muted);">${role.roleId}${role.isDefault ? ' (Standard)' : ''}</span>`;
            card.appendChild(header);

            const permsDiv = document.createElement('div');
            permsDiv.className = 'perm-grid';
            permsDiv.id = 'perms-' + role.roleId;
            buildPermCheckboxes(permsDiv, role.permissions, role.roleId);
            card.appendChild(permsDiv);

            const actions = document.createElement('div');
            actions.style.cssText = 'margin-top:0.75rem;display:flex;gap:0.5rem;';

            const saveBtn = document.createElement('button');
            saveBtn.className = 'btn btn-primary';
            saveBtn.textContent = 'Speichern';
            saveBtn.addEventListener('click', () => updateRole(role.roleId, permsDiv));
            actions.appendChild(saveBtn);

            if (!role.isDefault) {
                const delBtn = document.createElement('button');
                delBtn.className = 'btn btn-danger';
                delBtn.textContent = 'Löschen';
                delBtn.addEventListener('click', () => deleteRole(role.roleId));
                actions.appendChild(delBtn);
            }

            card.appendChild(actions);
            roleListEl.appendChild(card);
        });

        // Build checkboxes for "new role" form
        buildPermCheckboxes(newRolePermsEl, [], 'new');
    }

    async function updateRole(roleId, permsContainer) {
        const checked = Array.from(permsContainer.querySelectorAll('input[type=checkbox]:checked')).map(cb => cb.value);
        if (checked.length === 0) { showToast('Mindestens eine Berechtigung wählen', 'error'); return; }

        const res = await FT_AUTH.apiFetch('/manage/roles/' + encodeURIComponent(roleId), {
            method: 'PUT', admin: true, body: { permissions: checked }
        });
        if (res.ok) { showToast('Rolle aktualisiert'); }
        else { showToast(res.error, 'error'); }
    }

    async function deleteRole(roleId) {
        if (!confirm(`Rolle "${roleId}" wirklich löschen?`)) return;
        const res = await FT_AUTH.apiFetch('/manage/roles/' + encodeURIComponent(roleId), {
            method: 'DELETE', admin: true
        });
        if (res.ok) { showToast('Rolle gelöscht'); await loadRoles(); }
        else { showToast(res.error, 'error'); }
    }

    async function createRole() {
        const roleId = newRoleIdEl.value.trim().toLowerCase();
        const displayName = newRoleNameEl.value.trim();
        if (!roleId || !displayName) { showToast('ID und Anzeigename erforderlich', 'error'); return; }

        const checked = Array.from(newRolePermsEl.querySelectorAll('input[type=checkbox]:checked')).map(cb => cb.value);
        if (checked.length === 0) { showToast('Mindestens eine Berechtigung wählen', 'error'); return; }

        const res = await FT_AUTH.apiFetch('/manage/roles', {
            method: 'POST', admin: true,
            body: { roleId, displayName, permissions: checked }
        });
        if (res.ok) {
            showToast('Rolle angelegt');
            newRoleIdEl.value = '';
            newRoleNameEl.value = '';
            await loadRoles();
        } else {
            showToast(res.error, 'error');
        }
    }

    init();
})();
