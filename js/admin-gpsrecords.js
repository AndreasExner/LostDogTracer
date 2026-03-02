(function () {
    'use strict';

    const API_BASE = FT_AUTH.getApiBase();

    const filterDogEl = document.getElementById('filterDog');
    const pageSizeEl = document.getElementById('pageSize');
    const selectAllEl = document.getElementById('selectAll');
    const bodyEl = document.getElementById('recordsBody');
    const pageInfoEl = document.getElementById('pageInfo');
    const pageBtnsEl = document.getElementById('pageButtons');
    const deleteBtn = document.getElementById('deleteSelectedBtn');
    const exportCsvBtn = document.getElementById('exportCsvBtn');
    const exportExcelBtn = document.getElementById('exportExcelBtn');
    const toastEl = document.getElementById('toast');
    let toastTimeout = null;

    let currentPage = 1;
    let data = { records: [], totalCount: 0, page: 1, pageSize: 20, totalPages: 1, lostDogs: [] };

    // ── Load records ─────────────────────────────────────────────
    async function loadRecords() {
        bodyEl.innerHTML = '<tr><td colspan="8" style="color:#6e6e73;text-align:center;padding:2rem">Lädt…</td></tr>';
        const ps = pageSizeEl.value;
        const dog = filterDogEl.value;
        const params = new URLSearchParams();
        params.set('pageSize', ps);
        params.set('page', currentPage);
        if (dog) params.set('lostDog', dog);

        try {
            const res = await fetch(`${API_BASE}/manage/gps-records?${params}`, { headers: FT_AUTH.adminHeaders() });
            if (res.status === 401) { FT_AUTH.logout(); location.href = 'admin.html'; return; }
            if (!res.ok) throw new Error();
            data = await res.json();
            populateFilter(data.lostDogs);
            renderTable();
            renderPagination();
            updateDeleteButton();
        } catch {
            bodyEl.innerHTML = '<tr><td colspan="8" style="color:#ff3b30;text-align:center;padding:2rem">Fehler beim Laden</td></tr>';
        }
    }

    function populateFilter(dogs) {
        const current = filterDogEl.value;
        // keep first "Alle Hunde" option
        while (filterDogEl.options.length > 1) filterDogEl.remove(1);
        dogs.forEach(d => {
            const opt = document.createElement('option');
            opt.value = d;
            opt.textContent = d;
            filterDogEl.appendChild(opt);
        });
        filterDogEl.value = current;
    }

    // ── Render table ─────────────────────────────────────────────
    function renderTable() {
        bodyEl.innerHTML = '';
        selectAllEl.checked = false;

        if (data.records.length === 0) {
            bodyEl.innerHTML = '<tr><td colspan="8" style="color:#6e6e73;text-align:center;padding:2rem">Keine Einträge</td></tr>';
            return;
        }

        data.records.forEach(r => {
            const tr = document.createElement('tr');
            const photoCell = r.photoUrl
                ? `<td><img src="${esc(r.photoUrl)}" class="thumb" alt="Foto" onclick="document.getElementById('lightboxImg').src=this.src;document.getElementById('lightbox').classList.remove('hidden');"></td>`
                : '<td class="no-photo">—</td>';
            tr.innerHTML = `
                <td><input type="checkbox" class="row-cb" data-pk="${esc(r.partitionKey)}" data-rk="${esc(r.rowKey)}"></td>
                <td>${esc(r.name)}</td>
                <td>${esc(r.lostDog)}</td>
                ${photoCell}
                <td>${r.latitude.toFixed(6)}</td>
                <td>${r.longitude.toFixed(6)}</td>
                <td>${r.accuracy.toFixed(1)} m</td>
                <td>${formatDate(r.recordedAt)}</td>`;
            bodyEl.appendChild(tr);
        });
    }

    // ── Pagination ───────────────────────────────────────────────
    function renderPagination() {
        pageInfoEl.textContent = `${data.totalCount} Einträge — Seite ${data.page} von ${data.totalPages}`;
        pageBtnsEl.innerHTML = '';

        if (data.totalPages <= 1) return;

        const addBtn = (label, page, disabled, active) => {
            const b = document.createElement('button');
            b.textContent = label;
            b.disabled = disabled;
            if (active) b.classList.add('active');
            b.addEventListener('click', () => { currentPage = page; loadRecords(); });
            pageBtnsEl.appendChild(b);
        };

        addBtn('«', 1, currentPage === 1, false);
        addBtn('‹', currentPage - 1, currentPage === 1, false);

        // show max 5 page numbers around current
        let start = Math.max(1, currentPage - 2);
        let end = Math.min(data.totalPages, start + 4);
        start = Math.max(1, end - 4);

        for (let i = start; i <= end; i++) {
            addBtn(String(i), i, false, i === currentPage);
        }

        addBtn('›', currentPage + 1, currentPage >= data.totalPages, false);
        addBtn('»', data.totalPages, currentPage >= data.totalPages, false);
    }

    // ── Select all / checkboxes ──────────────────────────────────
    selectAllEl.addEventListener('change', () => {
        document.querySelectorAll('.row-cb').forEach(cb => { cb.checked = selectAllEl.checked; });
        updateDeleteButton();
    });
    bodyEl.addEventListener('change', e => {
        if (e.target.classList.contains('row-cb')) updateDeleteButton();
    });

    function getSelected() {
        return [...document.querySelectorAll('.row-cb:checked')].map(cb => ({
            partitionKey: cb.dataset.pk,
            rowKey: cb.dataset.rk
        }));
    }

    function updateDeleteButton() {
        const n = getSelected().length;
        deleteBtn.disabled = n === 0;
        deleteBtn.textContent = n > 0 ? `Auswahl löschen (${n})` : 'Auswahl löschen';
    }

    // ── Delete selected ──────────────────────────────────────────
    deleteBtn.addEventListener('click', async () => {
        const sel = getSelected();
        if (sel.length === 0) return;
        if (!confirm(`${sel.length} Einträge wirklich löschen?`)) return;

        deleteBtn.disabled = true;
        try {
            const res = await fetch(`${API_BASE}/manage/gps-records/delete`, {
                method: 'POST',
                headers: FT_AUTH.adminHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify(sel)
            });
            if (res.status === 401) { FT_AUTH.logout(); location.href = 'admin.html'; return; }
            if (!res.ok) throw new Error();
            const result = await res.json();
            showToast(`${result.deleted} Einträge gelöscht`);
            await loadRecords();
        } catch {
            showToast('Fehler beim Löschen', true);
        }
    });

    // ── CSV Export ────────────────────────────────────────────────
    exportCsvBtn.addEventListener('click', () => exportData('csv'));
    exportExcelBtn.addEventListener('click', () => exportData('excel'));

    async function exportData(format) {
        // Fetch ALL records (no pagination) for export
        const dog = filterDogEl.value;
        const params = new URLSearchParams();
        params.set('pageSize', 'all');
        if (dog) params.set('lostDog', dog);

        try {
            showToast('Exportiere…');
            const res = await fetch(`${API_BASE}/manage/gps-records?${params}`, { headers: FT_AUTH.adminHeaders() });
            if (res.status === 401) { FT_AUTH.logout(); location.href = 'admin.html'; return; }
            if (!res.ok) throw new Error();
            const allData = await res.json();

            if (allData.records.length === 0) {
                showToast('Keine Daten zum Exportieren', true);
                return;
            }

            const headers = ['Name', 'Hund', 'Breitengrad', 'L\u00e4ngengrad', 'Genauigkeit', 'Zeitpunkt', 'Foto-URL'];
            const rows = allData.records.map(r => [
                r.name, r.lostDog,
                r.latitude, r.longitude,
                r.accuracy, r.recordedAt,
                r.photoUrl || ''
            ]);

            if (format === 'csv') {
                downloadCSV(headers, rows);
            } else {
                downloadExcel(headers, rows);
            }
            showToast('Export abgeschlossen');
        } catch {
            showToast('Export fehlgeschlagen', true);
        }
    }

    function downloadCSV(headers, rows) {
        const BOM = '\uFEFF'; // UTF-8 BOM for Excel
        const sep = ';';
        let csv = BOM + headers.join(sep) + '\n';
        rows.forEach(r => {
            csv += r.map(v => `"${String(v).replace(/"/g, '""')}"`).join(sep) + '\n';
        });
        download(csv, 'GPS-Daten.csv', 'text/csv;charset=utf-8');
    }

    function downloadExcel(headers, rows) {
        // Simple XML-based Excel export (opens in Excel without library)
        let xml = '<?xml version="1.0" encoding="UTF-8"?>\n';
        xml += '<?mso-application progid="Excel.Sheet"?>\n';
        xml += '<Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet"\n';
        xml += '  xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">\n';
        xml += '<Worksheet ss:Name="GPS-Daten"><Table>\n';

        // Header row
        xml += '<Row>';
        headers.forEach(h => { xml += `<Cell><Data ss:Type="String">${escXml(h)}</Data></Cell>`; });
        xml += '</Row>\n';

        // Data rows
        rows.forEach(r => {
            xml += '<Row>';
            r.forEach((v, i) => {
                const isNum = (i >= 2 && i <= 4);
                const type = isNum ? 'Number' : 'String';
                xml += `<Cell><Data ss:Type="${type}">${escXml(String(v))}</Data></Cell>`;
            });
            xml += '</Row>\n';
        });

        xml += '</Table></Worksheet></Workbook>';
        download(xml, 'GPS-Daten.xls', 'application/vnd.ms-excel');
    }

    function download(content, filename, mimeType) {
        const blob = new Blob([content], { type: mimeType });
        const a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(a.href);
    }

    // ── Events ───────────────────────────────────────────────────
    filterDogEl.addEventListener('change', () => { currentPage = 1; loadRecords(); });
    pageSizeEl.addEventListener('change', () => { currentPage = 1; loadRecords(); });

    // ── Helpers ──────────────────────────────────────────────────
    function esc(s) {
        const d = document.createElement('div');
        d.textContent = s;
        return d.innerHTML;
    }
    function escXml(s) {
        return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }
    function formatDate(iso) {
        if (!iso) return '—';
        try {
            const d = new Date(iso);
            return d.toLocaleString('de-DE', {
                day: '2-digit', month: '2-digit', year: 'numeric',
                hour: '2-digit', minute: '2-digit', second: '2-digit'
            });
        } catch { return iso; }
    }
    function showToast(msg, isError) {
        clearTimeout(toastTimeout);
        toastEl.textContent = msg;
        toastEl.className = 'toast' + (isError ? ' error' : '');
        toastTimeout = setTimeout(() => toastEl.classList.add('hidden'), 2500);
    }

    // ── Init ─────────────────────────────────────────────────────
    loadRecords();
})();
