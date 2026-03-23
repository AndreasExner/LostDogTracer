# LostDogTracer

**Support App für die Suche vermisster Hunde**

LostDogTracer ist eine mobile-first Progressive Web App (PWA) zur Dokumentation von GPS-Standorten bei der Suche nach vermissten Hunden. Die App läuft als **Azure Static Web App** mit Azure Functions API und Azure Table Storage.

> **Lizenz:** MIT — siehe [LICENSE](LICENSE)

---

## Features

### Feldarbeit (Erfassen)
- GPS-Standort mit Kategorie, Kommentar und optionalem Foto speichern
- Automatische Erkennung des angemeldeten Benutzers
- Einträge und Karte zum ausgewählten Hund anzeigen
- Live-Standort-Tracking auf der Karte
- **Offline-Support**: Einträge werden in IndexedDB zwischengespeichert und bei Verbindung automatisch übertragen
- PWA-installierbar auf iOS und Android

### GPS-Daten (Verwaltung)
- Alle Einträge filtern (Name, Hund, Kategorie), sortieren und paginieren
- Einzel- und Massenbearbeitung (inkl. Zeitpunkt)
- Neuer Eintrag per Adresssuche (Nominatim) mit Kartenvorschau
- Export als KML und CSV
- Kartenansicht mit Marker-Clustering, farbcodierten Routen, Kategorie-SVG-Icons
- Navigation zu Standorten via Google Maps, Apple Maps, Waze

### Gast-Zugang
- Linkbasierter Zugang über einen 6-Zeichen-Schlüssel pro Hund
- Standorterfassung mit fester Kategorie (konfigurierbar)
- Keine Registrierung erforderlich

### Administration
- Benutzer, Hunde und Kategorien verwalten
- Daten-Backup (JSON) und Wiederherstellung
- Konfiguration (Banner, Links, Dokumentation) über Config-Tabelle

### Sicherheit & Rollen
- API-Key-Schutz aller Endpunkte
- Rollenbasierte Zugriffskontrolle (Backend + Frontend):

| Rolle | Level | Zugriff |
|-------|-------|---------|
| User | 1 | Erfassen, GPS-Daten (lesen), Profil, Dokumentation |
| PowerUser | 2 | + GPS-Daten (bearbeiten, löschen) |
| Manager | 3 | + Hunde, Benutzer (anlegen) |
| Administrator | 4 | + Kategorien, Wartung, Benutzer bearbeiten/löschen, Config |

- PBKDF2-gehashte Passwörter, HMAC-signierte Tokens (24h Lebensdauer)
- Rate-Limiting: Read 120/min, Write 15/min, Auth 10/min pro IP

---

## Architektur

```
┌─────────────────────────────────────────────┐
│  Frontend (Vanilla HTML/CSS/JS, PWA)        │
│  Hosted: Azure Static Web App               │
├─────────────────────────────────────────────┤
│  API (Azure Functions, .NET 8 Isolated)     │
│  /api/save-location, /api/config            │
│  /api/lost-dogs, /api/categories            │
│  /api/manage/gps-records, /api/manage/...   │
│  /api/auth/login, /api/auth/verify          │
├─────────────────────────────────────────────┤
│  Azure Table Storage (5 Tabellen + Config)  │
│  Azure Blob Storage (Fotos)                 │
└─────────────────────────────────────────────┘
```

### Tabellen

| Tabelle | PartitionKey | RowKey | Beschreibung |
|---------|-------------|--------|--------------|
| `GPSRecords` | Username | Rev-Timestamp | GPS-Einträge mit FK auf Users, LostDogs, Categories |
| `Users` | `users` | Username | Benutzerkonten mit Rolle und DisplayName |
| `LostDogs` | `lostdogs` | Name_Suffix | Vermisste Hunde mit DisplayName und Gast-Schlüssel |
| `Categories` | `categories` | Timestamp-ID | Kategorien mit DisplayName und SVG-Symbol |
| `Config` | `config` | `settings` | App-Konfiguration (Banner, Links, Dokumente) |

---

## Projektstruktur

```
LostDogTracer/
├── index.html                    # Login & Hauptmenü (rollenbasiert)
├── field-home.html               # Feldarbeit: Standort erfassen
├── field-records.html            # Feldarbeit: Einträge
├── field-map.html                # Feldarbeit: Karte
├── gpsrecords.html               # GPS-Daten Verwaltung
├── map.html                      # Kartenansicht (Verwaltung)
├── lostdogs.html                 # Hunde verwalten
├── categories.html               # Kategorien verwalten
├── users.html                    # Benutzerverwaltung
├── backup.html                   # Wartung (Backup/Restore)
├── profile.html                  # Eigenes Profil
├── docs.html                     # Dokumentation (PDF-Links)
├── guest-home.html               # Gast: Standort erfassen
├── guest-records.html            # Gast: Einträge
├── guest-map.html                # Gast: Karte
├── sw.js                         # Service Worker (versionierter Cache)
├── manifest.json                 # PWA Manifest
│
├── js/
│   ├── auth.js                   # Auth-Helper (Token, Rolle, API-Key)
│   ├── theme.js                  # Theme, Config-Loader, App-Reset
│   ├── nav.js                    # Hamburger-Menü (Hauptseiten)
│   ├── field-nav.js              # Hamburger-Menü (Feldarbeit)
│   ├── guest-nav.js              # Hamburger-Menü (Gast)
│   ├── field-app.js              # Feldarbeit: GPS-Erfassung + Offline
│   ├── gpsrecords.js             # GPS-Daten: Tabelle, Filter, Edit
│   ├── map.js                    # Karte: Marker, Routen, Edit
│   ├── lostdogs.js               # Hunde: CRUD mit Modalen
│   ├── categories.js             # Kategorien: CRUD + SVG-Picker
│   ├── users.js                  # Benutzer: CRUD (rollenabhängig)
│   ├── profile.js                # Profil: Passwort + Anzeigename
│   ├── backup.js                 # Backup: Export/Import
│   ├── offline-store.js          # IndexedDB Queue + Dropdown-Cache
│   ├── svg-icons.js              # SVG-Markersymbole
│   ├── guest-app.js              # Gast: Erfassung
│   ├── guest-map.js              # Gast: Karte
│   └── guest-records.js          # Gast: Einträge
│
├── api/
│   ├── Program.cs                # Azure Functions Host + DI
│   ├── Functions/
│   │   ├── SaveLocationFunction.cs
│   │   ├── GPSRecordsFunction.cs
│   │   ├── LostDogsFunction.cs
│   │   ├── CategoriesFunction.cs
│   │   ├── UsersFunction.cs
│   │   ├── AuthFunction.cs
│   │   ├── BackupRestoreFunction.cs
│   │   └── ConfigFunction.cs
│   └── Security/
│       ├── AdminAuth.cs          # Authentifizierung + Rollenverwaltung
│       ├── ApiKeyValidator.cs
│       ├── PasswordHasher.cs
│       └── RateLimiter.cs
│
├── docs/                         # PDF-Dokumentation
│   ├── LostDogTracer-1-Einrichtung_und_erste_Schritte.pdf
│   ├── LostDogTracer-2-Benutzer_Handbuch.pdf
│   └── LostDogTracer-3-Admin_Handbuch.pdf
│
├── scripts/
│   ├── MigrateGPS/               # Einmalige Datenmigration
│   ├── SeedTables/               # Tabellen-Seeding
│   └── QueryGPS/                 # GPS-Abfrage-Tool
│
└── .github/workflows/
    └── azure-static-web-apps.yml # CI/CD (API-Key + Version Injection)
```

---

## Dokumentation

Ausführliche Anleitungen als PDF im `docs/`-Ordner und über die App unter "Dokumentation":

| Dokument | Inhalt |
|----------|--------|
| **Einrichtung und erste Schritte** | Azure-Ressourcen, Deployment, Erstkonfiguration |
| **Benutzer Handbuch** | Feldarbeit, GPS-Erfassung, Karten, Offline-Nutzung |
| **Admin Handbuch** | Verwaltung, Rollen, Konfiguration, Backup (nur Administratoren) |

---

## Lokal starten

### Voraussetzungen
- [.NET 8 SDK](https://dotnet.microsoft.com/)
- [Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local) v4
- [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) (VS Code Extension)

### Setup
```bash
# 1. Azurite starten (VS Code: Ctrl+Shift+P → "Azurite: Start")

# 2. API starten
cd api
dotnet build
func start --dotnet-isolated --port 7071

# 3. Frontend: index.html mit Live Server öffnen
```

Standard-Seed-Login: `admin` / `LostDogTracer2026!`

---

## Deployment

Automatisch via GitHub Actions bei Push auf `main`:
- Prod API-Key aus GitHub Secrets injiziert (`%%PROD_API_KEY%%`)
- Build-Version generiert und in Navigation + Service Worker injiziert (`v1.3.N-hash`)
- Config-Tabelle wird beim ersten API-Aufruf auto-geseeded

---

## Technologie-Stack

| Komponente | Technologie |
|------------|-------------|
| Frontend | Vanilla HTML/CSS/JS (kein Framework) |
| Karte | Leaflet + MarkerCluster |
| API | Azure Functions v4 (.NET 8 Isolated) |
| Datenbank | Azure Table Storage |
| Dateispeicher | Azure Blob Storage (Fotos) |
| Hosting | Azure Static Web Apps |
| CI/CD | GitHub Actions |
| PWA | Service Worker, IndexedDB, Web App Manifest |

---

## Lizenz

MIT — siehe [LICENSE](LICENSE)
