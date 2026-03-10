/**
 * Predefined SVG marker icons for category markers.
 * Each key maps to the inner SVG content (viewBox 0 0 24 36, icon area around cx=12 cy=12).
 * Used by admin-categories (icon picker) and map views (marker rendering).
 */
var SVG_ICONS = {
    flyer: {
        label: 'Flyer',
        svg: '<rect x="7" y="5" width="10" height="13" rx="1" fill="none" stroke="#fff" stroke-width="1.5"/><line x1="9.5" y1="9" x2="14.5" y2="9" stroke="#fff" stroke-width="1.2"/><line x1="9.5" y1="12" x2="14.5" y2="12" stroke="#fff" stroke-width="1.2"/><line x1="9.5" y1="15" x2="12.5" y2="15" stroke="#fff" stroke-width="1.2"/>'
    },
    eye: {
        label: 'Sichtung',
        svg: '<ellipse cx="12" cy="12" rx="6" ry="4" fill="none" stroke="#fff" stroke-width="1.5"/><circle cx="12" cy="12" r="2" fill="#fff"/>'
    },
    paw: {
        label: 'Pfote',
        svg: '<circle cx="9" cy="9" r="1.5" fill="#fff"/><circle cx="15" cy="9" r="1.5" fill="#fff"/><circle cx="7" cy="13" r="1.3" fill="#fff"/><circle cx="17" cy="13" r="1.3" fill="#fff"/><ellipse cx="12" cy="15" rx="3" ry="2.2" fill="#fff"/>'
    },
    crosshair: {
        label: 'Falle/Ziel',
        svg: '<circle cx="12" cy="12" r="5" fill="none" stroke="#fff" stroke-width="1.5"/><circle cx="12" cy="12" r="1.5" fill="#fff"/><line x1="12" y1="5" x2="12" y2="8" stroke="#fff" stroke-width="1.3"/><line x1="12" y1="16" x2="12" y2="19" stroke="#fff" stroke-width="1.3"/><line x1="5" y1="12" x2="8" y2="12" stroke="#fff" stroke-width="1.3"/><line x1="16" y1="12" x2="19" y2="12" stroke="#fff" stroke-width="1.3"/>'
    },
    home: {
        label: 'Zuhause',
        svg: '<path d="M12 5L5 12h2v6h4v-4h2v4h4v-6h2z" fill="#fff"/>'
    },
    warning: {
        label: 'Warnung',
        svg: '<path d="M12 5L5 18h14z" fill="none" stroke="#fff" stroke-width="1.5" stroke-linejoin="round"/><line x1="12" y1="10" x2="12" y2="14" stroke="#fff" stroke-width="1.5"/><circle cx="12" cy="16" r="0.8" fill="#fff"/>'
    },
    camera: {
        label: 'Kamera',
        svg: '<rect x="6" y="9" width="12" height="9" rx="1.5" fill="none" stroke="#fff" stroke-width="1.5"/><circle cx="12" cy="13.5" r="2.5" fill="none" stroke="#fff" stroke-width="1.3"/><rect x="9" y="7" width="6" height="2.5" rx="0.5" fill="none" stroke="#fff" stroke-width="1"/>'
    },
    food: {
        label: 'Futter',
        svg: '<ellipse cx="12" cy="15" rx="6" ry="3" fill="none" stroke="#fff" stroke-width="1.5"/><path d="M6 15c0-5 3-9 6-9s6 4 6 9" fill="none" stroke="#fff" stroke-width="1.3"/>'
    },
    flag: {
        label: 'Flagge',
        svg: '<line x1="8" y1="5" x2="8" y2="19" stroke="#fff" stroke-width="1.5"/><path d="M8 5h8l-2 4 2 4H8z" fill="#fff" opacity="0.8"/>'
    },
    heart: {
        label: 'Herz',
        svg: '<path d="M12 17s-6-4.5-6-8a3.2 3.2 0 0 1 6-1 3.2 3.2 0 0 1 6 1c0 3.5-6 8-6 8z" fill="#fff"/>'
    },
    car: {
        label: 'Fahrzeug',
        svg: '<rect x="5" y="10" width="14" height="6" rx="1.5" fill="none" stroke="#fff" stroke-width="1.3"/><path d="M7 10l2-4h6l2 4" fill="none" stroke="#fff" stroke-width="1.3"/><circle cx="8" cy="16" r="1.3" fill="#fff"/><circle cx="16" cy="16" r="1.3" fill="#fff"/>'
    },
    pin: {
        label: 'Markierung',
        svg: '<circle cx="12" cy="10" r="4" fill="none" stroke="#fff" stroke-width="1.5"/><circle cx="12" cy="10" r="1.5" fill="#fff"/><line x1="12" y1="14" x2="12" y2="19" stroke="#fff" stroke-width="1.5"/>'
    },
    default: {
        label: 'Standard',
        svg: '<circle cx="12" cy="12" r="5" fill="#fff"/>'
    }
};

/** Resolve an icon key (or legacy raw SVG) to safe SVG inner content */
function resolveIconSvg(keyOrLegacy) {
    if (!keyOrLegacy) return SVG_ICONS.default.svg;
    if (SVG_ICONS[keyOrLegacy]) return SVG_ICONS[keyOrLegacy].svg;
    // Legacy: raw SVG stored in DB → fall back to default icon
    return SVG_ICONS.default.svg;
}
