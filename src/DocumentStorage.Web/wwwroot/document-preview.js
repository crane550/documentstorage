import { renderContent } from './document-content.js';
let pdfModules;
async function pdfLibrary() {
    return pdfModules ??= (async () => {
        const pdf = await import('./vendor/pdfjs/build/pdf.min.mjs');
        globalThis.pdfjsLib = pdf;
        pdf.GlobalWorkerOptions.workerSrc = new URL('./vendor/pdfjs/build/pdf.worker.min.mjs', import.meta.url).href;
        const web = await import('./vendor/pdfjs/web/pdf_viewer.mjs');
        return { pdf, web };
    })();
}
export function attach(root, options) {
    const lifetime = new AbortController();
    let run, loadingTask, pdfViewer, pdfDocument, pdfEvents, blob, generation = 0, scale = 1, rotation = 0, expanded = false, oldOverflow, oldFocus, passwordCallback;
    const q = selector => root.querySelector(selector);
    const status = text => { if (!lifetime.signal.aborted && q('[data-status]')) q('[data-status]').textContent = text; };
    const on = (target, name, listener) => target?.addEventListener(name, listener, { signal: lifetime.signal });
    const ready = enabled => { root.dataset.loaded = String(enabled); root.querySelectorAll('.preview-toolbar button:not([data-action=reload]):not([data-action=wrap]), .preview-toolbar input').forEach(x => x.disabled = !enabled); };
    const expand = value => {
        expanded = value; root.classList.toggle('preview-expanded', value);
        if (value) { oldOverflow = document.body.style.overflow; oldFocus = document.activeElement; document.body.style.overflow = 'hidden'; root.setAttribute('role','dialog'); root.setAttribute('aria-modal','true'); }
        else { document.body.style.overflow = oldOverflow ?? ''; root.removeAttribute('role'); root.removeAttribute('aria-modal'); oldFocus?.focus(); }
        const button = q('[data-action=expand]'); button.textContent = value ? 'Close expanded preview' : 'Expand'; button.setAttribute('aria-label',value ? 'Close expanded preview' : 'Expand preview'); button.focus();
        requestAnimationFrame(() => { if (!lifetime.signal.aborted) fit(); });
    };
    const pdfNavigation = () => {
        if (!pdfViewer || !pdfDocument) return;
        q('[data-page]').value = pdfViewer.currentPageNumber; q('[data-pages]').textContent = 'of ' + pdfDocument.numPages;
        q('[data-action=previous]').disabled = pdfViewer.currentPageNumber <= 1; q('[data-action=next]').disabled = pdfViewer.currentPageNumber >= pdfDocument.numPages;
        q('[data-zoom]').textContent = Math.round(pdfViewer.currentScale * 100) + '%';
    };
    const imageLayout = () => {
        const image = q('img'), stage = q('.image-stage'); if (!image?.naturalWidth) return;
        const sideways = rotation % 180 !== 0;
        stage.style.width = (sideways ? image.naturalHeight : image.naturalWidth) * scale + 'px';
        stage.style.height = (sideways ? image.naturalWidth : image.naturalHeight) * scale + 'px';
        image.style.width = image.naturalWidth * scale + 'px'; image.style.height = image.naturalHeight * scale + 'px'; image.style.transform = `translate(-50%,-50%) rotate(${rotation}deg)`;
        q('[data-zoom]').textContent = Math.round(scale * 100) + '%';
    };
    function fit() {
        if (pdfDocument && pdfViewer) pdfViewer.currentScaleValue = 'page-width';
        if (options.kind === 'image' && q('img')?.naturalWidth) { scale = Math.min(1,(q('.image-scroll').clientWidth - 24) / (rotation % 180 ? q('img').naturalHeight : q('img').naturalWidth)); imageLayout(); }
    }
    const search = previous => {
        if (!pdfEvents) return;
        pdfEvents.dispatch('find', { source: root, type: 'again', query: q('[data-search]').value, caseSensitive: false, entireWord: false, highlightAll: true, findPrevious: previous, matchDiacritics: false });
    };
    async function cleanup() {
        run?.abort(); run = null; passwordCallback = null;
        delete root.dataset.rendered;
        if (q('[data-matches]')) q('[data-matches]').textContent='';
        if (q('[data-search]')) q('[data-search]').value='';
        if (q('[data-password-form]')) q('[data-password-form]').hidden = true;
        pdfViewer?.setDocument(null); pdfViewer = null; pdfDocument = null; pdfEvents = null;
        const task = loadingTask; loadingTask = null;
        if (task) await task.destroy().catch(() => {});
        if (blob) { URL.revokeObjectURL(blob); blob = null; }
        q('img')?.removeAttribute('src'); q('.source-preview')?.replaceChildren(); q('.structured-preview')?.replaceChildren();
    }
    async function start() {
        const version = ++generation; ready(false); status('Loading preview…');
        await cleanup(); if (lifetime.signal.aborted || generation !== version) return;
        run = new AbortController(); const signal = run.signal; rotation = 0;
        try {
            if (options.kind === 'pdf') {
                const {pdf, web} = await pdfLibrary(); if (signal.aborted) return;
                pdfEvents = new web.EventBus();
                const links = new web.PDFLinkService({eventBus:pdfEvents}); links.externalLinkEnabled=false;
                const find = new web.PDFFindController({eventBus:pdfEvents,linkService:links});
                pdfViewer = new web.PDFViewer({container:q('.pdf-scroll'),viewer:q('.pdfViewer'),eventBus:pdfEvents,linkService:links,findController:find,
                    abortSignal:signal,annotationMode:pdf.AnnotationMode.ENABLE,annotationEditorMode:pdf.AnnotationEditorType.DISABLE,enableAutoLinking:false,maxCanvasPixels:8_388_608});
                links.setViewer(pdfViewer);
                pdfEvents.on('pagesinit', () => { if (signal.aborted) return; ready(true); fit(); pdfViewer.currentPageNumber=Math.max(1,Math.min(pdfDocument.numPages,Number(options.page)||1)); pdfNavigation(); });
                pdfEvents.on('pagechanging', () => { if (!signal.aborted) pdfNavigation(); });
                pdfEvents.on('scalechanging', () => { if (!signal.aborted) pdfNavigation(); });
                pdfEvents.on('pagerendered', event => { if (!signal.aborted) { status(event.error ? 'A PDF page could not render. Try reloading or download the original.' : ''); root.dataset.rendered = String(event.pageNumber); } });
                pdfEvents.on('updatefindmatchescount', event => { if (!signal.aborted) q('[data-matches]').textContent = `${event.matchesCount.current} of ${event.matchesCount.total} matches`; });
                pdfEvents.on('updatefindcontrolstate', event => { if (!signal.aborted && event.state === 1) q('[data-matches]').textContent = 'No matches'; });
                const assets = new URL('./vendor/pdfjs/',import.meta.url).href;
                loadingTask = pdf.getDocument({url:options.url,withCredentials:true,isEvalSupported:false,enableXfa:false,cMapUrl:assets+'cmaps/',cMapPacked:true,standardFontDataUrl:assets+'standard_fonts/',wasmUrl:assets+'wasm/',disableAutoFetch:true,disableStream:true});
                loadingTask.onPassword = callback => { if (signal.aborted) return; passwordCallback = callback; q('[data-password-form]').hidden = false; status('This PDF needs a password.'); q('[data-password]').focus(); };
                const document = await loadingTask.promise; if (signal.aborted) return;
                pdfDocument = document; links.setDocument(document); pdfViewer.setDocument(document);
                q('[data-page]').max = document.numPages;
            } else {
                let response;
                for (let attempt = 0; ; attempt++) {
                    response = await fetch(options.url,{credentials:'same-origin',signal});
                    if (response.status !== 425 || attempt >= 40) break;
                    status('Preparing text preview…');
                    await new Promise((resolve, reject) => {
                        const done = () => { clearTimeout(timer); signal.removeEventListener('abort', abort); resolve(); };
                        const abort = () => { clearTimeout(timer); reject(new DOMException('Aborted', 'AbortError')); };
                        const timer = setTimeout(done, 1500); signal.addEventListener('abort', abort, {once:true}); if (signal.aborted) abort();
                    });
                }
                if (!response.ok) { const problem = await response.json().catch(()=>({})); throw new Error(problem.detail || 'Preview unavailable. Sign in again, check storage, or reload the document.'); }
                if (options.kind === 'image') {
                    blob = URL.createObjectURL(await response.blob()); q('img').src = blob; await q('img').decode(); if (signal.aborted) return;
                    ready(true); fit(); status('');
                } else {
                    const result = await response.json(); if (signal.aborted) return;
                    if (options.kind === 'text') q('.source-preview').textContent = result.text;
                    else renderContent(options.kind, q('.structured-preview'), result, signal);
                    ready(true); status(result.truncated ? options.kind === 'text' ? 'Showing the first 200,000 characters. Download the original for the complete file.' : 'Showing a bounded preview. Download the original for the complete document.' : '');
                }
            }
        } catch (error) {
            if (signal.aborted || generation !== version || lifetime.signal.aborted) return;
            ready(false); status(options.kind === 'pdf' ? 'The PDF could not be opened. It may be damaged, changed, or unavailable. Reload the preview or download the original.' : error.message || 'Preview unavailable. Download the original to open it.');
        }
    }
    on(root,'click', event => {
        const action = event.target.closest('[data-action]')?.dataset.action;
        switch(action) {
            case 'expand': expand(!expanded); break;
            case 'reload': void start(); break;
            case 'previous': if (pdfViewer) pdfViewer.currentPageNumber--; break;
            case 'next': if (pdfViewer) pdfViewer.currentPageNumber++; break;
            case 'fit': fit(); break;
            case 'rotate': rotation = (rotation+90)%360; if (pdfViewer) pdfViewer.pagesRotation=rotation; else imageLayout(); break;
            case 'in': case 'out': if (pdfViewer) pdfViewer.currentScale = Math.max(.25,Math.min(4,pdfViewer.currentScale*(action==='in'?1.25:.8))); else { scale=Math.max(.1,Math.min(4,scale*(action==='in'?1.25:.8))); imageLayout(); } break;
            case 'find-next': search(false); break;
            case 'find-previous': search(true); break;
            case 'wrap': if (options.kind === 'document') q('.document-text')?.classList.toggle('nowrap'); else q('.source-preview').classList.toggle('wrap'); break;
        }
    });
    on(q('[data-page]'),'change', event => { if (pdfViewer && pdfDocument) pdfViewer.currentPageNumber=Math.max(1,Math.min(pdfDocument.numPages,Number(event.target.value)||1)); });
    on(q('[data-search]'),'keydown', event => { if (event.key==='Enter') { event.preventDefault(); search(event.shiftKey); } });
    on(q('[data-password-form]'),'submit', event => { event.preventDefault(); const callback=passwordCallback; passwordCallback=null; if (callback) { q('[data-password-form]').hidden=true; callback(q('[data-password]').value); q('[data-password]').value=''; status('Opening PDF…'); } });
    on(document,'keydown', event => {
        if (!expanded) return;
        if (event.key==='Escape') { event.preventDefault(); event.stopPropagation(); expand(false); }
        if (event.key==='Tab') { const items=Array.from(root.querySelectorAll('button:not(:disabled),a,input:not(:disabled),[tabindex="0"]')).filter(x=>x.getClientRects().length); const first=items[0], last=items.at(-1); if(event.shiftKey&&document.activeElement===first){event.preventDefault();last?.focus();}else if(!event.shiftKey&&document.activeElement===last){event.preventDefault();first?.focus();} }
    });
    const resize = new ResizeObserver(() => { if (!lifetime.signal.aborted && (pdfViewer?.currentScaleValue==='page-width' || options.kind==='image')) fit(); }); resize.observe(root);
    if (options.enabled) { root.dataset.kind=options.kind; const rect=root.getBoundingClientRect(); if(rect.top>window.innerHeight*.35||rect.top<0)root.scrollIntoView({block:'start',behavior:matchMedia('(prefers-reduced-motion: reduce)').matches?'instant':'smooth'}); void start(); }
    return { dispose() { if (expanded) expand(false); lifetime.abort(); resize.disconnect(); generation++; return cleanup(); } };
}
