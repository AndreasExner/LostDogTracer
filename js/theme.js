// ── Theme Toggle (Dark/Light Mode) ──────────────────────────────
(function () {
    'use strict';

    const STORAGE_KEY = 'lostdogtracer_theme';

    // Apply saved theme on load (before paint)
    function applySavedTheme() {
        const saved = localStorage.getItem(STORAGE_KEY);
        // Default to light mode if no preference saved
        document.documentElement.setAttribute('data-theme', saved || 'light');
    }

    function getCurrentTheme() {
        return document.documentElement.getAttribute('data-theme') || 'light';
    }

    function toggleTheme() {
        const current = getCurrentTheme();
        const next = current === 'dark' ? 'light' : 'dark';
        document.documentElement.setAttribute('data-theme', next);
        localStorage.setItem(STORAGE_KEY, next);
        updateToggleLabel();
    }

    function updateToggleLabel() {
        const btn = document.getElementById('themeToggleBtn');
        if (!btn) return;
        const isDark = getCurrentTheme() === 'dark';
        btn.querySelector('.nav-icon').textContent = isDark ? '☀️' : '🌙';
        btn.querySelector('.theme-label').textContent = isDark ? 'Light Mode' : 'Dark Mode';
    }

    // Create the toggle button element (to be inserted into nav drawers)
    function createToggleButton() {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.id = 'themeToggleBtn';
        btn.className = 'theme-toggle';
        btn.innerHTML = '<span class="nav-icon">🌙</span> <span class="theme-label">Dark Mode</span>';
        btn.addEventListener('click', toggleTheme);
        return btn;
    }

    // Expose for use by nav scripts
    window.FT_THEME = {
        applySavedTheme,
        createToggleButton,
        updateToggleLabel
    };

    // Apply immediately
    applySavedTheme();

    // ── Site Banner from Config ──────────────────────────────────
    (async function loadSiteBanner() {
        try {
            const IS_LOCAL = window.location.hostname === 'localhost' || window.location.hostname === '127.0.0.1';
            const API_BASE = IS_LOCAL ? 'http://localhost:7071/api' : '/api';
            const API_KEY = IS_LOCAL ? 'lostdogtracer-dev-key-2026' : '%%PROD_API_KEY%%';
            const cached = sessionStorage.getItem('lostdogtracer_config');
            if (cached) {
                const cfg = JSON.parse(cached);
                applyBanner(cfg.siteBanner);
                window.FT_CONFIG = cfg;
                return;
            }
            const res = await fetch(`${API_BASE}/config`, { headers: { 'X-API-Key': API_KEY } });
            if (res.ok) {
                const cfg = await res.json();
                sessionStorage.setItem('lostdogtracer_config', JSON.stringify(cfg));
                applyBanner(cfg.siteBanner);
                window.FT_CONFIG = cfg;
            }
        } catch { /* use hardcoded fallback */ }
    })();

    function applyBanner(text) {
        if (!text) return;
        const el = document.querySelector('.site-banner');
        if (el) el.textContent = text;
    }
})();
