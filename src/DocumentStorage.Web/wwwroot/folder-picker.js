// Local dialog focus and keyboard behavior only; all folder operations go through Core.
export function attach(content) {
    const dialog = content.closest('[role="dialog"]');
    const previous = document.activeElement;
    let disposed = false;
    dialog.tabIndex = -1;
    const enabled = () => [...dialog.querySelectorAll('button, input, [tabindex="0"]')]
        .filter(item => !item.disabled && item.tabIndex >= 0 && item.getClientRects().length);
    function contain(event) {
        if (!disposed && [...document.querySelectorAll('[role="dialog"]')].at(-1) === dialog && !dialog.contains(event.target))
            (enabled()[0] ?? dialog).focus({ preventScroll: true });
    }
    function key(event) {
        const target = event.target;
        if (!(target instanceof Element)) return;
        if (event.key === 'Tab') {
            const items = enabled();
            const first = items[0], last = items.at(-1);
            if (event.shiftKey && (target === first || !items.includes(target))) { event.preventDefault(); last?.focus(); }
            else if (!event.shiftKey && (target === last || !items.includes(target))) { event.preventDefault(); first?.focus(); }
        }
        if (target.matches('[role="treeitem"]') && ['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End', 'Enter', ' '].includes(event.key)) event.preventDefault();
        // An enclosing editor may be a form. Enter in these inputs belongs to this picker.
        if (event.key === 'Enter' && target.matches('[data-folder-search], [data-folder-name]')) {
            event.preventDefault();
            if (target.matches('[data-folder-search]')) content.querySelector('[role="treeitem"][tabindex="0"]')?.focus();
        }
    }
    dialog.addEventListener('keydown', key);
    document.addEventListener('focusin', contain);
    return {
        focus(target) {
            if (disposed) return;
            const found = target === 'search' ? content.querySelector('[data-folder-search]') : target === 'name' ? content.querySelector('[data-folder-name]') : content.querySelector(`[data-folder-id="${CSS.escape(target)}"]`);
            found?.focus({ preventScroll: true });
            if (found?.matches('[role="treeitem"]')) found.scrollIntoView({ block: 'nearest' });
        },
        dispose() {
            disposed = true;
            dialog.removeEventListener('keydown', key);
            document.removeEventListener('focusin', contain);
            if (previous instanceof HTMLElement && previous.isConnected) previous.focus({ preventScroll: true });
        }
    };
}
