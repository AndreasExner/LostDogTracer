# LostDogTracer

**GPS Standort Tracker für die Suche nach vermissten Hunden**

LostDogTracer ist eine mobile-first Progressive Web App (PWA), die von Freiwilligen des Vereins *Hundesuchhilfe-Ostfriesland e.V.* zur Dokumentation von GPS-Standorten bei der Suche nach vermissten Hunden eingesetzt wird. Die App läuft als **Azure Static Web App** mit integrierter Azure Functions API und Azure Table Storage.

---

## Features

### Benutzer (Feld-App)
- GPS-Standort mit Kategorie, Kommentar und optionalem Foto speichern
- Eigene Einträge in Tabelle und auf Karte anzeigen
- Einträge direkt auf der Karte löschen
- **Live-Tracking**: eigenen Standort in Echtzeit auf der Karte anzeigen (ein-/ausschaltbar)
- Leaflet-Karte mit 6 Kartenebenen (OSM, Topo, Esri Satellite, Google Roads/Satellite/Hybrid)
- Marker-Clustering, farbcodierte Routen je Suchhund, Laufrouten-Anzeige
- Navigation zu Standorten via Google Maps, Apple Maps oder Waze
- PWA-installierbar mit Offline-Support (Service Worker + IndexedDB Queue)
- Dark Mode / Light Mode Umschaltung

### Admin
- GPS-Daten verwalten (filtern, sortieren, paginieren, bearbeiten, löschen)
- Kartenansicht mit allen Datensätzen, Kategorie-Mehrfachfilter, In-Map-Editing inkl. Löschen
- Neuer Eintrag per Adresssuche (Nominatim) mit Mini-Map und Marker-Feinkorrektur
- Export als KML und CSV
- Stammdaten verwalten: Namen, Hunde, Kategorien (inkl. SVG-Markersymbole)
- Admin-Konten verwalten (Passwort ändern, neue Benutzer, Reset)
- **Wartung**: Daten-Backup (JSON-Export) und Wiederherstellung (JSON-Import)
- Hamburger-Navigation zwischen allen Admin-Seiten

### Sicherheit
- API-Key Schutz aller Endpunkte
- Gestaffeltes Rate-Limiting: Read 120/min, Write 15/min, Auth 10/min pro IP
- Prod API-Key wird über GitHub Secrets zur Build-Zeit injiziert (nie im Quellcode)
- Admin-Authentifizierung mit PBKDF2-gehashten Passwörtern
- Session-Expired Toast bei 401-Redirects

---

## Architektur

```
┌─────────────────────────────────────────────┐
│  Frontend (Vanilla HTML/CSS/JS)             │
│  Hosted: Azure Static Web App               │
├─────────────────────────────────────────────┤
│  API (Azure Functions, .NET 8 Isolated)     │
│  /api/save-location                         │
│  /api/names, /api/lost-dogs, /api/categories│
│  /api/manage/gps-records, /api/manage/...   │
│  /api/auth/login, /api/auth/verify          │
├─────────────────────────────────────────────┤
│  Azure Table Storage                        │
│  + Azure Blob Storage (Fotos)               │
└─────────────────────────────────────────────┘
```

### Tabellen

| Tabelle         | PartitionKey   | RowKey             | Felder |
|-----------------|----------------|--------------------|--------|
| `GPSRecords`    | `<name>`       | `<rev-timestamp>`  | `lostDog`, `latitude`, `longitude`, `accuracy`, `recordedAt`, `category`, `comment`, `photoUrl` |
| `Names`         | `names`        | `<id>`             | `name` |
| `LostDogs`      | `locations`    | `<id>`             | `location` |
| `Categories`    | `categories`   | `<id>`             | `name`, `svgSymbol` |
| `AdminUsers`    | `users`        | `<username>`       | `passwordHash`, `salt` |

### API-Endpunkte

| Methode | Endpunkt | Beschreibung |
|---------|----------|--------------|
| POST | `/api/save-location` | GPS-Eintrag speichern (JSON oder Multipart mit Foto) |
| GET | `/api/names` | Alle Namen |
| GET | `/api/lost-dogs` | Alle Hunde |
| GET | `/api/categories` | Alle Kategorien (inkl. SVG-Symbol) |
| GET | `/api/my-records` | Eigene Einträge (gefiltert, paginiert) |
| GET | `/api/manage/gps-records` | Alle Einträge (Admin, gefiltert, paginiert) |
| POST | `/api/manage/gps-records/update` | Einträge bearbeiten (Einzel-/Bulk) |
| POST | `/api/manage/gps-records/delete` | Einträge löschen |
| POST | `/api/my-records/delete` | Eigene Einträge löschen |
| GET | `/api/manage/backup` | Daten-Backup (JSON-Export aller Tabellen) |
| POST | `/api/manage/restore` | Daten-Wiederherstellung (JSON-Import) |
| POST | `/api/auth/login` | Admin-Login |
| GET | `/api/auth/verify` | Token validieren |

---

## Projektstruktur

```
LostDogTracer/
├── index.html                    # Hauptseite (GPS erfassen)
├── my-records.html               # Eigene Einträge
├── my-map.html                   # Eigene Karte
├── admin.html                    # Admin Login & Menü
├── admin-gpsrecords.html         # Admin: GPS-Daten Tabelle
├── admin-map.html                # Admin: Karte
├── admin-names.html              # Admin: Namen verwalten
├── admin-lostdogs.html           # Admin: Hunde verwalten
├── admin-categories.html         # Admin: Kategorien verwalten
├── admin-users.html              # Admin: Konten verwalten
├── admin-backup.html             # Admin: Wartung (Backup/Restore)
├── sw.js                         # Service Worker (Offline Cache)
├── manifest.json                 # PWA Manifest
├── staticwebapp.config.json      # Azure SWA Konfiguration
│
├── css/
│   ├── shared.css                # Gemeinsame Styles (Theme, Toast, Nav)
│   ├── style.css                 # Styles für index.html
│   ├── admin.css                 # Styles für Admin-Seiten
│   └── map.css                   # Styles für Karten-Seiten
│
├── js/
│   ├── app.js                    # Hauptlogik (GPS speichern, Offline-Queue)
│   ├── auth.js                   # Auth-Helper (Token, API-Key, Session)
│   ├── theme.js                  # Dark/Light Mode Toggle
│   ├── offline-store.js          # IndexedDB Offline-Queue & Dropdown-Cache
│   ├── main-nav.js               # Hamburger-Menü (Benutzer-Seiten)
│   ├── admin-nav.js              # Hamburger-Menü (Admin-Seiten)
│   ├── admin-gpsrecords.js       # Admin: GPS-Tabelle, Filter, Bearbeiten
│   ├── admin-map.js              # Admin: Karte mit Marker-Editing
│   ├── admin-categories.js       # Admin: Kategorien + SVG-Upload
│   ├── admin-names.js            # Admin: Namen CRUD
│   ├── admin-lostdogs.js         # Admin: Hunde CRUD
│   ├── admin-users.js            # Admin: Konten CRUD
│   ├── admin-backup.js           # Admin: Backup/Restore
│   ├── my-map.js                 # Benutzer: Karte + Live-Tracking
│   └── my-records.js             # Benutzer: Eigene Einträge
│
├── api/
│   ├── Program.cs                # Azure Functions Host
│   ├── Functions/
│   │   ├── SaveLocationFunction.cs
│   │   ├── GPSRecordsFunction.cs
│   │   ├── NamesFunction.cs
│   │   ├── LostDogsFunction.cs
│   │   ├── CategoriesFunction.cs
│   │   ├── AuthFunction.cs
│   │   ├── AdminUsersFunction.cs
│   │   └── BackupRestoreFunction.cs
│   └── Security/                 # Auth & Rate-Limiting Middleware
│
├── docs/                         # Benutzer-Dokumentation
│   ├── LostDogTracer - Benutzeransicht.pdf
│   ├── LostDogTracer - Benutzeransicht.docx
│   ├── LostDogTracer - Admin Ansicht.pdf
│   └── LostDogTracer - Admin Ansicht.docx
│
└── .github/workflows/
    └── azure-static-web-apps.yml # CI/CD Pipeline
```

---

## Offline-Support

Die App bietet zweistufigen Offline-Support für den Feldeinsatz:

### Stufe 1: App-Shell Caching
Der Service Worker (`sw.js`) cached alle statischen Assets. Die App startet auch ohne Netzverbindung. Ein roter **„⚡ Offline"**-Badge erscheint oben links.

### Stufe 2: GPS-Queue
Wenn beim Speichern kein Netz verfügbar ist:
1. Der Eintrag wird in **IndexedDB** gespeichert (inkl. Foto)
2. Der Badge wechselt zu **„📶 3 ausstehend"** (orange)
3. Bei Verbindungsaufbau werden Einträge automatisch synchronisiert
4. Dropdown-Daten (Namen, Hunde, Kategorien) werden ebenfalls gecacht

---

## Dark Mode

Umschaltbar über das Hamburger-Menü auf jeder Seite. Der gewählte Modus wird in `localStorage` gespeichert. Standard ist Light Mode.

---

## Lokal starten

### Voraussetzungen
- [.NET 8 SDK](https://dotnet.microsoft.com/)
- [Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local) v4
- [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) (VS Code Extension oder npm)
- VS Code Extension „Live Server"

### Setup
```bash
# 1. Azurite starten (VS Code: Ctrl+Shift+P → "Azurite: Start")

# 2. API bauen & starten
cd api
dotnet build
func start --dotnet-isolated --port 7071

# 3. Frontend: index.html mit Live Server öffnen
```

Die API läuft auf `http://localhost:7071`, das Frontend erkennt localhost automatisch und verwendet den Dev-API-Key.

---

## Deployment

Das Projekt wird über **GitHub Actions** automatisch bei Push auf `main` deployed. Dabei werden:
- Der **Prod API-Key** aus GitHub Secrets injiziert (Platzhalter `%%PROD_API_KEY%%`)
- Die **Build-Version** generiert und in die Navigation injiziert (`v1.0.N-hash`)

```yaml
# .github/workflows/azure-static-web-apps.yml
on:
  push:
    branches: [main]
```

### Manuelles Deployment
```bash
# SWA CLI
swa deploy . --api-location api --deployment-token <token>
```

---

## Technologie-Stack

| Komponente | Technologie |
|------------|-------------|
| Frontend | Vanilla HTML/CSS/JS (kein Framework) |
| Design | Apple-inspiriert, CSS Custom Properties |
| Karte | Leaflet + MarkerCluster + Google Maps Layers |
| API | Azure Functions v4 (.NET 8 Isolated Worker) |
| Datenbank | Azure Table Storage |
| Dateispeicher | Azure Blob Storage (Fotos) |
| Hosting | Azure Static Web Apps (Free Tier) |
| CI/CD | GitHub Actions |
| PWA | Service Worker, IndexedDB, Web App Manifest |

---

## Dokumentation

Benutzer- und Admin-Dokumentation im Ordner `docs/`:
- [Benutzeransicht (PDF)](docs/LostDogTracer%20-%20Benutzeransicht.pdf)
- [Admin Ansicht (PDF)](docs/LostDogTracer%20-%20Admin%20Ansicht.pdf)

---

## Lizenz

Dieses Projekt wurde für den Verein *Hundesuchhilfe-Ostfriesland e.V.* entwickelt.
