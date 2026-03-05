// ── Theme Toggle (Dark/Light Mode) ──────────────────────────────
(function () {
    'use strict';

    const STORAGE_KEY = 'flyertracker_theme';

    // Apply saved theme on load (before paint)
    function applySavedTheme() {
        const saved = localStorage.getItem(STORAGE_KEY);
        if (saved) {
            document.documentElement.setAttribute('data-theme', saved);
        }
        // If no saved preference, system preference via CSS media query handles it
    }

    function getCurrentTheme() {
        const attr = document.documentElement.getAttribute('data-theme');
        if (attr) return attr;
        return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
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
})();
