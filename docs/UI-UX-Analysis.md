# FlyerTracker — UI/UX Analyse

## 1. Mobile Responsiveness

### 1.1 `user-scalable=no` auf index.html
`index.html` (Zeile 5) blockiert Pinch-to-Zoom — ein Accessibility-Problem. Die anderen Seiten haben das korrekt *nicht* gesetzt.

### 1.2 Kaum Media Queries
- `css/style.css` enthält **null** Media Queries — keinerlei responsive Anpassungen für die Hauptseite.
- `css/admin.css` (Zeile 270) hat nur einen einzigen Breakpoint bei `480px`. Kein Tablet-Breakpoint (768px), kein Landscape-Breakpoint.

### 1.3 Tabellen auf Mobilgeräten
Die GPS-Tabelle (`admin-gpsrecords.html`, Zeile 55–71) hat **10 Spalten** mit `white-space: nowrap`. Auf einem 375px-Display ist das praktisch unbedienbar — nur horizontales Scrollen, kein Card-Layout-Fallback.

### 1.4 Map-Höhe hardcoded
`calc(100vh - 120px)` in beiden Map-Seiten (`my-map.html` Zeile 14, `admin-map.html` Zeile 14) — der 120px-Offset berücksichtigt weder die dynamische Browser-Chrome-Höhe noch unterschiedliche Toolbar-Konfigurationen.

### 1.5 Zu kleine Touch-Targets
- Tabellen-Checkboxen: nur 16×16px (`css/admin.css`, Zeile 241–244) — weit unter der empfohlenen 44px-Mindestgröße.
- Pagination-Buttons: 36px Mindestbreite (`css/admin.css`, Zeile 248) — grenzwertig.

---

## 2. Navigation

### 2.1 Kein gemeinsames Navigationsmenü
Jede Seite hat nur einen einzelnen "Zurück"-Link. Es gibt **kein Hamburger-Menü, keine Sidebar, keine Tab-Bar**. Um zwischen Admin-Unterseiten zu wechseln, muss man immer erst zurück zur Übersicht.

### 2.2 Keine Breadcrumbs
Tiefere Seiten wie `admin-map.html` verlinken auf `admin-gpsrecords.html` (nicht auf `admin.html`), wodurch die Orientierung schwerfällt.

### 2.3 Keine Quernavigation
Zwischen den User-Seiten `my-map.html` und `my-records.html` gibt es keinen direkten Link — nur jeweils zurück zu `index.html`.

---

## 3. Formulare & Eingaben

### 3.1 Fehlende Labels in Admin-Formularen
- Filter-Dropdowns in `admin-gpsrecords.html` (Zeile 22–43) nutzen nur `title` statt `<label>` — nicht screenreader-tauglich.
- Eingabefelder auf den Admin-Listen-Seiten haben nur `placeholder`, kein `<label>`.

### 3.2 Keine Validierungshinweise
- Der Save-Button auf `index.html` wird deaktiviert, aber es gibt **keinen Hinweis**, welches Feld fehlt.
- Admin-Formular „Name hinzufügen": Leere Eingabe wird still ignoriert, kein visuelles Feedback.

### 3.3 Kein Zeichenzähler
Das Kommentar-Feld (`index.html`, Zeile 42) hat `maxlength="40"`, aber keinen Live-Zähler.

---

## 4. Karten-UX

### 4.1 Dichte Popups
Popups enthalten 7+ Zeilen Text plus Foto und Navi-Links (`js/admin-map.js`, Zeile 243–267). Auf kleinen Screens werden sie sehr hoch und könnten über den Viewport hinausragen. `maxWidth: 280` kann bei 320px-Viewports seitlich überlaufen.

### 4.2 Z-Index-Konflikte
- Toast und `.map-info` teilen sich `z-index: 1000` — Toasts könnten hinter Map-Controls verschwinden.
- Auf Map-Seiten könnte ein Toast vom Edit-Bar (`z-index: 2000`) verdeckt werden.

### 4.3 Kein Lade-Indikator für Map-Daten
Wenn `loadAndDisplay()` läuft, bleibt die Karte einfach leer — kein Spinner, kein Skeleton, kein Hinweis.

### 4.4 Google Maps API Key offen im HTML
`my-map.html` (Zeile 105) und `admin-map.html` (Zeile 147): Der Key ist im Quellcode sichtbar — sollte per Referrer-Restriction eingeschränkt werden.

---

## 5. Visuelle Konsistenz

### 5.1 CSS-Duplikation
`.site-banner` und `.toast` sind identisch in `css/style.css` und `css/admin.css` definiert — Wartungsrisiko.

### 5.2 Inline-Styles in vielen HTML-Dateien
`my-map.html` (Zeile 13–82) und `admin-map.html` (Zeile 13–103) haben große `<style>`-Blöcke mit nahezu identischem Code (`.toggle-badge`, `.nav-chooser`, `.map-info`). Das sollte in eine gemeinsame CSS-Datei.

### 5.3 Kein Dark Mode
Keine `prefers-color-scheme` Unterstützung. Für eine Outdoor-/Feld-App wäre Dark Mode bei schlechten Lichtverhältnissen sinnvoll.

---

## 6. Accessibility

### 6.1 Fehlende ARIA-Attribute
- Toasts: kein `role="status"` oder `aria-live` — Screenreader ignorieren sie.
- Lightbox: kein `role="dialog"`, kein `aria-modal`, nicht per Escape schließbar.
- Edit-Modal: ebenso keine `role="dialog"` oder Focus-Trap.

### 6.2 Kontrast-Probleme
Sekundärtext `#6e6e73` auf `#f5f5f7` hat nur ~3.9:1 Kontrast — **unter** WCAG AA (4.5:1 erforderlich). Betrifft alle Labels auf `index.html` und Pagination-Text.

### 6.3 Keyboard-Navigation lückenhaft
- Lightbox nur per Klick schließbar, kein Escape-Handler.
- Foto-Thumbnails nutzen `onclick` auf `<img>` — nicht tastaturzugänglich.
- Keine Focus-Traps in Modalen.

---

## 7. Performance

### 7.1 Kein Debounce bei Filter-Änderungen
Kategorie-Multi-Select löst bei jedem einzelnen Checkbox-Klick sofort einen API-Call aus (`js/admin-map.js`, Zeile 148). Schnelles Klicken erzeugt parallele Requests.

### 7.2 Kategorien werden unnötig oft geladen
Sowohl `js/admin-map.js` (Zeile 168–174) als auch `js/my-map.js` (Zeile 118–124) rufen `/api/categories` bei **jedem** Filterwechsel erneut ab — sollte gecacht werden.

### 7.3 `escHtml()` erzeugt Wegwerf-DOM-Elemente
Die HTML-Escape-Funktion erstellt für jeden Aufruf ein temporäres `div`-Element. Bei Hunderten von Markern sind das Tausende unnötiger DOM-Operationen.

---

## 8. Fehlerbehandlung

### 8.1 Toast-Klassen in admin-users.js fehlerhaft
`js/admin-users.js` (Zeile 16–18) nutzt `'toast-ok'`/`'toast-err'` — diese CSS-Klassen existieren nicht. Error-Toasts werden dort **nicht** rot dargestellt.

### 8.2 Kein Offline-Support
Trotz PWA-Manifest gibt es keinen Service Worker. Für eine Feld-App mit potentiell instabiler Verbindung wäre Offline-Queuing essenziell.

### 8.3 Stille Auth-Redirect
Bei 401 wird der User ohne Erklärung auf `admin.html` umgeleitet — ein kurzer Toast „Sitzung abgelaufen" wäre hilfreich.

---

## Prioritäten-Übersicht

| Priorität | Bereich | Problem |
|-----------|---------|---------|
| **Hoch** | Accessibility | Fehlende ARIA-Rollen (Toasts, Modals, Lightbox) |
| **Hoch** | Accessibility | Farbkontrast `#6e6e73` unter WCAG AA |
| **Hoch** | Mobile | `user-scalable=no` blockiert Zoom |
| **Hoch** | Mobile | 10-Spalten-Tabelle ohne Card-Alternative |
| **Hoch** | Bug | Falsche Toast-CSS-Klassen in admin-users.js |
| **Mittel** | Navigation | Kein gemeinsames Navigationsmenü |
| **Mittel** | Mobile | Touch-Targets unter 44px |
| **Mittel** | Performance | Kein Debounce, unnötige API-Wiederholungen |
| **Mittel** | UX | Kein Offline-Support trotz Feld-Einsatz |
| **Mittel** | Map | Dense Popups, kein Lade-Indikator |
| **Niedrig** | Konsistenz | CSS-Duplication, Toast-Timing variiert |
| **Niedrig** | UX | Kein Dark Mode, kein Zeichenzähler |
