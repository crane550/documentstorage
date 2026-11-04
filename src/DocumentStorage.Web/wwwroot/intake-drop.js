export function attach(root) {
    let depth = 0, pending = false, dispatching = false;
    const filesOnly = event => Array.from(event.dataTransfer?.types || []).includes('Files');
    const clear = () => { depth = 0; root.classList.remove('window-dragging'); };
    const feedback = text => { root.querySelector('[data-drop-feedback]').textContent = text; };
    const valid = files => {
        if (files.length > 20) { feedback('Choose at most 20 files per batch. No files were copied.'); return false; }
        if (files.some(file => file.size > 100 * 1024 * 1024)) { feedback('Each file must be 100 MB or smaller. No files were copied.'); return false; }
        return true;
    };
    const change = event => {
        if (event.target.matches('select')) { feedback(''); return; }
        if (!event.target.matches('input[type=file]')) return;
        if ((pending && !dispatching) || !valid(Array.from(event.target.files || []))) {
            event.stopImmediatePropagation();
            if (pending) feedback('A batch is already copying. Wait for it to finish.');
            else event.target.value = '';
            return;
        }
        pending = true;
    };
    const enter = event => {
        if (!filesOnly(event)) return;
        event.preventDefault(); depth++; root.classList.add('window-dragging');
    };
    const over = event => {
        if (!filesOnly(event)) return;
        event.preventDefault(); event.dataTransfer.dropEffect = !pending && root.dataset.ready === 'true' ? 'copy' : 'none';
    };
    const leave = event => { if (filesOnly(event) && --depth <= 0) clear(); };
    const drop = event => {
        if (!filesOnly(event)) return;
        event.preventDefault(); event.stopPropagation(); clear();
        if (pending) { feedback('A batch is already copying. Wait for it to finish.'); return; }
        if (root.dataset.ready !== 'true') { feedback('Choose an available intake folder before dropping files.'); return; }
        const items = Array.from(event.dataTransfer.items || []);
        if (items.some(item => item.webkitGetAsEntry?.()?.isDirectory)) { feedback('Drop individual files, not folders. No files were copied.'); return; }
        const files = Array.from(event.dataTransfer.files || []);
        if (!files.length || !valid(files)) return;
        const input = root.querySelector('input[type=file]');
        if (!input || input.disabled) return;
        feedback('');
        try {
            const transfer = new DataTransfer();
            for (const file of files) transfer.items.add(file);
            pending = true; dispatching = true;
            input.files = transfer.files; input.dispatchEvent(new Event('change', { bubbles: true }));
        }
        catch { pending = false; feedback('The browser could not read this drop. Use Choose files instead.'); }
        finally { dispatching = false; }
    };
    root.addEventListener('change', change, true);
    document.addEventListener('dragenter', enter);
    document.addEventListener('dragover', over);
    document.addEventListener('dragleave', leave);
    document.addEventListener('drop', drop, true);
    window.addEventListener('blur', clear);
    root.dataset.dropReady = 'true';
    return { complete() { pending = false; feedback(''); const input = root.querySelector('input[type=file]'); if (input) input.value = ''; }, dispose() {
        clear(); root.removeEventListener('change', change, true); document.removeEventListener('dragenter', enter); document.removeEventListener('dragover', over);
        document.removeEventListener('dragleave', leave); document.removeEventListener('drop', drop, true); window.removeEventListener('blur', clear);
    }};
}
