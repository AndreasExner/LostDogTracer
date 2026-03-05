// ── Main Page Hamburger Navigation ──────────────────────────────
(function () {
    'use strict';

    // Build DOM
    const overlay = document.createElement('div');
    overlay.className = 'nav-overlay';

    const drawer = document.createElement('div');
    drawer.className = 'nav-drawer';

    drawer.innerHTML = '<a class="active"><span class="nav-icon">📍</span> Standort erfassen</a>';

    const links = [
        { href: '#', icon: '📝', label: 'Meine Einträge', action: 'records' },
        { href: '#', icon: '🗺️', label: 'Meine Karte', action: 'map' },
    ];

    links.forEach(p => {
        const a = document.createElement('a');
        a.href = p.href;
        a.innerHTML = `<span class="nav-icon">${p.icon}</span> ${p.label}`;
        a.addEventListener('click', e => {
            e.preventDefault();
            const userNameEl = document.getElementById('userName');
            const lostDogEl = document.getElementById('lostDog');
            if (!userNameEl.value || !lostDogEl.value) {
                // Close drawer and show hint
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
            params.set('name', userNameEl.value);
            params.set('lostDog', lostDogEl.value);
            if (p.action === 'records') {
                location.href = 'my-records.html?' + params;
            } else if (p.action === 'map') {
                location.href = 'my-map.html?' + params;
            }
        });
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
