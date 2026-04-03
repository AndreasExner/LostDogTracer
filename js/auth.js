/**
 * LostDogTracer v2 – Shared Auth Helper
 * Multi-tenant, permission-based auth with centralized API config.
 * All API calls should use FT_AUTH.publicHeaders() or FT_AUTH.adminHeaders().
 * Guest/Owner apps use FT_AUTH.getApiBase() and FT_AUTH.getApiKey().
 */
const FT_AUTH = (function () {
    'use strict';

    const IS_LOCAL = window.location.hostname === 'localhost' || window.location.hostname === '127.0.0.1';
    const API_KEY = IS_LOCAL ? 'lostdogtracer-dev-key-2026' : '%%PROD_API_KEY%%';
    const API_BASE = IS_LOCAL ? 'http://localhost:7071/api' : '/api';

    const TOKEN_KEY = 'lostdogtracer_admin_token';
    const PERMISSIONS_KEY = 'lostdogtracer_permissions';
    const TENANT_KEY = 'lostdogtracer_tenant_id';
    const ROLE_NAME_KEY = 'lostdogtracer_role_name';
    const ROLE_ID_KEY = 'lostdogtracer_role_id';
    const DISPLAY_NAME_KEY = 'lostdogtracer_display_name';

    // ── API access ───────────────────────────────────────────────

    function getApiBase() { return API_BASE; }
    function getApiKey() { return API_KEY; }
    function getTenantId() { return sessionStorage.getItem(TENANT_KEY) || ''; }

    /** Headers for public (non-admin) fetch calls */
    function publicHeaders(extra) {
        return Object.assign({ 'X-API-Key': API_KEY }, extra || {});
    }

    /** Headers for admin fetch calls (includes token) */
    function adminHeaders(extra) {
        const token = sessionStorage.getItem(TOKEN_KEY);
        return Object.assign({
            'X-API-Key': API_KEY,
            'X-Admin-Token': token || ''
        }, extra || {});
    }

    // ── Centralized fetch wrapper ────────────────────────────────

    /**
     * Fetch wrapper with automatic API key, optional admin token, error handling.
     * @param {string} url - Relative URL (e.g. '/auth/login') or full URL
     * @param {object} options - { method, body, admin: bool, headers: {} }
     * @returns {Promise<{ok: boolean, status: number, data: any, error: string?}>}
     */
    async function apiFetch(url, options = {}) {
        const fullUrl = url.startsWith('http') ? url : `${API_BASE}${url}`;
        const headers = options.admin ? adminHeaders(options.headers) : publicHeaders(options.headers);
        if (options.body && typeof options.body === 'object' && !(options.body instanceof FormData)) {
            headers['Content-Type'] = 'application/json';
            options.body = JSON.stringify(options.body);
        }
        try {
            const res = await fetch(fullUrl, {
                method: options.method || 'GET',
                headers,
                body: options.body
            });
            if (res.status === 401) {
                sessionExpired();
                return { ok: false, status: 401, data: null, error: 'Sitzung abgelaufen' };
            }
            if (res.status === 429) {
                return { ok: false, status: 429, data: null, error: 'Zu viele Anfragen. Bitte warten.' };
            }
            let data = null;
            try { data = await res.json(); } catch {}
            if (!res.ok) {
                return { ok: false, status: res.status, data, error: data?.error || `Fehler ${res.status}` };
            }
            return { ok: true, status: res.status, data, error: null };
        } catch (e) {
            return { ok: false, status: 0, data: null, error: 'Netzwerkfehler: ' + e.message };
        }
    }

    // ── Login / Auth ─────────────────────────────────────────────

    /**
     * Login with tenantId + username + password.
     * Returns token string or null. Stores permissions + tenant info in sessionStorage.
     */
    async function login(tenantId, username, password) {
        try {
            const res = await fetch(`${API_BASE}/auth/login`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'X-API-Key': API_KEY },
                body: JSON.stringify({ tenantId, username, password })
            });
            if (!res.ok) {
                login._lastDebug = `HTTP ${res.status}`;
                try { const b = await res.json(); login._lastDebug += ': ' + (b.error || JSON.stringify(b)); } catch {}
                return null;
            }
            const data = await res.json();
            if (data.token) {
                try {
                    sessionStorage.setItem(TOKEN_KEY, data.token);
                    sessionStorage.setItem(TENANT_KEY, tenantId);
                    sessionStorage.setItem(PERMISSIONS_KEY, JSON.stringify(data.permissions || []));
                    sessionStorage.setItem(ROLE_NAME_KEY, data.roleName || '');
                    sessionStorage.setItem(ROLE_ID_KEY, data.roleId || '');
                    sessionStorage.setItem(DISPLAY_NAME_KEY, data.displayName || username);
                    if (!sessionStorage.getItem(TOKEN_KEY)) {
                        login._lastDebug = 'sessionStorage: Token konnte nicht gespeichert werden';
                        return null;
                    }
                } catch (e) {
                    login._lastDebug = 'sessionStorage blockiert: ' + e.message;
                    return null;
                }
                return data.token;
            }
            login._lastDebug = 'Kein Token in Antwort';
            return null;
        } catch (e) {
            login._lastDebug = 'Netzwerkfehler: ' + e.message;
            return null;
        }
    }
    login._lastDebug = '';

    /** Verify token and refresh permissions from server */
    async function isLoggedIn() {
        const token = sessionStorage.getItem(TOKEN_KEY);
        if (!token) return false;
        if (!navigator.onLine) return true;
        try {
            const res = await fetch(`${API_BASE}/auth/verify`, {
                headers: { 'X-API-Key': API_KEY, 'X-Admin-Token': token }
            });
            if (!res.ok) return false;
            const data = await res.json();
            // Refresh permissions from server
            if (data.permissions) sessionStorage.setItem(PERMISSIONS_KEY, JSON.stringify(data.permissions));
            if (data.roleName) sessionStorage.setItem(ROLE_NAME_KEY, data.roleName);
            if (data.roleId) sessionStorage.setItem(ROLE_ID_KEY, data.roleId);
            if (data.displayName) sessionStorage.setItem(DISPLAY_NAME_KEY, data.displayName);
            return true;
        } catch {
            return true; // Network error but token exists: allow access
        }
    }

    function logout() {
        sessionStorage.removeItem(TOKEN_KEY);
        sessionStorage.removeItem(PERMISSIONS_KEY);
        sessionStorage.removeItem(TENANT_KEY);
        sessionStorage.removeItem(ROLE_NAME_KEY);
        sessionStorage.removeItem(ROLE_ID_KEY);
        sessionStorage.removeItem(DISPLAY_NAME_KEY);
    }

    // ── Permissions ──────────────────────────────────────────────

    /** Get cached permissions array */
    function getPermissions() {
        try { return JSON.parse(sessionStorage.getItem(PERMISSIONS_KEY) || '[]'); } catch { return []; }
    }

    /** Check if user has a specific permission */
    function hasPermission(perm) {
        return getPermissions().includes(perm);
    }

    /** Check if user has ANY of the given permissions */
    function hasAnyPermission(...perms) {
        const userPerms = getPermissions();
        return perms.some(p => userPerms.includes(p));
    }

    /** Get role display name */
    function getRoleName() {
        return sessionStorage.getItem(ROLE_NAME_KEY) || '';
    }

    /** Get role ID */
    function getRoleId() {
        return sessionStorage.getItem(ROLE_ID_KEY) || '';
    }

    /** Get display name */
    function getDisplayName() {
        return sessionStorage.getItem(DISPLAY_NAME_KEY) || '';
    }

    /**
     * Require login + specific permission. Redirects to index.html if not met.
     * @param {string} perm - Required permission (e.g. 'dogs.write')
     * @returns {Promise<boolean>}
     */
    async function requirePermission(perm) {
        const ok = await isLoggedIn();
        if (!ok) { location.href = 'index.html'; return false; }
        if (!hasPermission(perm)) { location.href = 'index.html'; return false; }
        return true;
    }

    /** Require login only (any permission). Redirects if not logged in. */
    async function requireLogin() {
        const ok = await isLoggedIn();
        if (!ok) { location.href = 'index.html'; return false; }
        return true;
    }

    /** Handle 401 — toast + redirect */
    function sessionExpired() {
        logout();
        const t = document.getElementById('toast');
        if (t) {
            t.textContent = 'Sitzung abgelaufen – bitte erneut anmelden';
            t.className = 'toast error';
        }
        setTimeout(() => { location.href = 'index.html'; }, 1500);
    }

    // ── Backward compatibility (deprecated — use hasPermission instead) ──

    function getRoleLevel() {
        // Map permissions to approximate role levels for legacy code
        if (hasPermission('maintenance.admin')) return 4;
        if (hasPermission('dogs.write')) return 3;
        if (hasPermission('equipment.read')) return 2;
        return 1;
    }
    function getRole() { return getRoleName(); }
    function requireRole(minLevel) { return requireLogin(); }
    function isAccountant() { return hasPermission('deployments.manage'); }

    return {
        // Core
        publicHeaders, adminHeaders, apiFetch,
        login, isLoggedIn, logout, sessionExpired,
        // Config
        getApiBase, getApiKey, getTenantId,
        // Permissions (v2)
        getPermissions, hasPermission, hasAnyPermission,
        getRoleName, getRoleId, getDisplayName,
        requirePermission, requireLogin,
        // Backward compat (deprecated)
        getRole, getRoleLevel, requireRole, isAccountant
    };
})();

/* ── Password visibility toggle (delegated) ──────────────────── */
document.addEventListener('click', function (e) {
    const btn = e.target.closest('.pw-toggle');
    if (!btn) return;
    const input = btn.parentElement.querySelector('input');
    if (!input) return;
    input.type = input.type === 'password' ? 'text' : 'password';
    btn.textContent = input.type === 'password' ? '\u{1F441}' : '\u{1F441}\u200D\u{1F5E8}';
});
