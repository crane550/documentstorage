// Survives the Blazor circuit being closed during a service restart. No credentials or writes.
export function watch(element, commit) {
    let disposed = false, offline = false, running = false;
    const tick = async () => {
        if (disposed || !element.isConnected || element.dataset.updateActive !== 'true' || running) return;
        running = true;
        try {
            const response = await fetch('/health', { cache: 'no-store', signal: AbortSignal.timeout(4000) });
            if (!response.ok) throw new Error('unavailable');
            const health = await response.json();
            if (!disposed && element.isConnected && health.status === 'ready' && (health.revision !== commit || offline)) {
                location.replace('/settings?tab=updates');
            }
        } catch {
            offline = true;
            const notice = element.querySelector('[data-update-reconnect]');
            if (notice) notice.hidden = false;
        } finally { running = false; }
    };
    const timer = setInterval(tick, 2500);
    return { dispose() { disposed = true; clearInterval(timer); } };
}
