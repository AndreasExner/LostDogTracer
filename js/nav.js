// ── Hamburger Navigation ──────────────────────────────────
(function () {
    // Inject on sub-pages and index.html menu page
    const path = location.pathname;
    const isSubPage = /(?:gpsrecords|map|lostdogs|categories|users|equipment|deployments|deployment-records|deployment-accounting|maintenance|profile|docs)\.html$/i.test(path);
    const isHome = /index\.html$/i.test(path) || path === '/' || path.endsWith('/');
    if (!isSubPage && !isHome) return;

    const pages = [
        { href: 'field-home.html', icon: '🚩', label: 'Erfassen', minRole: 1 },
        { href: 'gpsrecords.html', icon: '📍', label: 'GPS-Daten', minRole: 1 },
        { href: 'deployments.html', icon: '⏱️', label: 'Einsatzzeiten', minRole: 1, feat: 'deployment' },
        { href: 'equipment.html',  icon: '📷', label: 'Equipment', minRole: 2, feat: 'equipment' },
        { href: 'lostdogs.html',   icon: '🐕', label: 'Hunde', minRole: 3 },
        { href: 'categories.html', icon: '🏷️', label: 'Kategorien', minRole: 4 },
        { href: 'users.html',      icon: '🔑', label: 'Benutzer', minRole: 3 },
        { href: 'maintenance.html', icon: '🔧', label: 'Wartung', minRole: 4 },
        { href: 'profile.html',    icon: '👤', label: 'Mein Profil', minRole: 1 },
        { href: 'docs.html',       icon: '📖', label: 'Dokumentation', minRole: 1 },
    ];

    const roleLevel = (typeof FT_AUTH !== 'undefined') ? FT_AUTH.getRoleLevel() : 1;

    // Build DOM
    const overlay = document.createElement('div');
    overlay.className = 'nav-overlay';

    const drawer = document.createElement('div');
    drawer.className = 'nav-drawer';

    // Home link
    drawer.innerHTML = `<a href="index.html"${isHome ? ' class="active"' : ''}><span class="nav-icon">🏠</span> Übersicht</a><div class="nav-divider"></div>`;

    // Page links (filtered by role + feature flags)
    const currentFile = path.split('/').pop();
    var cfg = window.FT_CONFIG;
    if (!cfg) {
        try { var cached = sessionStorage.getItem('lostdogtracer_config'); if (cached) cfg = JSON.parse(cached); } catch {}
    }
    pages.forEach(p => {
        if (roleLevel < (p.minRole || 1)) return;
        if (p.feat && cfg) {
            if (p.feat === 'deployment' && cfg.featDeployment === false) return;
            if (p.feat === 'equipment' && cfg.featEquipment === false) return;
        }
        const a = document.createElement('a');
        a.href = p.href;
        a.innerHTML = `<span class="nav-icon">${p.icon}</span> ${p.label}`;
        if (currentFile === p.href) a.classList.add('active');
        drawer.appendChild(a);
    });

    // Divider + theme toggle + logout
    const divider = document.createElement('div');
    divider.className = 'nav-divider';
    drawer.appendChild(divider);

    // Dark mode toggle
    if (window.FT_THEME) {
        drawer.appendChild(FT_THEME.createToggleButton());
        setTimeout(() => FT_THEME.updateToggleLabel(), 0);
    }

    const divider2 = document.createElement('div');
    divider2.className = 'nav-divider';
    drawer.appendChild(divider2);

    const logoutLink = document.createElement('a');
    logoutLink.href = '#';
    logoutLink.className = 'nav-logout';
    logoutLink.innerHTML = '<span class="nav-icon">🚪</span> Abmelden';
    logoutLink.addEventListener('click', e => {
        e.preventDefault();
        if (typeof FT_AUTH !== 'undefined') FT_AUTH.logout();
        location.href = 'index.html';
    });
    drawer.appendChild(logoutLink);

    // Build version
    const versionEl = document.createElement('div');
    versionEl.style.cssText = 'padding:0.75rem 1rem;font-size:0.7rem;color:var(--text-muted);';
    versionEl.textContent = '%%BUILD_VERSION%%';
    drawer.appendChild(versionEl);

    // App reset
    const resetLink = document.createElement('a');
    resetLink.href = '#';
    resetLink.style.cssText = 'font-size:0.75rem;color:var(--text-muted);';
    resetLink.innerHTML = '<span class="nav-icon">🔄</span> App zurücksetzen';
    resetLink.addEventListener('click', e => {
        e.preventDefault();
        if (confirm('App-Cache leeren und neu laden?')) {
            if (window.FT_THEME) FT_THEME.resetApp();
            else location.reload(true);
        }
    });
    drawer.appendChild(resetLink);

    // Hamburger button
    const btn = document.createElement('button');
    btn.className = 'hamburger-btn';
    btn.setAttribute('aria-label', 'Menü öffnen');
    btn.innerHTML = '<span></span><span></span><span></span>';

    // Toggle
    function toggle() {
        const isOpen = drawer.classList.toggle('open');
        overlay.classList.toggle('open', isOpen);
        btn.classList.toggle('open', isOpen);
        btn.setAttribute('aria-label', isOpen ? 'Menü schließen' : 'Menü öffnen');
    }

    btn.addEventListener('click', toggle);
    overlay.addEventListener('click', toggle);

    // Close on Escape
    document.addEventListener('keydown', e => {
        if (e.key === 'Escape' && drawer.classList.contains('open')) toggle();
    });

    document.body.appendChild(overlay);
    document.body.appendChild(drawer);
    document.body.appendChild(btn);
})();
