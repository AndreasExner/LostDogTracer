// ── Main Page Hamburger Navigation ──────────────────────────────
(function () {
    'use strict';

    const currentPath = location.pathname.split('/').pop() || 'my-home.html';
    const urlParams = new URLSearchParams(location.search);
    const paramName = urlParams.get('name') || '';
    const paramDog = urlParams.get('lostDog') || '';

    // Build DOM
    const overlay = document.createElement('div');
    overlay.className = 'nav-overlay';

    const drawer = document.createElement('div');
    drawer.className = 'nav-drawer';

    const pages = [
        { href: 'my-home.html', icon: '📍', label: 'Standort erfassen' },
        { href: 'my-records.html', icon: '📝', label: 'Meine Einträge', needsParams: true },
        { href: 'my-map.html', icon: '🗺️', label: 'Meine Karte', needsParams: true },
    ];

    pages.forEach(p => {
        const a = document.createElement('a');
        const isActive = currentPath === p.href;
        if (isActive) a.classList.add('active');

        a.innerHTML = `<span class="nav-icon">${p.icon}</span> ${p.label}`;

        if (!isActive) {
            a.href = '#';
            a.addEventListener('click', e => {
                e.preventDefault();
                if (p.needsParams) {
                    // Get name/dog from current page context
                    let name = paramName;
                    let dog = paramDog;
                    // On my-home.html, read from dropdowns
                    const nameEl = document.getElementById('userName');
                    const dogEl = document.getElementById('lostDog');
                    if (nameEl && nameEl.value) name = nameEl.value;
                    if (dogEl && dogEl.value) dog = dogEl.value;

                    if (!name || !dog) {
                        toggle();
                        const toast = document.getElementById('toast');
                        if (toast) {
                            toast.textContent = 'Bitte zuerst Name und Hund auswählen';
                            toast.className = 'toast error';
                            setTimeout(() => toast.className = 'toast hidden', 2500);
                        }
                        return;
                    }
                    const params = new URLSearchParams();
                    params.set('name', name);
                    params.set('lostDog', dog);
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
