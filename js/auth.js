/**
 * LostDogTracer – Shared Auth Helper
 * Provides API key header and admin token management.
 */
const FT_AUTH = (function () {
    'use strict';

    const IS_LOCAL = window.location.hostname === 'localhost' || window.location.hostname === '127.0.0.1';
    const API_KEY = IS_LOCAL ? 'lostdogtracer-dev-key-2026' : '%%PROD_API_KEY%%';
    const TOKEN_KEY = 'lostdogtracer_admin_token';
    const ROLE_KEY = 'lostdogtracer_role';
    const API_BASE = IS_LOCAL ? 'http://localhost:7071/api' : '/api';

    /** Headers for public (non-admin) fetch calls */
    function publicHeaders(extra) {
        return Object.assign({ 'X-API-Key': API_KEY }, extra || {});
    }

    /** Headers for admin fetch calls (includes Bearer token) */
    function adminHeaders(extra) {
        const token = sessionStorage.getItem(TOKEN_KEY);
        return Object.assign({
            'X-API-Key': API_KEY,
            'X-Admin-Token': token || ''
        }, extra || {});
    }

    /** Login → returns token string or null. If debugLogin config is true, returns error details. */
    async function login(username, password) {
        try {
            const res = await fetch(`${API_BASE}/auth/login`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'X-API-Key': API_KEY },
                body: JSON.stringify({ username, password })
            });
            if (!res.ok) {
                // Store debug info for caller
                login._lastDebug = `HTTP ${res.status}`;
                try { const b = await res.json(); login._lastDebug += ': ' + (b.error || JSON.stringify(b)); } catch {}
                return null;
            }
            const data = await res.json();
            if (data.token) {
                try {
                    sessionStorage.setItem(TOKEN_KEY, data.token);
                    if (data.role) sessionStorage.setItem(ROLE_KEY, data.role);
                    if (data.accountant) sessionStorage.setItem('lostdogtracer_accountant', '1');
                    else sessionStorage.removeItem('lostdogtracer_accountant');
                    // Verify it was actually stored
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

    /** Check if admin token exists and is valid (calls /auth/verify) */
    async function isLoggedIn() {
        const token = sessionStorage.getItem(TOKEN_KEY);
        if (!token) return false;
        if (!navigator.onLine) return true; // Offline: trust cached token
        try {
            const res = await fetch(`${API_BASE}/auth/verify`, {
                headers: { 'X-API-Key': API_KEY, 'X-Admin-Token': token }
            });
            return res.ok;
        } catch {
            return true; // Network error but token exists: allow access
        }
    }

    function logout() {
        sessionStorage.removeItem(TOKEN_KEY);
        sessionStorage.removeItem(ROLE_KEY);
        sessionStorage.removeItem('lostdogtracer_accountant');
    }

    /** Get cached role string */
    function getRole() {
        return sessionStorage.getItem(ROLE_KEY) || 'User';
    }

    /** Get numeric role level: User=1, Manager=2, Administrator=3 */
    function getRoleLevel() {
        const r = getRole();
        if (r === 'Administrator') return 4;
        if (r === 'Manager') return 3;
        if (r === 'PowerUser') return 2;
        return 1;
    }

    /** Check login + minimum role level. Redirects if insufficient. */
    async function requireRole(minLevel) {
        const ok = await isLoggedIn();
        if (!ok) { location.href = 'index.html'; return false; }
        if (getRoleLevel() < minLevel) { location.href = 'index.html'; return false; }
        return true;
    }

    /** Handle 401 — show brief message, then redirect */
    function sessionExpired() {
        logout();
        // Show a brief toast before redirecting
        const t = document.getElementById('toast');
        if (t) {
            t.textContent = 'Sitzung abgelaufen – bitte erneut anmelden';
            t.className = 'toast error';
        }
        setTimeout(() => { location.href = 'index.html'; }, 1500);
    }

    function getApiBase() { return API_BASE; }

    function isAccountant() {
        return sessionStorage.getItem('lostdogtracer_accountant') === '1';
    }

    return { publicHeaders, adminHeaders, login, isLoggedIn, logout, sessionExpired, getApiBase, getRole, getRoleLevel, requireRole, isAccountant };
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
