// Inbox layout: the drag bar between the preview and the slip, and folding the list away.
// Both are remembered on this device. Registered once for the document; it only sets a CSS variable and a
// data attribute on the inbox grid, which Blazor never renders, so a re-render never undoes them.
const SPLIT_KEY = "stash.inboxSplit", TRAY_KEY = "stash.inboxTray";
const MIN = 0.22, MAX = 0.72, START = 0.45;
let attached = false;

const grid = () => document.querySelector("[data-sort-inbox]");
const read = (key) => { try { return localStorage.getItem(key); } catch { return null; } };
const write = (key, value) => { try { localStorage.setItem(key, value); } catch { /* private window: per page only */ } };
const clamp = (value) => Math.min(MAX, Math.max(MIN, value));

function setSplit(target, ratio, save) {
    ratio = clamp(ratio);
    target.style.setProperty("--preview-fr", ratio.toFixed(3) + "fr");
    target.style.setProperty("--slip-fr", (1 - ratio).toFixed(3) + "fr");
    const bar = target.querySelector("[data-inbox-split]");
    if (bar) { bar.setAttribute("aria-valuenow", String(Math.round(ratio * 100))); bar.setAttribute("aria-valuemin", String(MIN * 100)); bar.setAttribute("aria-valuemax", String(MAX * 100)); }
    if (save) write(SPLIT_KEY, ratio.toFixed(3));
}

function setTray(target, folded, save) {
    target.dataset.tray = folded ? "folded" : "open";
    const button = target.querySelector("[data-tray-toggle]");
    if (button) { button.setAttribute("aria-expanded", folded ? "false" : "true"); button.setAttribute("aria-label", folded ? "Show the list" : "Hide the list"); button.title = folded ? "Show the list" : "Hide the list"; }
    if (save) write(TRAY_KEY, folded ? "folded" : "open");
}

function currentRatio(target) {
    const value = parseFloat(target.style.getPropertyValue("--preview-fr"));
    return Number.isFinite(value) ? value : START;
}

// Puts the remembered layout back on the grid; cheap, so the page calls it after every render.
export function apply() {
    const target = grid();
    if (!target) return;
    const saved = parseFloat(read(SPLIT_KEY));
    if (!target.style.getPropertyValue("--preview-fr")) setSplit(target, Number.isFinite(saved) ? saved : START, false);
    if (!target.dataset.tray) setTray(target, read(TRAY_KEY) === "folded", false);
}

export function attach() {
    apply();
    if (attached) return;
    attached = true;

    document.addEventListener("pointerdown", (event) => {
        const bar = event.target instanceof Element ? event.target.closest("[data-inbox-split]") : null;
        const target = grid();
        if (!bar || !target || event.button !== 0) return;
        const preview = target.querySelector(".inbox-preview"), slip = target.querySelector(".sort-slip");
        if (!preview || !slip) return;
        event.preventDefault();
        bar.setPointerCapture(event.pointerId);
        target.classList.add("resizing");
        const left = preview.getBoundingClientRect().left, right = slip.getBoundingClientRect().right;
        const move = (e) => setSplit(target, (e.clientX - left) / Math.max(1, right - left), false);
        const up = (e) => {
            bar.releasePointerCapture(e.pointerId);
            bar.removeEventListener("pointermove", move); bar.removeEventListener("pointerup", up); bar.removeEventListener("pointercancel", up);
            target.classList.remove("resizing");
            write(SPLIT_KEY, currentRatio(target).toFixed(3));
        };
        bar.addEventListener("pointermove", move); bar.addEventListener("pointerup", up); bar.addEventListener("pointercancel", up);
    });

    document.addEventListener("dblclick", (event) => {
        const target = grid();
        if (target && event.target instanceof Element && event.target.closest("[data-inbox-split]")) setSplit(target, START, true);
    });

    // The bar is a focusable separator: arrows move it, Home and End go to the limits.
    document.addEventListener("keydown", (event) => {
        const target = grid();
        if (!target || !(event.target instanceof Element) || !event.target.closest("[data-inbox-split]")) return;
        const step = event.shiftKey ? 0.08 : 0.03, ratio = currentRatio(target);
        const next = event.key === "ArrowLeft" ? ratio - step : event.key === "ArrowRight" ? ratio + step : event.key === "Home" ? MIN : event.key === "End" ? MAX : null;
        if (next === null) return;
        event.preventDefault(); event.stopPropagation();
        setSplit(target, next, true);
    }, true);

    document.addEventListener("click", (event) => {
        const target = grid();
        if (target && event.target instanceof Element && event.target.closest("[data-tray-toggle]")) setTray(target, target.dataset.tray !== "folded", true);
    });
}
