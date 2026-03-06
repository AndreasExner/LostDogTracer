// ── Admin Hamburger Navigation ──────────────────────────────────
(function () {
    // Inject on admin sub-pages and admin.html menu page
    const path = location.pathname;
    const isSubPage = /admin-(gpsrecords|map|names|lostdogs|categories|users|backup)\.html$/i.test(path);
    const isAdminHome = /admin\.html$/i.test(path);
    if (!isSubPage && !isAdminHome) return;

    const pages = [
        { href: 'admin-gpsrecords.html', icon: '📍', label: 'GPS-Daten' },
        { href: 'admin-names.html',      icon: '👤', label: 'Namen' },
        { href: 'admin-lostdogs.html',   icon: '🐕', label: 'Hunde' },
        { href: 'admin-categories.html', icon: '🏷️', label: 'Kategorien' },
        { href: 'admin-users.html',      icon: '🔑', label: 'Admin-Konten' },
        { href: 'admin-backup.html',     icon: '🔧', label: 'Wartung' },
    ];

    // Build DOM
    const overlay = document.createElement('div');
    overlay.className = 'nav-overlay';

    const drawer = document.createElement('div');
    drawer.className = 'nav-drawer';

    // Home link
    drawer.innerHTML = `<a href="admin.html"${isAdminHome ? ' class="active"' : ''}><span class="nav-icon">🏠</span> Übersicht</a><div class="nav-divider"></div>`;

    // Page links
    const currentFile = path.split('/').pop();
    pages.forEach(p => {
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
        location.href = 'admin.html';
    });
    drawer.appendChild(logoutLink);

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
