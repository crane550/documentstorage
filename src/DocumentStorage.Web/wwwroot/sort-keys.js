// Inbox keys: J / ↓ next card, K / ↑ previous, Enter approves the open slip.
// Keys typed into a field are left alone (Enter there submits the slip's own form).
// It only clicks buttons Blazor rendered; it never changes the page itself.
let handler = null;

function typing(target) {
    if (!(target instanceof Element)) return false;
    return target.closest("input, textarea, select, [contenteditable=''], [contenteditable='true'], .document-preview, [role='menu'], dialog") !== null;
}

export function attach() {
    if (handler) document.removeEventListener("keydown", handler);
    handler = (event) => {
        if (event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey) return;
        if (!document.querySelector("[data-sort-inbox]") || typing(event.target)) return;
        const key = event.key;
        let target = null;
        if (key === "j" || key === "J" || key === "ArrowDown") target = document.querySelector("[data-sort-next]");
        else if (key === "k" || key === "K" || key === "ArrowUp") target = document.querySelector("[data-sort-previous]");
        else if (key === "Enter" && !(event.target instanceof HTMLButtonElement) && !(event.target instanceof HTMLAnchorElement)) target = document.querySelector("[data-sort-approve]:not(:disabled)");
        if (!target) return;
        event.preventDefault();
        target.click();
    };
    document.addEventListener("keydown", handler);
}

export function detach() {
    if (handler) document.removeEventListener("keydown", handler);
    handler = null;
}

// Puts the cursor in a box Blazor just rendered, such as a field added on the slip.
export function focusById(id) {
    const element = document.getElementById(id);
    if (element instanceof HTMLElement) element.focus();
}
