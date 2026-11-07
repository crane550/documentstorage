// Shared presentations for extracted artifacts. File formats select a presentation in the registry.
// All labels, cell values and document text are inserted as text, never HTML or executable formulas.
const renderers = new Map();
export function registerPresentation(kind, render) { renderers.set(kind, render); }
export function renderContent(kind, container, result, signal) {
    container.replaceChildren();
    const render = renderers.get(kind);
    if (!render) throw new Error('This content preview is not available.');
    render(container, result, signal);
}
registerPresentation('document', (container, result) => {
    const note = document.createElement('p'); note.className = 'content-note'; note.textContent = 'Text preview · original page layout and embedded pictures are not shown.';
    const text = document.createElement('pre'); text.className = 'document-text'; text.textContent = result.text;
    container.append(note, text);
});
registerPresentation('spreadsheet', (container, result, signal) => {
    const sections = result.artifact?.sections || [];
    if (!sections.length) throw new Error('No worksheet preview is available.');
    const note = document.createElement('p'); note.className = 'content-note'; note.textContent = 'Saved cell values · formulas are not recalculated; charts and original formatting are not shown.';
    const tabs = document.createElement('div'); tabs.className = 'sheet-tabs'; tabs.setAttribute('role', 'tablist'); tabs.setAttribute('aria-label', 'Worksheets');
    const panel = document.createElement('div'); panel.className = 'sheet-panel'; panel.setAttribute('role', 'tabpanel'); panel.tabIndex = 0;
    const prefix = 'sheet-' + crypto.randomUUID(); panel.id = prefix + '-panel';
    container.append(note, tabs, panel);
    function columnName(column) { let name = ''; while (column > 0) { column--; name = String.fromCharCode(65 + column % 26) + name; column = Math.floor(column / 26); } return name; }
    function select(index) {
        if (signal.aborted) return;
        [...tabs.children].forEach((button, i) => { button.setAttribute('aria-selected', String(i === index)); button.tabIndex = i === index ? 0 : -1; });
        const section = sections[index]; panel.setAttribute('aria-labelledby', prefix + '-' + index); panel.replaceChildren();
        const heading = document.createElement('h3'); heading.textContent = section.name;
        const table = document.createElement('table'); table.className = 'sheet-table'; table.setAttribute('aria-label', section.name + ' cells');
        const columns = [...new Set(section.rows.flatMap(row => row.cells.map(cell => cell.column)))].sort((a, b) => a - b);
        const head = document.createElement('thead'), header = document.createElement('tr');
        const corner = document.createElement('th'); corner.scope = 'col'; corner.textContent = 'Row'; header.append(corner);
        for (const column of columns) { const cell = document.createElement('th'); cell.scope = 'col'; cell.textContent = columnName(column); header.append(cell); }
        head.append(header); const body = document.createElement('tbody');
        for (const row of section.rows) {
            const tr = document.createElement('tr'), label = document.createElement('th'); label.scope = 'row'; label.textContent = row.number; tr.append(label);
            const cells = new Map(row.cells.map(cell => [cell.column, cell]));
            for (const column of columns) { const td = document.createElement('td'), cell = cells.get(column); td.textContent = cell?.text ?? ''; if (cell) td.title = section.name + '!' + cell.reference; tr.append(td); }
            body.append(tr);
        }
        table.append(head, body); panel.append(heading, table);
        if (!section.rows.length) { const empty = document.createElement('p'); empty.textContent = 'No saved cells in the preview window.'; panel.append(empty); }
    }
    sections.forEach((section, index) => {
        const button = document.createElement('button'); button.type = 'button'; button.id = prefix + '-' + index; button.textContent = section.name; button.setAttribute('role', 'tab'); button.setAttribute('aria-controls', panel.id);
        button.addEventListener('click', () => select(index), { signal });
        button.addEventListener('keydown', event => { let next; if (event.key === 'ArrowRight') next = (index + 1) % sections.length; if (event.key === 'ArrowLeft') next = (index + sections.length - 1) % sections.length; if (next !== undefined) { event.preventDefault(); select(next); tabs.children[next].focus(); } }, { signal });
        tabs.append(button);
    });
    select(0);
});
