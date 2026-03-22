// ── Guest Page Hamburger Navigation ─────────────────────────────
(function () {
    'use strict';

    const currentPath = location.pathname.split('/').pop() || 'guest-home.html';
    const urlParams = new URLSearchParams(location.search);
    const guestKey = urlParams.get('key') || '';
    const paramName = urlParams.get('name') || 'HALTER*IN';
    const paramDog = urlParams.get('lostDog') || '';

    // Build DOM
    const overlay = document.createElement('div');
    overlay.className = 'nav-overlay';

    const drawer = document.createElement('div');
    drawer.className = 'nav-drawer';

    const homeHref = guestKey ? `guest-home.html?key=${encodeURIComponent(guestKey)}` : 'guest-home.html';

    const pages = [
        { href: homeHref, match: 'guest-home.html', icon: '📍', label: 'Standort erfassen' },
        { href: 'guest-records.html', icon: '📝', label: 'Einträge', needsParams: true },
        { href: 'guest-map.html', icon: '🗺️', label: 'Karte', needsParams: true },
    ];

    pages.forEach(p => {
        const a = document.createElement('a');
        const matchPath = p.match || p.href;
        const isActive = currentPath === matchPath;
        if (isActive) a.classList.add('active');

        a.innerHTML = `<span class="nav-icon">${p.icon}</span> ${p.label}`;

        if (!isActive) {
            a.href = '#';
            a.addEventListener('click', e => {
                e.preventDefault();
                if (p.needsParams) {
                    let dog = paramDog;
                    // On guest-home.html, read resolved dog name
                    const dogNameEl = document.getElementById('dogName');
                    if (dogNameEl && dogNameEl.textContent !== '—' && dogNameEl.textContent !== 'Unbekannter Hund') {
                        dog = dogNameEl.textContent;
                    }

                    if (!dog) {
                        toggle();
                        const toast = document.getElementById('toast');
                        if (toast) {
                            toast.textContent = 'Hundename konnte nicht ermittelt werden';
                            toast.className = 'toast error';
                            setTimeout(() => toast.className = 'toast hidden', 2500);
                        }
                        return;
                    }
                    const params = new URLSearchParams();
                    params.set('name', paramName);
                    params.set('lostDog', dog);
                    if (guestKey) params.set('key', guestKey);
                    location.href = p.href + '?' + params;
                } else {
                    location.href = p.href;
                }
            });
        }
        drawer.appendChild(a);
    });

    // Divider + theme toggle
    const divider = document.createElement('div');
    divider.className = 'nav-divider';
    drawer.appendChild(divider);

    if (window.FT_THEME) {
        drawer.appendChild(FT_THEME.createToggleButton());
        setTimeout(() => FT_THEME.updateToggleLabel(), 0);
    }

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

    function toggle() {
        const isOpen = drawer.classList.toggle('open');
        overlay.classList.toggle('open', isOpen);
        btn.classList.toggle('open', isOpen);
        btn.setAttribute('aria-label', isOpen ? 'Menü schließen' : 'Menü öffnen');
    }

    btn.addEventListener('click', toggle);
    overlay.addEventListener('click', toggle);

    document.addEventListener('keydown', e => {
        if (e.key === 'Escape' && drawer.classList.contains('open')) toggle();
    });

    document.body.appendChild(overlay);
    document.body.appendChild(drawer);
    document.body.appendChild(btn);
})();
