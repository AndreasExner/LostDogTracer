// ── FlyerTracker Offline Store (IndexedDB) ──────────────────────
const FT_OFFLINE = (function () {
    'use strict';

    const DB_NAME = 'flyertracker_offline';
    const DB_VERSION = 1;
    const STORE_QUEUE = 'pendingEntries';
    const STORE_DROPDOWNS = 'dropdownData';

    let db = null;

    function openDB() {
        return new Promise((resolve, reject) => {
            if (db) { resolve(db); return; }
            const req = indexedDB.open(DB_NAME, DB_VERSION);
            req.onupgradeneeded = e => {
                const d = e.target.result;
                if (!d.objectStoreNames.contains(STORE_QUEUE)) {
                    d.createObjectStore(STORE_QUEUE, { keyPath: 'id', autoIncrement: true });
                }
                if (!d.objectStoreNames.contains(STORE_DROPDOWNS)) {
                    d.createObjectStore(STORE_DROPDOWNS, { keyPath: 'key' });
                }
            };
            req.onsuccess = e => { db = e.target.result; resolve(db); };
            req.onerror = e => reject(e.target.error);
        });
    }

    // ── Queue: save a GPS entry for later upload ─────────────────
    async function queueEntry(entry) {
        const d = await openDB();
        return new Promise((resolve, reject) => {
            const tx = d.transaction(STORE_QUEUE, 'readwrite');
            tx.objectStore(STORE_QUEUE).add(entry);
            tx.oncomplete = () => resolve();
            tx.onerror = e => reject(e.target.error);
        });
    }

    // ── Queue: get all pending entries ────────────────────────────
    async function getPendingEntries() {
        const d = await openDB();
        return new Promise((resolve, reject) => {
            const tx = d.transaction(STORE_QUEUE, 'readonly');
            const req = tx.objectStore(STORE_QUEUE).getAll();
            req.onsuccess = () => resolve(req.result);
            req.onerror = e => reject(e.target.error);
        });
    }

    // ── Queue: remove an entry after successful upload ───────────
    async function removeEntry(id) {
        const d = await openDB();
        return new Promise((resolve, reject) => {
            const tx = d.transaction(STORE_QUEUE, 'readwrite');
            tx.objectStore(STORE_QUEUE).delete(id);
            tx.oncomplete = () => resolve();
            tx.onerror = e => reject(e.target.error);
        });
    }

    // ── Queue: count pending entries ─────────────────────────────
    async function pendingCount() {
        const d = await openDB();
        return new Promise((resolve, reject) => {
            const tx = d.transaction(STORE_QUEUE, 'readonly');
            const req = tx.objectStore(STORE_QUEUE).count();
            req.onsuccess = () => resolve(req.result);
            req.onerror = e => reject(e.target.error);
        });
    }

    // ── Dropdown cache: save dropdown data ───────────────────────
    async function saveDropdownData(key, data) {
        const d = await openDB();
        return new Promise((resolve, reject) => {
            const tx = d.transaction(STORE_DROPDOWNS, 'readwrite');
            tx.objectStore(STORE_DROPDOWNS).put({ key, data, savedAt: Date.now() });
            tx.oncomplete = () => resolve();
            tx.onerror = e => reject(e.target.error);
        });
    }

    // ── Dropdown cache: load dropdown data ───────────────────────
    async function getDropdownData(key) {
        const d = await openDB();
        return new Promise((resolve, reject) => {
            const tx = d.transaction(STORE_DROPDOWNS, 'readonly');
            const req = tx.objectStore(STORE_DROPDOWNS).get(key);
            req.onsuccess = () => resolve(req.result ? req.result.data : null);
            req.onerror = e => reject(e.target.error);
        });
    }

    return { queueEntry, getPendingEntries, removeEntry, pendingCount, saveDropdownData, getDropdownData };
})();
